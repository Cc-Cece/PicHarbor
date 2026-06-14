# Sprint 3.4 — Done (the disconnect escape-hatch: kill the native-spin CLASS for good, #45)

**Branch:** `fix/sprint-3.4` (off `fix/sprint-3.3` `1554365`) · **Status:** ✅ implemented, CI-green,
**PR open (based on `main`) — stopped at the producer review gate (NOT merged).** · **Owner:** Dev
(Sage — device/transfer). **Gates next:** producer independent review → QA #21 Phase B hardware
re-acceptance (real cable-yank, CPU-trace-to-idle on exit 3) → merge the combined 3.2 + 3.3 + 3.4 line →
**v1.0**. **CEO cap:** this is the **last** disconnect round — a 6th hardware failure ships v1.0 with a
documented known-limitation, no round 6.

The mid-copy USB-disconnect bug manifested **five** times (#11 park → #25 open-park → #38 between-file
spin → #42 intra-file spin → **#45 spinning close**) because every prior fix guarded a specific native
**call** and hardware found the next one. #45 captured **ground truth** (live thread stack + 139 MB dump):
the spin is in `afc_file_close` during `await using` disposal, not the read. Sprint 3.4 stops depending on
having enumerated every native call: when the device is proven gone, **don't unwind through native code at
all — write the summary and hard-terminate.** Two commits (failing repro → fix).

---

## The mechanism (#45) and why the 3.3 watchdog wasn't enough

A mid-copy yank → `afc_file_read` returns `EmptyResponse`/0 → the fault unwinds into
`FileCopier.StreamToStagingAsync`'s `await using Stream source` disposal → `AbandonableReadStream.Dispose`
(the read **returned**, so `abandoned == false`) → `DisposeInner` → `AfcReadStream.Dispose()` →
synchronous native `afc_file_close()` on the dead transport → **busy-spins forever**. The 3.3
`ForwardProgressWatchdog` correctly trips on its independent timer (the byte heartbeat is dead), but its
only action was to **cancel a token** — and a `CancellationToken` cannot interrupt a thread already wedged
inside a synchronous native call. So the run pins a core forever. (Round 4 had *inferred* a "non-returning
read"; hardware falsified it — the read returns, the **close** spins.)

## The fix — one escape-hatch, proven-safe by construction

On `ForwardProgressWatchdog` trip, the **independent timer thread** (which the wedged main thread cannot
block) runs `DisconnectEscapeHatch.Activate()`:

1. prints `Device disconnected. Progress saved — reconnect and re-run to resume.`,
2. writes **and flushes** `summary.txt` from a **fresh `TransferJournal.OpenReadOnly`** connection (WAL →
   a second reader is safe; the wedged main thread's connection is never touched; **no device call**), and
3. terminates with exit code 3 via a **finalizer-skipping** hard exit.

This is the first design whose correctness does **not** depend on enumerating native calls: it never waits
for or re-enters native device code on the disconnect path. The orphaned spinning thread dies with the
process (the OS reaps it → the pinned core drops to idle).

### Why `Environment.Exit(3)` was REJECTED (the load-bearing detail)

`Environment.Exit` **runs finalizers**. Two of them re-enter the busy-spinning native layer on the dead
transport, so the exit *itself* would hang:

- the `AfcReadStream` **finalizer** → `afc_file_close` (the very call that is already spinning), and
- the `AfcClientHandle` SafeHandle **critical finalizer** → `afc_client_free`.

`TerminateProcess(GetCurrentProcess(), 3)` (kernel32, `[LibraryImport]`, `OperatingSystem.IsWindows()`-
guarded) tells the OS to tear the process down immediately: **no managed or critical finalizer runs**, so
nothing re-enters native code. Non-Windows (never a real target; the native AFC layer isn't even loaded
there, so no spin can occur) falls back to `Environment.Exit`.

### The injected-terminator seam

`IProcessTerminator { void Terminate(int) }` — real impl `TerminateProcessTerminator` (= `TerminateProcess`);
test double `RecordingProcessTerminator` records the exit code instead of killing the host. `CopyCommand`
injects the real one; tests inject the recorder. `DisconnectEscapeHatch` is the composition (print →
write+flush summary → terminate); the watchdog only knows an `Action? onTrip`.

## What stays / what we did NOT do

- **Kept** the 3.3 `ForwardProgressWatchdog` (independent timer + byte heartbeat) and
  `AbandonableReadStream` — the right liveness signal. 3.4 only changes **what trip does** (terminate, not
  cancel-and-hope). The token-cancel path is retained (it still unwinds the abandonable cases cleanly and
  is defense-in-depth), but the **terminate is the load-bearing guarantee.**
- **Did NOT** guard `afc_file_close` individually, **did NOT** audit native calls one by one (that is the
  band-aid that failed five times), **did NOT** add auto-reconnect/retry (out of scope for v1.0).

## Data-safety argument (must hold — it does)

- **Journal:** SQLite **WAL** + each `MarkDone` is a committed transaction → **crash-safe**; an un-closed
  connection recovers on next open. The hard terminate cannot corrupt it.
- **`summary.txt`:** explicitly written **and fsync-flushed** (`SummaryWriter.Write(flushToDisk:true)`)
  **before** the terminate, so it is current on disk even though the exit is abrupt. Best-effort: a
  summary-write failure never blocks the terminate (the journal is the durable record; the next run
  regenerates the summary).
- **`.partial`:** transient staging, never published; `CleanStaging` removes it on the next run.
- **Device:** read-only throughout; the terminate issues **no** device call. `ReadOnlyContractTests`
  unaffected (`TerminateProcess`/`GetCurrentProcess` are process-control, not AFC/lockdown symbols).

## Hard rules — status

- **Read-only contract:** ✅ `ReadOnlyContractTests` green (2/2); no AFC/lockdown write/delete/rename
  symbol added.
- **Data integrity:** ✅ interrupted file left non-`done`/resumable; no published `.partial`; manifest only
  `done` rows — unchanged invariants, covered by existing + new tests.
- **Healthy hot path:** ✅ unchanged — the escape-hatch only runs on a trip; a healthy run never arms it
  (test: `A_healthy_run_with_the_escape_hatch_wired_never_terminates`).
- **No new `var`:** ✅ explicit types throughout. **XML `<summary>`** on every new public Core type
  (`IProcessTerminator`, `TerminateProcessTerminator`, `DisconnectEscapeHatch`).
- **CI:** ✅ `dotnet format` clean, Release build 0/0, **132 tests green** (SafetyTests 2 + Tests 130).

## Tests — fail-before / pass-after (round-6 bar)

The **failing #45 repro was committed FIRST** (`4e89734`), then the fix (`e7f7b8b`).

- **#45 spinning-close repro** — a read faults (premature EOF) then the **disposal/close busy-spins**
  (`SpinOnDisposeStream`, a `SpinWait` loop — a *spinning unwind*, explicitly NOT a stall double or a
  cancellation-ignoring read). Asserts on a fake clock: heartbeat dead → summary written → injected
  terminator invoked with **3** → host survives → no hang. **Fail-before:** `terminator.WasInvoked should
  be True but was False`. **Pass-after:** green.
- **Prior manifestations** end in exit 3: parked-read-with-escape-hatch terminates 3 (and stays resumable
  via the token-cancel path); existing #11/#25/#38/#42 + premature-EOF tests unchanged.
- **False-positive guards:** healthy run exits with no terminate; slow-but-alive never runs `onTrip`;
  0-byte/short file never trips; isolated per-file failures never trip.
- **Escape-hatch unit tests:** writes summary then terminates 3; fires once; still terminates when the
  summary can't be written (load-bearing terminate).

## For the reviewer (extra weight, per the plan)

1. Does the terminate **skip finalizers** and never re-enter native code? → `TerminateProcessTerminator`
   uses `TerminateProcess` (not `Environment.Exit`); `DisconnectEscapeHatch` makes **no** device call and
   reads the journal on a **separate** read-only connection. Rationale above.
2. Does a test model a **spinning unwind/close**, not a stall? → `SpinOnDisposeStream.Dispose` busy-spins;
   the repro is wired through the real `FileCopier` → `AbandonableReadStream` disposal path; fail-before
   evidence recorded.
3. Is the terminate the **load-bearing guarantee**? → fires on every trip (inactivity and failure-burst);
   summary write is best-effort and cannot block it.
4. Read-only intact + data-safe? → `ReadOnlyContractTests` green; WAL crash-safe; summary fsync'd;
   `.partial` transient; resumable.

## For QA (#21 Phase B — the v1.0 gate)

Real cable-yank **mid-large-file** AND **between files**: expect, within ~`--read-timeout`, the disconnect
line, `summary.txt` present (current), **exit 3**, and **CPU drops to idle** (the proof — the orphaned
spinning `afc_file_close` thread dies with the process). Reconnect + re-run resumes **byte-identical**;
long-path + read-only still hold. A debug build with `GAS_DEBUG_READS=1` captures native per-read values
if anything still surprises. **If this fails hardware, re-scope v1.0 with a documented limitation — no
round 6.**

## Merge mechanics (for the producer)

One PR, **based on `main`**, so CI runs the combined 3.2 + 3.3 + 3.4 line. On hardware PASS: merge the
combined line → supersedes/closes the open 3.2/3.3 PRs → update `PROJECT_BRIEF.md` §7/§8 → cut **v1.0**.
`Closes #45`.
