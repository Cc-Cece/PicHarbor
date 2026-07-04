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
- **Done.** `DateFolderOrganizer.GetRelativeDestination(fileName, captureDate, scheme)` + public
  `ResolveDate(exif, mtime)`; RemoteFile overloads delegate (no copy behavior change — 43 organizer/copier
  tests green). Extracted `Organize/CollisionSuffix.Resolve(path, isAvailable)`; copier routes through it.
  `FileCopier.organizeScheme` now required; updated 15 call sites (2 prod, 13 test). Build 0/0. Commit `2872bb0`.

### Phase 2 — journal ops for reorganize
- **Done.** `UpdateDestPath` (single `UPDATE dest_path`), `EnumerateDoneForReorganize` (`WHERE state='done'`
  `ORDER BY source_path`, parses stored EXIF wall-clock + UTC mtime into a `ReorganizeEntry`), and the
  `reorganize_target` marker `Get/Set/Clear` (settings k/v — **no schema-version bump**). 5 journal tests
  green (update-keeps-done, deterministic order + excludes non-done, unsorted null dates, marker round-trip,
  marker independent of recorded scheme). Parameterized SQL throughout.

### Phase 3 — `Reorganizer` engine
- **Done.** `Core/Reorganize/Reorganizer` (+ `PlannedMove`/`ReorganizePlan`/`ReorganizeReport`). Plan is
  deterministic (by `source_path`, in-run assignment set only); execute does per-file atomic
  same-volume `File.Move` → `UpdateDestPath` → crash-reconcile (file-at-target + size match ⇒ heal;
  missing/foreign ⇒ per-file fail, never drop) → bottom-up empty-dir cleanup → stamp scheme + clear marker
  only on a clean run. Offline (zero `IPhoneClient`); every path via `LongPath.ToExtended`. 12 engine tests
  green (incl. the crux crash-resume + deterministic-collision-under-interruption). Commit `e726bc5`.

### Phase 4 — `ReorganizeCommand` (CLI)
- **Done.** `Cli/Commands/ReorganizeCommand` mirrors `SearchCommand`: `--dest` (req), `--organize-by`
  (req, target), `--dry-run`; opens the journal **writable**; exit `0/1/2/130` (no `3`). Injectable
  `IAnsiConsole`. Registered in `Program.cs`. `reorganize --help` renders. 8 CLI tests (parse, no-archive=2,
  dry-run side-effect-free, real-move success).

### Phase 5 — cash the gate nit + copy guard
- **Done.** `FileCopier.organizeScheme` was made required in Phase 1. Added
  `CopyCommand.IncompleteReorganizeError(journal, dest)` → wired into `ExecuteAsync` (refuse + actionable
  message + exit `2`) after the journal open/device upsert. 3 guard unit tests (conflict → actionable, no
  marker → allow, benign stamp-then-clear window → allow).

### Phase 6 — docs + real-`.exe` E2E + full validation
- **Done.** README `reorganize` section + `--organize-by` cross-link; `manifest-schema.md` `reorganize_target`
  settings key. Real-`.exe` E2E: offline reorganize round-trip (year-month↔month, byte-identical) + copy
  refuses an archive with an unfinished reorganize (exit 2). Offline structural guard test (engine holds no
  `IPhoneClient`). Fast lane **293** `GetAndSee.Tests` + **2** `SafetyTests` green; **E2E 5** green; format
  clean; Release build `0/0` (`TreatWarningsAsErrors`); `ReadOnlyContractTests` green.

### Phase 7 — layer-1 self-review + fixes
- **Done.** Independent reviewer (find-problems framing) → **PASS-WITH-NITS, 0 blockers / 0 majors**; verified
  move-integrity, crash-resume, clobber-safety, the required-param change, the marker lifecycle, and offline.
  Report: `docs/review/sprint-4.1-review.md`; profile updated. **Fixed all 4 findings:**
  - **[MINOR]** an `unsorted/` file could move out on a later run under a device-clock-skew edge (stored raw
    EXIF + a `now+1day` sanity bound) → `Reorganizer.Plan` now keeps any file whose current `dest_path` is
    under `unsorted/` put, respecting copy's original null-date decision (+ regression test).
  - **[MINOR]** the `Failed`/clobber-guard branch was untested → added a foreign-file-at-target test (no
    overwrite, row untouched, marker stays set, exit 1, re-run completes).
  - **[NIT]** `ORDER BY source_path` → `ORDER BY source_path, source_size` (provably total = deterministic).
  - **[NIT]** empty-dir cleanup now name-protects `unsorted\` (matches the plan's guardrail).

## Bugs / Issues Found

- **Self-review [MINOR]: reorganize placement was time-dependent for `unsorted/` files.** A future-dated file
  `copy` binned as `unsorted` (its date exceeded the `now+1day` sanity bound at copy time) could later
  re-resolve as sane and move out of `unsorted/`. Data-safe (atomic move, journal accurate) but a spec gap.
  **Fixed:** reorganize treats a file already under `unsorted/` as staying put.
- **Self-review [MINOR]: the per-file failure / clobber-guard branch had no test.** **Fixed:** added a
  foreign-file-at-target test proving no clobber, no dropped row, marker retained, exit 1, clean re-run.
- **Self-review [NIT]×2:** non-total `ORDER BY` and emptiness-only (not name) protection of `unsorted\`. Fixed.

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
