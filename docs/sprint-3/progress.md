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
| C docs | #9 README (+ "What this does NOT do", redacted screenshot) | ✅ Done |
| C docs | #10 troubleshooting (real error strings) | ✅ Done |
| C docs | #11 manifest-schema | ✅ Done |
| C docs | #12 release-notes template + v1.0 notes | ✅ Done |
| build | #26 Restrict build to win-x64 (drop foreign native libs, silence Defender FP) | ✅ Done |
| close | #15 progress/done/handoff + PR | ✅ Done |

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
  - **EUII:** the real personal device-name fixture (`<name>'s iPhone`) → synthetic `"Sample iPhone"` in
    `RemoteModels.cs` (XML doc), `SummaryWriterTests.cs`, and `JournalSchemaV2Tests.cs` (the plan named two
    files; the third had the same fixture name — all three renamed).
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

### Phase 3 — Track C docs (Quill + Kira) ✅
Commit: `sprint-3: user docs — README, troubleshooting, manifest-schema, release notes`

Finalized after the engine flags froze (Phase 1), so docs match shipped behavior and exact strings.

- **#9 `README.md`:** full rewrite — overview, prominent **read-only promise**, the **"What this tool does
  NOT do"** section (R19, wording from brief §2), prerequisites (incl. the lazy Apple Devices service),
  install (download + verify checksum, or build from source), full `copy`/`status` usage incl.
  `--verify-hash`, an **exit-code table**, a **redacted text dashboard rendering** + redacted `summary.txt`
  (synthetic `D:\Photos` / `Sample iPhone`), and a Safety section.
- **#10 `docs/user/troubleshooting.md`:** every failure mode with the **exact emitted string** pulled from
  the code — no device (AC-13), driver service / R21 (the #1 ticket), Trust/AC-14, disk-full/AC-15,
  not-writable/R15, watchdog stall/#11 (exit 3), failed files, long paths, Ctrl+C, and `--verify-hash`.
- **#11 `docs/user/manifest-schema.md`:** the `manifest` view + `files`/`devices`/`runs` tables (full
  columns, identity rule, `user_version`), example SQL (counts, by-month, largest, GPS, integrity, run
  history), read-only-open tip, and a privacy note.
- **#12 release notes:** `docs/release-notes-template.md` + `docs/release-notes/v1.0.0.md` (Sprints 1–3
  summary, read-only guarantee, known limitations; dated "pending QA #21 sign-off" since the tag waits).
- **EUII sweep:** repo-wide grep for real names / UDIDs / `C:\Users\` / GPS — clean. All doc examples use
  synthetic/redacted values; redacted the old fixture name in this progress file's own change note too.

### Phase 4 — #26 win-x64 build trim (folded in early, producer request) ✅
Commit: `fix: #26 restrict build to win-x64 (drop foreign native libs, silences Defender FP)`

Folded in before final close at the producer's request (rebased onto `origin/main` first to pick up the
review-profile #26 note). **Root cause** (per `docs/review/review-profile.md`): `GetAndSee.Core` and the
test projects set no RID, so the **portable** build copied the all-platform `imobiledevice-net` native
graph (osx-x64/arm64, maccatalyst, linux-*) into `bin/.../runtimes/`, where Windows Defender heuristically
flags `libirecovery*.dylib` as `Exploit:MacOS/LimeRain.C!MTB` — a **known false positive** (inert macOS
libs, never committed, not in the shipped EXE).

- **`Directory.Build.props`:** added restore-time pin `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`
  (plural) — shared, so Core + all test projects inherit it; pins the **restore graph** to win-x64.
- **Test projects (`GetAndSee.Tests`, `GetAndSee.SafetyTests`):** added singular
  `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`. **Why both were needed:** plural `<RuntimeIdentifiers>`
  constrains *restore*, but a **runnable** (`OutputType=Exe`) project built RID-agnostic still copies the
  *full* native graph into its output. Pruning a runnable project's `runtimes/` requires a *singular* RID —
  which is exactly why `GetAndSee.Cli` (already singular win-x64) was clean while the two test hosts were
  not. The test hosts are runnable, so they get the same singular pin. Safe: CI runs tests on
  `windows-latest`, the tool is Windows-only, and `ReadOnlyContractTests` locates `Core.dll` via
  `Assembly.Location` (its own output dir), unaffected by the RID subfolder.
