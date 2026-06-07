# Sprint 3.1 — Done (dev handoff)

> Branch: `fix/25-afc-open-watchdog` → PR **"fix: #25 unplug-safe AFC (watchdog over open/stat/list)"**.
> Plan: [`plan.md`](plan.md) · Progress: [`progress.md`](progress.md) · Issue: **#25**.
>
> **Hotfix for the one #21 release-gate failure** (physical cable-unplug → indefinite hang). Stops at the
> producer review gate; **not merged**. Hardware re-acceptance is QA #21 Phase B.

## The bug (#25)

On a physical USB cable-unplug mid-copy the tool hung **indefinitely** (194 s observed, force-kill
required) against a 15 s `--read-timeout`. The Sprint 2 watchdog (`WatchdogReadStream`) only guarded the
**byte read of an already-open file**; the blocking native AFC calls checked `cancellationToken` only
*before* the call, and a token can't interrupt a call already parked in native code. So on a disconnect
the in-flight read failed fast, the loop advanced, and the **next file's `afc_file_open` parked forever**.
The #11 hang didn't get fixed in Sprint 2 — it **relocated** from the read to the open.

## What changed

### One shared watchdog (the right extraction — review-profile drift item, now resolved)
- **New [`DeviceWatchdog`](../../src/GetAndSee.Core/Device/DeviceWatchdog.cs)** — the **single**
  abandon-on-timeout implementation:
  - `RaceAgainstTimeoutAsync<T>(Task<T> op, timeout, TimeProvider, ct, Action<Task<T>>? onAbandoned)` —
    races the op against `Task.Delay(timeout, clock)`; on timeout **abandons** the orphaned op (schedules
    `onAbandoned` for whenever it settles → any resource it produced is released, **no handle leak**) and
    throws `DeviceStallException`; caller cancellation → `OperationCanceledException` (not a stall).
  - `RunWithTimeoutAsync<T>(Func<T> blockingNativeCall, …)` — thin wrapper that runs the blocking native
    call on a worker (`Task.Run`) and delegates to the same core.
- **`WatchdogReadStream` refactored onto it** — its private `AbandonRead` copy is **deleted**. The read
  passes its own `inner.ReadAsync(…, ct)` task into the core, so the exact Sprint-2 cancellation +
  scratch-buffer semantics are preserved (all existing `WatchdogReadStreamTests` stay green).

### Guard the native open/stat/list (the #25 fix)
- **[`AfcIPhoneClient`](../../src/GetAndSee.Core/Device/AfcIPhoneClient.cs)** now takes
  `(TimeSpan readTimeout = default, TimeProvider? clock = null)` and routes `OpenReadAsync` /
  `ListDirectoryAsync` / `GetFileInfoAsync` through a private `GuardAsync<T>` → `DeviceWatchdog`. The
  blocking native call runs on a worker; a stall → `DeviceStallException` → existing `CopyCommand` catch →
  **clean exit 3**, in-flight file non-`done` (resumable), `summary.txt` written.
  - **`readTimeout == TimeSpan.Zero` → unguarded inline native call** (unchanged Sprint-1 behavior).
    `CopyCommand` passes the existing `--read-timeout` (default 30 s) into the client — the same value
    `FileCopier` already threads into the read watchdog.
  - **No hot-path cost when healthy** — same "materialize the timer only while the call is in flight,
    cancel it the instant the call returns" pattern the read path uses.
  - **Open abandon-hook** disposes a late-returning stream → the AFC file handle is never leaked.

### Don't march a dead connection into repeated hangs (#3, conservative)
- **New [`AfcErrors`](../../src/GetAndSee.Core/Device/AfcErrors.cs)** — `IsConnectionFatal(AfcError)` is
  `true` **only** for `MuxError` / `ServiceNotConnected` / `ServiceClientFailed` (transport/socket gone).
  Per-file codes (`ObjectNotFound`, `PermDenied`, `ReadError`, `IoError`, `OpTimeout`, …) are deliberately
  excluded so a single bad file is **still** failed-and-skipped.
- **New `DeviceConnectionLostException : DeviceException`** (sibling of `DeviceStallException`).
  `AfcErrors.ToException` maps a failed read-only AFC call → connection-lost (stop, resumable) **or** plain
  `DeviceException` (per-file). Wired into all four read sites (open / read / list / stat). `FileCopier`
  and `CopyCommand` now treat stall **or** connection-lost identically: leave the file `in_progress`, stop
  the run cleanly, **exit 3**.

## How #25 is tested (the REAL failure mode, not a read mock)

- **`DeviceWatchdogTests.A_parked_native_call_times_out_with_DeviceStallException`** — parks a **real
  blocking call on a worker thread** (`() => gate.Task.GetAwaiter().GetResult()`, exactly how a hung
  `afc_file_open` parks a thread) and drives a **`FakeTimeProvider`** past the timeout → asserts
  `DeviceStallException`. This is the parked-*call* path Sprint 2 left unguarded; all three native calls
  (open/stat/list) share this exact helper.
- **`DeviceWatchdogTests`** also covers: late result disposed (no AFC-handle leak), caller-cancellation →
  `OperationCanceledException` (not a stall), fast path returns the value, and a per-file device error
  **propagates unchanged** (not converted to a stall).
- **`AfcErrorsTests`** — transport codes → connection-fatal → `DeviceConnectionLostException`; per-file
  codes → plain `DeviceException`.
- **`FileCopierTests`** (+2) — a connection-lost open leaves the file `in_progress` and **stops the run**
  (no `.partial` leak, no final-tree dir); a single unreadable file is **`MarkFailed` and the run
  continues** (per-file resilience not regressed). The existing read-stall test still passes.
- The end-to-end **exit-3 on a real cable yank** is QA #21 Phase B on hardware (per the plan).

## Read-only contract — confirmed intact

The watchdog wraps the **same** read-only native calls (`afc_file_open` `FopenRdonly`,
`afc_read_directory`, `afc_get_file_info`, `afc_file_read`, `afc_file_close`). **No** AFC/lockdown
write/delete/rename/truncate/mkdir/link symbol was introduced anywhere in `GetAndSee.Core`.
`tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` (Mono.Cecil IL scan — both the forbidden-symbol
case and the `afc_file_open` write-mode case) is **green**.

## Validation (local, mirrors CI)
- `dotnet build -c Release` ✅ **0 warning / 0 error**
- `dotnet test -c Release` ✅ **104 tests** (102 `GetAndSee.Tests` + 2 `ReadOnlyContractTests`), 0 failed
- `dotnet format --verify-no-changes` ✅ (exit 0)

## Notes / decisions for the producer
- **Timeout plumbed via the `AfcIPhoneClient` constructor** (`new AfcIPhoneClient(readTimeout)`); the
  client uses `TimeProvider.System` in production. The deterministic fake-clock tests target
  `DeviceWatchdog` directly (the shared helper all three native calls route through) — the most honest
  unit seam, since `AfcIPhoneClient` itself needs real native libs/hardware.
- **"Connection lost" is intentionally narrow** (3 transport codes). False-positives are data-safe anyway
  (exit 3 is resumable), but the narrow set protects per-file resilience as the plan asks.
- **Beyond the brief:** added a verbatim copy of `plan.md` to this folder (it lived only on the producer
  clone, not `main`) so the sprint docs are self-consistent — unmodified. `PROJECT_BRIEF.md` §7/§8 and the
  §8 watchdog line (now also open/stat/list) are left for the producer to update on merge, per "stop at
  the review gate."
