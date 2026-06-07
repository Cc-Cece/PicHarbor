# Sprint 3 — Hardening, Packaging & Release (v1 ship)

**Sprint Goal:** Ship a trustworthy, documented **v1.0** of get-and-see: harden the engine (long-path, opt-in hash verification), build the release pipeline (single-file EXE → GitHub Release on tag), and write the user docs — gated on a full real-hardware QA acceptance.

**Branch:** `feature/sprint-3` (dev) · runs in **parallel** with `feature/qa-2` (QA Stage 2, issue #21)

> **Read first:** `PROJECT_BRIEF.md` (§9 Safety still governs; §11 release pipeline; §13.6 review gate), `docs/brainstorm/sprint-3-consilium.md` (the agreed tracks + cut line), `docs/review/review-profile.md` (the merge-gate profile — EUII + secrets + sacred invariants), `docs/risk-register.md` (R6, R12, R16, R18).
>
> **Carry-over:** **#21** (QA Stage 2 hardware — gates the release) and **#17** (Sprint 2 review polish, incl. EUII fixture rename).

## Execution mode: 3 parallel tracks + 1 release gate

```
Track A (engine)  ─┐
Track B (release) ─┼─► all merge to main via PR + review gate
Track C (docs)    ─┘
Gate: #21 QA Stage 2 (parallel, hardware) ──► MUST PASS before the v1.0 tag is cut
```

## Prioritized Task List

| # | Task | Track | Owner | Est | Description |
|---|------|-------|-------|-----|-------------|
| 1 | **Long-path `\\?\` support (R6)** | A | Sage | M | Normalize every destination path through a long-path-aware helper (`\\?\` prefix and/or `<LongPathsEnabled>`). **Unit-test with a synthetic >260-char path.** Non-cuttable — it's a latent correctness bug at depth. |
| 2 | **`--verify-hash` (R12)** | A | Sage/Nova | M | Opt-in SHA-256 computed **during copy** (single pass over the existing stream buffer) → write to the `sha256` manifest column. **Read-only; zero cost when the flag is absent** (default copy byte-identical to Sprint 2). `--help` + README one-liner. Verify-only re-check mode = Sprint 4. |
| 3 | **#17 polish** | A | Nova | M | `StatusCommandTests` (mock journal: missing-db exit code, output sections); zero the `WatchdogReadStream` scratch buffer on reuse; remove or test+document the sync `Read()` override; **EUII: replace the real first name in `RemoteModels.cs` (XML doc) + `SummaryWriterTests.cs` (fixture+assertion) with a synthetic name** (e.g. "Sample iPhone"). Keep `ReadOnlyContractTests` green. Closes #17. |
| 4 | Large-file correctness | A | Ivy/Sage | S | Confirm a multi-GB file (ProRes/4K) copies, size-verifies, and `--verify-hash`es correctly (unit/integration where possible; full check in #21 on hardware). |
| 5 | **`release.yml`** | B | Dash | M | Trigger on tag `v*`: `dotnet publish` single-file self-contained **win-x64** with `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`; compute SHA-256; create GitHub Release; attach `get-and-see.exe` + `get-and-see.exe.sha256`. `GITHUB_TOKEN` only. Lands **dormant** (no tag yet). |
| 6 | **Published-EXE smoke test in CI** | B | Dash | S | After publish (in `ci.yml` or a job), run the single-file `get-and-see.exe --help` on a clean `windows-latest` runner — proves the native DLLs unpack and the EXE actually runs before any release is trusted. |
| 7 | **gitleaks secret-scanner in CI** | B | Dash | S | Add a gitleaks (or trufflehog) job to `ci.yml` — pre-public security gate (complements the review skill's secrets lens). Fail the build on a detected secret. |
| 8 | LICENSE polish + public-repo metadata | B | Dash | S | Confirm MIT LICENSE (© 2026 owner), repo description/topics, ensure no EUII in any committed sample before public. |
| 9 | **`README.md`** | C | Quill/Kira | M | Overview; **"What this tool does NOT do"** (R19 — no Apple Photos albums/keywords/favorites/faces; wording per brief §2); prerequisites + install/run; the read-only promise; a **redacted** dashboard screenshot (no real paths/UDIDs). |
| 10 | **`docs/user/troubleshooting.md`** | C | Quill | M | Real failure modes with the **exact emitted error strings**: no device (AC-13), locked/untrusted (AC-14), **R21 Apple Devices service not running** (#1 predicted ticket), disk-full (AC-15), watchdog stall (#11), long-path. Pull strings from the dev track once frozen. |
| 11 | **`docs/user/manifest-schema.md`** | C | Quill | S | `get-and-see.db` schema (`files`, `runs`, `devices`) + the `manifest` view + example SQL queries (the "substrate, not app" promise). Cuttable-to-shallow only if the sprint runs long. |
| 12 | Release-notes template + v1.0 notes | C | Quill | S | `docs/release-notes-template.md` + the v1.0 changelog (Sprints 1–3 summary, read-only guarantee, known limitations). |
| 13 | **#21 QA Stage 2 hardware acceptance (RELEASE GATE)** | Gate | Ivy | L | Full uninterrupted ~269 GB run (closes S1 AC-7/AC-17: full archive + live `summary.txt`); **real cable-unplug watchdog test**; dashboard-no-slowdown; read-only B-9 re-proof; plus Sprint-3 new-code checks (long-path, `--verify-hash`, EXE smoke). Write `docs/qa/sprint-3-signoff.md`. **PASS is the precondition for cutting `v1.0`.** SAFETY RULE #0 governs. |
| 14 | XML doc comments on new public API | A | Nova/Sage | S | Every new public `GetAndSee.Core` type gets a `<summary>`. |
| 15 | `docs/sprint-3/progress.md` + `done.md` + handoff | A | Nova | S | Per PROJECT_BRIEF §12. |

**Size:** S = <1hr, M = 1–3hr, L = 3–5hr (or hardware-time for #13).

## Work Schedule

- **Day 1 (all tracks start):**
  - Track A: long-path (#1) + #17 polish (#3) → commit.
  - Track B: `release.yml` (#5) + EXE smoke (#6) + gitleaks (#7) → commit (dormant).
  - Track C: README skeleton (#9) + manifest-schema (#11) start.
  - **Gate:** Ivy starts #21 hardware run in `feature/qa-2` (needs only the phone + Sprint-2 main).
- **Day 2:**
  - Track A: `--verify-hash` (#2) + large-file (#4) → commit. **Engine flags freeze here.**
  - Track C: troubleshooting (#10) + `--help` finalize (now that flags are frozen) → commit.
- **Close:** tests (#14), handoff (#15), single dev PR `sprint-3: hardening, packaging & release`. QA #21 sign-off lands its own PR.

Update `docs/sprint-3/progress.md` after each phase.

## Success Criteria

- [ ] `dotnet test` green incl. `ReadOnlyContractTests` + new long-path / verify-hash / status tests
- [ ] CI green (build + format + tests + **gitleaks** + **published-EXE `--help` smoke**) on PR
- [ ] **Long-path:** a >260-char destination path copies without error (synthetic test passes)
- [ ] **`--verify-hash`:** flag computes SHA-256 into the manifest; a tampered byte is detected; default (no flag) path is unchanged and not slowed; stays read-only
- [ ] **#17 closed:** StatusCommandTests added; scratch buffer zeroed; sync `Read()` resolved; **no real personal name in any fixture/source**
- [ ] **`release.yml`** produces a single-file `get-and-see.exe` + `.sha256` attached to a GitHub Release on a `v*` tag; the EXE runs `--help` on a clean runner
- [ ] **README** with the "What this does NOT do" section; **troubleshooting** covers AC-13/14, R21, disk-full, watchdog, long-path with real strings; **manifest-schema** documents the db + example SQL
- [ ] **Read-only contract intact** — `ReadOnlyContractTests` passes; no new device-write symbol; verify-hash + long-path are read-only
- [ ] **#21 QA Stage 2 PASS** — full hardware run + watchdog unplug + read-only B-9 proof; `docs/qa/sprint-3-signoff.md` written
- [ ] **v1.0 tagged only after #21 PASS** → GitHub Release published

## What's NOT in This Sprint

| Cut | Reason |
|-----|--------|
| `--verify-hash` verify-only re-check mode (no re-copy) | Ship during-copy hash now; re-check = Sprint 4 fast-follow |
| Auto-reconnect mid-run (R2 soft recovery) | Watchdog stop+resume is the v1 contract; reconnection = post-v1 |
| `SetThreadExecutionState` keep-awake (R13) | Sprint 2 only warns; prevention = post-v1 |
| macOS, HEIC convert, geocoding, smart-folder views, GUI, album export | Post-v1 / v2 backlog |
| GitHub Support GC of old PR-diff objects (UDID in `refs/pull/*`) | Only if/when repo goes public; private = nil exposure |

## Dev + QA Team Prompt

```
You are the get-and-see team. Execute Sprint 3 (the v1 ship) in PARALLEL tracks.

DEV (Nova, Sage, Dash, with Kira+Quill on docs), in e:\src\get-and-see-dev:
  git checkout main && git fetch origin && git reset --hard origin/main
  git checkout -b feature/sprint-3

Read: PROJECT_BRIEF.md (§9 Safety, §11 release, §13.6 review gate),
docs/brainstorm/sprint-3-consilium.md, docs/sprint-3/plan.md,
docs/review/review-profile.md (the merge-gate profile — EUII + secrets +
sacred invariants), docs/risk-register.md.

Track A (engine): long-path \\?\ (R6, synthetic >260-char test) · --verify-hash
  (opt-in SHA-256 during copy → sha256 manifest column; READ-ONLY; zero cost when
  flag absent) · #17 polish (StatusCommandTests, zero scratch buffer, resolve sync
  Read(), and RENAME the real first name in RemoteModels.cs + SummaryWriterTests.cs
  to a synthetic name) · large-file check.
Track B (release): release.yml (tag v* → single-file win-x64 EXE +
  IncludeNativeLibrariesForSelfExtract=true → SHA-256 → GitHub Release) · published-
  EXE --help smoke test in CI · gitleaks in CI · LICENSE/metadata. Lands DORMANT.
Track C (docs, Quill+Kira): README (+ "What this does NOT do", redacted screenshot)
  · troubleshooting (real error strings — finalize AFTER engine flags freeze) ·
  manifest-schema (db + example SQL) · release-notes template + v1.0 notes.

HARD RULES (unchanged): IPhoneClient read-only; no new device-write symbol in
GetAndSee.Core; ReadOnlyContractTests must pass; --verify-hash and long-path stay
read-only. No CLI flag mutates the device. No real EUII (names, UDIDs, GPS, user
paths) in any committed source, fixture, doc, or screenshot.

Process: all dev work on feature/sprint-3; commit + update docs/sprint-3/progress.md
per phase; use the PR template; reference issues ("Closes #17"); CI green before the
final PR. Never push to main. When done, open ONE PR "sprint-3: hardening, packaging
& release" and stop for the producer review gate.

QA (Ivy), in e:\src\get-and-see-qa, IN PARALLEL from day one (needs only the phone):
  git checkout main && git fetch origin && git reset --hard origin/main
  git checkout -b feature/qa-2
  Execute #21: full uninterrupted ~269 GB run (closes S1 AC-7/AC-17), real cable-
  unplug watchdog test, dashboard-no-slowdown, read-only B-9 before/after /DCIM proof.
  After the dev PR lands, also verify the new code (long-path, --verify-hash, the
  published EXE smoke). Write docs/qa/sprint-3-signoff.md. SAFETY RULE #0 — never
  issue a device write, ever.

DO NOT cut the v1.0 tag until #21 (QA Stage 2) signs off PASS. The release workflow
is dormant until then.

Take your time — this is the v1 ship. Correctness, the read-only contract, and an
honest README beat speed.
```
