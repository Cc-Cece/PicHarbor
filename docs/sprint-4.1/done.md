# Sprint 4.1 — done (handoff)

**Branch:** `feature/sprint-4.1` (off `main`) · **PR:** _(opened below — dev done, STOP at the producer gate)_
**Status:** Dev complete + self-reviewed. **Not merged.** Next: producer independent gate → QA (light hardware
is optional — this sprint is offline/PC-only; the crux is on-disk move integrity, fully covered in CI).

## What was built

`reorganize` — the offline command Sprint 4 deferred: migrate an **existing** archive between `--organize-by`
layouts by **moving files on the PC**. It is the first operation in the product that moves/renames files, so
it earns an atomic, resumable, crash-safe design. **Data integrity is sacred: no byte is ever lost,
truncated, or clobbered, and the journal always points at where the file actually is.**

1. **`DateFolderOrganizer` refactor (no `copy` behavior change).** New `GetRelativeDestination(fileName,
   captureDate, scheme)` (pure folder-shape rule) and public `ResolveDate(exif, mtime)` (the R16 trust rule);
   the `RemoteFile` overloads now delegate to them, so copy and reorganize resolve placement identically and
   can never disagree. Extracted the `_2/_3` collision rule into `Organize/CollisionSuffix.Resolve(path,
   isAvailable)`, shared by the copier and the reorganizer (the caller owns the availability predicate).
2. **Journal ops (parameterized SQL, no schema-version bump).** `UpdateDestPath` (single `UPDATE dest_path`,
   row stays `done`), `EnumerateDoneForReorganize` (`WHERE state='done'` `ORDER BY source_path, source_size`,
   parses the stored EXIF wall-clock + UTC mtime into a `ReorganizeEntry`), and the `reorganize_target`
   settings marker `Get/Set/Clear` (a settings k/v — **no** DDL, so no `user_version` bump).
