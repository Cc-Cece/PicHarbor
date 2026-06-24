# Sprint 4 — progress

**Branch:** `feature/sprint-4` (off `main`) · **Owner:** Dev (Nova — CLI/organize; Sage — journal)
**Status:** Dev complete — self-review next, then ONE PR, then STOP for the producer gate.

> Live progress tracker (mid-sprint recovery). Decisions made under ambiguity are logged here.

## What shipped (the three pieces)

### 1. `--organize-by` presets (default flipped to flat `month`)
- New `OrganizeScheme` enum + `OrganizeSchemes` token helper (`Core/Organize/OrganizeScheme.cs`):
  `month` (flat `YYYY-MM`, **new default**), `year-month` (nested, the old v1.0 layout), `year`, `flat`.
- `DateFolderOrganizer.GetRelativeDestination` now takes the scheme — a **pure** path function. `unsorted\`
  is identical under every scheme; a shared capture date co-locates a Live Photo pair under every scheme.
- `copy --organize-by <month|year-month|year|flat>` (default `month`), validated by `AcceptOnlyFromAmong`.

### 2. Per-archive recorded scheme (the migration-safety crux)
- `TransferJournal.SchemaVersion` 2 → **3**; new `settings (key, value)` table; typed
  `GetOrganizeScheme()` / `SetOrganizeScheme()` accessors (no generic KV public API — one real key today).
- `Migrate()` is now stepwise. The **v2→v3 step stamps `organize_scheme = 'year-month'` only when the
  `files` table already has rows** (a pre-existing archive) — so existing nested archives keep resolving to
  the same paths and resume **byte-stable**. A brand-new database (empty `files` at migration time) is left
  unstamped, so the first `copy` records the chosen/default scheme.
- `OrganizeSchemeResolver.Resolve(recorded, requested, isExplicit)` — pure decision: recorded wins; an
  explicit conflicting flag **warns + keeps** recorded; a defaulted flag silently yields; a brand-new
  archive records the request. `copy` resolves once after opening the journal (uses the recorded value, not
  a row count, so it's correct regardless of `EnsurePending` ordering). Dry-run previews the effective
  layout via a **read-only** peek.

### 3. `search` command (read-only, no device)
- `Core/Search/`: `MediaType` + `MediaTypeClassifier` (extension-only; `.png` ⇒ screenshot since iOS
  screenshots are PNG while the camera shoots HEIC/JPG), `MediaSearchCriteria`, `MediaSearchHit`,
  `MediaSearch.Find` (pure filter + order). `TransferJournal.ReadSearchRows()` projects the `manifest` view.
- `search --dest [--from --to --type --camera --min-size --max-size --has-gps --open]`. Opens the journal
  **`OpenReadOnly`** (no schema create/alter), prints a Spectre table (path, date, size, type) + a count.
- `--open` reveals the matches' distinct containing folders via an **injectable `IFolderOpener` seam**
  (`ExplorerFolderOpener`, Windows-guarded, best-effort) — unit-testable without launching Explorer; capped
  at `MaxFoldersToOpen = 10` so a broad match never sprays windows. **No device connection.**

## Decisions made under ambiguity (logged per project rules)

- **`FileCopier.organizeScheme` is an *optional* parameter defaulting to `OrganizeScheme.YearMonth`** (the
  historical layout), not required. Layout *policy* lives at the CLI/resolver (a new archive's product
  default is `Month`); `FileCopier` is the *mechanism* and the one production caller (`CopyCommand`) always
  passes the resolved effective scheme explicitly. Rationale: making it required would have churned ~14
  copier-construction sites across the disconnect/escape-hatch tests for zero behavioural gain, and the
  historical default keeps every pre-Sprint-4 test byte-identical. Documented in the `FileCopier` ctor XML.
- **`search` date filters use whole-day bounds:** `--from D` ⇒ `D 00:00`, `--to D` ⇒ `D 23:59:59.9999999`
  (inclusive of the whole day). `--min/max-size` are **bytes** (unambiguous + testable; a human-size parser
  is a possible follow-up). Undated files sort **last**.
- **`search` exit codes** mirror `status`: `0` on success (an empty result is success, not an error), `2`
  when no archive exists at `--dest`.
- The in-process `CopyPipelineHarness` now **resolves the scheme from the journal exactly as `copy` does**
  (so the v2-fixture resume is exercised end-to-end); its `requestedScheme` defaults to `YearMonth` so the
  pre-existing 15 harness callers keep their nested-layout assertions.

## Bugs / issues found

- **`MediaSearch.Find` ordering bug (found + fixed by the seeded-manifest tests):** the first cut used
  `Nullable.Compare(CapturedAt…)`, which sorts `null` (undated) **first**; the intended order is undated
  **last**. Replaced with an explicit `CompareCaptured` (nulls last). Caught by
  `Filters_by_max_size` and `No_filters_returns_all_ordered…` before any commit.
- **System.CommandLine 2.0.8 explicit-vs-defaulted detection** verified to be
  `parseResult.GetResult(option) is { Implicit: false }` (built + unit-tested in `CopyCommandTests`).

## Tests (write the v2-fixture back-compat test first)

- **Organizer** (`DateFolderOrganizerTests`): per-scheme path (4), `unsorted` unchanged per scheme (4),
  Live-Photo co-location per scheme (4), exif/mtime/unsorted/sanitize.
- **Resolver** (`OrganizeSchemeResolverTests`): brand-new records; defaulted yields silently; explicit
  conflict warns + keeps + no-record; matching explicit silent.
- **Journal v3** (`JournalSchemaV3Tests`): brand-new not stamped; set/get round-trip + overwrite;
  `user_version = 3`; **v2-fixture with done rows migrates → stamped `year-month` → same path byte-stable**;
  `OpenReadOnly` on a pre-v3 db returns null + creates no `settings` table.
- **CLI parse** (`CopyCommandTests`): `--organize-by` default `month` + implicit; explicit when passed;
  rejects unknown.
- **Search** (`MediaTypeClassifierTests`, `MediaSearchTests`, `SearchCommandTests`): each filter + combos +
  empty over a seeded manifest; `--open` seam targets (distinct folders, absolute, capped); read-only
  (schema snapshot unchanged); no-archive ⇒ exit 2; criteria parse + bad-input rejection.
- **Harness** (`CopyPipelineHarnessTests`): full library byte-identical per scheme + recorded; collision
  `_2/_3` per scheme; **v2-fixture resumes byte-stable under the new `month` default (0 copied, all
  skipped, stays nested)**; explicit conflicting scheme ignored on an existing archive.
- **Long-path** (`FileCopierTests`): copies past `MAX_PATH` under every scheme (even flat).
- **E2E** (`FakeDeviceExeE2ETests`): expected layout updated to flat `month` (the real EXE's new default).

## Validation (mirrors CI)

- `dotnet format --verify-no-changes` — clean.
- `dotnet build -c Release` — **0 warnings / 0 errors** (`TreatWarningsAsErrors`).
- Fast lane: **257 `GetAndSee.Tests` + 2 `SafetyTests`** green; E2E lane **3** green → **262 total**.
- `ReadOnlyContractTests` green (sacred invariant intact); `search` makes no device call.
- `search --help` / `copy --help` render the new options (`--organize-by … [default: month]`).
