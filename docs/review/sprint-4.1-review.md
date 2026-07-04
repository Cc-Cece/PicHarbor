# Sprint 4.1 review — `reorganize` (offline atomic resumable layout migration)

**Change:** `feature/sprint-4.1` vs `main` · **Layer:** 1 (dev self-review, independent reviewer, find-problems framing)
**Date:** 2026-07-04 · **Verdict:** **PASS-WITH-NITS** — 0 blockers, 0 majors.

> Layer-1 self-review per PROJECT_BRIEF §13.6. Independent reviewer (fresh context, not the author), calibrated
> to `docs/review/review-profile.md`, extra weight on move-integrity + crash-resume + the required-param change.
> This does **not** replace the producer's independent gate.

## High-stakes items verified FINE

- **Move-integrity.** Every move is a same-volume, non-overwrite `File.Move` (atomic OS rename) followed by a
  single `UpdateDestPath`. No cross-volume copy-then-delete exists by construction (one archive root). Only
  journal `done` media rows are enumerated as moves, so `get-and-see.db`, `summary.txt`, `.get-and-see-tmp/`,
  and user files are never moved. Empty-dir cleanup deletes only provably-empty dirs (double-guarded) and can
  never reach the root.
- **Crash-resume / idempotency.** An interrupt between `File.Move` and `UpdateDestPath` heals on resume
  (source gone + target present + size matches ⇒ journal-only heal), never a double-move, new suffix, or drop.
  Suffix assignment is reproducible (in-run assignment set over a fixed enumerate order). The deliberate
  "in-run set only, no on-disk check during planning" decision is **correct and necessary** — an on-disk check
  would re-suffix a physically-moved-but-unjournaled file and break determinism.
- **Clobber-safety.** Non-overwrite `File.Move` throws on an occupied target (⇒ per-file fail, no clobber);
  a `current&&target both exist ⇒ false` pre-check adds a second layer; the reconcile heal requires
  `on-disk size == recorded source size`, so it never heals onto foreign bytes. A per-file failure keeps the
  row `done` at its current path — never dropped.
- **Required-param change.** `FileCopier.organizeScheme` is required; all construction sites (prod + every
  test) pass it; the sole prod caller passes the **resolved** `effectiveScheme` — `copy` behaviour unchanged.
- **Marker lifecycle.** `SetReorganizeTarget` at start; on `failed == 0`, stamp scheme **then** clear marker
  (a crash in that window leaves `marker == scheme`, which the copy guard treats as benign). A partial failure
  leaves the marker set so `copy` refuses and `reorganize` resumes.
- **Offline.** `Reorganizer` holds no `IPhoneClient` (reflection-asserted); `ReadOnlyContractTests` green.
- **Also verified:** all new SQL parameterized; `reorganize_target` is settings k/v with **no** schema bump;
  every FS path via `LongPath.ToExtended`; exit codes `0/1/2/130`; `--dry-run` proven side-effect-free; the
  real-`.exe` E2E does an offline round-trip + copy-refusal; docs use only synthetic values — **no real EUII**;
  **no new `var`** in `src/`.

## Findings (all addressed)

- **[MINOR — FIXED] Reorganize placement was time-dependent for an `unsorted/` file.** `IsSane` uses a
  `now + 1 day` upper bound and `MarkDone` stores the raw EXIF timestamp, so a clock-skewed *future* date that
  `copy` binned as `unsorted` could later re-resolve as sane and **move out of `unsorted/`** — contradicting
  the "unsorted never moves" guardrail (data-safe, but a spec gap the tests missed). **Fix:** `Reorganizer.Plan`
  now treats any file whose current `dest_path` is under `unsorted/` as staying put (respects copy's original
  null-date decision). New test `A_file_recorded_as_unsorted_never_moves_even_if_its_stored_date_resolves_sane`.
- **[MINOR — FIXED] The `Failed > 0` / clobber-guard branch was untested.** Added
  `A_foreign_file_blocking_a_target_fails_that_move_without_clobbering`: pre-places a foreign file at a mover's
  target, asserts the move fails (foreign bytes intact), the row is untouched (`done` at its old path), the
  marker stays set + scheme unchanged + exit 1, and a re-run completes once the blocker is removed.
- **[NIT — FIXED] `ORDER BY source_path` was not provably total** (identity is `source_path + source_size`).
  Now `ORDER BY source_path, source_size` so collision-suffix determinism is explicit, not incidental.
- **[NIT — FIXED] Empty-dir cleanup protected `unsorted\` by emptiness, not by name.** Added `unsorted` to the
  name-skip alongside `.get-and-see-tmp`, matching the plan's named-protected-dirs guardrail.

## Deliberate deviations (reviewer confirmed sound)

- Collision resolution uses the **in-run assignment set only** (not on-disk existence during planning) — required
  for crash-resume determinism; clobber-safety is preserved by `File.Move` + the size-checked reconcile.
- Scheme is stamped + marker cleared **only on a fully clean run** (`failed == 0`); a partial run stays resumable.
