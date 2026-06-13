# Sprint 3.4 Consilium — The disconnect escape-hatch (kill the native-spin class for good)

> Producer-facilitated, deliberately short. Convened after the **fifth** manifestation of the mid-copy
> USB-disconnect bug (#11 → #25 → #38 → #42 → **#45**). This time QA captured **ground truth** (live
> thread stack + 139 MB dump), not an inference. **This is the last disconnect round** — CEO rule: if
> hardware finds a sixth, we ship v1.0 with a documented limitation, not a round six.

## What five rounds finally taught us

| # | Where the spin was | What we guarded | Result |
|---|--------------------|-----------------|--------|
| #11 | native read parks | inactivity watchdog on the read | relocated |
| #25 | `afc_file_open` parks | watchdog over open/stat/list | relocated |
| #38 | between-file fast-fail | per-file failure breaker | relocated |
| #42 | "intra-file read" (we *inferred* a non-returning read) | run-level forward-progress watchdog | **inference was wrong** |
| #45 | **`afc_file_close` in `await using` disposal** (captured live) | — | the read returns `EmptyResponse`; the spin is in the **close** |

**The real class — stated correctly at last:** *every synchronous native call on a dead transport can busy-spin forever, and our graceful-unwind path keeps walking into the next unguarded one.* We guarded open/read/stat/list; `afc_file_close` was missed; `afc_client_free` / lockdown / idevice teardown may be next. **Guarding `close` is band-aid #6 with the identical risk.** We must stop depending on having enumerated every native call.

> Producer, on the record: round 4 shipped a *confident* diagnosis ("non-returning read") that was an **inference by elimination** in a harness that couldn't model the dispose/close path. Hardware falsified it. Lesson booked in the profile: **a harness conclusion is a hypothesis; only a hardware capture is truth.** QA's `GAS_DEBUG_READS` + dump is what finally settled it — vindicating the "ship device-I/O tracing as a real feature" idea.

## The decisive realization

When the byte heartbeat is dead, **the device is provably gone** — and the data is *already* safe (atomic journal, proven across all five rounds). At that point there is **no value** in unwinding gracefully through native code that may spin. So: **don't unwind through native code at all. Write the summary and terminate the process with exit 3.** The watchdog's *independent timer thread* (already built in 3.3) is not blocked by the spinning main thread, so it can do this. This is the first design whose correctness does **not** depend on enumerating native calls.

## The voices

### Sage (Backend) — "The hard part is the hard-exit must skip finalizers."
> The escape is simple to state and I believe it's bulletproof — *but* `Environment.Exit` is **not** safe here, and that's the trap. `Environment.Exit` runs finalizers: the `AfcReadStream` finalizer calls `afc_file_close`, and the `AfcClientHandle` **SafeHandle critical finalizer** calls `afc_client_free` — both re-enter the **spinning** native layer on the dead transport during shutdown. So `Environment.Exit(3)` would itself hang in finalization. The escape **must** be a finalizer-skipping terminate: P/Invoke `TerminateProcess(GetCurrentProcess(), 3)` (kernel32) — the OS reaps every thread and handle, **no managed/critical finalizer runs**, nothing re-enters native code. We write & flush `summary.txt` first (no device involved; the journal is consistent), then terminate. SQLite is crash-safe (WAL + committed per-file transactions), the `.partial` is transient (cleaned next run), so skipping normal disposal loses nothing.
>
> I will NOT also try to "guard `afc_file_close`" for a graceful path — that's the band-aid that keeps failing, and it adds a second mechanism. One escape-hatch, proven-safe, is simpler and complete.

### Ivy (QA) — "The test must reproduce a SPINNING native call during unwind, and the terminate must be observable without killing the test host."
> Every prior test modeled the wrong shape (a stall, then a cancellation-ignoring read). The #45 repro is a **native call that returns control to a busy-loop during *disposal***. The new test must drive the *real* shape: a read that returns `EmptyResponse`/fault, then a **disposal/close that spins** (a fake stream whose `Dispose` blocks/spins), and assert the escape fires. And you can't `TerminateProcess` in a unit test — so the terminate action must be behind an **injectable seam** (`IProcessTerminator` / `Action<int>`), default = real `TerminateProcess`, test double = records the call. Assert: heartbeat dies → summary written → terminator invoked with **3**, deterministically (fake clock), and the test process survives. I'll still gate v1.0 on hardware: yank mid-large-file + between files, CPU pinned → **drops to idle on a real exit 3**, summary present, resume byte-identical, read-only intact.

