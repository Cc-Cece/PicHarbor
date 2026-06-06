# PROJECT_BRIEF.md — get-and-see

> Updated 2026-06-06 after brainstorm Session 2. Supersedes Session 1 (Python).
> See `docs/brainstorm/session-2.md` for the constraint shift (C#, read-only, 400GB).

## 1. Project Overview

**get-and-see** is a single-file C# / **.NET 10** CLI that copies the full media archive (photos, videos, Live Photos) from an iPhone 12 Pro to a Windows PC over USB. It uses Apple's native **AFC** protocol (the same protocol Finder, iTunes, and iMazing rely on) — explicitly avoiding Windows MTP / Explorer, which is unreliable for large libraries. The tool is **read-only by architectural design**: no write or delete code paths against the device exist in the codebase. It is built for a worst-case 400 GB transfer: SQLite-backed journal, atomic per-file writes, resumable, and verifiable.

## 2. Concept / Product Description

**The problem.** Getting a full iPhone media library onto a Windows PC is painful and risky at scale:
- **Windows Explorer / MTP** drops connections, stalls enumeration, and silently produces 0-byte files. Unworkable at hundreds of GB.
- **iCloud** requires a paid storage tier, is slow, and depends on the cloud.
- **iTunes / Apple Devices app** doesn't expose the raw media tree as a browsable folder.
- **Third-party paid tools** (iMazing, etc.) solve it but are commercial and opaque.

**The solution.** `get-and-see` is a focused, open, read-only CLI that:
- Talks to the iPhone over **AFC** (Apple File Conduit) via the `NetiMobileDevice` library — the validated, battle-tested protocol family
- **Never** writes to or deletes from the iPhone (enforced by a build-time check, not just convention)
- Streams files to PC, organized by date: `YYYY/YYYY-MM/filename.ext`
- Uses **atomic writes** (`.partial` → rename) so a yanked cable can never produce a half-written destination file
- Tracks every file in a **SQLite journal** at `<dest>/.get-and-see/journal.db`, making transfers fully resumable
- Verifies each file's size on completion; optional SHA-256 verification (`--verify-hash`)
- Renders a **live progress dashboard** (Spectre.Console) for the multi-hour transfer
- Recovers cleanly from Ctrl+C, USB disconnect, or sleep

**Target user.** Anyone with a large iPhone media library who wants a reliable, local, scriptable, open-source way to back it up to Windows — without trusting MTP and without paying for iCloud or iMazing.

**What this tool does NOT do** (see brainstorm Session 3 for the UX rationale):

`get-and-see` copies the iPhone's raw camera roll (the `/DCIM/` directory). It does **not** preserve information that lives inside the Apple Photos app:

- Albums and Smart Albums
- User-added keywords or descriptions
- Favorites / hearts
- People and Face tags
- iCloud Shared Album membership
- Memories and auto-curated collections

That information lives in Apple's internal photo database, which is not accessible via the read-only file protocol this tool uses. To export those, a different tool that accesses the Photos library directly would be needed (different scope, different safety profile).

What you get is **complete and faithful at the raw-file level**: every photo, video, Live Photo, screen recording, and screenshot in the camera roll, with all original EXIF metadata preserved, organized by date, plus a SQLite manifest and a human-readable summary at the destination root.

## 3. Tech Stack

All versions validated against current standards as of 2026-06-06. See `docs/brainstorm/session-2.md` correction note for the .NET 8 → .NET 10 bump (.NET 8 entered maintenance May 2026, EOL Nov 2026).

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **.NET 10 (LTS)** | 10.0.x | Runtime | Current LTS (Active support through Nov 2028). .NET 8 is in maintenance (EOL Nov 2026); starting on it would already be tech debt. |
| **C# 14** | with .NET 10 | Language | Latest stable. Brings extension members, null-conditional assignment, `field` keyword, partial constructors. |
| **NetiMobileDevice** | latest 2.x | iPhone AFC client | Pure C# port of pymobiledevice3. No native DLLs. Uses Apple's native AFC protocol — same family as Finder / iTunes / iMazing. Validated for production use. |
| **imobiledevice-net** | latest | Fallback AFC client | C# bindings around `libimobiledevice` (C library). Battle-tested but ships native DLLs. Documented fallback path if NetiMobileDevice has gaps. |
| **System.CommandLine** | 2.0.x | CLI parsing | Stable in .NET 10 ecosystem. Supports subcommands, async handlers, rich help generation. |
| **Spectre.Console** | latest | Terminal UI | Live multi-region progress, tables, styled output — the C# equivalent of Python's `rich`. |
| **Microsoft.Data.Sqlite** | 10.x | Transfer journal | Lightweight, single-file DB. Tracks each file's state (pending/in-progress/done/failed) across runs. |
| **MetadataExtractor** | latest | EXIF parsing | Reads `DateTimeOriginal` from photos to drive date-folder organization. |
| **xUnit v3** | 3.x | Test framework | xUnit v3 (GA 2025) is the modern target — better async support, native parallelism config, dropped legacy baggage. |
| **Shouldly** | latest | Assertion library | BSD-licensed, actively maintained. Replaces FluentAssertions, which moved to a paid commercial license in v8 (Jan 2025) — not acceptable for an open project. |
| **NSubstitute** | latest | Mocking | Clean syntax, modern, MIT-licensed. Used for mocking `IPhoneClient` in unit tests. |
| **dotnet format + analyzers** | bundled with SDK 10 | Style & static checks | No extra tooling needed. |

**Explicitly rejected:**
- **.NET 8** — in maintenance (security-only) as of May 2026, EOL Nov 2026. Inappropriate runtime for a brand-new project.
- **.NET 9 (STS)** — also in maintenance, EOL Nov 2026. Same problem.
- **WPD (Windows Portable Devices) / MTP** — known unreliable for iPhone, especially at scale. User-reported pain. This is the experience we are explicitly replacing.
- **iTunes COM API** — deprecated; iTunes for Windows is being phased out.
- **FluentAssertions v8+** — relicensed to commercial-paid in Jan 2025. Use Shouldly instead.
- **Python (Session 1 stack)** — superseded by C# per CEO requirement.

## 4. Architecture

```
┌────────────────┐    USB / lockdownd + AFC     ┌──────────────────────┐
│   iPhone 12    │◄────────────────────────────►│  NetiMobileDevice    │
│   Pro          │   (Apple's native protocol;  │  (pure C# AFC client)│
│                │    NOT MTP, NOT WPD)         └──────────┬───────────┘
│   /DCIM/       │                                         │
│   ├─100APPLE/  │                                         │  READ-ONLY
│   ├─101APPLE/  │                                         │  (no Write/Delete
│   └─ ...       │                                         │   methods bound)
└────────────────┘                                         ▼
                                              ┌───────────────────────┐
                                              │  get-and-see (.NET 10)│
                                              │                       │
                                              │  1. Pre-flight checks │
                                              │  2. Enumerate /DCIM   │
                                              │  3. Plan + journal    │
                                              │  4. Per-file pipeline │◄────┐
                                              │     a. open AFC stream│     │
                                              │     b. write .partial │     │
                                              │     c. fsync          │     │
                                              │     d. verify size    │     │
                                              │     e. rename final   │     │
                                              │     f. mark done      │     │
                                              │  5. Summary           │     │
                                              └───────────┬───────────┘     │
                                                          │                 │
                                                          ▼                 │
                                              ┌───────────────────────┐     │
                                              │   Destination PC      │     │
                                              │                       │     │
                                              │   D:\Photos\          │     │
                                              │   ├─ get-and-see.db ───┼─────┘
                                              │   │  (journal +       │  (resume
                                              │   │   manifest view)  │   source)
                                              │   ├─ summary.txt       │
                                              │   │  (human-readable) │
                                              │   ├─ 2024\             │
                                              │   │  ├─ 2024-01\       │
                                              │   │  └─ 2024-02\       │
                                              │   ├─ 2025\             │
                                              │   └─ unsorted\         │
                                              └───────────────────────┘
```

**Why this shape:**
- The arrow into the iPhone is **read-only**. The C# code references zero AFC write methods — enforced by a unit test that fails the build if anyone adds one.
- The manifest DB sits at the destination root (visible, not hidden) so the destination folder is fully self-describing and portable. Move the folder, the manifest moves with it. Same file doubles as the in-progress journal and the long-term metadata index.
- `summary.txt` is regenerated at the end of every run from the DB. Human-readable answer to "is the archive complete and what's in it?"
- Per-file open/close (not a single long-lived AFC session) — survives transient device or USB hiccups during a multi-hour run.
- Sequential, single-file-at-a-time transfer. USB / AFC bandwidth is the bottleneck; concurrency adds reliability risk for zero throughput gain.

## 5. Key Files Map

| Area | Path | Contents |
|------|------|----------|
| Solution | `get-and-see.sln` | .NET solution file |
| CLI project | `src/GetAndSee.Cli/GetAndSee.Cli.csproj` | Main executable project, single-file publish target |
| CLI entry | `src/GetAndSee.Cli/Program.cs` | `System.CommandLine` root + subcommand wiring |
| Copy command | `src/GetAndSee.Cli/Commands/CopyCommand.cs` | `copy` verb: pre-flight + plan + execute |
| Status command | `src/GetAndSee.Cli/Commands/StatusCommand.cs` | `status` verb: open journal, print last-run summary |
| Device layer | `src/GetAndSee.Core/Device/IPhoneClient.cs` | AFC client wrapper. **Read-only surface only.** |
| Enumeration | `src/GetAndSee.Core/Device/DcimEnumerator.cs` | Walks `/DCIM/`, yields file descriptors |
| Organizer | `src/GetAndSee.Core/Organize/DateFolderOrganizer.cs` | EXIF date → destination path (`YYYY/YYYY-MM/name.ext`) |
| Copier | `src/GetAndSee.Core/Transfer/FileCopier.cs` | Per-file: stream → `.partial` → verify → rename → journal update |
| Journal + manifest | `src/GetAndSee.Core/Journal/TransferJournal.cs` | SQLite wrapper at `<dest>/get-and-see.db` (visible at root, not hidden). State per file, resume logic, AND a `manifest` SQL view exposing EXIF date, GPS, camera, size, paths — queryable with any sqlite3 tool. |
| Summary writer | `src/GetAndSee.Core/Summary/SummaryWriter.cs` | Generates `<dest>/summary.txt` at end of every run: totals by type, date range, devices seen, last-run line. Human-readable archive overview. |
| Pre-flight | `src/GetAndSee.Core/Preflight/PreflightChecks.cs` | Disk space, dest writable, AFC handshake, etc. |
| Dashboard | `src/GetAndSee.Cli/Ui/LiveDashboard.cs` | Spectre.Console live progress UI |
| Errors | `src/GetAndSee.Core/Errors/` | Typed exceptions, user-friendly messages |
| Safety test | `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` | **Build-fail test** — fails if any AFC write method is referenced |
| Other tests | `tests/GetAndSee.Tests/` | Unit tests for organizer, copier (with mocked AFC), journal, etc. |
| CI workflow | `.github/workflows/ci.yml` | Build + test + format-check on every PR and push to `main` (Windows runner). Required status check. |
| Release workflow | `.github/workflows/release.yml` | On tag push (`v*`): build single-file EXE, attach to GitHub Release |
| Bug template | `.github/ISSUE_TEMPLATE/bug_report.yml` | Structured form for QA-filed bugs (component, repro steps, severity) |
| Feature template | `.github/ISSUE_TEMPLATE/feature_request.yml` | Structured form for v2 ideas |
| PR template | `.github/PULL_REQUEST_TEMPLATE.md` | Checklist: linked issues, tests added, brief updated if needed |
| Repo root | `.gitignore` | Seeded in Sprint 0 with rules for local-only files (`.github/agents/*-local.agent.md`, `*.partial`). **Extended** in Sprint 1 Phase 0 by Dash with .NET-standard entries (`bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`). |
| Local agents | `.github/agents/*-local.agent.md` | Per-developer AI agent customizations (Producer, Dev, QA). **Gitignored** — each developer has their own. Shared agent definitions (no `-local` suffix) may live alongside and ARE tracked. |
| Repo root | `LICENSE` | MIT |
| Repo root | `README.md` | User-facing docs (written in Sprint 3 by Quill) |
| User docs | `docs/user/troubleshooting.md` | "iPhone not detected", "Trust dialog didn't appear", driver install, common errors. Sprint 3, Quill. |
| User docs | `docs/user/manifest-schema.md` | Reference for the `get-and-see.db` schema and `manifest` view, with example SQL queries. Sprint 3, Quill. |
| Docs | `docs/` | Brainstorm, sprint plans, QA sign-offs, risk register |
| Brief | `PROJECT_BRIEF.md` | This file — single source of truth |

## 6. Team Roles

| Agent | Role | Focus |
|-------|------|-------|
| **Kira** | Product Designer | CLI UX, live dashboard, summary formatting, user-facing error messages, `--help` copy review |
| **Nova** | App Developer | .NET solution structure, CLI wiring (System.CommandLine), Spectre.Console UI, packaging (single-file publish) |
| **Sage** | Systems Engineer | AFC integration (NetiMobileDevice), journal (SQLite), atomic writes, pre-flight checks, reliability |
| **Dash** | DevOps Engineer | GitHub Actions CI + release pipeline, branch protection, issue/PR templates, repo hygiene |
| **Ivy** | QA Engineer | Risk register, edge-case testing, large-file scenarios, integrity verification, **read-only contract enforcement**, GitHub Issues triage |
| **Quill** | Technical Writer | README, troubleshooting guide, manifest/schema reference, release notes. Reviews `--help` text and error-message copy. Active primarily in Sprint 3, on-call in Sprints 1–2 when something user-facing ships. |
| **Remy** | Producer | Sprint plans, scope control, coordination, merging PRs |

### 6.1 Documentation Ownership Matrix

Docs are split by audience and lifetime — no single person writes everything.

| Artifact | Audience | Owner | When written |
|----------|----------|-------|--------------|
| `README.md` | End users (first impression) | **Quill** | Sprint 3 |
| `docs/user/troubleshooting.md` | End users (when stuck) | **Quill** | Sprint 3 |
| `docs/user/manifest-schema.md` | End users (power) | **Quill** | Sprint 3 |
| Release notes per tag | End users (changelog) | **Quill** | At every release |
| CLI `--help` text | End users (in-tool) | Nova drafts; Kira + Quill review for tone | Where the command lives (Sprint 1+) |
| User-facing error messages | End users (in-tool) | Sage drafts; Kira + Quill review for clarity | Where the error lives |
| XML doc comments on public API | Future maintainers | Author of the class | As code is written (Sprint 1+) |
| `PROJECT_BRIEF.md` | All teams, all chats | **Remy** | Sprint 0; updated per sprint |
| `docs/sprint-N/plan.md` | Dev / DevOps / QA | **Remy** | Before each sprint |
| `docs/sprint-N/progress.md` | All teams (recovery) | Whoever's executing the sprint | During the sprint |
| `docs/sprint-N/done.md` | Next sprint's team | Whoever executed the sprint | At sprint close |
| `docs/qa/sprint-N-signoff.md` | Remy + dev team | **Ivy** | At QA close |
| `docs/risk-register.md` | All teams | Ivy (additions); anyone (updates) | Living doc |
| `docs/ideas-backlog.md` | All teams | Anyone | Living doc |
| `docs/brainstorm/session-N.md` | All teams (historical) | Whichever agent facilitated | One-shot per brainstorm |

## 7. Sprint Status

| Sprint | Name | Status | Scope |
|--------|------|--------|-------|
| 0 | Bootstrap | ✅ Done | Brainstorm S1, S2, S3, PROJECT_BRIEF (this doc), risk register, Sprint 1 plan |
| 1 | Core Pipeline + Safety Contract + CI | ⬜ Planned | .NET 10 scaffolding, AFC connect, enumerate, journal-at-root (`get-and-see.db`), atomic copy, date-org, read-only test, `summary.txt` writer, `manifest` SQL view, **GitHub Actions CI (build + test on PR)**, issue/PR templates, branch protection on `main` |
| 2 | UX, Resumability & Pre-flight | ⬜ Planned | Live dashboard, pre-flight checks, summary, Ctrl+C handling, `status` subcommand, **`devices` + `runs` tables**, **Live Photo pair detection** |
| 3 | Hardening, Packaging & Release | ⬜ Planned | Long-path, large-file stress tests, `--verify-hash`, **release workflow (single-file EXE attached to GitHub Release on tag push)**, **full user docs (Quill): `README.md`, `docs/user/troubleshooting.md`, `docs/user/manifest-schema.md`, release-notes template**, LICENSE polish |

## 8. Current State

**What exists:** Empty repo with three brainstorm docs (`session-1.md` historical Python direction, `session-2.md` C#/.NET 10/read-only/400GB pivot, `session-3.md` destination-organization UX), `docs/ideas-backlog.md`, `docs/risk-register.md`, `docs/sprint-1/plan.md`, and this PROJECT_BRIEF.

**What works:** Nothing — Sprint 0 (planning) just completed.

**What's next:** Sprint 1 — stand up the .NET 10 solution, integrate NetiMobileDevice (or imobiledevice-net fallback), enumerate `/DCIM/` read-only, copy files atomically into date folders, ship the build-time read-only contract test, **and stand up GitHub Actions CI + issue/PR templates so QA can start filing bugs**.

**Blockers:** None. **Prerequisites for the dev machine:**
- .NET 10 SDK (10.0.x — current LTS)
- Apple device USB drivers — installed by either iTunes for Windows OR the "Apple Devices" app from the Microsoft Store
- iPhone unlocked and "Trust This Computer" tapped at least once

**Prerequisites for the GitHub remote (one-time, CEO does this before Sprint 1):**
- GitHub repo created (private or public, owner's choice)
- Local clone has `origin` pointing to the GitHub repo
- First push of `main` so branch protection can be configured against it

## 9. Security & Data Safety Rules

This section is treated as **architectural**, not just a checklist. Violations are build failures, not review comments.

### 9.1 Device safety (the most important rule)

**The iPhone is never written to. Ever. By design.**

1. The AFC client wrapper (`IPhoneClient`) exposes **only** read operations: `ListDirectory`, `GetFileInfo`, `OpenRead`. Write/delete methods on the underlying library are not exposed.
2. A unit test (`ReadOnlyContractTests`) scans the compiled assemblies via reflection for any reference to AFC write methods (`AfcWriteFile`, `AfcRemovePath`, `AfcMakeDirectory`, `AfcRenamePath`, `house_arrest` write mode, etc.). If any reference is found, the test fails. **Build fails.**
3. No CLI flag exists that toggles into a write/delete/move mode. There is no `--delete-after-copy`, no `--move`, no `--cleanup`. Adding one is a design conversation, not a code change.
4. README states this explicitly: "This tool never writes to or deletes from your iPhone."

### 9.2 Data integrity (in transit and on disk)

| Risk | Mitigation |
|------|-----------|
| Truncated file from USB drop | **Atomic write**: stream to `<final>.partial`, fsync, verify size matches AFC-reported size, then atomic `File.Move` to final name. A `.partial` is never treated as complete. |
| Tool crash mid-transfer | **SQLite journal**: every file's state (pending / in-progress / done / failed) flushed on transition. Re-run resumes from journal — skips done, retries in-progress / failed. |
| Disk full mid-write | **Pre-flight free-space check** ≥ (estimated total + 5% headroom); per-file write detects out-of-space and marks file `failed` instead of corrupting it. |
| Filename collision (two source files, same name) | Detect via journal; append `_2`, `_3`, … to disambiguate. **Never overwrite an existing destination file.** |
| Long Windows paths | All destination paths use the `\\?\` long-path prefix. |
| Silent bit corruption | Mandatory size verification per file. Optional `--verify-hash` for SHA-256 (slower; for the paranoid). |
| Antivirus quarantine of `.partial` | Catch IO exception, mark failed, log; do not crash the whole run. |

### 9.3 No-secrets posture

- **No credentials stored.** USB pairing is handled by iOS's trust dialog + `lockdownd` pairing records (managed by the OS / driver layer).
- **No network access.** Tool is local USB only. No telemetry, no auto-update, no cloud calls.
- **No code execution from device.** EXIF data is parsed as data, never evaluated.
- **Path safety.** Destination paths are built from sanitized EXIF dates + sanitized original filenames; iPhone-supplied strings are never used as raw path components without sanitization.

### 9.4 Process safety

- Ctrl+C / SIGINT is handled cleanly: flush journal, close AFC handles, exit non-zero. A partial in-flight file is left as `.partial` (never as a final name).
- All AFC handles are wrapped in `using` / `await using`; no resource leaks across cancellation.

## 10. How to Run Locally

```pwsh
# Prerequisites
# 1. .NET 10 SDK installed (dotnet --version → 10.0.x)
# 2. iTunes for Windows OR "Apple Devices" app from Microsoft Store
#    (either one provides the Apple Mobile Device USB driver)
# 3. iPhone connected via USB, unlocked, "Trust This Computer" tapped

cd e:\src\get-and-see

# Restore + build
dotnet restore
dotnet build

# Run tests (includes the read-only contract test)
dotnet test

# Run the tool (debug build)
dotnet run --project src/GetAndSee.Cli -- copy --dest "D:\Photos"
dotnet run --project src/GetAndSee.Cli -- copy --dest "D:\Photos" --dry-run
dotnet run --project src/GetAndSee.Cli -- status --dest "D:\Photos"
dotnet run --project src/GetAndSee.Cli -- --help

# Single-file release build
dotnet publish src/GetAndSee.Cli -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# Output: src/GetAndSee.Cli/bin/Release/net10.0/win-x64/publish/get-and-see.exe
```

## 11. How to Deploy

This is a local CLI tool, not a hosted service. "Deployment" = produce the single-file EXE and publish it as a GitHub Release.

### 11.1 Local one-off build

```pwsh
dotnet publish src/GetAndSee.Cli -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: one `get-and-see.exe` (~30–60 MB self-contained). Copy it anywhere. No install, no admin rights needed at run time.

### 11.2 CI pipeline (Sprint 1 deliverable)

`.github/workflows/ci.yml` runs on **every push to `main` and every PR**:

1. Checkout
2. Setup .NET 10 SDK (`actions/setup-dotnet@v4`, `dotnet-version: 10.0.x`)
3. `dotnet restore`
4. `dotnet format --verify-no-changes` — style gate, fails the workflow on diff
5. `dotnet build --configuration Release --no-restore`
6. `dotnet test --configuration Release --no-build --logger trx` — **must include `ReadOnlyContractTests`** (the read-only safety gate)
7. Upload test results artifact on failure

**Runner:** `windows-latest` only (the tool only targets Windows; no value in running on Linux/macOS in v1).

**Branch protection on `main`** (configured once by Dash via repo settings):
- Require PR before merging
- Require CI status check (`ci.yml`) to pass
- Require linear history disabled (we use regular merge, not squash)
- No direct pushes (matches the team rule already in Section 14)

### 11.3 Release pipeline (Sprint 3 deliverable)

`.github/workflows/release.yml` runs on **tag push matching `v*`** (e.g. `v0.1.0`):

1. Checkout at tag
2. Setup .NET 10 SDK
3. `dotnet publish` with `PublishSingleFile=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`
4. Compute SHA-256 of the resulting `get-and-see.exe`
5. Create GitHub Release for the tag (`softprops/action-gh-release` or `gh release create`)
6. Attach `get-and-see.exe` and `get-and-see.exe.sha256` to the release

No signing in v1 (no code-signing certificate). Sprint 3+ may add Authenticode signing as an ideas-backlog item.

### 11.4 Secrets

No secrets needed for CI or release in v1 — `GITHUB_TOKEN` is auto-provided by GitHub Actions and is sufficient for creating releases on the same repo.

## 12. Cross-Chat Handoff Protocol

Every sprint chat must do these before finishing:

1. Write `docs/sprint-X/done.md` — what was built, what's not done, what needs manual setup, files changed/created
2. Update PROJECT_BRIEF.md: Section 7 (mark sprint done) + Section 8 (rewrite current state)
3. Commit all changes with descriptive message: `sprint-X: <summary>`

This is how context survives across chats. If skipped, the next chat starts blind and may overwrite or duplicate work. The repo is the shared memory — keep it accurate.

## 13. Bug & Fix Tracking

All bugs and feature requests live as **GitHub Issues** on the repo. This is the single source of truth for all teams — docs and chat do not survive across context boundaries.

### 13.1 Labels (set up by Dash in Sprint 1)

- **Type**: `bug`, `enhancement`, `infra`, `docs`, `qa`
- **Severity** (bugs only): `severity:blocker`, `severity:major`, `severity:minor`
- **Area**: `area:device`, `area:copy`, `area:journal`, `area:cli`, `area:ux`, `area:docs`, `area:ci`
- **Status**: `triage`, `accepted`, `wontfix`, `duplicate`
- **Safety** (rare, high-priority): `safety:read-only-contract` — anything that risks the device-write safety contract

### 13.2 For QA (Ivy)

File bugs as GitHub Issues using `.github/ISSUE_TEMPLATE/bug_report.yml`. Required fields: component (area label), severity, steps to reproduce, expected vs actual, environment (.NET version, device model, iOS version when possible).

When a sprint completes with no blockers found: write `docs/qa/sprint-X-signoff.md` with test count, pass rate, and an explicit "no blockers" statement — AND add a comment on the sprint's tracking issue (if one exists) linking to the sign-off.

When QA verifies a fix: comment on the original bug issue with "verified in <commit-or-PR>" before closing. Do not close issues without verification.

### 13.3 For Dev Team (Nova, Sage)

Before starting a sprint: check open issues filtered by `severity:blocker` and `severity:major` — these are pre-sprint work, not Sprint N scope additions. Fix them on a hotfix branch, PR to `main`.

During a sprint: reference issue numbers in commits (`fix: #42 description`) and PR descriptions (`Closes #42`). GitHub auto-closes issues on PR merge when the syntax is correct.

When a bug is genuinely not reproducible: comment with what was tried, label `triage`, ping QA — don't close unilaterally.

### 13.4 For DevOps (Dash)

Label infra issues `infra`. CI workflow failures that block merging are `severity:blocker`.

### 13.5 For Feature Ideas

Add to `docs/ideas-backlog.md` first (cheap, no triage cost). Promote to a GitHub Issue (`enhancement` label) only when it's accepted into a sprint plan.

## 14. Multi-Repo Setup

Code is hosted on **GitHub**. Each team works in their own separate clone of the repo. No worktrees. Everyone works on their own branch, pushes to origin, opens PRs.

**Teams:**
- Producer on `main` (coordination hub, merges PRs)
- Dev Team on `feature/sprint-X`
- QA on `feature/qa-X`
- DevOps on `feature/devops-X` (CI/release pipeline changes)

**Setup:**
```pwsh
git clone https://github.com/<owner>/get-and-see.git <folder>
cd <folder>
git checkout -b <branch>
dotnet restore
dotnet build
```

**Branch strategy:** Feature branches → PR → regular merge to `main`. Never push directly to `main`. Never squash. Never rebase feature branches (causes commit loss). PRs require CI to pass before merge (branch protection, see Section 11.2).

**PR conventions:**
- Title: imperative present (`add atomic copier`, not `added atomic copier`)
- Description: short summary + `Closes #NN` for any issues fixed
- Use the PR template (`.github/PULL_REQUEST_TEMPLATE.md`) — checklist for linked issues, tests added, brief updated
- One reviewer (Remy) merges. Producer never authors application code, only reviews and merges.
