# Sprint 3.4 — progress

> The disconnect escape-hatch: the FINAL fix for the mid-copy USB-disconnect bug (#45, the 5th
> manifestation). Branch `fix/sprint-3.4` off `fix/sprint-3.3` (@ `1554365`, carries 3.2 long-path +
> 3.3 watchdog). One PR **based on `main`**, then STOP for the producer review gate. Never merge here;
> never push `main`. **CEO cap:** last disconnect round — a 6th hardware failure ships v1.0 with a
> documented limitation, no round 6.

## Ground truth (#45, captured live by QA — thread stack + 139 MB dump, NOT inferred)

A mid-copy yank makes `afc_file_read` return `EmptyResponse`/0; the fault unwinds into
`FileCopier.StreamToStagingAsync`'s `await using` disposal → `AbandonableReadStream.Dispose` (the read
returned, so it is **not** abandoned) → `DisposeInner` → `AfcReadStream.Dispose()` → synchronous native
`afc_file_close()` on the dead transport → **busy-spins forever** (`AfcReadStream.cs:108`, the only call
site, unguarded). The 3.3 `ForwardProgressWatchdog` timer still trips on its independent thread, but its
only action was to cancel a token — useless against a thread already wedged in a synchronous native call.

**The real class (stated correctly at last):** *every* synchronous native call on a dead transport can
busy-spin, and graceful unwinding keeps walking into the next unguarded one (open → read → stat → list →
**close** → `afc_client_free`/lockdown next). Guarding `afc_file_close` would be band-aid #6 with the
identical risk. **We stop enumerating native calls.**

## The fix — one escape-hatch (NOT a 6th band-aid)

When the byte heartbeat is dead the device is provably gone and the data is already safe. So on
`ForwardProgressWatchdog` trip, the **independent timer thread** now:

1. prints the one disconnect line,
2. writes **and flushes** `summary.txt` from a **fresh read-only** journal connection (no device call), and
3. **terminates the process with exit code 3 via a finalizer-skipping hard exit.**

It never waits for or re-enters native device code. The orphaned native thread (spinning `afc_file_close`,
or anything else) is reaped by the OS.

### THE critical detail — the hard exit MUST skip finalizers

`Environment.Exit(3)` is **WRONG** here: it runs finalizers, and the `AfcReadStream` finalizer
(`afc_file_close`) **and** the `AfcClientHandle` SafeHandle **critical** finalizer (`afc_client_free`)
would each re-enter the busy-spinning native layer on the dead transport during shutdown → the exit
itself hangs. The escape-hatch uses `TerminateProcess(GetCurrentProcess(), 3)` (kernel32 P/Invoke,
`OperatingSystem.IsWindows()`-guarded): the OS reaps every thread and handle, **no managed or critical
finalizer runs**, nothing re-enters native code.

### Testability — the terminate is injected

`TerminateProcess` cannot run in a unit test. It is behind a seam, `IProcessTerminator`
(real impl `TerminateProcessTerminator` = `TerminateProcess`; test double `RecordingProcessTerminator`
records the exit code). `DisconnectEscapeHatch` composes print → write+flush summary → terminate, and the
watchdog invokes it via a new `onTrip` hook.

## What changed

| File | Change |
|------|--------|
| `Transfer/IProcessTerminator.cs` (new) | The injectable finalizer-skipping terminate seam. |
| `Transfer/TerminateProcessTerminator.cs` (new) | Real impl: `[LibraryImport]` `TerminateProcess`+`GetCurrentProcess`, `IsWindows`-guarded, non-Windows `Environment.Exit` fallback (no native spin there). |
| `Transfer/DisconnectEscapeHatch.cs` (new) | `Activate()` (once): print line → `TryWriteSummary` (fresh `OpenReadOnly` + `SummaryWriter.Write(flushToDisk:true)`, best-effort) → `terminator.Terminate(3)`. |
| `Transfer/ForwardProgressWatchdog.cs` | New optional `Action? onTrip`; `Trip()` invokes it **once, outside the gate**, after the existing `cts.Cancel()`. `null` = unchanged 3.3 behavior. |
| `Transfer/FileCopier.cs` | New optional `Action? onDisconnect`, forwarded to the watchdog's `onTrip`. |
| `Summary/SummaryWriter.cs` | `Write(..., bool flushToDisk = false)`; `true` writes via `FileStream` + `Flush(flushToDisk:true)` (UTF-8 no BOM, same long-path). Default unchanged. |
| `Cli/Commands/CopyCommand.cs` | Builds `DisconnectEscapeHatch(destination, new TerminateProcessTerminator())` and passes `onDisconnect: escapeHatch.Activate`. |

