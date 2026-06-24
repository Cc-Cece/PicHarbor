# Sprint 4 — done (handoff)

**Branch:** `feature/sprint-4` (off `main`) · **PR:** _(opened below — dev done, STOP at the producer gate)_
**Status:** Dev complete + self-reviewed. **Not merged.** Next: producer independent gate → QA (light hardware).

## What was built

Three things — flat `YYYY-MM` as the new default layout, a per-archive recorded scheme, and a read-only
`search` command.

1. **`--organize-by {month | year-month | year | flat}` on `copy`, default flipped to flat `month`.**
   `OrganizeScheme` enum + `OrganizeSchemes` token helper. `DateFolderOrganizer.GetRelativeDestination`
   now takes the scheme (pure path function): `month → YYYY-MM\name`, `year-month → YYYY\YYYY-MM\name`
   (the old v1.0 layout), `year → YYYY\name`, `flat → name`. `unsorted\` is identical under every scheme;
   a shared capture date co-locates a Live Photo pair under every scheme.
2. **Per-archive recorded scheme (the migration-safety crux).** Journal schema **v2 → v3**: a `settings`
   table with `organize_scheme`; typed `GetOrganizeScheme()` / `SetOrganizeScheme()`. The **v2→v3
   migration stamps `organize_scheme = 'year-month'` for any archive that already holds files**, so an
   existing v1.0/v2 nested archive resumes **byte-stable** (no re-shuffle, no re-copy). A brand-new
   archive records the chosen/default scheme on first copy. `OrganizeSchemeResolver` decides: recorded
   wins; an explicit conflicting `--organize-by` **warns and is ignored**; a defaulted flag silently
   yields. (`reorganize` to actually change a layout is **Sprint 4.1**, not here.)
3. **`search` command (read-only, no device).** `Core/Search/` (`MediaType` + `MediaTypeClassifier`,
   `MediaSearchCriteria`, `MediaSearchHit`, `MediaSearch.Find`) + `TransferJournal.ReadSearchRows()`.
   `search --dest [--from --to --type --camera --min-size --max-size --has-gps --open]` opens the journal
   **`OpenReadOnly`**, prints a Spectre table (path, date, size, type) + a count. `--open` reveals the
   matches' distinct containing folders via an injectable `IFolderOpener` seam (`ExplorerFolderOpener`,
   Windows-guarded), capped at 10. **No device connection; `ReadOnlyContractTests` green.**

## Self-review (PROJECT_BRIEF §13.6, layer 1) — summary

Ran the `code-review` skill on the diff (fresh independent subagent, find-problems framing, extra weight on
the v2→v3 migration, explicit-vs-defaulted logic, and `search` read-only). **Verdict: PASS-WITH-NITS, 0
blockers / 0 majors.** Resolved before the PR:

- **[MINOR] dry-run preview lied for pre-v3 archives** — `--dry-run`'s read-only peek didn't migrate, so a
  v1.0/v2 nested archive previewed flat-`month` while the real run stamps + copies `year-month`. **Fixed:**
  `TryReadRecordedScheme` now mirrors the migration (a pre-v3 archive with files previews `year-month`);
  made `internal` and tested over a `DowngradeToV2` fixture.
- **[NIT] stale `FileCopier` pipeline doc** (`YYYY/YYYY-MM`) → now references the organize scheme.
- **[NIT] new `var`** → converted the new **`src`** code to explicit types. (New **test** `var` is left to
  the dedicated #23 explicit-types reformat — it matches the test suite's pervasive style and the review
  profile defers it; flagged here for transparency.)
- **[NIT] `--open` >10-folder cap untested** → added a cap test.
- **[NIT] `--open` long-path** — deliberately **not** changed: `explorer.exe` cannot open a `\\?\` path, so
  routing through `LongPath.ToExtended` would make `--open` worse; best-effort no-op on a >260 folder is
  correct.

Verified fine and called out: read-only contract intact, the v2-fixture byte-stability proven end-to-end
(0 copied / all skipped / byte-identical / scheme stays `year-month`), SQL parameterized, `--open` has no
injection surface, EUII not newly logged/transmitted/committed, copy hot path resolves the scheme once.

## What is NOT in this sprint (by design)

- **`reorganize`** (migrate an existing archive between layouts — the first op that moves files on the PC)
  → **Sprint 4.1**, its own atomic/resumable design + QA. Existing archives keep working untouched until then.
- **TUI / wizard** → Sprint 5 (`docs/brainstorm/tui-experience.md`).
- **Human-friendly `--min/max-size`** (e.g. `100MB`) — sizes are bytes today; possible follow-up.

## Tests / validation (mirrors CI)

- `dotnet format --verify-no-changes` clean · `dotnet build -c Release` **0/0** (TreatWarningsAsErrors).
- **Fast lane: 261 `GetAndSee.Tests` + 2 `SafetyTests`** green; **E2E lane: 3** green → **266 total**.
- `ReadOnlyContractTests` green; `search` makes no device call. `copy --help` shows
  `--organize-by … [default: month]`; `search --help` shows the full filter set.
- The v2-fixture back-compat test was written first and proves byte-stable resume under the new default.

## Files (high level)

- **New src:** `Core/Organize/OrganizeScheme.cs`, `Core/Organize/OrganizeSchemeResolver.cs`,
  `Core/Search/MediaType.cs`, `Core/Search/MediaSearch.cs`, `Cli/Commands/SearchCommand.cs`,
  `Cli/Commands/FolderOpener.cs`.
- **Changed src:** `Core/Organize/DateFolderOrganizer.cs` (scheme param), `Core/Journal/TransferJournal.cs`
  (v3 + settings + `ReadSearchRows`), `Core/Transfer/FileCopier.cs` (scheme field), `Cli/Commands/CopyCommand.cs`
  (`--organize-by` + resolution + dry-run preview), `Cli/Program.cs` (wire `search`).
- **Tests:** new resolver / journal-v3 / copy-command-parse / media-classifier / media-search /
  search-command / test doubles (`RecordingFolderOpener`, `JournalFixtures`); per-scheme + v2-fixture
  additions to the organizer, copier (long-path), and harness suites; E2E expected layout → flat `month`.
- **Docs:** `README.md` (organize-by + search), `docs/user/manifest-schema.md` (v3 / `settings` /
  migration note), `docs/sprint-4/progress.md` + this file.

## For the producer gate (where to look hardest)

1. **v2→v3 back-compat migration** — does an existing v1.0 nested archive resume byte-stable (no
   re-shuffle / no duplicate)? Crux test: `CopyPipelineHarnessTests.A_v2_nested_archive_resumes_byte_stable…`
   + `JournalSchemaV3Tests.V2_archive_with_existing_files_is_stamped_year_month_and_stays_byte_stable`.
2. **Explicit-vs-defaulted** `--organize-by` (warn + keep recorded vs silent yield) — `OrganizeSchemeResolverTests`,
   `CopyCommandTests`, and the harness explicit-conflict test.
3. **`search` truly read-only** (no device call, no schema change) — `SearchCommandTests.Search_does_not_modify_the_database`.
4. **Long-path per scheme** — `FileCopierTests.Copies_into_a_long_destination_path_under_every_scheme`.
5. **Design call to confirm:** `FileCopier.organizeScheme` is optional defaulting to `YearMonth` (mechanism
   vs policy; the one prod caller always passes explicitly; the default is the byte-stable-safe direction).

## Manual setup needed

None. No new dependencies, no CI changes, no secrets. `reorganize` (Sprint 4.1) is the next planned step.
