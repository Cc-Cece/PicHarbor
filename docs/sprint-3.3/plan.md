# Sprint 3.3 — The universal forward-progress watchdog (kill the unplug bug as a class)

**Type:** hotfix — root-cause / step-back · **Gates:** v1.0 (#21) · **Branch:** `fix/sprint-3.3` (dev)
**Owner:** Dev (Sage — device/transfer) · **Design:** `docs/brainstorm/sprint-3.3-consilium.md` · **Re-acceptance:** QA #21 Phase B (hardware)

> **Read first:** the **consilium** (`docs/brainstorm/sprint-3.3-consilium.md`), issues **#42** (intra-file spin) + **#38** (between-file spin) + **#25**/**#11** (parks), `PROJECT_BRIEF.md` §9, `docs/review/review-profile.md` (the recurring "test the real failure mode" blind spot).

## Why we are here (read this, it matters)

The mid-copy USB-disconnect bug has now manifested **four** times (#11 park → #25 open-park → #38 between-file spin → #42 intra-file spin). **Every prior fix guarded a specific code location, and hardware then found the next location.** We are stopping the whack-a-mole and fixing the **class**.

The one invariant across all four (Ivy measured it every time): **forward byte-progress stops** — the `.partial` freezes. The robust signal is "are bytes still moving?", which we already emit via `onBytesStreamed`. Data safety, resume, and the read-only contract have held all four times — **only graceful detection fails.** That is the entire job of this sprint.

## Relationship to PR #41 (important)

PR #41 (`fix/sprint-3.2`) is **open, green, NOT merged**. It contains: the **#39 long-path fix (good, keep)** and the **#38 `ForwardProgressMonitor` (insufficient — between-files only)**. Decision: **branch `fix/sprint-3.3` off `fix/sprint-3.2`** (not off `main`) so the long-path fix and the existing breaker come along; this sprint **supersedes** the #38 mechanism with the universal one (fold the per-file breaker into the run-level watchdog, or keep it as a cheap complement — dev's call after diagnosis). At the end we merge **one** combined line (3.2 + 3.3) once hardware passes. Producer will confirm the merge mechanics; dev just branches off `fix/sprint-3.2`.

## Phase 0 — DIAGNOSE FIRST (mandatory; no design is final until this reproduces)

We have **assumed** the mechanism four times. Not a fifth.

1. Instrument the read path with a verbose debug switch: per-read `AfcError`, byte count, elapsed ms, and the `.partial` size. (Behind a hidden `--debug-reads` or an env var; must NOT change the healthy path or ship enabled.)
2. **Reproduce the intra-file spin deterministically** in a test harness whose fake device stream returns the *real* hardware signature — fast `Success+0`, and/or a fast-throw, and/or a tiny-trickle — inside a single file's read loop, and show it pins/loops the way hardware did.
3. **Write down the confirmed mechanism** in `docs/sprint-3.3/progress.md` (what exactly spins: a caught-and-retried fast error? a re-issued zero read? the `WhenAny` race re-arming?). QA can provide a logged hardware capture (debug build) if the harness can't fully settle it.
4. Hand QA (Ivy) the debug build so a real yank confirms the native return values.

> If diagnosis shows the spin is a **pure-CPU loop that never awaits**, the watchdog MUST be timer/thread-based and the read path made to observe cancellation — a token alone won't break a non-awaiting loop. This is the single most important thing to get right; design to the confirmed mechanism.

## Phase 1 — The universal forward-progress (liveness) watchdog

One run-level liveness guard, fed by the existing `onBytesStreamed` heartbeat:
- Track "last time bytes advanced." While a copy is in flight, if **zero bytes flow for N seconds** (tie to `--read-timeout`), declare the device lost → trip **one** `CancellationToken` → unwind to the **existing** `DeviceConnectionLostException` / exit-3 path (resumable; `summary.txt` written; the one Nova-approved disconnect message).
- It must cover **all four**: park, between-file spin, intra-file spin, fast-fail — because it watches *bytes*, not a location.
- **Unify the liveness model.** There must be ONE byte-based liveness concept. Keep the inactivity `WatchdogReadStream` only if it still earns its place under the new model; otherwise fold it in. Do not leave two half-overlapping timers.
- Complement: treat `afc_file_read` returning `Success` with **0 bytes before the expected file size** as premature-EOF / connection-loss, not clean EOF.
- **Healthy path stays free:** the heartbeat is already emitted; the watchdog is a background check — no per-chunk cost, no allocation in the copy loop, no dashboard impact at 400 GB / 38k files.

## Phase 2 — Tests that reproduce EACH manifestation (the anti-false-green bar)

Deterministic harnesses, fake clock where time is involved, each **failing before / passing after**:
- intra-file spin (#42), between-file spin (#38), native park (#25/#11), premature-EOF (Success+0 before size).
- Each asserts: bytes-stop ⇒ `DeviceConnectionLostException`/exit 3, in-flight file left non-`done`/resumable, no `.partial` published, **no spin** (the loop actually terminates).
- **False-positive guards:** a genuinely slow-but-alive device (slow but >0 bytes) must NOT trip; a legitimately short or 0-byte file must NOT trip; isolated per-file failures on a healthy device must NOT trip.
- A test that only models a *park* does NOT satisfy this sprint (that is exactly how #38/#42 shipped green).

## Hard rules (non-negotiable)

- **Read-only contract:** no AFC/lockdown write/delete/rename/truncate/mkdir/link symbol in `GetAndSee.Core`; `afc_file_open` stays `FopenRdonly`-only; `ReadOnlyContractTests` green.
- **Data integrity:** interrupted file non-`done`/resumable; no published `.partial`; manifest only `done` rows.
- **Healthy hot path unaffected** at scale; **no new `var`**; XML `<summary>` on new public Core types; `dotnet format` + build (0/0) + tests green before the PR.

## Acceptance criteria

- [ ] **Diagnosis reproduced**: a deterministic test reproduces the intra-file spin (and the mechanism is written down) — fails before the fix.
- [ ] **Universal**: park, between-file spin, intra-file spin, and premature-EOF all ⇒ clean **exit 3**, resumable, no spin — each covered by a test that fails before / passes after.
- [ ] **No false positives**: slow-but-alive device, short/0-byte file, isolated per-file failures do NOT trip.
- [ ] One unified byte-based liveness model (not two overlapping timers).
- [ ] `ReadOnlyContractTests` green; no device-write symbol; healthy path unchanged; CI green.
- [ ] **Hardware (QA #21 Phase B):** real cable-yank **mid-large-file** and **between files** ⇒ stop within a few seconds, disconnect message, `summary.txt`, **exit 3**, **CPU drops to idle** (CPU trace is the proof); reconnect + re-run resumes with zero loss; deep-destination long-path copy still works.

## Process

Dev on `fix/sprint-3.3` **off `fix/sprint-3.2`**. Commit Phase 0 (diagnosis + failing repro) **before** Phase 1 so the mechanism is on record. `Closes #42`; supersede/keep #38 as diagnosis dictates. Update `docs/sprint-3.3/progress.md` (incl. the confirmed mechanism) + `done.md`. CI green. Open **one** PR `sprint-3.3: universal forward-progress watchdog (#42)`, then **STOP for the producer review gate**. Never push to `main`. **Take the time to diagnose — a fifth band-aid is worse than a slow, correct fix.**

## Then (producer + QA)

1. Producer independent review gate — extra weight on: does a test **reproduce the real intra-file spin** (not a park)? is the liveness model **unified** and **manifestation-agnostic**? false-positive guards present? read-only intact?
2. QA #21 Phase B hardware re-acceptance (Ivy): yank mid-large-file **and** between files, CPU-trace proof of idle-after-exit-3, resume, long-path. Update `docs/qa/sprint-3-signoff.md`.
3. On PASS → merge the combined 3.2+3.3 line → merge PR #31 (gate record) → update `PROJECT_BRIEF.md` §7/§8 → cut **v1.0**.

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Sage leads — device/transfer). Implement Sprint 3.3: the
UNIVERSAL fix for the mid-copy USB-disconnect bug, which has now manifested FOUR times because every
prior fix guarded a specific code location and hardware found the next one. Real CODE work:
diagnose, implement, test, commit, push ONE PR, then STOP for the producer review gate. Do NOT merge.
Never push to main.

WORK ONLY IN: e:\src\get-and-see-dev
  git fetch origin
  git checkout fix/sprint-3.2 && git pull        (branch OFF fix/sprint-3.2, NOT main — it carries the
  git checkout -b fix/sprint-3.3                  good #39 long-path fix + the existing #38 breaker)
Read: docs/brainstorm/sprint-3.3-consilium.md (the design), docs/sprint-3.3/plan.md (this brief),
issues #42 #38 #25 #11, PROJECT_BRIEF.md §9, docs/review/review-profile.md.

THE INVARIANT: across all 4 manifestations (park / open-park / between-file spin / intra-file spin)
the SAME thing happens — forward BYTE progress stops (the .partial freezes). Fix the SYMPTOM (bytes
stopped), not the location. The byte heartbeat already exists: FileCopier calls onBytesStreamed(read).

PHASE 0 — DIAGNOSE FIRST (mandatory; we have ASSUMED the mechanism 4 times — not a 5th):
  1. Instrument the read path (hidden --debug-reads or env var; per-read AfcError + byte count +
     elapsed + .partial size; must NOT change or slow the healthy path, must NOT ship enabled).
  2. Reproduce the INTRA-FILE spin (#42) deterministically in a harness whose fake device stream
     returns the real hardware signature (fast Success+0, and/or fast-throw, and/or tiny trickle)
     inside ONE file's read loop, and show it loops/pins like hardware. NOTE the puzzle: a Success+0
     makes `while(read>0)` EXIT (that's the #38 between-file path) yet QA saw the per-file counter
     FROZEN — so the loop is NOT exiting. Find out exactly why it spins without progress (caught-and-
     retried fast error? re-issued zero read? the WhenAny race in DeviceWatchdog re-arming hot?).
  3. Write the confirmed mechanism into docs/sprint-3.3/progress.md BEFORE designing the fix.
  If the spin is a PURE-CPU loop that never awaits, a CancellationToken alone won't break it — the
  watchdog must be timer/thread-based and the read path made to observe it. Design to what you CONFIRM.

PHASE 1 — UNIVERSAL forward-progress (liveness) watchdog:
  One run-level guard fed by onBytesStreamed: while a copy is in flight, if ZERO bytes flow for N
  seconds (tie to --read-timeout) → device lost → trip ONE CancellationToken → unwind to the EXISTING
  DeviceConnectionLostException / exit-3 path (resumable, summary.txt written, one clear "Device
  disconnected. Progress saved — reconnect and re-run to resume." message). Must cover park +
  between-file + intra-file + fast-fail UNIFORMLY. Unify the liveness model — ONE byte-based concept;
  fold the inactivity WatchdogReadStream in if it no longer earns its place; don't leave two
  overlapping timers. Complement: treat afc_file_read Success+0-before-expected-size as premature-
  EOF/connection-loss, not clean EOF.

PHASE 2 — TESTS that reproduce EACH manifestation (anti-false-green bar):
  Deterministic, fake clock where time matters, each FAILS BEFORE / PASSES AFTER: intra-file spin,
  between-file spin, native park, premature-EOF. Each asserts bytes-stop ⇒ exit 3, in-flight file
  resumable, no .partial published, NO spin (loop terminates). False-positive guards: slow-but-alive
  device (slow but >0 bytes), short/0-byte file, isolated per-file failures — must NOT trip. A test
  that only models a PARK does NOT satisfy this sprint.

HARD RULES: read-only contract (no device-write symbol; ReadOnlyContractTests green); data integrity
(interrupted file non-done/resumable; no published .partial; manifest only done rows); healthy hot
path unaffected at 400GB/38k files; no new var; XML <summary> on new public Core types; CI green.

PROCESS: commit Phase 0 (diagnosis + failing repro) BEFORE Phase 1 so the mechanism is on record.
Closes #42. Update docs/sprint-3.3/progress.md (incl. confirmed mechanism) + done.md. Open ONE PR
"sprint-3.3: universal forward-progress watchdog (#42)" then STOP for the producer review gate.

REPORT BACK: the CONFIRMED intra-file mechanism (from Phase 0); the unified liveness design and why
it covers all 4; how each manifestation test fails-before/passes-after; the false-positive guards;
read-only status; dotnet test counts + CI; the PR number. Take the time to diagnose — a 5th band-aid
is worse than a slow correct fix.
```