### Nova (CLI/UX) — "Same one-line message; exit 3; just don't hang."
> From the user's seat: within ~`--read-timeout` of the device going quiet, print the one line — "Device disconnected. Progress saved — reconnect and re-run to resume." — and exit. Whether that exit is a graceful unwind or a hard terminate is invisible to them. ~30 s of a pinned core before the escape fires is acceptable (it's the timeout they set) and is a world better than "pinned forever, force-kill." Healthy path: untouched.

### Kira (Product/scope) — "Escape-hatch only. This is the last round."
> Scope: the escape-hatch is the *whole* job. Not guarding `close`, not auditing every native call, not auto-reconnect. The forward-progress watchdog from 3.3 stays (it's the right liveness signal); 3.4 only changes what it *does* on trip: terminate instead of cancel-and-pray. And the CEO cap stands: **if hardware fails a sixth time, we ship v1.0 with the README limitation.**

### Remy (Producer) — synthesis
> Converged. One change: on watchdog trip, the independent thread writes+flushes `summary.txt`, prints the message, and **`TerminateProcess(…, 3)`** — never touching native device code. Finalizer-skipping terminate is the non-negotiable design detail (Sage). Testable via an injected terminator seam (Ivy). The 3.3 watchdog/abandon machinery stays; we just replace "trip the token and unwind" with "trip → terminate." Hard cap: last round.

## Converged design (to build in Sprint 3.4)

1. **On `ForwardProgressWatchdog` trip** (heartbeat dead for `timeout`, on its independent timer thread): print the disconnect line → write **and flush** `summary.txt` from the journal → **terminate the process with exit code 3 via a finalizer-skipping hard exit** (`TerminateProcess(GetCurrentProcess(), 3)` P/Invoke; Windows-only, guarded by `OperatingSystem.IsWindows()`). The orphaned native thread (spinning `afc_file_close` or anything else) is reaped by the OS. **Never wait for or re-enter native device code on the disconnect path.**
2. **Inject the terminate action** behind a seam (`IProcessTerminator`/`Action<int>`), real impl = `TerminateProcess`, so tests assert the escape without killing the host.
3. **Keep** the 3.3 forward-progress watchdog + abandonable read as the liveness signal and the normal cancellation path. The token-cancel path still handles the cases that *do* unwind cleanly; the terminate is the guarantee for the ones that don't. (If the consilium-implementer finds the token path is now fully redundant, simplify — but the terminate is the load-bearing guarantee.)
4. **Do NOT** guard `afc_file_close` individually or audit native calls one-by-one — the escape-hatch makes that unnecessary by construction.

## Data-safety argument for the hard exit (must hold)
- Journal: SQLite WAL + each `MarkDone` is a committed transaction → **crash-safe**; an un-closed connection recovers on next open. No corruption.
- `summary.txt`: explicitly written **and flushed** before terminate.
- `.partial`: transient in staging, never published, `CleanStaging` removes it next run.
- Device: read-only throughout; terminate issues **no** device call. `ReadOnlyContractTests` unaffected (`TerminateProcess` is process-control, not an AFC/lockdown symbol).

## The test bar (anti-false-green, round 6 edition)
- A deterministic test where a read faults (`EmptyResponse`) and the subsequent **disposal/close spins**, proving the escape fires: summary written, terminator invoked with **3**, fake clock, host survives, no hang. **A stall double or a cancellation-ignoring read does NOT satisfy this** — it must model a *spinning unwind/close*.
- False-positive guards retained: slow-but-alive (>0 bytes) never trips; short/0-byte file never trips; isolated failures never trip; **a healthy run completes and exits 0 with no terminate**.

## Definition of done
Real hardware cable-yank — mid-large-file **and** between files — pins a core, then within ~`--read-timeout` prints the disconnect line, writes `summary.txt`, **exits 3, and CPU drops to idle** (the orphan dies with the process). Reconnect + re-run resumes byte-identical. Long-path + read-only still hold. **CEO cap: if this fails hardware, re-scope v1.0 — no round 6.**
