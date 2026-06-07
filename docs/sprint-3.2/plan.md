# Sprint 3.2 — Hotfix: unplug-safe (busy-spin) + long-path journal (v1.0 unblock)

**Type:** hotfix (two defects) · **Gates:** v1.0 release (#21) · **Branch:** `fix/sprint-3.2` (dev)
**Owner:** Dev (Sage — device + journal) · **Review:** Producer gate · **Re-acceptance:** QA #21 Phase B (hardware)

> **Read first:** issues **#38** (unplug busy-spin) and **#39** (long-path journal crash), `docs/qa/sprint-3-signoff.md` (Phase B BLOCKED record), `docs/review/sprint-3.1-review.md` (the prior #25 fix that proved incomplete), `PROJECT_BRIEF.md` §9 (read-only contract), `docs/review/review-profile.md` (sacred invariants + the **false-green / "test the real failure mode"** blind spot).

## Why this exists

QA #21 Phase B (hardware re-acceptance after the #25 fix) found **two** named-criterion failures. Both are **data-safe** (read-only contract held, journal resumable, zero data loss), so both are `severity:major`, not blocker — **but both block the v1.0 gate.**

## Defect 1 — #38: the #25 unplug fix is incomplete on hardware (the STEP-BACK item)

**Symptom:** on a real mid-copy USB unplug the process **busy-spins at 100% CPU forever** — no stall message, no `summary.txt`, no exit 3, force-kill required. Reproduced twice with independent device probes.

**Root cause (confirmed in source):** the shared `DeviceWatchdog` only trips on a call that **parks** (never returns) — an *inactivity* timer. A yanked cable instead makes the native call **return fast and wrong**: `AfcReadStream.Read` returns `0` when `afc_file_read` yields `Success` + `bytesRead == 0` ([AfcReadStream.cs](../../src/GetAndSee.Core/Device/AfcReadStream.cs#L62)), or an `AfcError` that isn't one of the 3 codes in `AfcErrors.IsConnectionFatal`. A fast return sails **past** the inactivity timer → `CopyStreamAsync`'s `while (read > 0)` exits → size-mismatch → `MarkFailed` → the loop advances to the next file and spins. CPU pegged, no exit.

**STEP BACK — do NOT just add another timer.** This is the **third** time the "test the real failure mode" gap has bitten the same area (#11 stalled read → #25 parked open → #38 busy-spin). A disconnect has **at least two manifestations** — idle-PARK (now handled) and fast-fail/SPIN (this) — and a per-call inactivity timer structurally cannot see the second. The fix needs a **forward-progress / connection-health** model. Candidate directions (dev chooses, justify the choice):
- **Truncation-as-fault:** a `0`-byte read *before* the AFC-reported size is reached is a fault, not clean EOF — throw so the file fails *and* signals "stream died," don't treat it as a normal short read.
- **Circuit-breaker:** N consecutive fast failures (or a fail-rate over a short window) ⇒ the device is gone ⇒ stop the run cleanly, **exit 3**, resumable — rather than marching all remaining files into the same fast-fail.
- **Connection-health probe:** on a suspicious failure, cheaply check the device is still enumerable; if not, `DeviceConnectionLostException` → exit 3.

A combination is fine. The goal: **a real cable-yank, in any manifestation, ⇒ clean resumable stop within a few seconds, exit 3, no CPU spin, no hang.**

**HARD test constraint:** the new test **must reproduce the SPIN** — a fake stream/op that returns **fast / 0-byte / fast-error in a tight loop** (Ivy's repro guidance), NOT another parked-call/stall mock. A green suite that only models PARK is exactly what shipped #38. Drive it with the existing `FakeTimeProvider` pattern where a clock is involved.

## Defect 2 — #39: long-path R6 gap — journal DB path not prefixed (the QUICK one)

**Symptom:** with a deep destination, `copy` and `status` crash with an unhandled `SqliteException` (`SQLITE_CANTOPEN`) **before any file is copied**.

**Root cause (confirmed in source):** [TransferJournal.cs](../../src/GetAndSee.Core/Journal/TransferJournal.cs#L51) builds `DataSource = Path.Combine(destinationRoot, "get-and-see.db")` with **no** `LongPath.ToExtended` — in **both** `Open` and `OpenReadOnly`. The media path is `\\?\`-prefixed in `FileCopier`, but the journal opens first and unprefixed, so it hits the legacy `MAX_PATH` limit. Boundary (Ivy): dest root 233 chars works; 239/249 crash.

**Fix:**
- Apply `LongPath.ToExtended(databasePath)` to the `DataSource` in **both** `TransferJournal.Open` and `OpenReadOnly`.
- Confirm the `-wal` / `-shm` sidecars inherit the `\\?\` prefix (SQLite derives them from `DataSource`).
- Sanity-check the `SummaryWriter` `summary.txt` path for the same `MAX_PATH` exposure; prefix if needed.
- Add a long-path **journal-open** unit/integration test (and ideally a `summary.txt` long-path test).

## Hard rules (unchanged — non-negotiable)

- **Read-only device contract holds.** No AFC/lockdown write/delete/rename/truncate/mkdir/link symbol in `GetAndSee.Core`. `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` (IL scan, full §5.5 blocklist) MUST stay green. The #38 fix touches the read path only.
- **Data-integrity invariants hold:** interrupted file left non-`done` (resumable), never falsely `done`; no `.partial` published; manifest exposes only `done` rows.
- **No new `var`** (explicit-types convention; #23 still pending). XML `<summary>` on any new public `GetAndSee.Core` type.
- **CI green** (build+test, gitleaks, EXE smoke) before the PR.

## Acceptance criteria

- [ ] **#38:** a simulated cable-yank that **fast-fails / returns 0-byte in a loop** is detected within a few seconds ⇒ `DeviceConnectionLostException`/`DeviceStallException` ⇒ clean **exit 3**, in-flight file non-`done`/resumable, `summary.txt` written, **no CPU spin** — proven by a test that reproduces the SPIN (not a stall mock).
- [ ] **#38:** the original parked-open/idle-stall path still works (don't regress #25).
- [ ] **#39:** a destination whose `…/get-and-see.db` path exceeds 260 chars opens the journal and copies end-to-end (or fails with a clean, actionable message — never an unhandled `SqliteException`); WAL/SHM sidecars work; `summary.txt` writes at depth. Covered by a new long-path journal test.
- [ ] `ReadOnlyContractTests` green; no new device-write symbol; `dotnet test` + CI green.
- [ ] Healthy-path copy unaffected (no measurable per-file slowdown at 38k-file scale).
- [ ] **Hardware (QA #21 Phase B re-run):** real cable-unplug ⇒ clean resumable stop, no hang, no spin; long-path dest copies end-to-end.

## Process

Dev on `fix/sprint-3.2` off current `main` (`12dda62`). Two logical commits (`Closes #38`, `Closes #39`) or two if cleaner; update `docs/sprint-3.2/progress.md`; CI green; open **one** PR `sprint-3.2: unplug-safe (busy-spin) + long-path journal` and **stop for the producer review gate**. Never push to `main`. Write `docs/sprint-3.2/done.md`. **Take the time to rethink #38 properly — a third band-aid is worse than a real fix.**

## Then (producer + QA)

1. Producer independent review gate — extra weight on **Correctness/Reliability** + the read-only contract, and a hard check that **the #38 test reproduces the real spin**, not a stall.
2. **QA #21 Phase B re-acceptance (one combined hardware run):** the unplug re-test (both fast-fail and parked manifestations if reproducible) + the long-path re-test + the still-deferred `--verify-hash` / EXE-smoke if not yet signed. Ivy updates `docs/qa/sprint-3-signoff.md`.
3. On PASS → merge PR #31 (final gate record) → cut **v1.0** → `release.yml` ships the EXE + SHA-256.

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Sage leads — device + journal). Implement the Sprint 3.2
hotfix: two data-safe but gate-blocking defects found in QA #21 Phase B hardware testing.
This is real CODE work: implement, test, commit, push, open ONE PR, then STOP for the
producer review gate. Do NOT merge. Never push to main.

WORK ONLY IN: e:\src\get-and-see-dev
  git checkout main && git pull origin main && git checkout -b fix/sprint-3.2
Read: docs/sprint-3.2/plan.md (the full brief), issues #38 and #39, PROJECT_BRIEF.md §9,
docs/review/review-profile.md (esp. the "false-green / test the real failure mode" blind spot).

#39 (the quick one — long-path journal crash):
  TransferJournal.Open AND OpenReadOnly build DataSource = Path.Combine(dest,"get-and-see.db")
  with NO LongPath.ToExtended — so a deep destination crashes SQLite (SQLITE_CANTOPEN) before
  any copy. Prefix the DataSource via LongPath.ToExtended in BOTH methods; confirm -wal/-shm
  sidecars inherit \\?\; sanity-check SummaryWriter summary.txt for the same exposure; add a
  long-path journal-open test.

#38 (the STEP-BACK one — unplug busy-spin; do NOT just add another timer):
  On a real cable-yank the native read returns FAST and WRONG (afc_file_read Success + 0 bytes,
  or a non-allow-listed AfcError) — it does NOT park. The inactivity watchdog only catches a
  PARKED call, so the fast path defeats it: CopyStreamAsync's while(read>0) exits, the file
  fails, the loop spins to the next file → 100% CPU, no stall, no exit 3. This is the 3rd
  manifestation of the same disconnect bug (#11 stall → #25 park → #38 spin). A per-call
  inactivity timer is structurally insufficient. Implement a FORWARD-PROGRESS / connection-
  health model — pick and justify: (a) treat a 0-byte read BEFORE the expected size as a fault
  (truncation), not EOF; and/or (b) a circuit-breaker: N consecutive fast failures ⇒ device gone
  ⇒ stop, exit 3, resumable; and/or (c) a cheap "device still enumerable?" health check on a
  suspicious failure. Goal: a real yank in ANY manifestation ⇒ clean resumable stop within a few
  seconds, exit 3, NO spin, NO hang.

  HARD TEST CONSTRAINT: the new test MUST reproduce the SPIN — a fake stream/op that returns
  fast / 0-byte / fast-error in a TIGHT LOOP — NOT another stall/parked mock. A suite that only
  models PARK is exactly what let #38 ship. Keep the existing parked-open/idle-stall test green
  too (don't regress #25).

HARD RULES: read-only contract holds (no device-write symbol; ReadOnlyContractTests green);
data-integrity invariants hold (interrupted file non-done/resumable, no .partial published,
manifest only done rows); no new var; XML <summary> on new public Core types; CI green before PR.

PROCESS: commit per defect (Closes #38 / Closes #39), update docs/sprint-3.2/progress.md, write
docs/sprint-3.2/done.md, open ONE PR "sprint-3.2: unplug-safe (busy-spin) + long-path journal",
then STOP for the producer review gate. Take the time to do #38 right — a third band-aid is
worse than a real fix.

REPORT BACK: files changed; the #38 design you chose and WHY; how the #38 test reproduces the
spin (not a stall); how read-only stays green; dotnet test counts + CI; the PR number.
```
