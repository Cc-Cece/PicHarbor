# Sprint 1 — Core Pipeline + Safety Contract + CI

**Sprint Goal:** Stand up the **.NET 10 (LTS)** solution, connect to the iPhone over AFC (read-only), enumerate `/DCIM/`, copy files atomically into date-organized folders on the PC — with the read-only architectural contract enforced by a build-failing test, **and stand up GitHub Actions CI + issue/PR templates so the team can start collaborating through the repo**.

**Branch:** `feature/sprint-1`

> **Read first:** `PROJECT_BRIEF.md` (especially Section 9 — Security & Data Safety, Section 11 — CI/Release pipeline, and Section 13 — Bug & Fix Tracking) and `docs/risk-register.md`.
>
> **Repo prerequisite:** GitHub remote exists and `main` has been pushed at least once. If not done, CEO does this before Sprint 1 starts.

## Prioritized Task List

| # | Task | Owner | Est | Description |
|---|------|-------|-----|-------------|
| 1 | .NET 10 solution scaffolding | Nova | S | `get-and-see.sln`, `GetAndSee.Cli` (`net10.0`), `GetAndSee.Core` lib, `GetAndSee.Tests`, `GetAndSee.SafetyTests`. `.editorconfig`, `Directory.Build.props` with `<LangVersion>14</LangVersion>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Test projects on **xUnit v3** + **Shouldly** + **NSubstitute** (NOT FluentAssertions — went paid in v8). |
| 2 | Choose + integrate AFC library | Sage | M | Try **NetiMobileDevice** first via NuGet. Smoke test: connect to device, list `/DCIM/`. If blocked, fall back to **imobiledevice-net**. Document the choice and any prerequisites in `docs/sprint-1/notes.md`. |
| 3 | `IPhoneClient` wrapper (read-only surface) | Sage | M | Wrapper class exposing **only** `ConnectAsync`, `ListDirectoryAsync`, `GetFileInfoAsync`, `OpenReadAsync`. No write/delete methods exposed. XML doc comment on the class states the read-only contract. |
| 4 | **Read-only contract test** (R11) | Ivy | M | `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` — uses reflection to scan `GetAndSee.Core` for any reference to known AFC write APIs. Fails the build if any are found. **This test must exist and pass before merge.** |
| 5 | DCIM enumerator | Sage | S | Walk `/DCIM/` recursively via `IPhoneClient`. Yield `RemoteFile { Path, Size, ModifiedAt }`. |
| 6 | EXIF date extractor + date-folder organizer | Nova | S | `MetadataExtractor` reads `DateTimeOriginal`. Fallback chain: EXIF → file mtime → `unsorted/`. Sanity-check dates (R16: in `[1990, now+1day]`). Build `<dest>/YYYY/YYYY-MM/<sanitized-filename>`. |
| 7 | SQLite journal **at `<dest>/get-and-see.db`** (visible at root) + `manifest` view | Sage | M | Single SQLite file at the **root** of the destination (not hidden under `.get-and-see/`). Schema: `files(id, source_path, source_size, source_mtime, dest_path, state, error_message, started_at, finished_at, exif_datetime_original, gps_latitude, gps_longitude, camera_make, camera_model, copied_at, sha256)`. States: `pending`, `in_progress`, `done`, `failed`. Identity is **source path + size** (R17). PLUS a `manifest` SQL view exposing the user-facing columns (no internal state). See brainstorm Session 3 §Phase 2 for the agreed schema. |
| 8 | Atomic file copier (R1, R7, R10) | Sage | L | Per-file pipeline: open AFC read stream → write to `<dest>.partial` → fsync → verify byte count == AFC size → `File.Move` to final → mark journal `done`. On any failure: leave `.partial`, mark journal `failed` with error. |
| 9 | Filename collision handling (R5) | Nova | S | If destination final name exists AND journal shows a different source, append `_2`, `_3`. **Never overwrite.** |
| 10 | Pre-flight: disk space (R4) + writable dest (R15) | Sage | S | Before starting: compute estimated total size (sum of enumeration); check `DriveInfo.AvailableFreeSpace` ≥ total × 1.05; write+delete a probe file at dest. Bail with clear message if either fails. |
| 11 | `copy` subcommand with `--dest` and `--dry-run` | Nova | M | `System.CommandLine` wiring. `--dry-run` enumerates + plans + prints what would happen, but opens no AFC read streams and writes no files. |
| 12 | Minimal text progress output | Kira | S | One line per file as it completes: `[done] 12,345/38,412  3.2/397 GB  → 2024\2024-08\IMG_1234.HEIC`. The pretty Spectre dashboard is Sprint 2 — Sprint 1 just needs honest text output. |
| 13 | End-of-run summary + **`summary.txt` writer** | Kira | S | Print to console: total enumerated, copied, skipped (already done), failed, elapsed, average MB/s. Exit non-zero if any failures. ALSO write `<dest>/summary.txt` (human-readable) with: totals by type (photos/videos/screenshots), date range, last-run line. See brainstorm Session 3 for the exact format. |
| 14 | Unit tests | Ivy | M | Tests for: organizer (date paths, sanitize, fallback), collision handling, journal state transitions, copier with mocked `IPhoneClient`, pre-flight bail-outs. |
| 14b | **XML doc comments on public API** | Nova / Sage (author of the class) | S | Every public class and public method in `GetAndSee.Core` gets a one-line XML `<summary>` doc comment. Future maintainers read this; it is NOT user-facing docs (that's Sprint 3). Quill reviews the `IPhoneClient` doc text since the read-only contract belongs there. |
| 15 | **Repo hygiene files** | Dash | S | **Extend** the seed `.gitignore` (created in Sprint 0 with local-agent + `*.partial` rules) by **appending** .NET-standard entries: `bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`. Do NOT replace the existing rules. ALSO: `LICENSE` (MIT), placeholder `README.md` (one paragraph + "docs in `docs/`"). |
| 16 | **GitHub Actions CI workflow** | Dash | M | `.github/workflows/ci.yml` on `windows-latest`. Triggers: PR to `main`, push to `main`. Steps: checkout, `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`, `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release --no-restore`, `dotnet test -c Release --no-build --logger trx`, upload TRX artifact on failure. Test stage MUST include `GetAndSee.SafetyTests` (the read-only contract gate). |
| 17 | **Issue + PR templates** | Dash | S | `.github/ISSUE_TEMPLATE/bug_report.yml` (component, severity dropdown, steps, expected vs actual, environment), `.github/ISSUE_TEMPLATE/feature_request.yml`, `.github/PULL_REQUEST_TEMPLATE.md` (checklist: linked issues `Closes #NN`, tests added/updated, PROJECT_BRIEF updated if needed). |
| 18 | **Repo labels** | Dash | S | Create labels per PROJECT_BRIEF §13.1: `bug`, `enhancement`, `infra`, `docs`, `qa`; `severity:blocker/major/minor`; `area:device/copy/journal/cli/ux/docs/ci`; `triage`, `accepted`, `wontfix`, `duplicate`; `safety:read-only-contract`. Use `gh label create` or the `.github/labels.yml` + a one-shot script. |
| 19 | **Branch protection note** | Dash | S | Write `docs/sprint-1/branch-protection-setup.md` with exact GitHub UI steps to enable on `main`: require PR, require CI status check `ci.yml`, no direct pushes. (Owner applies via GitHub UI — cannot be done from a PR.) |
| 20 | `docs/sprint-1/progress.md` + `done.md` + handoff | Nova | S | Per the protocol in PROJECT_BRIEF.md Section 12. |

