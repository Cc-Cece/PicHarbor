# Sprint 3.3 Consilium — Killing the unplug bug as a CLASS, not a location

> Producer-facilitated design consilium. Convened after the **fourth** manifestation of the
> mid-copy USB-disconnect failure (#11 → #25 → #38 → #42). The goal of this doc is to agree a
> design BEFORE any code, because four prior point-fixes each shipped green and failed on hardware.

## The pattern we have to confront

| # | Manifestation | What we guarded | Why hardware still broke |
|---|---------------|-----------------|--------------------------|
| #11 | native read **stalls/parks** forever | added the inactivity `WatchdogReadStream` | only catches a *slow/parked* read |
| #25 | native `afc_file_open` **parks** | generalized the watchdog over open/stat/list | only catches *parked* native calls |
| #38 | **between-file** fast-fail spin | `ForwardProgressMonitor` (N consecutive file failures) | only checked *between files* |
| #42 | **intra-file** fast-fail spin | — | the per-file check never runs mid-file |

**Every fix guarded a specific code location. Hardware then found the next location.** That is the
process failure. We have been playing whack-a-mole with a bug that has one essence.

> Producer's mea culpa, on the record: the hired reviewer explicitly named the intra-file read loop
> as an escape hatch and called it "not a realistic cable-yank." I relayed that as a NIT. Hardware
> proved it *is* the realistic yank. Lesson booked: when a reviewer says "the one remaining shape,"
> on a bug that has already mutated three times, that is a finding, not a nit.

## The one invariant (the key insight)

Across **all four** manifestations, Ivy measured the *same* observable every time:
**forward byte-progress stops** — the `.partial` staging file freezes (zero growth), the device
stops delivering bytes. Park, between-file spin, intra-file spin, fast-fail — all of them are
"bytes stopped flowing while we still expected bytes."

So the robust signal is not a code location. It is **"are bytes still moving?"** We already emit
that heartbeat: `FileCopier` calls `onBytesStreamed(read)` on every chunk.

---

## The voices

### Sage (Backend / device) — "Diagnose first. We have *assumed* the mechanism four times."
> I will not design fix #5 on a guess. #42's report is honest that QA saw the *symptom* (pinned
> core, frozen `.partial`, frozen per-file counter) — not the instruction. And the symptom is
> genuinely puzzling: a `Success+0` read would make `while (read > 0)` **exit** the file (→ that's
> the #38 between-file path), yet the per-file counter is **frozen** — so the loop is *not* exiting.
> Something is spinning *without completing a normal await and without writing bytes*. I need to
> **instrument the read path and reproduce the exact intra-file loop** before I touch the design.
> My bet is one of: (a) the native side throws a fast non-fatal error that is caught and retried in
> a tight loop somewhere below `ReadAsync`; (b) a `Success` with a tiny/zero count that re-issues
> without progress; (c) the `WhenAny` race in `DeviceWatchdog` re-arming in a hot loop when the
> inner read completes instantly. I will nail which, with a logged debug build + a repro harness,
> and only then commit to the mechanism.
>
> On the fix itself: I agree the signal is the **byte heartbeat**, run-level. A background
> forward-progress watchdog that trips a `CancellationToken` when zero bytes flow for N seconds
> while a copy is in flight is *manifestation-agnostic*. Cancellation propagates through the awaited
> `ReadAsync`/`WriteAsync` and unwinds to the existing exit-3 path. The subtlety: if the spin is a
> *pure-CPU* loop that never awaits, a token won't interrupt it — which is **exactly why I must
> confirm the mechanism first**. If it never yields, the watchdog has to live on its own thread/timer
> and the read path has to be made to observe it. I'd rather know than assume.

### Ivy (QA) — "Hardware is the only proof, and I want a repro harness that fails before / passes after."
> I have now reproduced this class four times; I trust the symptom, not any mock. Two asks: (1) give
> me a **debug build with verbose read-loop logging** (per-read: return code, byte count, elapsed)
> so the next yank captures the exact native return values and timing — that ends the guessing. (2)
> Whatever the fix, the acceptance test must be a **harness that reproduces the real spin** — a fake
> device stream that returns the *actual* hardware signature (fast `Success+0`, or fast-throw, or
> trickle) in a tight loop — and that harness must **fail before the fix and pass after**, on any
> machine. Every prior fix had a green test that didn't model the real failure. No more. And I will
> still gate v1.0 on a real cable-yank: CPU must drop and the process must exit 3, not pin a core.

### Nova (CLI / UX) — "One consistent disconnect experience, however it manifests."
> From the user's seat all four are identical: "it froze, I killed it." The fix should make the
> *experience* uniform: within ~`--read-timeout` of bytes stopping, print one clear line —
> "Device disconnected. Progress saved — reconnect and re-run to resume." — write `summary.txt`,
> exit 3. Same message whether it parked, spun between files, or spun mid-file. The user never needs
> to know which manifestation it was. And keep the healthy path untouched — no dashboard stutter,
> no per-chunk cost.

### Kira (Product / scope) — "Fix the class for v1.0. Nothing more."
> Scope discipline: v1.0 ships **one robust mechanism** that turns "bytes stopped flowing" into a
> clean resumable stop. That is the whole job. **Not** in scope: auto-reconnect, mid-run retry,
> resuming a partial file without re-copying it. Those are v1.1+. We are buying *graceful detection*,
> which is the only thing that has ever failed — data safety, resume, and read-only have held all
> four times. If diagnosis reveals the proper fix is large, we re-convene; we do not scope-creep it
> silently.

### Remy (Producer) — synthesis
> We converge. The fix is a **run-level forward-progress (liveness) watchdog** on the existing byte
> heartbeat — watch the *symptom* (bytes stopped), not the *location*. But it is **gated behind a
> mandatory diagnosis step**: Sage instruments and reproduces the exact intra-file mechanism, with a
> harness, before committing the design — because "assume the mechanism" is the through-line of all
> four failures. Ivy's hardware is the only sign-off. Kira holds scope to detection only.

---

## Converged design direction (to be CONFIRMED by diagnosis, not assumed)

1. **Phase 0 — Diagnose (mandatory, do this FIRST).** Instrument the read loop (per-read AfcError +
   byte count + elapsed). Reproduce the **intra-file** spin in a deterministic harness that mimics
   the real device signature. Write down the exact mechanism. **No fix design is final until this
   reproduces.** If the spin is pure-CPU / non-awaiting, the watchdog must be thread/timer based and
   the read path must be made to observe cancellation.

2. **Phase 1 — Universal forward-progress watchdog.** A single run-level liveness guard fed by the
   existing `onBytesStreamed` heartbeat: if **zero bytes flow for N seconds while a copy is in
   flight**, declare the device lost → trip one `CancellationToken` → unwind to the **existing**
   `DeviceConnectionLostException`/exit-3 path (resumable, `summary.txt` written). This subsumes the
   inactivity `WatchdogReadStream` *conceptually* — keep or fold it, but there must be **one**
   liveness model, byte-based, covering park + between-file + intra-file + fast-fail uniformly.
   Complementary: treat `afc_file_read` returning `Success` with **0 bytes before the expected file
   size** as premature-EOF / connection-loss, not a clean EOF.

3. **Phase 2 — Tests that reproduce EACH manifestation.** Park, between-file spin, intra-file spin,
   premature-EOF — each as a deterministic harness that **fails before and passes after**, driven by
   a fake clock where time is involved, asserting: bytes-stop ⇒ exit 3, in-flight file resumable, no
   `.partial` published, no CPU spin. Plus the false-positive guard (a genuinely slow-but-alive
   device, and a legitimately short/0-byte file, must NOT trip).

4. **Non-negotiables (unchanged):** read-only contract (`ReadOnlyContractTests` green, no
   device-write symbol); data-integrity (interrupted file non-`done`/resumable, no published
   `.partial`, manifest only `done`); healthy hot path unaffected at 400 GB / 38k files; no new
   `var`; XML `<summary>` on new public Core types.

## Out of scope (Kira holds the line)
Auto-reconnect; mid-run retry; partial-file resume-without-recopy; guarding the connect-time
handshake (that's the separately-tracked #35). v1.0 buys **graceful disconnect detection** only.

## Definition of done
A real hardware cable-yank — **mid-stream of a large file** and between files — makes the tool stop
within a few seconds, print the one disconnect line, write `summary.txt`, **exit 3**, leave the
in-flight file resumable, and **drop CPU to idle** (Ivy's CPU trace is the proof). Then reconnect +
re-run resumes with zero loss. Plus a deep-destination long-path copy still works end-to-end (#39,
already fixed in #41 — carry it forward).
