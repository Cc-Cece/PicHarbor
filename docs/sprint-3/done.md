# Sprint 3 — Done (dev handoff)

> Branch: `feature/sprint-3` → PR **"sprint-3: hardening, packaging & release"**.
> Plan: [`plan.md`](plan.md) · Progress: [`progress.md`](progress.md) · Consilium:
> [`../brainstorm/sprint-3-consilium.md`](../brainstorm/sprint-3-consilium.md).
>
> **This is the v1 ship's dev work. It is gated on QA Stage 2 (#21): no `v1.0.0` tag is cut until #21
> signs off PASS.** The release workflow lands **dormant** until then.

## What was built (all 3 dev tracks)

### Track A — Engine hardening (commit `51eb492`)
- **Long-path `\\?\` (R6):** new [`LongPath.ToExtended`](../../src/GetAndSee.Core/Util/LongPath.cs);
  `FileCopier` normalizes `destinationRoot` once so every derived path beats `MAX_PATH`. Synthetic
  >260-char copy test. PC-side only — **device read path untouched**.
- **`--verify-hash` (R12):** opt-in SHA-256 via `IncrementalHash`, hashed in the **same streaming pass**
  (no extra device/disk read → read-only), written to the `sha256` manifest column. **Zero cost when the
  flag is absent** (default copy byte-identical to Sprint 2). `CopyResult.Sha256` + CLI option + `--help`.
- **#17 polish (Closes #17):** `StatusCommandTests` (missing-db exit 2 + output sections); watchdog
  scratch buffer zeroed after each read; sync `Read()` now **throws** instead of bypassing the watchdog;
  **EUII fixture rename** (personal device name → `Sample iPhone`) across `RemoteModels.cs`,
  `SummaryWriterTests.cs`, **and** `JournalSchemaV2Tests.cs` (the third file had the same fixture).
- **Large-file:** multi-chunk (5 MiB+) copy test exercises the streaming loop a multi-GB file uses
  (true multi-GB is QA #21). XML docs on all new public API.

### Track B — Release & CI (commit `7f7a688`)
- **`.github/workflows/release.yml`** (DORMANT): on `v*` tag → single-file self-contained **win-x64**
  publish (`IncludeNativeLibrariesForSelfExtract`, `Version` stamped from the tag) → `--help` smoke →
  SHA-256 → `gh release create` with `get-and-see.exe` + `.sha256`. `permissions: contents: write`,
  `GITHUB_TOKEN` only.
- **`ci.yml` `publish-smoke` job:** builds the same single-file EXE on `windows-latest` and runs `--help`
  every PR (proves native unpack).
- **`ci.yml` `secret-scan` job:** pinned gitleaks **8.18.4** binary, `--no-git --redact --exit-code 1`.
- **`Directory.Build.props`:** public-repo EXE metadata (Product/Company/Description/Copyright/MIT/RepoUrl/
  Version 1.0.0). LICENSE confirmed MIT © 2026 owner (kept).

### Track C — User docs (commit `95d6514`)
- **`README.md`** (full rewrite): read-only promise, **"What this tool does NOT do"** (R19), prerequisites,
  install (download+verify / build), full usage incl. `--verify-hash`, exit-code table, **redacted** text
  dashboard + `summary.txt` renderings.
- **`docs/user/troubleshooting.md`:** every failure mode with the **exact emitted string**.
- **`docs/user/manifest-schema.md`:** `manifest` view + `files`/`devices`/`runs` tables + example SQL.
- **`docs/release-notes-template.md`** + **`docs/release-notes/v1.0.0.md`** (Sprints 1–3 changelog).

### Track D — #26 win-x64 build trim (commit `d30943e`, folded in on producer request)
- **Why:** the portable build copied the all-platform `imobiledevice-net` native graph (osx/linux/
  maccatalyst dylibs) into `bin/.../runtimes/`, where Windows Defender heuristically flags
  `libirecovery*.dylib` (`Exploit:MacOS/LimeRain.C!MTB` — a **known false positive**, never committed).
- **`Directory.Build.props`** now pins the restore graph with plural `<RuntimeIdentifiers>win-x64</>`; the
  two **runnable** test projects (`GetAndSee.Tests`, `GetAndSee.SafetyTests`) add singular
  `<RuntimeIdentifier>win-x64</>` to prune their native output — the same pin `GetAndSee.Cli` already
  carries (plural pins *restore*; a runnable project needs a *singular* RID to prune its `bin/`).
- After the trim: **no osx/linux/maccatalyst `runtimes/` and no `.dylib`/`.so` under any `bin/`**, all 8
  win-x64 AFC DLLs still present. Pure build/packaging change — **read-only contract untouched**, no device
  symbol added. Branch was **rebased onto `origin/main`** first (to pick up the review-profile #26 note);
  conflict-free.

## Validation (local, mirrors CI)
- `dotnet format --verify-no-changes` ✅
- `dotnet build -c Release` ✅ (0/0) · `dotnet test -c Release` ✅ **88 tests** (86 + 2 safety)
- **`ReadOnlyContractTests` green** — no new device-write symbol; `--verify-hash` + long-path are read-only
- Single-file publish ✅ (`get-and-see.exe` ~84 MB, `--help` exit 0)
- gitleaks working-tree scan ✅ no leaks · repo-wide EUII grep ✅ clean
- **#26 win-x64 trim verified** — no osx/linux/maccatalyst `runtimes/` or `.dylib`/`.so` under any `bin/`;
  win-x64 AFC natives intact; rebased on `origin/main` (conflict-free)

## Hard rules — all upheld
- `IPhoneClient` read-only; **no new device-write symbol** in `GetAndSee.Core`; `ReadOnlyContractTests`
  passes; `--verify-hash` and long-path are PC-side/read-only; **no** CLI flag mutates the device.
- **No real EUII** (names, UDIDs, GPS, user paths) in any committed source/fixture/doc/screenshot
  (the README "screenshot" is a redacted text rendering, not a binary image).

## NOT done / deferred (by design)
- **No `v1.0.0` tag cut** — that's the producer's deliberate last step **after QA #21 PASS**. The release
  workflow is dormant until a tag is pushed.
- `--verify-hash` **verify-only re-check** (compare an existing archive without re-copy) → Sprint 4
  fast-follow (per consilium cut line).
- True **multi-GB** large-file copy + the full ~269 GB run, real cable-unplug watchdog, dashboard-no-
  slowdown, read-only B-9 re-proof → **QA #21** (hardware; not dev's job).

## Needs manual setup (producer / DevOps)
- **Review gate:** run the independent `code-review` skill on this PR (PROJECT_BRIEF §13.6) before QA/merge.
- **Repo description + topics** for going public (a GitHub repo setting, not in-repo) — left for the
  producer/DevOps; LICENSE + EXE metadata are in place.
- **After merge + QA #21 PASS:** cut `v1.0.0` (`git tag v1.0.0 && git push origin v1.0.0`) to arm
  `release.yml`. Confirm the published EXE `--help` smoke step is green before announcing.
- **CI note:** the new `secret-scan` (ubuntu) and `publish-smoke` (windows) jobs join the existing
  `build` job — three required checks now.

## Files changed/created
**Source:** `src/GetAndSee.Core/Util/LongPath.cs` (new), `…/Transfer/FileCopier.cs`,
`…/Transfer/CopyResult.cs`, `…/Device/WatchdogReadStream.cs`, `…/Device/RemoteModels.cs`,
`src/GetAndSee.Cli/Commands/CopyCommand.cs`, `…/Commands/StatusCommand.cs`.
**Tests:** `tests/GetAndSee.Tests/Cli/StatusCommandTests.cs` (new), `…/Transfer/FileCopierTests.cs`,
`…/Device/WatchdogReadStreamTests.cs`, `…/Summary/SummaryWriterTests.cs`, `…/Journal/JournalSchemaV2Tests.cs`.
**Build/CI:** `Directory.Build.props` (EXE metadata + #26 win-x64 RID pin), `.github/workflows/ci.yml`,
`.github/workflows/release.yml` (new), `tests/GetAndSee.Tests/GetAndSee.Tests.csproj` +
`tests/GetAndSee.SafetyTests/GetAndSee.SafetyTests.csproj` (#26 win-x64 RID pin).
**Docs:** `README.md`, `docs/user/troubleshooting.md` (new), `docs/user/manifest-schema.md` (new),
`docs/release-notes-template.md` (new), `docs/release-notes/v1.0.0.md` (new), `docs/sprint-3/progress.md`
(new), `docs/sprint-3/done.md` (this file).
