# Sprint 3.3 — Done (the universal forward-progress watchdog: kill the unplug bug as a CLASS, #42)

**Branch:** `fix/sprint-3.3` (off `fix/sprint-3.2` `d0da545`) · **Status:** ✅ implemented, CI-green,
**PR open — stopped at the producer review gate (NOT merged).** · **Owner:** Dev (Sage — device/transfer)
**Gates next:** producer independent review → QA #21 Phase B hardware re-acceptance (real cable-yank,
CPU-trace-to-idle) → merge the combined 3.2 + 3.3 line → v1.0.

The mid-copy USB-disconnect bug manifested **four** times (#11 park → #25 open-park → #38 between-file
spin → #42 intra-file spin) because every prior fix guarded a specific code **location** and hardware
found the next one. This sprint fixes the **class**: watch the one invariant — **forward byte progress
stopped** — not the location. Three commits (Phase 0 diagnosis → Phase 1 fix → Phase 2 tests).

---

## Phase 0 — diagnosis first (we had ASSUMED the mechanism four times)

An 8-signature harness drove the **real** read path (`FileCopier` → `WatchdogReadStream` →
`CopyStreamAsync`), each signature modeled faithfully at the `AfcReadStream` boundary (sync `Read` on a
thread-pool worker, like production). **Result: no `afc_file_read` _return value_ reproduces #42's
signature** (frozen `.partial` + ~100 % CPU + stuck on one file):

- fast `Success`+0 / fast errors → the loop **exits** the file → per-file size-mismatch/`Failed` → next
  file at ~1 ms/read (the counter **advances**; not 100 % CPU). That is the **between-file** path (#38),
  bounded by the failure breaker. The exact #42 byte count (361,758,720) is reproduced by "345×1 MB then
  `Success`+0" — but it **terminates** as a per-file failure; it does **not** spin.
- positive/trickle forever → an unbounded loop that **grows** the `.partial` (not frozen).

**Confirmed mechanism:** #42 is a single `afc_file_read` native call that **never returns while pinning a
core** (a non-cooperative busy-loop in the USB/usbmux layer on the dead transport) — matching QA's
hardware trace exactly. The old per-read inactivity timer only **armed** when a read failed to complete
promptly and was enforced via a **thread-pool-scheduled** continuation, so it gave **no run-level,
read-independent liveness**; `ForwardProgressMonitor` (#38) checked failures once per file at the top of
`CopyAsync`, which **never runs while a read is stuck**. Full evidence table + reasoning in
`docs/sprint-3.3/progress.md`.

**Instrumentation shipped (off by default):** `GAS_DEBUG_READS` env var → `ReadDiagnostics` logs per-read
`AfcError` + requested/received bytes + native elapsed + running total (and a per-file begin marker), so
the next hardware yank confirms the native return values directly. One cached boolean on the healthy
path; no `Stopwatch` allocated when off; never ships enabled.

---

## Phase 1 — the universal run-level forward-progress watchdog

**`ForwardProgressWatchdog`** (`src/GetAndSee.Core/Transfer/`, new) — ONE liveness model on the existing
byte heartbeat (`onBytesStreamed`), trips a single `CancellationToken` on either:
1. **no progress for `--read-timeout`** — an **independent** `TimeProvider` timer (checked 4×/window) that
   ticks regardless of whether any read returns, so a native read pinned in a CPU spin **cannot evade
   it** (this is the #42 fix); and
2. **N consecutive per-file failures** (default 10) with no intervening progress — the folded-in #38 fast
   path, so a fast-fail burst stops in ~N files instead of churning the journal for a whole timeout.

Any streamed byte **or** any copied/skipped file resets **both**, so a slow-but-alive device and isolated
bad files never trip it.

**`AbandonableReadStream`** (renamed from `WatchdogReadStream`) — keeps the safety-critical scratch-buffer
shield (an orphaned read can never corrupt the caller's pooled `ArrayPool` buffer) and the abandon-the-
orphan behavior, but **drops its own per-read timer**; it now races each read against the watchdog's token
only. So there is **exactly one** byte-liveness timer (the run-level watchdog), not two overlapping ones.

**`FileCopier`** owns the watchdog (only when `readTimeout > 0`; `--read-timeout 0` disables all
forward-progress detection = Sprint-1 unguarded behavior), is now `IDisposable`, feeds the heartbeat,
gates each file on `Tripped`, and converts a watchdog-trip cancellation of an in-flight read into a
resumable `DeviceConnectionLostException` (exit 3) — distinct from a user Ctrl+C (exit 130). The
orphaned native read is **abandoned** (it dies when the process exits right after exit 3, so the pinned
core drops to idle).

**Unchanged:** `DeviceWatchdog` still guards the blocking native **open/stat/list** calls (#25) — a
separate, non-overlapping concern (no byte heartbeat exists during those). **Deleted:**
`ForwardProgressMonitor` (folded into the watchdog).

**Why universal:** park (#11), open-park (#25, via `DeviceWatchdog`), between-file spin (#38), intra-file/
native spin (#42), and a premature-EOF burst all reduce to "the byte heartbeat stopped," which the
watchdog watches on an independent clock — not a code location.

---

## Phase 2 — tests that reproduce EACH manifestation (anti-false-green)

Deterministic, fake-clock-where-time-matters, each verified **fail-before / pass-after**:

- **`ForwardProgressWatchdogTests`** — timeout trip; no-trip-before; N-failure trip; byte resets the
  timer; progress resets the streak; **slow-but-alive never trips**; dispose stops the timer; ctor
  validation.
- **`FileCopierTests`** — **intra-file freeze after bytes flowed (#42)** via `ChunkThenStallReadStream`
  (delivers chunks, then the read stops and **ignores cancellation** — the native-read shape) ⇒
  `DeviceConnectionLostException`, resumable, no published `.partial`, **loop terminates (no spin)**; the
  read-stall (#11) unified path; the two between-file fast-fail bursts (#38); a 0-byte-file false-positive
  guard; isolated-failures guard.
- **`DeviceWatchdogTests`** — native open/stat/list park (#25), unchanged.
- **`Sprint33DiagnosisTests`** — a single premature `Success`+0 is a per-file failure (resumable), never a
  run stop (can't be told from a shrunk file); only a sustained burst trips.

**Fail-before recorded:** the headline intra-file test, run against the pre-fix code in a throwaway
worktree, threw `DeviceStallException`, **not** the asserted `DeviceConnectionLostException`:

```
Shouldly.ShouldAssertException : … should throw DeviceConnectionLostException but threw DeviceStallException
```

---

## Hard rules — status

- **Read-only contract:** ✅ `ReadOnlyContractTests` green; no AFC/lockdown write/delete/rename symbol
  added. The only device-side change is read **instrumentation**, which observes values only.
- **Data integrity:** ✅ interrupted file left non-`done`/resumable; no published `.partial`; manifest
  only `done` rows — unchanged invariants, covered by existing + new tests.
- **Healthy hot path:** ✅ one `Interlocked.Exchange` + a `GetUtcNow` per 1 MB chunk (~30/s at the USB-2
  ceiling), no allocation; timer on its own thread. No measurable cost at 400 GB / 38k files.
- **No new `var`:** ✅ explicit types in all new/touched code. **XML `<summary>`** on new public surface
  (`ReadDiagnostics`; the watchdog/stream are `internal` but fully documented).
- **CI:** ✅ Release build 0/0, `dotnet format` clean, **119 + 2 tests green**.

---

## Supersession of #38 (for the merge)

The #38 `ForwardProgressMonitor` is **superseded** by this sprint: its consecutive-failure logic is folded
into `ForwardProgressWatchdog` (same default limit 10, same reset-on-progress semantics, same
`DeviceConnectionLostException` → exit 3). The known limit documented for #38 (≥10 genuinely-corrupt
files in a row stops the run) carries forward unchanged and is still resumable. `Closes #42`.

## For the reviewer (extra weight, per the plan)

1. Does a test **reproduce the real intra-file spin** (not a park)? → `FileCopierTests`
   `Intra_file_freeze_after_bytes_flowed…` (read delivers bytes then stops and **ignores cancellation**),
   plus the fail-before evidence above.
2. Is the liveness model **unified** and **manifestation-agnostic**? → one `ForwardProgressWatchdog`
   timer + folded-in counter; the per-read timer is gone; `DeviceWatchdog` is the separate (non-byte)
   open/stat/list guard.
3. False-positive guards present? → slow-but-alive, 0-byte file, isolated failures, single premature-EOF.
4. Read-only intact? → `ReadOnlyContractTests` green; instrumentation observes only.

## For QA (#21 Phase B)

Real cable-yank **mid-large-file** AND **between files**: expect stop within ~`--read-timeout`, the
disconnect message, `summary.txt`, **exit 3**, **CPU drops to idle** (the proof — the abandoned native
read dies on process exit), in-flight file resumable on re-run, long-path copy still works. A debug build
with `GAS_DEBUG_READS=1` will capture the exact native per-read values if anything still surprises.
