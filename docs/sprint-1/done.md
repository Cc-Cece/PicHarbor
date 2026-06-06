# Sprint 1 (Phases 1–5) — Done

**Branch:** `feature/sprint-1` · **PR:** _sprint-1: core pipeline (phases 1–5)_
**Team:** Nova (app), Sage (systems), Kira (UX copy). QA gate: Ivy's `ReadOnlyContractTests`.

Phase 0 (CI, issue/PR templates, labels, `.gitignore`, LICENSE, README) shipped earlier (PR #1/#6)
and is not part of this PR.

---

## What was built

A working, read-only iPhone → PC media copier on **.NET 10 / C# 14**.

| Area | Type(s) | Notes |
|------|---------|-------|
| Solution | `get-and-see.sln` (classic `.sln`), `Directory.Build.props`, `.editorconfig` | LangVersion 14, nullable, **warnings-as-errors**. |
| Device (read-only) | `IPhoneClient`, `AfcIPhoneClient`, `AfcReadStream`, `DeviceInfo`, `RemoteFile`, `RemoteFileInfo` | Binds only the AFC/lockdown **read** path of `iMobileDevice-net 1.3.17`. |
| Enumerate | `DcimEnumerator` | Recursive `/DCIM/` walk → `RemoteFile`. |
| Organize | `DateFolderOrganizer`, `ExifMetadataExtractor`, `IMediaMetadataExtractor`, `MediaMetadata` | `YYYY/YYYY-MM`, EXIF→mtime→`unsorted`, R16 sanity, filename sanitize. |
| Journal | `TransferJournal`, `FileState`, `JournalCounts`, `ManifestEntry` | `<dest>/get-and-see.db` (visible, WAL) + `manifest` view; identity = path+size (R17). |
| Copy | `FileCopier`, `CopyResult`, `CopyStatus` | Atomic staging→fsync→verify→move; collision `_2/_3`; resumable. |
| Pre-flight | `PreflightChecks` | 27015 probe (R21), writable dest (R15), free space (R4). |
| Summary | `SummaryWriter`, `RunStats` | `<dest>/summary.txt` (Session-3 format). |
| Errors / util | `DeviceException`, `PreflightException`, `ByteSize` | User-facing messages. |
| CLI | `Program`, `Commands/CopyCommand` | `copy --dest -d --dry-run`; per-file progress; run summary; Ctrl+C → resumable. |
| Tests | `GetAndSee.SafetyTests` (2), `GetAndSee.Tests` (34) | Mono.Cecil contract scan + unit tests. |

## Safety contract (the non-negotiable)

`ReadOnlyContractTests` (Mono.Cecil IL scan of compiled `GetAndSee.Core`) **fails the build** if:
1. any blocklisted AFC/lockdown mutator is referenced (decision doc §5.5), or
2. `afc_file_open` is ever called in a write/append mode (detected via IL constant analysis, since
   enum constants are inlined and invisible to plain reflection).

`AfcIPhoneClient` uses only `FopenRdonly`. There is no `--delete`, `--move`, or `--cleanup` flag.

## Verification (local)

- `dotnet build -c Release` → **0 errors, 0 warnings**.
- `dotnet test -c Release` → **36 passed** (34 unit + 2 safety, incl. `ReadOnlyContractTests`).
- `dotnet format --verify-no-changes` → clean (matches the CI gate).
- `get-and-see --help` and `get-and-see copy --help` render correctly.

## Not done in this PR (by design — later sprints)

- **On-device end-to-end run** against a real iPhone — needs hardware; QA (Ivy) to verify the
  device-dependent acceptance criteria (live `copy`, `sqlite3 … "SELECT COUNT(*) FROM manifest"`,
  resume, Ctrl+C). All logic is unit-tested against a mocked device.
- Spectre live dashboard, `status` subcommand, `devices`/`runs` tables, Live-Photo pair detection — **Sprint 2**.
- Long-path `\\?\`, `--verify-hash`, large-file stress, release workflow, user docs — **Sprint 3**.

## Manual setup / runtime prerequisites

- .NET 10 SDK; Apple driver service (Apple Devices app **opened once**, or iTunes) listening on
  `127.0.0.1:27015`; iPhone unlocked + "Trust This Computer".
- Branch protection on `main` is **discipline-only** (private free-tier repo) — do not push to `main`.

## Design decisions worth knowing

- `IPhoneClient` is an **interface** (read-only surface + mockable); `AfcIPhoneClient` is the impl.
- Copy stages bytes in `<dest>/.get-and-see-tmp/*.partial`, then atomically moves into the date
  folder **after** EXIF is read and size verified — the final path never sees a partial file.
- `afc_file_open` handle and `afc_file_read` bytesRead are **`ref`** params in this binding.
- `dotnet new sln` defaults to `.slnx`; we use a classic `.sln` so the CI guard (`Test-Path *.sln`)
  actually runs build/test instead of silently passing.