- **`GetAndSee.Cli` unchanged** — keeps its singular `<RuntimeIdentifier>win-x64</>` for the single-file
  release publish.

**Verified (clean rebuild):** `dotnet restore` + `dotnet build -c Release` → **0 warn / 0 err**;
`dotnet test -c Release` → **88 tests green** (86 + 2 safety; `ReadOnlyContractTests` intact);
**no `osx-*` / `linux-*` / `maccatalyst-*` runtimes and no `.dylib`/`.so` under any `bin/`** (RID-specific
builds flatten the win-x64 natives to the output root); all 8 win-x64 AFC DLLs present (functionality
intact); `dotnet format --verify-no-changes` ✅. Did **not** need `dotnet nuget locals --clear` — with the
pin the build no longer references the quarantined osx dylib. **Read-only contract unaffected** — a
build/packaging change only; no device symbol touched.

## Decisions / Notes
- README's dashboard "screenshot" is a **redacted text rendering** (synthetic `D:\Photos`, `Sample
  iPhone`, no real UDID/GPS/user path), not a binary PNG — honest, and avoids committing an image that
  could carry hidden EUII. A real captured screenshot needs hardware (QA), out of scope for dev.
- gitleaks runs as a **pinned binary** (not the `gitleaks/gitleaks-action`) to avoid the action's
  org-license/telemetry behavior and keep the supply chain pinned + under our control. Scans the working
  tree (`--no-git`) for determinism (history was already scrubbed in a separate, audited procedure).
- **Defender FP on foreign native libs — RESOLVED by #26 (Phase 4).** `imobiledevice-net` bundles
  all-platform native restore tools (`libirecovery*.dylib`, `idevicerestore`); the portable build used to
  copy the osx/linux/maccatalyst ones into `bin/.../runtimes/`, where Defender heuristically flagged them
  (`Exploit:MacOS/LimeRain.C!MTB`) — a known false positive (never committed; not in the shipped win-x64
  EXE). The win-x64 RID trim (Phase 4) stops those from ever landing in `bin/`, so the per-build popup and
  the earlier `MSB4018 GenerateDepsFile … FileNotFoundException` (seen when Defender quarantined a cached
  dylib) no longer reproduce. CI was always unaffected (windows-latest).

## Final validation (Phase 5, close)
- `dotnet format --verify-no-changes` ✅
- `dotnet build -c Release` ✅ (0 warnings, 0 errors)
- `dotnet test -c Release` ✅ — **88 tests** (86 unit + 2 safety; `ReadOnlyContractTests` green)
- Single-file publish ✅ — `get-and-see.exe` (~84 MB), `--help` exit 0 (native unpack works)
- gitleaks working-tree scan ✅ — no leaks found
- **Post-#26 re-validation:** clean Release rebuild after the win-x64 trim → **0/0**, **88 tests green**, no
  `osx-*`/`linux-*`/`maccatalyst-*` runtimes or `.dylib`/`.so` under any `bin/`, all 8 win-x64 AFC DLLs
  present; single-file EXE `--help` exit 0; rebased on `origin/main` (conflict-free). PR opened:
  **"sprint-3: hardening, packaging & release"** (Closes #17) — **STOP** for the producer review gate
  (§13.6) + QA #21.
- Long-path mechanism is the `\\?\` prefix in code (not `<LongPathsEnabled>` manifest) because the prefix
  works on any runner regardless of registry/OS config — deterministic for CI.
- `--verify-hash` ships **during-copy** hashing only this sprint; **verify-only re-check** (compare an
  existing archive) is the Sprint 4 fast-follow (per consilium cut line).
- Lowercase hex for the `sha256` column (conventional for sha256 manifests; matches `sha256sum`).