3. **`Reorganizer` engine (`Core/Reorganize/`).** `Plan(target)` → deterministic move list (by
   `source_path, source_size`; new collisions resolved with the shared `_2/_3` rule against the **in-run
   assignment set only**; a file already under `unsorted/` stays put). `Execute(plan)` → set the in-flight
   marker → per-file **atomic same-volume `File.Move`** → `UpdateDestPath` → **crash-reconcile**
   (source-gone + target-present + size-matches ⇒ heal the journal; both-missing or wrong-size ⇒ per-file
   fail, never drop) → bottom-up empty-dir cleanup (never the root, `unsorted\`, `.get-and-see-tmp\`, or a
   non-empty dir) → on a fully clean run, stamp the new scheme **then** clear the marker. Every path via
   `LongPath.ToExtended`. **Zero `IPhoneClient` — offline** (reflection-asserted).
4. **`ReorganizeCommand` (CLI).** Mirrors `SearchCommand`: `--dest` (req), `--organize-by` (req, the target),
   `--dry-run`; opens the journal **writable**; exit codes `0` (ok / no-op), `1` (some files could not move —
   resumable), `2` (no archive / unreadable db), `130` (Ctrl+C). Registered in `Program.cs`; injectable
   `IAnsiConsole` for testing.
5. **Cashed the Sprint 4 gate nit + `copy` guard.** `FileCopier.organizeScheme` is now a **required** ctor
   param (dropped the `= YearMonth` default so every caller threads the resolver's decision). Added the `copy`
   **incomplete-reorganize guard**: on start, if the `reorganize_target` marker is set to a scheme other than
   the recorded one, `copy` refuses with an actionable message (exit `2`) rather than copying new files into a
   half-migrated tree.

## Self-review (PROJECT_BRIEF §13.6, layer 1) — summary

Independent reviewer (fresh context, find-problems framing, extra weight on move-integrity + crash-resume +
the required-param change). **Verdict: PASS-WITH-NITS, 0 blockers / 0 majors.** Verified FINE: move-integrity
(same-volume non-overwrite `File.Move` + single `UpdateDestPath`; only `done` media rows move; cleanup only
deletes provably-empty dirs), crash-resume (heal between the move and the journal update; determinism from the
in-run set), clobber-safety (non-overwrite `File.Move` + the size-checked reconcile), the required-param change
(all call sites; `copy` unchanged), the marker stamp-then-clear lifecycle + the copy guard, offline
(`ReadOnlyContractTests` green; engine holds no `IPhoneClient`), parameterized SQL + no schema bump, long-path,
exit codes, and a side-effect-free dry-run. **All 4 findings fixed before the PR:**

- **[MINOR]** an `unsorted/` file could move out on a later run under a device-clock-skew edge (`copy` stores
  the raw EXIF; the `now + 1 day` sanity bound advances) → `Reorganizer.Plan` now keeps any file whose current
  `dest_path` is under `unsorted/` put, respecting copy's original null-date decision (+ regression test).
- **[MINOR]** the per-file `Failed`/clobber-guard branch was untested → added a foreign-file-at-target test
  (no overwrite, the row is kept `done` at its old path, the marker stays set, exit `1`, and a re-run
  completes once the blocker is removed).
- **[NIT]** `ORDER BY source_path` → `ORDER BY source_path, source_size` (a provably total order = reproducible
  collision suffixes).
- **[NIT]** empty-dir cleanup now name-protects `unsorted\` (matching the plan's named-protected-dirs guardrail).

Report: `docs/review/sprint-4.1-review.md`; the deliberate deviations (in-run collision set; stamp-then-clear
only on a clean run) were confirmed sound and are now recorded as accepted trade-offs in the review profile.

## What is NOT in this sprint (by design)

- **Cross-volume / "reorganize to a different root."** Reorganize is in-place within one archive root by
  definition (keeps every move an atomic rename). Moving an archive to a new drive is a separate idea.
- **TUI / wizard → Sprint 5** (`docs/brainstorm/tui-experience.md`).
- **Human-friendly `--min/max-size`** and `search` date-frame polish remain backlog items.

## Tests / validation (mirrors CI)

- `dotnet format --verify-no-changes` clean · `dotnet build -c Release` **0/0** (`TreatWarningsAsErrors`).
- **Fast lane: 295 `GetAndSee.Tests` + 2 `SafetyTests`** green; **E2E lane: 5** green → **302 total** (was 266).
- `ReadOnlyContractTests` green; the `Reorganizer` makes no device call (structural + reflection assertions).
- The plan's 13 test areas are covered: byte-identity (SHA-256 multiset), crash-resume between the move and
  the journal update, round-trip (incl. through a coarsening scheme), deterministic + interrupt-stable
  collisions, no-op, unsorted-never-moves (+ the clock-skew edge) + Live-Photo co-location, long-path,
  journal accuracy, offline, the copy guard, cleanup, side-effect-free dry-run, and the required-param
  regression — plus a real-`.exe` E2E reorganize round-trip and the clobber/foreign-file branch.

## Files (high level)

- **New src:** `Core/Organize/CollisionSuffix.cs`, `Core/Reorganize/Reorganizer.cs`,
  `Cli/Commands/ReorganizeCommand.cs`.
- **Changed src:** `Core/Organize/DateFolderOrganizer.cs` (new overloads), `Core/Journal/TransferJournal.cs`
  (`UpdateDestPath`, `EnumerateDoneForReorganize`, `reorganize_target` marker, `ReorganizeEntry`),
  `Core/Transfer/FileCopier.cs` (required scheme + shared `CollisionSuffix`), `Cli/Commands/CopyCommand.cs`
  (`IncompleteReorganizeError` guard), `Cli/Program.cs` (register `reorganize`).
- **Tests:** new `Reorganize/ReorganizerTests`, `Journal/ReorganizeJournalTests`, `Cli/ReorganizeCommandTests`;
  copy-guard cases in `Cli/CopyCommandTests`; the reorganize round-trip + copy-refusal in `E2E/…E2ETests`; the
  `FileCopier` required-param call-site updates across the copier/watchdog/escape-hatch test files.
- **Docs:** `README.md` (`reorganize` section + `--organize-by` cross-link), `docs/user/manifest-schema.md`
  (`reorganize_target`), `docs/review/sprint-4.1-review.md`, `docs/review/review-profile.md`,
  `docs/sprint-4.1/progress.md` + this file.

## For the producer gate (where to look hardest)

1. **Move integrity + crash-resume** — `ReorganizerTests`: `Reorganize_preserves_every_byte…`,
   `A_move_interrupted_before_the_journal_update_is_healed_on_resume`,
   `Resuming_after_a_subset_of_moves_finishes_identically_to_a_clean_run`,
   `A_foreign_file_blocking_a_target_fails_that_move_without_clobbering`.
2. **Deterministic collisions under interruption** — `Coarsening_creates_a_deterministic_numeric_suffix…` +
   `A_collided_move_interrupted_before_journaling_heals_to_the_same_suffix`.
3. **The two deliberate deviations** — in-run collision set (not on-disk) for crash-determinism; stamp-then-
   clear only on a clean run. Rationale + safety in `docs/sprint-4.1/progress.md` and the review profile.
4. **Required-param change** — `FileCopier.organizeScheme` required; the one prod caller passes the resolved
   scheme; `copy` behaviour unchanged (copier + E2E lanes green).
5. **Offline** — `ReadOnlyContractTests` + `The_engine_has_no_device_dependency`.

## Manual setup needed

None. No new dependencies, no CI changes, no secrets. `reorganize` is offline (no device). Next planned step:
**Sprint 5 (TUI)**.