**Size:** S = <1hr, M = 1–3hr, L = 3–5hr.

## Work Schedule

1. **Phase 0 — Repo Bootstrap** (Tasks 15–19): `.gitignore`, `LICENSE`, README placeholder, CI workflow, issue/PR templates, labels, branch-protection note. **Open a small PR first** so CI is alive and required-status-check can be configured against a real run. **Commit + push + PR + merge.**
2. **Phase 1 — Foundation** (Tasks 1–4): Solution scaffold + AFC library decision + read-only wrapper + the safety contract test. **Commit + push.** (CI now runs against every push.)
3. **Phase 2 — Enumerate & Plan** (Tasks 5–7, 10): DCIM walk + organizer + journal + pre-flight. **Commit.**
4. **Phase 3 — Core Copy** (Tasks 8–9): Atomic copier + collision handling. **Commit.**
5. **Phase 4 — CLI & Output** (Tasks 11–13): `copy` subcommand, `--dry-run`, per-file progress line, summary. **Commit.**
6. **Phase 5 — Tests & Handoff** (Tasks 14, 20): Unit tests, progress.md, done.md, PROJECT_BRIEF.md update. **Final commit + push + PR.**

Update `docs/sprint-1/progress.md` after each phase.

## Success Criteria

- [ ] `dotnet test` passes locally, **including `ReadOnlyContractTests`**
- [ ] **GitHub Actions CI workflow is green on PR** — build, format-check, tests (incl. safety contract) all pass on `windows-latest`
- [ ] **Branch protection on `main` requires CI to pass** (verified by attempting a push to main — should be rejected)
- [ ] **Issue and PR templates render** when opening a new issue / PR via GitHub UI
- [ ] **Repo labels exist** per Section 13.1 of PROJECT_BRIEF (`bug`, severities, areas, etc.)
- [ ] `dotnet run -- copy --dest D:\test --dry-run` enumerates the device's `/DCIM/` and prints planned destination paths, with no AFC read streams opened and no files written
- [ ] `dotnet run -- copy --dest D:\test` copies all DCIM media to date-organized folders, atomically (no `.partial` files left on success)
- [ ] **Destination root contains `get-and-see.db` (visible, not hidden) and `summary.txt` after a successful run**
- [ ] **`sqlite3 D:\test\get-and-see.db "SELECT COUNT(*) FROM manifest"` returns the copied file count**
- [ ] **`summary.txt` is human-readable and matches the format in brainstorm Session 3**
- [ ] Running the same command a second time skips all already-done files (journal-aware)
- [ ] Bailing with Ctrl+C leaves the journal in a clean state; re-running resumes correctly
- [ ] Clear error message when no iPhone connected
- [ ] Clear error message when iPhone is locked / not trusted
- [ ] Clear error message when destination has insufficient free space
- [ ] Filename collisions across DCIM subfolders are disambiguated (`_2`, `_3`), never overwritten
- [ ] End-of-run summary lists total / copied / skipped / failed / elapsed / MB/s
- [ ] Exit code is non-zero if any file failed

