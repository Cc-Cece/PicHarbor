# Sprint 4.1 — progress (`reorganize`)

**Branch:** `feature/sprint-4.1` (off `main` @ Sprint 4 merged). Dev: Sage (journal/move engine), Nova (CLI).
**Status:** In progress.

> Live tracker — updated per phase. Logs every bug + fix (project rule). Handoff docs (`done.md`,
> PROJECT_BRIEF §7/§8) written at close.

## Plan (build order, from `plan.md`)

1. `DateFolderOrganizer` — expose `GetRelativeDestination(fileName, captureDate, scheme)` + reusable
   `ResolveDate(exif, mtime)`; extract the `_2/_3` collision helper (shared copier + reorganizer). No copy
   behavior change.
2. Journal — `UpdateDestPath`, `EnumerateDoneForReorganize` (deterministic order), `reorganize_target`
   settings marker get/set/clear. Parameterized SQL; no schema-version bump.
3. `Reorganizer` engine (Core) — plan → atomic same-volume `File.Move` → `UpdateDestPath` → crash-reconcile
   → deterministic new-collision suffixes → empty-dir cleanup → stamp scheme + clear marker on success.
4. `ReorganizeCommand` (CLI) — `--dest` (req), `--organize-by` (req, target), `--dry-run`; journal writable;
   exit `0/1/2/130`.
5. Cash the gate nit — `FileCopier.organizeScheme` required; `copy` incomplete-reorganize guard.

## Phase log

### Phase 1 — organizer refactor + shared collision helper + required scheme
- _in progress_

## Bugs / Issues Found

_(none yet)_

## Decisions / deviations

- **Reorganize collision resolution uses the in-run assigned set only (not on-disk existence during
  planning).** The plan suggested checking on-disk too, but an on-disk check during planning wrongly bumps a
  file that a prior interrupted run already physically moved to its (suffixed) target but hadn't journaled
  yet — breaking the highest-priority crash-resume determinism. Clobber-safety is instead enforced at move
  time: `File.Move` is non-overwrite (throws on a genuine foreign file → per-file failure, never a clobber),
  and the reconcile heal additionally requires the on-disk target's size to equal the recorded source size
  (never heal the journal onto foreign bytes).
- **Scheme is stamped + marker cleared only on a fully clean run (`failed == 0`, not cancelled).** A partial
  run leaves the `reorganize_target` marker set so `copy` refuses (actionable message) and a re-run of
  `reorganize` retries the stragglers — matches the plan's "resumable" contract.
