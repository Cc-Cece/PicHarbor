# Sprint 3 — Progress

> Live tracker. Updated per phase (PROJECT_BRIEF §12). Branch: `feature/sprint-3`.
> Plan: [`plan.md`](plan.md) · Consilium: [`../brainstorm/sprint-3-consilium.md`](../brainstorm/sprint-3-consilium.md)

## Status by track

| Track | Item | Status |
|-------|------|--------|
| A engine | #1 Long-path `\\?\` (R6) + synthetic >260-char test | ✅ Done |
| A engine | #2 `--verify-hash` (R12) — opt-in SHA-256 during copy → manifest | ✅ Done |
| A engine | #3 #17 polish (StatusCommandTests, scratch zero, sync `Read()`, EUII rename) | ✅ Done |
| A engine | #4 Large-file correctness (multi-chunk unit; full GB on #21) | ✅ Done (unit) |
| A engine | #14 XML doc comments on new public API | ✅ Done |
| B release | #5 `release.yml` (tag `v*` → single-file EXE → Release) | ✅ Done (dormant) |
| B release | #6 Published-EXE `--help` smoke in CI | ✅ Done |
| B release | #7 gitleaks secret-scan in CI | ✅ Done |
| B release | #8 LICENSE polish + public-repo metadata | ✅ Done |
| C docs | #9 README (+ "What this does NOT do", redacted screenshot) | ⬜ |
| C docs | #10 troubleshooting (real error strings) | ⬜ |
| C docs | #11 manifest-schema | ⬜ |
| C docs | #12 release-notes template + v1.0 notes | ⬜ |
| close | #15 progress/done/handoff + PR | ⬜ |

## Phase log

### Phase 1 — Track A engine (Sage + Nova) ✅
Commit: `sprint-3: engine hardening — long-path, --verify-hash, #17 polish`

- **#1 Long-path (R6):** new `GetAndSee.Core/Util/LongPath.cs` — `ToExtended()` prefixes a fully-qualified
  Windows path with `\\?\` (or `\\?\UNC\`). `FileCopier` normalizes `destinationRoot` once in the
  constructor, so every derived path (staging, final, collision checks) exceeds `MAX_PATH` for free.
  Works regardless of the machine's `LongPathsEnabled` setting. **Read-only:** PC-side paths only,
  the device/AFC read path is untouched. Synthetic >260-char copy test added (200-char filename).
- **#2 `--verify-hash` (R12):** opt-in SHA-256 via `IncrementalHash`, hashed in the **same pass** that
  streams the bytes (no extra device read, no extra disk read → stays read-only). Written to the
  existing `sha256` manifest column via `MarkDone`. **Zero cost when the flag is absent** — no hasher
  is created and the hot path is byte-for-byte identical to Sprint 2. `CopyResult.Sha256` added.
  CLI `--verify-hash` option + `--help` copy. Tests: known-vector match, single-byte change → different
  digest, default path records no hash.
- **#3 #17 polish:**
  - `StatusCommandTests` — missing-db → exit 2 (and does **not** create a db); existing archive → exit 0
    + prints summary sections. `StatusCommand.Run` made `internal` for the test (Cli already has
    `InternalsVisibleTo`). Isolated in a non-parallel `console` collection to avoid `Console.SetOut` races.
  - `WatchdogReadStream` scratch buffer zeroed after each read (`CryptographicOperations.ZeroMemory`) so
    no device bytes linger in the long-lived buffer; sync `Read()` now **throws** `NotSupportedException`
    (it bypassed the watchdog timeout) instead of silently falling through. Tests for both.
  - **EUII:** real first name `"Denis's iPhone"` → synthetic `"Sample iPhone"` in `RemoteModels.cs`
    (XML doc), `SummaryWriterTests.cs`, and `JournalSchemaV2Tests.cs` (the plan named two files; the
    third had the same fixture name — all three renamed).
- **#4 Large-file:** multi-chunk (5 MiB + change) copy test exercises the same streaming loop a multi-GB
  file uses, verifying size + SHA-256. True multi-GB ProRes is QA #21 on hardware.
- **#14 XML docs:** `LongPath` documented; new `verifyHash` param, `CopyResult.Sha256`, and
  `StatusCommand.Run` carry `<summary>`/`<param>`.

**Gates:** `dotnet test` green (88 tests: 86 unit + 2 safety). `ReadOnlyContractTests` ✅ — no new
device-write symbol. `dotnet format --verify-no-changes` ✅.

## Bugs / Issues Found
- None so far.

### Phase 2 — Track B release & CI (Dash + Nova) ✅
Commit: `sprint-3: release pipeline, EXE smoke + gitleaks CI gates, metadata`

- **#5 `release.yml`:** triggers on `v*` tag → `dotnet publish` single-file self-contained **win-x64**
  with `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true` (stamps `Version` from the
  tag) → `--help` smoke on the published EXE → SHA-256 (sha256sum-compatible) → `gh release create`
  attaching `get-and-see.exe` + `.sha256`. `permissions: contents: write`, `GITHUB_TOKEN` only. **Lands
  dormant** — no tag is cut here; the tag waits on QA #21 PASS.
- **#6 Published-EXE smoke (`ci.yml` `publish-smoke` job):** every PR builds the same single-file EXE and
  runs `--help` on a clean `windows-latest` runner — proves the native AFC DLLs unpack and the EXE runs
  before any release is trusted. Guarded by `Test-Path *.sln`.
- **#7 gitleaks (`ci.yml` `secret-scan` job, ubuntu-latest):** pinned official gitleaks **8.18.4** binary
  (no third-party action / telemetry / license logic), `detect --source . --no-git --redact --exit-code 1`.
  Pre-validated locally → **no leaks found**.
- **#8 LICENSE + metadata:** LICENSE confirmed MIT © 2026 owner (kept). Added public-repo EXE file
  metadata to `Directory.Build.props` (Product/Company/Description/Copyright/License/RepositoryUrl/Version).

**Validated locally:** single-file publish → 84 MB EXE, `--help` exit 0 (native unpack works). Both
workflow YAMLs structurally checked; release-notes PowerShell dry-run OK. Build green; 88 tests green.

## Decisions / Notes
- gitleaks runs as a **pinned binary** (not the `gitleaks/gitleaks-action`) to avoid the action's
  org-license/telemetry behavior and keep the supply chain pinned + under our control. Scans the working
  tree (`--no-git`) for determinism (history was already scrubbed in a separate, audited procedure).
- The `imobiledevice-net` package bundles **unused osx-x64** native restore tools (`idevicerestore`,
  `libirecovery`) that some Windows AV flags as PUA. They live only in `bin/` (gitignored, never
  committed) and are **not** in the win-x64 single-file EXE we ship — observation only, R20-adjacent.
- Long-path mechanism is the `\\?\` prefix in code (not `<LongPathsEnabled>` manifest) because the prefix
  works on any runner regardless of registry/OS config — deterministic for CI.
- `--verify-hash` ships **during-copy** hashing only this sprint; **verify-only re-check** (compare an
  existing archive) is the Sprint 4 fast-follow (per consilium cut line).
- Lowercase hex for the `sha256` column (conventional for sha256 manifests; matches `sha256sum`).