**Kept (3.3, the right liveness signal):** `ForwardProgressWatchdog` (independent timer + byte heartbeat)
and `AbandonableReadStream`. 3.4 only changes what **trip** does. The token-cancel path is retained — it
still unwinds the abandonable cases cleanly and is defense-in-depth — but the **terminate is the
load-bearing guarantee**. We did **NOT** guard `afc_file_close`, did **NOT** audit native calls one by
one, did **NOT** auto-reconnect.

## Tests — fail-before / pass-after (anti-false-green, round-6 bar)

Committed the **failing repro FIRST** (commit `4e89734`), then the fix (commit `e7f7b8b`).

- **`Sprint34EscapeHatchTests.Spinning_close_during_disposal_writes_summary_and_terminates_with_exit_3`** —
  THE #45 repro. `SpinOnDisposeStream` delivers 2 MB (heartbeat), returns a premature zero-byte read
  (the yank), then its `Dispose` **busy-spins** (a `SpinWait` loop — the `afc_file_close` shape, NOT a
  cancellable stall). Copy runs on a worker (the sync spin would otherwise wedge the test thread, exactly
  as the real spin wedges the main copy thread). Fake clock advances past the timeout → the watchdog trips
  on the (independent) timer thread → assert: **summary written**, **injected terminator invoked with 3**,
  **host survives**, no hang. `ReleaseSpin` reaps the orphan (the OS analogue), in a `finally` so no test
  ever leaks a pinned core.
  - **Fail-before (commit 1):** `terminator.WasInvoked should be True but was False` — the watchdog tripped
    and cancelled the token but never terminated, so the spin would hang. Deterministic, 155 ms, no leak.
  - **Pass-after (commit 2):** green once `FileCopier` forwards the escape-hatch to the watchdog `onTrip`.
- **`A_parked_read_with_the_escape_hatch_wired_terminates_with_exit_3`** — a prior manifestation (#11/#42,
  a parked read that ignores cancellation) reaches the same exit-3 terminate; the token-cancel path still
  unwinds it to a resumable `DeviceConnectionLostException`, in-progress, no published `.partial`.
- **`A_healthy_run_with_the_escape_hatch_wired_never_terminates`** — false-positive guard: a normal copy
  completes `Copied`/`Done`, terminator **not** invoked.
- **`DisconnectEscapeHatchTests`** — the escape-hatch in isolation: writes summary then terminates 3;
  fires only once; **still terminates when the summary write fails** (no journal) — the terminate is the
  load-bearing guarantee.
- **`ForwardProgressWatchdogTests`** (added) — inactivity trip and failure-burst trip each run `onTrip`
  exactly once; a slow-but-alive device never runs it.
- Existing false-positive guards retained: slow-but-alive, 0-byte/short file, isolated per-file failures,
  single premature-EOF (`Sprint33DiagnosisTests`).

**CI mirror:** `dotnet format --verify-no-changes` exit 0; `dotnet build -c Release` 0/0; `dotnet test -c
Release` → **SafetyTests 2/2, Tests 130/130** green.

## Data-safety argument (holds; restated in done.md)

- **Journal:** SQLite **WAL** + each `MarkDone` is a committed transaction → **crash-safe**; an un-closed
  connection recovers on next open. No corruption from the hard terminate.
- **`summary.txt`:** explicitly written **and fsync-flushed** before the terminate.
- **`.partial`:** transient staging, never published; `CleanStaging` removes it next run.
- **Device:** read-only throughout; the terminate issues **no** device call.

## Read-only status

✅ `ReadOnlyContractTests` green (2/2). No AFC/lockdown write/delete/rename symbol added —
`TerminateProcess`/`GetCurrentProcess` are process-control (kernel32), not device symbols, so the
Mono.Cecil IL scan is unaffected.

## Bugs / issues found

- None new. The escape-hatch closes #45; the watchdog/abandon machinery from 3.3 is unchanged.
- Note (by design, stated for the reviewer): with the escape-hatch wired, a real disconnect terminates
  from the timer thread, so `CopyCommand`'s `catch (DeviceStallException or DeviceConnectionLostException)`
  → summary → exit-3 path is now the **secondary** path (still reached for a connection-fatal AFC error
  thrown directly from `OpenReadAsync`, which never arms the byte-heartbeat timer). The escape-hatch
  writes the summary first, so a terminate never loses it.

## Commits

1. `4e89734` — `test: failing #45 spinning-close repro + escape-hatch capability` (repro fails; the copier
   does not yet forward its escape-hatch).
2. `e7f7b8b` — `fix: wire disconnect escape-hatch into the forward-progress watchdog (#45)` — `Closes #45`.
