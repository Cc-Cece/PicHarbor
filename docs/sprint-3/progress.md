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
| B release | #5 `release.yml` (tag `v*` → single-file EXE → Release) | ⬜ |
| B release | #6 Published-EXE `--help` smoke in CI | ⬜ |
| B release | #7 gitleaks secret-scan in CI | ⬜ |
| B release | #8 LICENSE polish + public-repo metadata | ⬜ |
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

## Decisions / Notes
- Long-path mechanism is the `\\?\` prefix in code (not `<LongPathsEnabled>` manifest) because the prefix
  works on any runner regardless of registry/OS config — deterministic for CI.
- `--verify-hash` ships **during-copy** hashing only this sprint; **verify-only re-check** (compare an
  existing archive) is the Sprint 4 fast-follow (per consilium cut line).
- Lowercase hex for the `sha256` column (conventional for sha256 manifests; matches `sha256sum`).
