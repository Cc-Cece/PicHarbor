# Sprint 1 — Progress (Phases 1–5)

Branch: `feature/sprint-1`. Dev team: Nova (app), Sage (systems), Kira (UX copy).
Phase 0 (CI, templates, labels, gitignore) was merged separately (PR #1/#6) — not repeated here.

Update this file after every phase (PROJECT_BRIEF §12).

---

## Phase 1 — Foundation (Tasks 1–4) ✅

**Built:**
- Solution `get-and-see.sln` with four projects:
  - `src/GetAndSee.Core` — class library (net10.0), `GenerateDocumentationFile` on.
  - `src/GetAndSee.Cli` — exe (net10.0), `AssemblyName=get-and-see`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` so the native AFC DLLs land next to the app (decision doc §5.2). Placeholder `Program.cs`; real CLI wiring is Phase 4.
  - `tests/GetAndSee.Tests` — xUnit v3 + Shouldly + NSubstitute.
  - `tests/GetAndSee.SafetyTests` — xUnit v3 + Shouldly + Mono.Cecil.
- `Directory.Build.props`: `LangVersion 14`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `InvariantGlobalization true`, and `NoWarn=NETSDK1206` (cosmetic stale-RID warning from the 2021 library — R20).
- `.editorconfig` (file-scoped namespaces, System-first usings, 4-space C#).
- **AFC integration (Task 2):** `iMobileDevice-net 1.3.17` referenced in Core. Bound **only** the read path:
  `idevice_get_device_list`, `idevice_new`, `lockdownd_client_new_with_handshake`,
  `lockdownd_get_value` (read), `lockdownd_start_service`, `afc_client_new`,
  `afc_read_directory`, `afc_get_file_info`, `afc_file_open` (FopenRdonly **only**),
  `afc_file_read`, `afc_file_close`, `plist_get_string_val`.
- **`IPhoneClient` (Task 3):** read-only interface — `ConnectAsync`, `ListDirectoryAsync`,
  `GetFileInfoAsync`, `OpenReadAsync` — plus `AfcIPhoneClient` implementation and a forward-only
  `AfcReadStream`. XML doc comments carry the read-only contract. No write/delete/rename/truncate
  symbol referenced anywhere in Core.
- **`ReadOnlyContractTests` (Task 4, Ivy):** Mono.Cecil IL scan of the compiled `GetAndSee.Core`.
  Two facts: (1) no reference to any blocklisted mutator (decision doc §5.5), and (2) `afc_file_open`
  is never called in a write/append mode (caught via IL constant analysis, since enum constants are
  inlined and invisible to plain reflection). **Fails the build on violation.**

**Verified locally:** `dotnet build -c Release` → 0 errors / 0 warnings. `dotnet test` → 3 passed
(1 unit + 2 safety). `dotnet format --verify-no-changes` → clean (CI format gate).

**Decisions / notes:**
- `IPhoneClient` is modelled as an **interface** (not a concrete class) so unit tests can mock it
  with NSubstitute; `AfcIPhoneClient` is the imobiledevice-net implementation. The read-only contract
  lives on the interface and is enforced against the whole Core assembly.
- `afc_file_open` handle and `afc_file_read` bytesRead are **`ref`** params in this binding (not
  `out`) — discovered by reflecting over the real 1.3.17 assembly before writing code.
- AFC `st_mtime` is nanoseconds since epoch (÷1e6 → ms), per decision doc §3.
- Device name / product type read best-effort via `lockdownd_get_value` for the summary line; never fatal.

## Phase 2 — Enumerate & Plan (Tasks 5–7, 10) ✅

**Built:**
- **`DcimEnumerator` (Task 5):** iterative (stack-based) recursive walk of `/DCIM/` over `IPhoneClient`,
  yielding `RemoteFile { Path, Size, ModifiedAt }` for every regular file; descends into `NNNAPPLE`
  buckets. Async stream with cancellation.
- **EXIF + organizer (Task 6):** `IMediaMetadataExtractor` / `ExifMetadataExtractor` (MetadataExtractor)
  reads `DateTimeOriginal`, GPS, and camera make/model — never throws (returns `MediaMetadata.Empty`
  for screenshots / corrupt / unsupported). `DateFolderOrganizer` maps to `YYYY\YYYY-MM\<name>` with
  fallback EXIF → mtime → `unsorted\`, date sanity bound `[1990, now+1day]` (R16, injectable
  `TimeProvider`), and filename sanitization against `Path.GetInvalidFileNameChars()` (§9.3).
- **Journal + manifest (Task 7):** `TransferJournal` at `<dest>/get-and-see.db` (visible at root, WAL).
  `files` table per the plan schema; `UNIQUE(source_path, source_size)` identity (R17); `manifest`
  view exposing user-facing columns of `state='done'` rows. State transitions: `EnsurePending`,
  `MarkInProgress`, `MarkDone` (+metadata), `MarkFailed`; plus `GetUsedDestPaths` (collision support)
  and `CountByState`. All SQL parameterized (no injection).
- **Pre-flight (Task 10):** `PreflightChecks` — `EnsureDriverServiceReachableAsync` probes
  `127.0.0.1:27015` and emits the exact R21 copy *"iPhone driver service not running — open the Apple
  Devices app once, or install iTunes."*; `EnsureDestinationWritable` (probe file, R15);
  `EnsureSufficientFreeSpace` (estimate × 1.05, R4). `ByteSize.Humanize` shared util.
- Typed `PreflightException` (user-facing) alongside `DeviceException`.

**Verified locally:** build 0/0, 3 tests still green, **safety contract intact** (new Core code uses
only read APIs + SQLite + MetadataExtractor), `dotnet format` clean.

**Notes:** `GeoLocation` is a nullable struct in MetadataExtractor — unwrapped via pattern match.

## Phase 3 — Core Copy (Tasks 8–9) ✅

**Built:**
- **`FileCopier` (Task 8, R1/R7/R10):** per-file atomic pipeline — skip if journal `done`; mark
  `in_progress`; stream the AFC read into a staging `.partial` (1 MB buffered, byte-counted); fsync
  (`FlushAsync` + `Flush(true)`); **verify byte count == AFC size** (mismatch ⇒ fail, never publish);
  extract EXIF from the local copy; resolve collision; **atomic `File.Move`** into the date folder;
  mark `done` with metadata. On failure the staging file is removed and the row marked `failed`;
  on cancellation it stays `in_progress` (resumable) and rethrows.
- **Collision handling (Task 9, R5):** `ResolveUniqueRelativePath` appends `_2`, `_3`, … until the
  relative path is free vs. the journal's used paths (seeded once) + the filesystem + this run's
  assignments. **Never overwrites.**
- `CopyResult` / `CopyStatus` (Copied / Skipped / Failed) for honest per-file reporting.
- `CleanStaging()` clears orphaned `.partial` files from a prior interrupted run.

**Design refinement (noted):** the brief sketched streaming to `<final>.partial` then renaming, but
the final folder depends on EXIF date, which needs the file's bytes. So bytes land in a staging
`.partial` under `<dest>/.get-and-see-tmp/` and are moved into `YYYY\YYYY-MM\` only after they are
complete + verified. Same safety invariant — **the final path never sees a partial file** — and the
move is atomic (same volume). `*.partial` is git-ignored.

**Verified locally:** build 0/0, 3 tests green, safety contract intact, format clean.

## Phase 4 — CLI & Output (Tasks 11–13) ✅

**Built:**
- **`copy` subcommand (Task 11):** `System.CommandLine 2.0.8` wiring — `--dest`/`-d` (required) and
  `--dry-run`. Orchestrates: pre-flight 27015 → connect read-only → enumerate `/DCIM/` →
  (dry-run: plan + print, no AFC read streams, no writes) or (real: writable + free-space checks →
  open journal → `EnsurePending` all → clean staging → per-file atomic copy). Ctrl+C is bridged to a
  cancellation token in `Program.Main`, so an interrupted run unwinds and stays resumable.
- **Per-file progress (Task 12, Kira):** exact plain-text line
  `[done] 12,345/38,412  3.2 GB/397.2 GB  → 2024\2024-08\IMG_1234.HEIC`; `[skip]` for already-done,
  `[fail]` (red) for failures.
- **End-of-run summary + `summary.txt` (Task 13):** console summary (enumerated / copied / skipped /
  failed / elapsed / avg MB/s + manifest & summary paths). `SummaryWriter` writes `<dest>/summary.txt`
  in the Session-3 format (totals, per-type breakdown HEIC/JPG/MOV/screenshots/other, date range,
  device line, last-run line). **Exit code is non-zero if any file failed.**
- Minimal Spectre.Console use (rules + colored pass/fail/error via `MarkupLineInterpolated`, which
  escapes dynamic content); per-file lines stay plain `Console.WriteLine` to avoid markup injection.

**Verified locally:** build 0/0, 3 tests green, safety intact, format clean. `--help` and
`copy --help` render correctly (root + `copy` with required `--dest`, `--dry-run`).

**Notes:** `AssemblyName=get-and-see` → the produced exe and root command are both `get-and-see`.
Live-Photo pair counts, `devices`/`runs` tables are Sprint 2 — `summary.txt` omits those lines for now.

## Phase 5 — Tests & Handoff (Tasks 14, 14b, 20) ✅

**Built:**
- **Unit tests (Task 14):** 34 tests in `GetAndSee.Tests` —
  - `DateFolderOrganizerTests`: EXIF date, mtime fallback, unsorted, R16 future/prehistoric rejection, filename sanitize, name extraction.
  - `TransferJournalTests`: pending→in_progress→done, R17 identity (path+size), idempotent EnsurePending, manifest = done-only, used-dest-paths, counts.
  - `FileCopierTests` (mocked `IPhoneClient` + real journal/temp dest): copy into date folder, **size-mismatch fails & never publishes**, skip-done, **collision `_2` without overwrite**.
  - `DcimEnumeratorTests` (mocked client): recursive walk yields files only.
  - `PreflightChecksTests`: writable pass/fail, free-space pass/fail.
  - `SummaryWriterTests`: Session-3 format, device-line omission.
  - `ByteSizeTests`.
- **XML doc comments (Task 14b):** every public type/method in `GetAndSee.Core` is documented
  (`GenerateDocumentationFile=true` makes a missing one a build error). The read-only contract is
  documented on `IPhoneClient`.
- **Handoff (Task 20):** this file, `done.md`, and PROJECT_BRIEF §7/§8.

**Verified locally:** build 0 errors / 0 warnings; **36 tests pass** (34 unit + 2 safety, incl.
`ReadOnlyContractTests`); `dotnet format --verify-no-changes` clean.

**Threaded `TestContext.Current.CancellationToken`** through async test calls (xUnit v3 analyzer
xUnit1051 under warnings-as-errors).

---

## Bugs / Issues Found

_None._ No blockers surfaced during Phases 1–5. On-device end-to-end verification (real iPhone copy)
is left for QA (Ivy) with hardware — all logic here is unit-tested against a mocked device.
