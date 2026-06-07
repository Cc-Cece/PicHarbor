# Sprint 3 Consilium — Hardening, Packaging & Release (the v1 ship)

**Date:** 2026-06-07
**Format:** Consilium (pre-sprint validation). Each agent reviews the proposed Sprint 3 scope, debates priorities, flags risk. We converge on a prioritized, **parallelized** task list.
**Facilitator:** Remy (Producer)
**Participants:** Kira (Product/UX), Nova (App), Sage (Systems), Ivy (QA), Quill (Tech Writer), Dash (DevOps), Remy (Producer)

---

## 0. Where Sprint 2 left us (the ground truth)

Sprint 2 shipped (PR #16): read-stall watchdog (#11), live dashboard + current speed (#10), `status` subcommand, `runs`/`devices` tables, Live-Photo pairing. Independent 4-lens code review **PASSED**. It carries forward:

- **Open #21** (`qa`, pre-release obligation): **QA Stage 2 hardware acceptance was deferred.** A full uninterrupted ~269 GB run (closes Sprint-1 AC-7/AC-17) + a real cable-unplug watchdog test + dashboard-no-slowdown verification. **This gates the release.**
- **Open #17** (`qa`, Sprint 3 polish): from the Sprint 2 review — add `StatusCommandTests`; zero the watchdog scratch buffer; remove/test the sync `Read()` override; **EUII: replace the real first name in `RemoteModels.cs` + `SummaryWriterTests.cs` fixtures with a synthetic one.**
- **Decision constraint:** **#21 (QA Stage 2) must PASS before the release workflow merges / a tag is cut.** No release on an unverified engine.

**Proposed Sprint 3 scope (brief §7):** long-path `\\?\` (R6) · `--verify-hash` (R12) · large-file stress · release workflow (single-file EXE on tag → GitHub Release) · full user docs (Quill: README, troubleshooting, manifest-schema, release-notes) · LICENSE polish. Risk bucket: R6, R12, R16 (re-verify), R18.

**Execution mode: PARALLEL (option B).** Sprint 3 dev work runs while QA executes #21. The release pipeline is built but only *armed* (first tag cut) after #21 passes.

---

## 1. Agent reviews

### Sage (Systems) — "Long-path and verify-hash are the two engine items; both are contained."

- **Long-path `\\?\` (R6):** On Windows, paths > 260 chars throw cryptic IO errors unless prefixed. Our date-folder + original-filename scheme rarely hits it, but a deep `<dest>` + long filename can. Fix: normalize every destination path through a `\\?\`-aware helper (or set `<LongPathsEnabled>` + manifest the app). I want this **unit-tested with a synthetic >260-char path**, not just hoped-for. Low risk, well-bounded.
- **`--verify-hash` (R12):** opt-in SHA-256 of each copied file, recorded in the `sha256` manifest column (already nullable, reserved for this). Two modes worth distinguishing: (a) **during copy** — hash the bytes as they stream (cheap, single pass); (b) **verify-only re-check** against an existing archive (no re-copy). I'd ship (a) in Sprint 3 and note (b) as a fast-follow. Must stay **read-only** and **not slow the default path** (only when the flag is set).
- These two are independent of the docs/release tracks → clean parallelization.

### Dash (DevOps) — "The release workflow is straightforward; the gate is the discipline."

- **`release.yml`:** trigger on tag `v*`; `dotnet publish` single-file self-contained win-x64; compute SHA-256; create the GitHub Release; attach `get-and-see.exe` + `.sha256`. The brief already specs this (§11.3). `GITHUB_TOKEN` suffices — no secrets.
- **Hard sequencing:** the workflow file can land early (it does nothing until a tag), but **the first tag must not be cut until #21 (QA Stage 2) passes.** I'll add a one-line guard in the release notes/checklist: "Do not tag before QA Stage 2 sign-off."
- **Repo-going-public checklist** (from the EUII work): I'll also wire a **secret-scanner (gitleaks) into CI** this sprint — cheap, and it's the right gate before any public release. That overlaps the security posture nicely.
- One caution: single-file + native `imobiledevice-net` DLLs — `IncludeNativeLibrariesForSelfExtract=true` must be set or the win-x64 natives won't unpack. We learned the RID lesson in Sprint 1; same family of issue. Test the published EXE actually runs (`--help`) in CI before trusting the release.

### Quill (Tech Writer) — "This is my sprint. Docs are a deliverable, not an afterthought."

Three user-facing docs, written against the *actual* shipped behavior:
1. **`README.md`** — what it is, the **"What this does NOT do"** honesty section (R19 — Apple Photos albums/keywords not preserved, already locked in brief §2), install/run, the read-only promise, a dashboard screenshot.
2. **`docs/user/troubleshooting.md`** — the real failure modes we *designed for*: "iPhone not detected" (AC-13), "Trust dialog" (AC-14), **R21 "Apple Devices service not running → open it once"** (the #1 predicted support ticket), disk-full (AC-15), watchdog stall message (#11), long-path. I'll pull the exact error strings the dev team emits so docs match reality.
3. **`docs/user/manifest-schema.md`** — the `get-and-see.db` schema + `manifest` view + example SQL (the "substrate, not app" promise from Session 3). This is what makes the archive *queryable* — power users need it documented.
4. **Release-notes template** + the v1.0 notes themselves.

Dependency: I need the dev track's final error strings and the `--verify-hash`/long-path flags settled before I finalize troubleshooting + `--help`. So docs **start in parallel** but **finalize after** the engine items land. Fine.

### Ivy (QA) — "#21 is the headline and it's mostly hardware time. Don't release without it."

- **#21 QA Stage 2** is my main deliverable and it runs in parallel from day one — it needs the phone, not the new code. It closes the Sprint-1 deferrals (full 269 GB run + live `summary.txt`) and proves the watchdog on a real cable-yank. **Verdict gates the release.**
- For the **new** Sprint 3 code: long-path (synthetic >260-char path test), `--verify-hash` (hash matches a known vector; tamper a byte → detected; read-only preserved), and the published **EXE smoke test** (does the single-file actually run on a clean box?).
- **#17 polish** items are mine to verify once dev fixes them (StatusCommandTests, scratch-buffer zeroing, sync Read removal, the EUII fixture rename).
- SAFETY RULE #0 still governs every hardware test — no device writes, ever.
- One ask: the release EXE must be tested **before** we tag, not after. A broken first release is a bad look for v1.

### Kira (Product/UX) — "v1 polish is about the first five minutes."

- The README's first screen and the troubleshooting tone are the product's face. I'll co-own copy with Quill.
- `--verify-hash` needs a one-line "what this does / when to use it" in `--help` and README ("slower; for the cautious — verifies every file byte-for-byte").
- The dashboard screenshot in the README should show a *real* run (redacted per EUII rules — no real paths/UDIDs in the screenshot).

### Nova (App) — "Wire the flags cleanly; keep the engine untouched where I can."

- `--verify-hash` and any long-path flag go through the existing `System.CommandLine` surface; the copy engine gets the hash hook in the **existing** stream loop (we already count bytes there — hashing is one more pass over the same buffer). No new architecture.
- I'll own the **#17 polish** fixes too (StatusCommandTests, scratch zeroing, sync Read decision, EUII fixture rename) — small, and they keep the review profile's blind-spot list shrinking.
- The single-file publish profile is mostly config; Dash and I pair on it.

### Remy (Producer) — "Three parallel tracks, one hard gate."

I'm hearing three tracks that genuinely parallelize, plus one release gate. Let me lock it.

---

## 2. Debate & convergence

**Remy:** Biggest sequencing risk?

**Dash:** Someone cuts the `v1.0` tag before #21 passes and we ship an unverified engine. Mitigation: the tag is a deliberate, last step, and the plan says so in bold. The workflow file landing early is safe — it's dormant until a tag.

**Ivy:** Agreed, and I'll make my #21 sign-off the literal precondition in the release checklist.

**Sage:** Second risk — `--verify-hash` accidentally slowing the *default* (non-flagged) path. Keep hashing strictly behind the flag; default copy is byte-for-byte identical to Sprint 2.

**Quill:** My risk — docs drifting from behavior. I finalize troubleshooting/`--help` only after the engine flags are frozen. Start early, finalize late.

**Remy:** What's cuttable if we run long?

**Nova:** `--verify-hash` **verify-only re-check mode** (Sage's mode-b) — ship the during-copy hash now, defer re-check. And the manifest-schema doc could slip to a v1.1 docs pass if truly needed, though I'd rather keep it.

**Kira:** Don't cut the README honesty section or troubleshooting — those are the v1 face. Cut depth elsewhere first.

**Sage:** Long-path is **not** cuttable — it's a correctness bug waiting at scale (deep dest + long names). Keep it.

**Remy:** Agreed. Cut order if needed: verify-only re-check (already deferred) → manifest-schema depth → nothing else. Long-path, release pipeline, README+troubleshooting, and #21 are all non-negotiable for a v1 ship.

---

## 3. Converged Sprint 3 scope (3 parallel tracks + release gate)

### Track A — Engine hardening (Sage + Nova)
1. **Long-path `\\?\` support (R6)** — normalize all dest paths; synthetic >260-char unit test. Non-cuttable.
2. **`--verify-hash` (R12)** — opt-in SHA-256 during copy → `sha256` manifest column; read-only; zero cost when flag absent. (Verify-only re-check mode = Sprint 4 fast-follow.)
3. **#17 polish** — `StatusCommandTests`, zero watchdog scratch buffer, remove/justify sync `Read()`, **EUII fixture rename** (real name → synthetic).
4. **Large-file stress** — verify a multi-GB file (ProRes/4K) copies + hashes correctly.

### Track B — Release & CI (Dash + Nova)
5. **`release.yml`** — tag `v*` → single-file self-contained win-x64 publish (`IncludeNativeLibrariesForSelfExtract=true`) → SHA-256 → GitHub Release with EXE + `.sha256`.
6. **Published-EXE smoke test in CI** — the built single-file runs `--help` on a clean runner before we trust it.
7. **gitleaks secret-scanner in CI** — pre-public security gate.
8. **LICENSE polish** + repo metadata for going public.

### Track C — User docs (Quill + Kira)
9. **`README.md`** — overview, **"What this does NOT do"** (R19), install/run, read-only promise, redacted dashboard screenshot.
10. **`docs/user/troubleshooting.md`** — real failure modes + the exact emitted error strings (AC-13/14, R21, disk-full, watchdog, long-path).
11. **`docs/user/manifest-schema.md`** — `get-and-see.db` schema + `manifest` view + example SQL.
12. **Release-notes template + v1.0 notes.**

### Release gate (Ivy) — runs in PARALLEL, blocks the tag
13. **#21 QA Stage 2 hardware acceptance** — full uninterrupted ~269 GB run (closes S1 AC-7/AC-17), real cable-unplug watchdog test, dashboard-no-slowdown, read-only B-9 re-proof. **Plus** Sprint-3 new-code verification (long-path, verify-hash, EXE smoke). **Sign-off (`docs/qa/sprint-3-signoff.md`) is the precondition for cutting `v1.0`.**

### Explicitly OUT (post-v1)
- `--verify-hash` verify-only re-check mode (Sprint 4)
- Auto-reconnect mid-run (R2 soft recovery), `SetThreadExecutionState` keep-awake (R13)
- macOS, HEIC convert, geocoding, smart-folder views, GUI, album export via house_arrest
- GitHub Support GC of old PR-diff objects → only if/when the repo is made public

### Risk coverage
R6 (long-path), R12 (`--verify-hash`), R16 (re-verify EXIF clamp on hardware), R18 (network-drive dest — document + spot-check). R20 remains accepted.

---

## 4. Consilium vote

- **Sage** ✅ "Long-path + verify-hash are bounded and read-only-safe. Long-path stays non-cuttable."
- **Nova** ✅ "Flags go through the existing surface; #17 polish shrinks the blind-spot list."
- **Dash** ✅ "Release pipeline lands dormant; tag only after #21. gitleaks before public."
- **Quill** ✅ "Docs start parallel, finalize after flags freeze. README honesty + troubleshooting are non-negotiable."
- **Ivy** ✅ "#21 runs from day one and gates the tag. EXE tested before release, not after."
- **Kira** ✅ "v1's first five minutes — README + troubleshooting carry it."
- **Remy** ✅ "Three parallel tracks, one hard release gate (#21). Long-path/release/README/#21 are the must-ships. Cut order is set. Ship v1."

**Unanimous. → `docs/sprint-3/plan.md`.**