## What's NOT in This Sprint

| Cut | Reason |
|-----|--------|
| Spectre.Console live dashboard | Sprint 2 — get the engine right first |
| `status` subcommand | Sprint 2 |
| HEIC conversion | v2 (ideas backlog) |
| Long-path `\\?\` prefix (R6) | Sprint 3 hardening; add only if a test path triggers MAX_PATH |
| `--verify-hash` (R12) | Sprint 3 |
| GUI | Out of scope for v1 |
| Concurrent file transfers | v2 after telemetry |
| **Release workflow** (build EXE on tag, attach to GitHub Release) | Sprint 3 — Sprint 1 ships only the CI workflow |
| **Cross-platform CI matrix** (Linux/macOS) | Tool only targets Windows in v1; no value in matrix runs |
| Code-signing / Authenticode | Out of scope for v1 (no certificate); ideas backlog |
| Installer / MSI | Out of scope for v1 (single-file EXE is the distribution) |

## Dev + DevOps Team Prompt

```
Read PROJECT_BRIEF.md (full), docs/risk-register.md, and docs/sprint-1/plan.md.
Then execute Sprint 1.

First: git checkout -b feature/sprint-1

This sprint has TWO parallel tracks. Do Phase 0 (Dash) FIRST so CI is alive
before the application code starts landing:

  Track A (Dash, DevOps): Phase 0 only.
    - .gitignore, LICENSE (MIT), placeholder README.md
    - .github/workflows/ci.yml (windows-latest, .NET 10, restore/format/build/test,
      MUST include GetAndSee.SafetyTests)
    - .github/ISSUE_TEMPLATE/{bug_report,feature_request}.yml
    - .github/PULL_REQUEST_TEMPLATE.md
    - Repo labels per PROJECT_BRIEF §13.1
    - docs/sprint-1/branch-protection-setup.md (instructions for the human)
    Open a small PR with these files; merge once CI is green. Then the rest
    of the sprint's PRs have a working CI gate to pass.

  Track B (Nova + Sage, Dev): Phases 1-5.
    Build the core pipeline for get-and-see: a C# / .NET 10 (LTS) CLI that
    connects to an iPhone via the AFC protocol (NetiMobileDevice preferred,
    imobiledevice-net fallback), enumerates /DCIM/ READ-ONLY, and copies files
    atomically into a date-organized folder structure on the PC.

    Target framework: net10.0. Language: C# 14. Tests: xUnit v3 + Shouldly +
    NSubstitute. Do NOT add FluentAssertions (paid commercial since Jan 2025).

Hard rules (Section 9 of the brief):
  - The IPhoneClient wrapper exposes ONLY read methods. No write/delete methods
    are bound, exposed, or called anywhere in GetAndSee.Core.
  - The ReadOnlyContractTests must exist and pass. It scans the assembly for
    references to AFC write APIs and fails the build if any are found.
  - No CLI flag exists that toggles into a write/delete/move mode.

Process rules:
  - All work goes through PRs. Never push to main.
  - PRs use the template (.github/PULL_REQUEST_TEMPLATE.md).
  - Reference issues in commits: "fix: #NN description". Use "Closes #NN" in
    PR description.
  - CI must be green before merge.

Commit after each phase. Update docs/sprint-1/progress.md after each phase.

Take your time — reliability matters more than speed. This tool will move
400 GB in the real world. Every shortcut in the copy pipeline becomes data
loss later.

When done, push and open the final PR. Follow Sections 12-14 of PROJECT_BRIEF.md
for handoff.
```
