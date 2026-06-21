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
- Talks to the iPhone over **AFC** (Apple File Conduit) via the `imobiledevice-net` library (a C# binding over native `libimobiledevice`) — the validated, battle-tested protocol family
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

> **AFC library: DECIDED 2026-06-06.** `imobiledevice-net 1.3.17` — smoke-tested end-to-end against an iPhone 12 Pro on iOS 26.5 with .NET 10. The earlier `NetiMobileDevice` name (from brainstorms 2 + 3) was fabricated and does not exist on NuGet; the smoke test caught it before any code was written. Full decision + evidence + caveats in [`docs/sprint-1/afc-library-decision.md`](docs/sprint-1/afc-library-decision.md).

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **.NET 10 (LTS)** | 10.0.x | Runtime | Current LTS (Active support through Nov 2028). .NET 8 is in maintenance (EOL Nov 2026); starting on it would already be tech debt. |
| **C# 14** | with .NET 10 | Language | Latest stable. Brings extension members, null-conditional assignment, `field` keyword, partial constructors. |
| **imobiledevice-net** | 1.3.17 | Talk to iPhone over USB (AFC) | Only validated C# AFC client; smoke-tested on .NET 10 + iOS 26.5 (`docs/sprint-1/afc-library-decision.md`). C# binding over native `libimobiledevice`. Stale (Feb 2021) but functional for our small read-only slice. **Requires `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`** (native DLLs) + Apple driver service (iTunes or "Apple Devices" Store app). |
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
- **`NetiMobileDevice`** — does not exist on NuGet (was fabricated in brainstorm Session 2; smoke-test caught it before Phase 1). Tracked in `docs/sprint-1/library-investigation.md`.

## 4. Architecture

```
┌────────────────┐    USB / lockdownd + AFC     ┌──────────────────────┐
│   iPhone 12    │◄────────────────────────────►│  imobiledevice-net   │
│   Pro          │   (Apple's native protocol;  │  (C# binding over     │
│                │    NOT MTP, NOT WPD)         │  native libimobile-   │
│   /DCIM/       │                              │  device; win-x64 RID) │
│   ├─100APPLE/  │                              └──────────┬───────────┘
│   ├─101APPLE/  │                                         │  READ-ONLY
│   └─ ...       │                                         │  (no Write/Delete
│                │                                         │   methods bound)
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
| Repo root | `.gitignore` | `*.partial` (safety) + .NET-standard entries (`bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`). |
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
| **Sage** | Systems Engineer | AFC integration (imobiledevice-net), journal (SQLite), atomic writes, pre-flight checks, reliability |
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
| 1 Phase 0 | Repo Bootstrap + CI | ✅ Done | `.gitignore` extended, MIT LICENSE, placeholder README, GitHub Actions CI (windows-latest, .NET 10), issue + PR templates, repo labels, branch-protection setup doc. PR #1 merged as `b46d931`. |
| 1 Phases 1–5 | Core Pipeline + Safety Contract | ✅ Done | .NET 10 solution, read-only AFC client (`imobiledevice-net 1.3.17`), `/DCIM/` enumerate, EXIF date-org, journal-at-root (`get-and-see.db`) + `manifest` view, atomic copier + collision handling, pre-flight (27015/writable/space), `copy --dest/--dry-run`, per-file progress, `summary.txt`, **`ReadOnlyContractTests` (build-failing)**. **QA Stage 2 PASS** on real iPhone 12 Pro / iOS 26.5 / 27,478 files / 269 GB — read-only proof device-unchanged. Merged PR #9 (`8bc600f`); sign-off PR #13. One non-blocking major (#11) → Sprint 2. |
| 2 | UX, Resumability & Pre-flight | ✅ Done | **Pillars:** #11/R2 **read-stall watchdog** (per-read timeout → clean resumable stop, exit 3) and **live Spectre dashboard** + #10 current/avg speed + ETA (graceful text fallback, `--no-dashboard`). Plus `status` subcommand, additive `runs`/`devices` journal tables (`user_version` migration), on-battery pre-flight warning (R14), Live-Photo detection + `summary.txt` `Live Photos: N pairs`. **Closed #11 + #10.** Independent 4-lens code review PASS; merged PR #16. **QA Stage 2 hardware acceptance deferred → issue #21 (pre-release gate).** |
| 3 | Hardening, Packaging & Release | ✅ Done | **A engine** (long-path `\\?\` R6, opt-in `--verify-hash` R12, #17 polish, large-file), **B release** (`release.yml` single-file EXE on `v*` tag, EXE `--help` smoke + gitleaks in CI, LICENSE/metadata), **C docs** (README, troubleshooting, manifest-schema, release notes). **#26** win-x64 build trim. `ReadOnlyContractTests` intact. Shipped in the combined v1.0 line (PR #47). |
| 3.1–3.4 | Unplug hotfixes — bug beaten as a **class** (#25 → #38 → #42 → #45) | ✅ Done | The mid-copy USB-disconnect bug, fixed across five hardware rounds. 3.1 (#25) watchdog over native open/stat/list; 3.2 (#38 + #39 long-path journal) between-file breaker; 3.3 (#42) the universal run-level **forward-progress watchdog** on the byte heartbeat; **3.4 (#45) the disconnect escape-hatch** — on a dead device the watchdog's independent timer thread writes `summary.txt` and **hard-terminates with a finalizer-skipping `TerminateProcess(3)`** so nothing re-enters the spinning native layer. **QA #21 Phase B PASS on hardware** (5 cable-yanks → exit 3 every time, CPU to idle, byte-exact resume, read-only intact). Merged in PR #47 (closed #45/#42/#38/#39). New **three-layer review** (§13.6) debuted here. Handoff: `docs/sprint-3.4/done.md`. |
| 3.5 | Test harness: fake device & fault injection | 🔄 Dev done — PR open, gated | **Inner-loop net for the feature batch.** `FakeAfcDevice : IPhoneClient` over a fluent virtual `/DCIM` spec + one `ScriptedReadStream` fault model (folds in the 3 ad-hoc fakes) + an in-process `CopyPipelineHarness` driving the REAL pipeline (enumerate → journal → organizer → copier → watchdog → escape-hatch → summary). 30 new tests: full-copy **byte-identical**, **resume byte-identical** after every managed disconnect shape (park / between-file / premature-EOF / spin / connection-fatal / disconnect-at-N) → exit-3, plus organize / collisions / `unsorted` / `--verify-hash`. **All test-only — `src/` untouched, `get-and-see.exe` byte-identical, `ReadOnlyContractTests` green.** Env-var EXE seam **deferred** (rationale in `done.md`); native-disconnect hardware-smoke boundary stated. Dev self-review PASS-WITH-NITS. **PR #56 open at the producer review gate.** Handoff: `docs/sprint-3.5/done.md`. |

## 8. Current State

**Repo:** https://github.com/denis-a-evdokimov/get-and-see (**private** on free tier, MIT). **v1.0.0 SHIPPED** — `main` carries the full Sprint 1 → 3.4 line (PR #47 merged after the QA #21 Phase B hardware PASS); the `v1.0.0` tag arms `release.yml` to publish the single-file win-x64 EXE + SHA-256. (Git history was scrubbed 2026-06-07 to redact a real device UDID committed in a Sprint-1 smoke-test doc; all SHAs before that point changed. Backup mirror retained at `e:\scratch\gas-backup.git`.)

**Clones in play (re-sync to `main` after the v1.0.0 merge):**
- Producer: `e:\src\get-and-see` — coordination hub (this chat)
- Dev: `e:\src\get-and-see-dev` — on `feature/sprint-3.5` (Sprint 3.5 fake-device harness, **PR #56 open/gated**)
- QA: `e:\src\get-and-see-qa` — re-sync, then `feature/qa-2` (#21, parallel)
- DevOps: `e:\src\get-and-see-devops` — used for Sprint 3 Track B (release)

**What works (Sprints 1 & 2 shipped):**
- CI: restore + `dotnet format` + build + tests on `windows-latest`
- Read-only `IPhoneClient` over AFC; `ReadOnlyContractTests` green — no device-write symbol anywhere in `GetAndSee.Core`
- `/DCIM/` enumerate → EXIF date-org → atomic copy → `get-and-see.db` + `manifest` + `summary.txt`; journal-aware resume
- **Watchdog (#11):** `--read-timeout` (default 30s) → abandon read → `DeviceStallException` → in-flight file non-done (resumable) → clean stop → exit 3. No native handle leak; unit-tested via mocked stalling stream.
- **Live dashboard (#10):** Spectre, current/avg MB/s + ETA, ≤4 Hz, no device I/O, graceful text fallback / `--no-dashboard`.
- **`status --dest`** (no device); **`runs`/`devices` tables** feeding `summary.txt`; **on-battery warning** (R14); **Live-Photo detection** + `Live Photos: N pairs`.

**Sprint 3 + 3.1–3.4 — SHIPPED in v1.0.0** (combined line, PR #47 merged after the QA #21 Phase B hardware PASS):
1. **Track A engine:** long-path `\\?\` (R6); opt-in `--verify-hash` (R12) — SHA-256 in the same read pass → `sha256` manifest column, **read-only**, zero cost when absent; #17 polish (StatusCommandTests, scratch zero, sync `Read()` throws, EUII fixture rename); large-file multi-chunk test. **Closes #17.**
2. **Track B release:** `release.yml` (single-file win-x64 EXE on `v*` tag → SHA-256 → GitHub Release) — **lands dormant**; published-EXE `--help` smoke + gitleaks secret-scan added to CI; LICENSE/metadata.
3. **Track C docs (Quill):** README (+ "What this does NOT do", redacted dashboard), troubleshooting (exact strings), manifest-schema, release-notes template + v1.0 notes.
4. **#26 win-x64 build trim** folded in: pins the build to win-x64 so the foreign (osx/linux/maccatalyst) `imobiledevice-net` native libs no longer land in `bin/` — silences the known Defender FP. No `.dylib`/`.so` under any `bin/`; win-x64 AFC natives intact.
5. **Release gate:** **#21 QA Stage 2 hardware acceptance MUST PASS before the `v1.0` tag is cut.**

**Open actions:** v1.0.0 shipped. **Sprint 3.5 (fake-device test harness) is dev-done — PR #56 open at the producer review gate** (the inner-loop net that the feature batch will build on; sequencing 3.5 → 4 → 4.1 → 5). Post-v1.0 backlog (not blockers): #34/#35/#36 (device defense-in-depth follow-ups — note #36's `AfcIPhoneClient`→`GuardAsync` wiring guard is **not** covered by the 3.5 fake, which replaces that layer), #23 (no-`var` enforcement), and the next feature batch — year-month folder organization + a richer terminal UI (brainstorms in `docs/brainstorm/`). All gates green on `main`: 131 + 2 tests, build 0/0, format clean, single-file EXE `--help` OK, gitleaks clean, read-only contract intact. (On `feature/sprint-3.5`: 161 + 2 tests green, `src/` unchanged.)

**Blockers:** None.

**Open issues:** #21 (QA Stage 2 hardware — release gate, open). #17 (Sprint 2 review polish) — **resolved in the Sprint 3 PR** (Closes #17 on merge).

> **Sprint 3.5 — fake-device test harness (dev-done, PR #56 open/gated):** a scriptable `FakeAfcDevice` + one `ScriptedReadStream` fault model + an in-process `CopyPipelineHarness` that drives the **real** copy pipeline with no hardware, so the disconnect/resume/organize/verify logic is a deterministic CI regression net for the upcoming feature batch. **Entirely in `tests/` — no shipped-binary change; `ReadOnlyContractTests` green.** The env-var EXE seam (`GAS_FAKE_DEVICE`) was **deferred** (would need a new opt-in assembly + a CLI startup seam → risk to the byte-identical default run; the in-process harness already covers the exit-3 + byte-identical-resume logic). **Honest boundary:** the managed fake reproduces managed-observable failures + the exit-3→resume logic, **not** the native `afc_file_close` core-pin or the real `TerminateProcess` — a small hardware cable-yank smoke stays authoritative. Detail: `docs/sprint-3.5/done.md` + `progress.md`.

> **Sprint 3.1–3.4 unplug hotfixes — DONE, bug beaten on hardware:** the v1.0 gate (#21 Phase B) surfaced that the mid-copy USB-disconnect bug kept relocating — #25 (open-park) → #38 (between-file spin) → #42 (intra-file spin) → **#45 (`afc_file_close` disposal-spin)**. Fixed as a **class**: one run-level **forward-progress watchdog** on the byte heartbeat (3.3) plus a **disconnect escape-hatch** (3.4) that, on a dead device, writes `summary.txt` and hard-terminates with a finalizer-skipping `TerminateProcess(3)` from the watchdog's independent timer thread — never re-entering the spinning native layer. **QA #21 Phase B PASS** (5 physical cable-yanks → exit 3 every time, CPU to idle, byte-exact resume, read-only intact). Shipped in PR #47 (closed #45/#42/#38/#39). Detail: `docs/sprint-3.4/done.md`.

**Known gaps (accepted, not blockers):**
- **Branch protection on `main` is NOT enforced.** The repo is private on free GitHub tier, which restricts both classic protection and rulesets to paid/public repos. Decision: stay private for now and enforce "no direct pushes to main" by discipline. Documented in `docs/sprint-1/branch-protection-setup.md`. Revisit if the repo goes public or upgrades.
- **Aging native deps in `imobiledevice-net`** (OpenSSL 1.1 EOL, 2021 libusb). Local-USB-only, read-only, no network — minimal exposure. Tracked as R20 in the risk register. **Sprint 3 #26** pins the build to win-x64, so the package's unused osx/linux/maccatalyst native libs (which Windows Defender flags as a known false positive, `Exploit:MacOS/LimeRain.C!MTB`) no longer restore into `bin/`.
- **"Apple Devices" Store-app usbmuxd service is lazy** (port 27015 closed until the app is launched once). Pre-flight check must detect + give actionable error. Tracked as R21.

**Prerequisites for the dev machine:**
- .NET 10 SDK (10.0.x — current LTS)
- Apple device USB drivers — installed by either iTunes for Windows OR the "Apple Devices" app from the Microsoft Store
- iPhone unlocked and "Trust This Computer" tapped at least once

## 9. Security & Data Safety Rules

This section is treated as **architectural**, not just a checklist. Violations are build failures, not review comments.

### 9.1 Device safety (the most important rule)

**The iPhone is never written to. Ever. By design.**

1. The AFC client wrapper (`IPhoneClient`) exposes **only** read operations: `ListDirectory`, `GetFileInfo`, `OpenRead`. Write/delete methods on the underlying library are not exposed.
2. A unit test (`ReadOnlyContractTests`) scans the compiled assemblies via reflection for any reference to AFC write methods (`AfcWriteFile`, `AfcRemovePath`, `AfcMakeDirectory`, `AfcRenamePath`, `house_arrest` write mode, etc.). If any reference is found, the test fails. **Build fails.**
3. No CLI flag exists that toggles into a write/delete/move mode. There is no `--delete-after-copy`, no `--move`, no `--cleanup`. Adding one is a design conversation, not a code change.
4. README states this explicitly: "This tool never writes to or deletes from your iPhone."

**Testing-safety rule (how the contract is verified — read this before writing any test):**
The read-only contract is proven by **static analysis, never by attempting a write.** `ReadOnlyContractTests` inspects compiled assembly *metadata* (like a grep over symbols) — it does not connect to a device or issue any command. No test, anywhere, may call a write/delete/rename API:
- **There is nothing to call.** The wrapper binds only the six read functions; no `DeleteAsync`/`WriteAsync` method exists in our code, so a write call cannot even compile.
- **QA never "tests the block by trying to delete."** Verification is "prove the dangerous code is absent," not "call it and check the phone refused." The scenario *"the app let me delete something, test failed"* is structurally impossible because no write command is ever issued.
- **Unit/integration tests run against a mocked `IPhoneClient`** (NSubstitute) returning canned listings/streams — not a real device.
- **The only real-device interaction is a read-only acceptance copy.** The "read-only proof" is a passive before/after `/DCIM` listing diff (must be empty) — an observation, never a mutation.

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

### 13.6 Review — three independent layers

Every PR that touches application source passes through **three complementary review layers** (defense-in-depth — each catches what the previous shares blind spots on):

1. **Dev self-review (author-run, pre-PR).** Before opening/updating a PR the dev team runs the `code-review` skill on its own diff with a *"find problems"* framing (never "confirm my approach") and **summarizes the findings in the PR**. Catches the obvious early (build/test green, conventions, missing tests, failing-repro-first, did-I-build-the-plan) and saves a Producer round-trip. Scale to the change; use the **default subagent**, not `Explore`.
2. **Producer independent gate (merge decision).** The Producer commissions **one reviewer independent of the authors** with a *risk-targeted per-sprint checklist*, across 5 gate lenses (Security, Correctness, Performance, **Simplicity/Design/Architecture**, Maintainability) + 1 advisory lens (Modernization → ideas-backlog). The per-project profile lives at `docs/review/review-profile.md` and carries an **Architecture & drift-watch** — a longitudinal list of hotspots (growing files/methods, duplication clusters, abstraction debt) that escalate to a blocking finding when a change touches them again or busts a budget, so the codebase can't slide into a big-ball-of-mud across many small changes. Reports land in `docs/review/<change-id>-review.md`.
3. **QA (behavioral / hardware).** The ultimate ground truth — shares zero assumptions with the code.

The self-review does **not** lower the bar for the Producer gate or QA — it is an early filter, not a free pass. Structural review never replaces behavioral/hardware QA — engine/safety sprints get all three. Skip only for docs-only / trivial PRs.

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

**Branch strategy:** Feature branches → PR → regular merge to `main`. Never push directly to `main`. Never squash. Never rebase feature branches (causes commit loss). PRs require CI to pass before merge.

> **Note (2026-06-06):** Branch protection is **not enforced** on the GitHub side because the repo is private on free tier (rulesets and classic protection both require Pro or public visibility). The "no direct pushes" and "PR-required" rules above are enforced **by discipline only**. Revisit if the repo goes public or upgrades to Pro. See `docs/sprint-1/branch-protection-setup.md`.

**PR conventions:**
- Title: imperative present (`add atomic copier`, not `added atomic copier`)
- Description: short summary + `Closes #NN` for any issues fixed
- Use the PR template (`.github/PULL_REQUEST_TEMPLATE.md`) — checklist for linked issues, tests added, brief updated
- One reviewer (Remy) merges. Producer never authors application code, only reviews and merges.
