# Sprint 3.3 — progress

> The universal forward-progress watchdog: kill the mid-copy USB-disconnect bug as a CLASS (#42).
> Branch `fix/sprint-3.3` off `fix/sprint-3.2` (carries #38 breaker + #39 long-path). One PR, then STOP
> for the producer review gate. Never merge here; never push `main`.

## Phase 0 — DIAGNOSIS (mandatory, on the record BEFORE any fix)

We have **assumed** the mechanism four times (#11 → #25 → #38 → #42) and hardware found the next code
location each time. Phase 0 rule: **reproduce, do not assume.** This was done with (a) a hidden read-path
instrumentation switch for the next hardware yank, and (b) a deterministic harness driving the *real*
`FileCopier`/`WatchdogReadStream`/`CopyStreamAsync` code path with 8 candidate device-read signatures.

### The one invariant (all four manifestations)

Forward **byte** progress stops — the staging `.partial` freezes. Park (#11), open-park (#25),
between-file fast-fail spin (#38), intra-file spin (#42) are all *"bytes stopped flowing while we still
expected bytes."* The byte heartbeat already exists: `FileCopier` calls `onBytesStreamed(read)` per chunk.

### Empirical harness (8 signatures, run against the real read path)

Each signature was modeled faithfully at the `AfcReadStream` boundary (overriding only **sync** `Read`,
so the async path flows through the base `Stream.ReadAsync` → thread-pool → sync read, exactly like
production), then run through a real `FileCopier` with `--read-timeout`-equivalent watchdog wiring. A
read-count cap and a wall-clock cap detected a spin.

| # | Device-read signature (after the "yank") | Result in the **current** code | Spin? |
|---|------------------------------------------|--------------------------------|-------|
| S0 | 5×1 MB then clean EOF | `Copied` (sanity) | no |
| A | fast `Success`+0 immediately (premature EOF) | per-file **`Failed`** (size mismatch, 0 B) → next file | no |
| B | 345×1 MB then fast `Success`+0 | per-file **`Failed`** (size mismatch, **361,758,720 B**) → next file | no |
| C | fast non-fatal error (e.g. `EmptyResponse`) | per-file **`Failed`** → next file | no |
| D | 345×1 MB then fast non-fatal error | per-file **`Failed`** → next file | no |
| E | fast connection-fatal error (`MuxError`…) | **`DeviceConnectionLostException`** → exit 3 (already correct) | no |
| F | positive bytes **forever** (infinite stream) | unbounded loop that **grows** `.partial` | grows |
| G | 1-byte **trickle** forever | unbounded loop that **grows** `.partial` (slowly) | grows |
| H | a single native read that **never returns** while burning CPU | per-read watchdog **fired** → `DeviceStallException` (on a healthy multi-core host) | caught |

### Confirmed mechanism (#42 intra-file spin)

1. **No `afc_file_read` _return value_ reproduces #42's signature** (frozen `.partial` + ~100 % CPU +
   stuck on one file). Fast `Success`+0 and fast errors (A–D) make the copy loop **exit the file** → a
   per-file size-mismatch/`Failed` → the run **advances to the next file** at ~1 ms/read (the counter
   *moves*; **not** 100 % CPU). That is the **between-file** path (#38), bounded by the consecutive-
   failure breaker (trips at 10). Notably **B reproduces the exact #42 byte count (361,758,720)** — but
   it **terminates** as a per-file failure; it does **not** spin. Positive/trickle (F, G) **grow** the
   `.partial`, so they are not the frozen-`.partial` signature either.
2. Therefore **#42 is a single `afc_file_read` native call that never returns while pinning a core** — a
   non-cooperative busy-loop in the native USB/usbmux layer on the dead transport. This matches QA's
   hardware trace exactly: `.partial` frozen at a clean 1 MB boundary (the last completed read), per-file
   counter frozen, one core ~100 %, zero byte progress, no exit. The hidden instrumentation
   (`GAS_DEBUG_READS`, below) will let the next hardware yank confirm the native return values directly.
3. **Why each existing guard misses it:**
   - `WatchdogReadStream`'s **per-read** inactivity timer only **arms** when a read does *not* complete
     promptly (`!operation.IsCompleted`) and is enforced by a `Task.Delay`/`Task.WhenAny` continuation
     **scheduled on the thread pool**. It is structurally **coupled to the read operation and the pool**,
     so it provides **no run-level, read-independent liveness guarantee**. (On a healthy host a *single*
     spinning read *is* caught — scenario H — but there is no run-level guarantee, which is exactly why
     the hang **relocated** four times.) A fast-returning read bypasses it entirely.
   - `ForwardProgressMonitor` (#38) counts consecutive **failures**, evaluated once per file at the **top
     of `CopyAsync`**. An intra-file stuck read means `CopyAsync` never returns → the per-file check never
     runs → the counter is **frozen** → #38 never fires.
   - `DeviceWatchdog` guards the blocking native **open/stat/list** calls (#25) — there is no byte
     heartbeat there; it is not the read loop's liveness guard.

### The fix direction (confirmed by the diagnosis, designed in Phase 1)

Watch the **symptom** (bytes stopped), not the code location. A **run-level forward-progress (liveness)
watchdog** fed by the existing `onBytesStreamed` heartbeat, on an **independent timer** (not coupled to a
read completing): while a copy is in flight, if **zero bytes flow for N seconds** (tie to
`--read-timeout`) → trip **one** `CancellationToken` → abandon the stuck read → unwind to the **existing**
`DeviceConnectionLostException` / exit-3 path (resumable; `summary.txt` written). Because it watches the
heartbeat, it covers park + open-park + between-file spin + intra-file spin + native-spin + fast-fail
**uniformly**. The per-read inactivity timer and the consecutive-failure breaker fold **into** this one
liveness model so there are not two overlapping timers; `DeviceWatchdog` stays for the (separate,
non-overlapping) blocking open/stat/list calls.

> Per the brief: the intra-file case is a **pure-CPU native loop that never awaits**, so a
> `CancellationToken` alone cannot break the *orphaned* read — it is **abandoned** (left to die when the
> process exits, which is immediate after exit 3, so CPU drops to idle), while the **main** flow unwinds
> on the independent timer. The watchdog timer must therefore be independent of the read returning.

### Instrumentation shipped (off by default)

`GetAndSee.Core/Util/ReadDiagnostics.cs` — gated by the `GAS_DEBUG_READS` environment variable, evaluated
**once** (a single cached boolean check on the healthy read path; no `Stopwatch` allocated when off; never
ships enabled). When set, `AfcReadStream` logs **per read**: device path, `AfcError`, requested vs
received bytes, native elapsed ms, and the running total (≈ `.partial` size); `FileCopier` logs a
per-file begin marker with the expected size. This is the capture the next hardware yank needs to confirm
the native return values at the instruction level.

## Phase 1 — the universal forward-progress watchdog

Implemented the confirmed fix: one run-level liveness model on the byte heartbeat, on an independent
timer, replacing both the per-read inactivity timer and the standalone between-file failure breaker.

- **`ForwardProgressWatchdog`** (`src/GetAndSee.Core/Transfer/ForwardProgressWatchdog.cs`, new) — the
  single liveness guard. It is fed by the existing per-chunk heartbeat and trips a `CancellationToken` on
  either of two conditions, both "forward progress stopped":
  1. **No progress for `timeout`** (an independent `TimeProvider.CreateTimer`, checked four times per
     window so detection lands near `timeout`). This catches a parked read, a native spin that never
     returns (#42), and any other no-byte stall — because the timer ticks regardless of whether any read
     returns.
  2. **`N` consecutive per-file failures with no intervening progress** (the folded-in #38 fast path),
     so a cable-yank that fast-fails every file stops in ~`N` files instead of churning the journal for a
     whole timeout window.
  Any streamed byte or any completed/skipped file resets both, so an isolated bad/changed file never
  trips it. The trip cancels one CTS; `RecordProgress` is lock-free and hot-path safe.
- **`AbandonableReadStream`** (renamed from `WatchdogReadStream`) — keeps the safety-critical parts of the
  old decorator (the private reusable **scratch buffer** so an orphaned read can never corrupt the
  caller's pooled buffer, and the abandon-the-orphan-on-stall behavior) but **drops its own per-read
  timer**. It now races each read against the watchdog's cancellation token only, so there is exactly
  **one** byte-liveness timer (the run-level watchdog). When the watchdog trips, the in-flight read is
  abandoned (left to die when the process exits, right after exit 3, so the pinned core drops to idle) and
  the await unblocks.
- **`FileCopier`** owns the watchdog (created only when `readTimeout > 0`; `--read-timeout 0` disables all
  forward-progress detection, preserving the Sprint-1 unguarded behavior) and is now `IDisposable` so the
  timer is released. `CopyAsync` feeds the heartbeat (`RecordProgress` on each chunk and on every
  copied/skipped file; `RecordFailure` on a per-file failure), gates each file on `Tripped` (a between-file
  trip → `DeviceConnectionLostException`), and converts a watchdog-trip cancellation of an in-flight read
  into a `DeviceConnectionLostException` (resumable, in-progress) — distinguished from a user Ctrl+C, which
  still propagates as cancellation (exit 130).
- **`DeviceWatchdog`** is unchanged and still guards the blocking native **open/stat/list** calls (#25) —
  a separate, non-overlapping concern (no byte heartbeat exists during those calls).
- **`ForwardProgressMonitor` (#38) deleted** — folded into the watchdog's failure counter.

Why this is universal: every manifestation (#11 park, #25 open-park, #38 between-file spin, #42 intra-file
spin/native-spin, plus a fast premature-EOF burst) reduces to "the byte heartbeat stopped," and the
watchdog watches exactly that on an independent clock — not a code location, and not coupled to any read
returning.

## Phase 2 — tests that reproduce EACH manifestation

Deterministic, fake-clock-where-time-matters, each verified to **fail before / pass after**. Anti-false-
green: every test models the real failure mode (a read that returns fast-and-wrong, or one that pins a
core and never returns), not a convenient stand-in, and a test that only models a *park* does not count.

**`ForwardProgressWatchdogTests`** (the unified model, direct):
- trips when no progress for the timeout; does **not** trip before; trips after the N-failure burst; a
  recorded byte resets the inactivity clock; progress resets the failure streak; **a slow-but-alive device
  (a byte every `timeout − 1s`) never trips**; dispose stops the timer; ctor rejects non-positive
  timeout/limit.

**`FileCopierTests`** (end-to-end through the real copy pipeline):
- **intra-file freeze after bytes flowed (#42)** — `ChunkThenStallReadStream` delivers chunks then the
  read stops and ignores cancellation (the native-read shape); the watchdog resets on the bytes that
  flowed, then trips a full timeout after they stop ⇒ `DeviceConnectionLostException` (exit 3), file
  resumable, no `.partial` published, loop terminated (no spin). **This is the intra-file manifestation,
  not a park.**
- **read stall mid-file (#11)** — bytes never flow ⇒ trip ⇒ same clean stop.
- **between-file fast-fail burst (#38)** — fast `Success`+0 for every file, and fast per-file errors for
  every file, each stop after `N` files instead of churning all 50 (no spin).
- **native open-park (#25)** — `DeviceWatchdogTests` (a real blocking call parked on a worker).
- **premature-EOF guard** — `Sprint33DiagnosisTests`: a single premature `Success`+0 is a per-file failure
  (resumable), never a run stop (can't be told from a shrunk file); only a sustained burst trips the run.

**False-positive guards (must NOT trip):** slow-but-alive device (watchdog unit test); a 0-byte file
copies cleanly; isolated failures between successes never trip the breaker (`FileCopierTests`).

**Fail-before evidence (recorded):** the headline intra-file test was run against the pre-fix code in a
throwaway worktree at the Phase-0 commit. It failed:

```
Shouldly.ShouldAssertException : … should throw
GetAndSee.Core.Errors.DeviceConnectionLostException
    but threw
GetAndSee.Core.Errors.DeviceStallException
```

i.e. the old per-read watchdog produced `DeviceStallException` (and, with a virtual clock, the per-read
timer is armed *during* the read, racing the disconnect — it could also hang), never the unified
`DeviceConnectionLostException`. Only the run-level watchdog produces the clean, deterministic stop.

**Healthy hot path:** the watchdog adds one `Interlocked.Exchange` + a `GetUtcNow` per 1 MB chunk
(~30 calls/s at the USB-2 ceiling) and no allocation; the timer ticks on its own thread. No measurable
cost at 400 GB / 38k files; the dashboard already paid the per-chunk callback.

**Counts:** 119 `GetAndSee.Tests` + 2 `GetAndSee.SafetyTests` green (Release); `dotnet format` clean;
build 0/0. `ReadOnlyContractTests` green — no device-write symbol added (the only device-side change is
the read instrumentation, which observes values only).

## Bugs / Issues Found

- (none beyond the #42 diagnosis — the fix is additive/behavioral; data-safety + read-only invariants held
  throughout and are covered by unchanged tests.)

## Decisions / notes

- Branched `fix/sprint-3.3` off `fix/sprint-3.2` @ `d0da545` per the plan (keeps #38 breaker + #39
  long-path). Supersedes the #38 mechanism by folding it into the unified run-level watchdog.
