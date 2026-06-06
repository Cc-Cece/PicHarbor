# Sprint 1 — QA Test Plan

**Owner:** Ivy (QA Engineer)
**Branch:** `feature/qa-1`
**Date:** 2026-06-06
**Status:** Stage 1 (written before execution, for the record). Execution happens in Stage 2 after the dev team opens the Sprint 1 PR.

> **Read first:** `PROJECT_BRIEF.md` §2, §9 (the safety contract), §13 (bug filing); `docs/risk-register.md` (R1–R21); `docs/sprint-1/plan.md` (Success Criteria); `docs/sprint-1/afc-library-decision.md` §5 (caveats) + §5.5 (write-method blocklist); `docs/brainstorm/session-3.md` (manifest schema + `summary.txt` format).

This plan covers **what** to test, **how**, the **expected result**, and the **risk(s)** each test covers. It is intentionally executable: every check is a concrete command or query whose output can be pasted into the Stage 2 sign-off.

---

## Testing-Safety — SAFETY RULE #0 (governs this entire plan)

> **This rule overrides everything else in this document.** If any step below appears to conflict with it, this rule wins and that step is invalid. Locked in `PROJECT_BRIEF.md` §9.1.

**We NEVER issue a write, delete, rename, or any mutation against the real iPhone — not as a positive test, not as a negative test, not ever.**

The read-only contract is proven by **static analysis only**:

- `ReadOnlyContractTests` reflects over **compiled assembly metadata** and **fails the build** if any AFC write symbol (Appendix 1) is referenced. It **never connects to a device or sends a command.**
- There is **no write method in our code to call** — by design it would not compile. Verification is *"prove the dangerous code is absent,"* **never** *"call it and see if it failed."* We do **not** "test the block by trying to delete."
- Everything device-related in unit tests uses a **mocked `IPhoneClient` (NSubstitute)** — no real device, no real device I/O.
- The **only** real-device action anywhere in this plan is a **read-only `copy`** (AFC read → PC-side write). The runtime read-only proof (B-9) is a **passive** before/after `/DCIM/` diff, expected empty.

**Negative / failure-path tests are induced by the *host or physical environment*, never by a mutating device command:** physically unplugging USB (B-1, B-2, AC-13), pressing Ctrl+C (B-3, AC-12), locking / un-trusting the device (AC-14), stopping the Apple Devices service (B-7), or pointing at a near-full / unwritable destination (B-4, AC-15). **None of these send a write/delete/rename to the iPhone.**

Any path where the iPhone could be written, deleted, renamed, or otherwise mutated is an automatic **`severity:blocker` + `safety:read-only-contract`**, no matter how unlikely.

---

## 0. Test Environment

| Item | Value |
|------|-------|
| Device | iPhone 12 Pro (`iPhone13,3`), iOS **26.5** |
| Device state | Unlocked, "Trust This Computer" tapped, **Apple Devices app launched once this boot** (R21 — opens usbmuxd on `127.0.0.1:27015`) |
| Host OS | Windows 11, x64 |
| SDK | .NET 10 SDK (`dotnet --version` → `10.0.x`; smoke-tested on `10.0.204`) |
| Driver | "Apple Devices" Store app **or** iTunes for Windows (Apple Mobile Device USB driver) |
| Build | `feature/sprint-1` checked out, `dotnet build -c Release` |
| Destination | A real local volume with ≥ library size free (primary), plus a small/near-full volume for the disk-full test |
| Tools | `sqlite3` CLI (for manifest queries), `Test-NetConnection` (port probe), the project's own `--dry-run` (read-only enumeration snapshot) |

**Coarse device baseline to capture once (used by several tests):** total `/DCIM/` file count and total bytes, via `copy --dest <scratch> --dry-run` (it enumerates read-only and opens no streams). Record these as `N_total` and `Bytes_total`.

---

## A. Acceptance Test Matrix

One row per Success Criterion in `docs/sprint-1/plan.md`. **AC-#** = acceptance criterion id.

| AC | Success Criterion | How to test | Expected result | Risks |
|----|-------------------|-------------|-----------------|-------|
| AC-1 | `dotnet test` passes incl. `ReadOnlyContractTests` | `dotnet test -c Release` | All tests green; output lists `GetAndSee.SafetyTests` with `ReadOnlyContractTests` passing | R11 |
| AC-2 | CI green on PR (build, format, tests) on `windows-latest` | Open/inspect the Sprint 1 PR; view the GitHub Actions `ci.yml` run | Restore + `dotnet format --verify-no-changes` + build + test all pass; `GetAndSee.SafetyTests` present in the test step | R11, CI |
| AC-3 | Branch protection on `main` requires CI to pass | **Doc check only — do NOT actually push to `main`** (protection is unenforced, so a real push would succeed and break the no-direct-push rule). Confirm the setup doc + brief gap note instead. | **KNOWN ACCEPTED GAP** — protection is *not* enforced (repo is private on free tier). Verify `docs/sprint-1/branch-protection-setup.md` exists, is accurate, and §8 of the brief documents the gap. **Do not file a bug.** | — |
| AC-4 | Issue + PR templates render in GitHub UI | Open "New issue" and "New pull request" in the GitHub UI | `bug_report.yml` shows component/severity/steps/expected-vs-actual/environment fields; PR template shows the linked-issues/tests/brief checklist | — |
| AC-5 | Repo labels exist per §13.1 | `gh label list` (or repo → Labels) | All labels present: `bug`,`enhancement`,`infra`,`docs`,`qa`; `severity:blocker/major/minor`; `area:device/copy/journal/cli/ux/docs/ci`; `triage`,`accepted`,`wontfix`,`duplicate`; `safety:read-only-contract` | — |
| AC-6 | `copy --dest D:\test --dry-run` enumerates `/DCIM/`, prints planned dest paths, **opens no AFC read streams, writes no files** | Run dry-run; watch console; `Get-ChildItem -Recurse D:\test` after | Prints planned `YYYY/YYYY-MM/<file>` paths for every source file; **destination is empty** (no folders, no `get-and-see.db`, no `summary.txt`, no `.partial`); read-only proof (B-9) shows device untouched | R11 |
| AC-7 | `copy --dest D:\test` copies all DCIM media to date folders, atomically | Run full copy to a clean dest; on completion `Get-ChildItem -Recurse -Filter *.partial D:\test` | Files organized as `YYYY/YYYY-MM/<file>`; **zero `.partial` files remain**; copied count == `N_total` (minus any genuinely undated → `unsorted/`) | R1, R7, R10 |
| AC-8 | Dest root has `get-and-see.db` (visible) + `summary.txt` | `Test-Path D:\test\get-and-see.db`, `Test-Path D:\test\summary.txt` | Both exist at the **root** (not under a hidden `.get-and-see/`) | — |
| AC-9 | `sqlite3 get-and-see.db "SELECT COUNT(*) FROM manifest"` == copied count | Run the query (see C-1) | Returns the number of successfully copied files; equals the end-of-run "copied + previously-done" total | — |
| AC-10 | `summary.txt` human-readable, matches Session 3 format | Open `summary.txt`; compare to C-10 | Header line, "Last updated" UTC, totals by type, date range, last-run line all present and correctly formatted (see C-10 for S1 caveats on Devices/Runs/Live-Photos lines) | — |
| AC-11 | Second run skips all already-done files | Re-run the exact same `copy` command | End-of-run summary: **copied = 0, skipped = total, failed = 0**; no `.partial`; manifest count unchanged | R8 |
| AC-12 | Ctrl+C leaves journal clean; re-run resumes | Ctrl+C mid-copy, then re-run (see B-3) | After Ctrl+C: exit non-zero, journal flushed, in-flight file is `.partial` (never a final name). Re-run: resumes, retries the interrupted file, skips done; final state 0 pending / 0 in_progress / 0 failed | R3, R10 |
| AC-13 | Clear error when no iPhone connected | Unplug device, run `copy --dest D:\test` | Actionable error (e.g. "no iPhone detected — connect & unlock"); **non-zero exit**; nothing written to dest. **Distinct** from the R21 service-down message | R-device |
| AC-14 | Clear error when iPhone locked / not trusted | Lock device (or revoke trust), run `copy` | Actionable error naming "unlock the iPhone / tap Trust"; non-zero exit; nothing written | R-device |
| AC-15 | Clear error when dest free space insufficient | Point `--dest` at a near-full volume (see B-4) | **Pre-flight bails before any copy** with a clear message stating required vs available; non-zero exit; no `.partial`, no partial tree | R4 |
| AC-16 | Filename collisions across DCIM buckets disambiguated, never overwritten | After full copy, run collision queries (C-5) + B-5 | Colliding names become `_2`, `_3`; **no `dest_path` appears twice**; both source files present and copied (neither overwritten) | R5, R17 |
| AC-17 | End-of-run summary lists total/copied/skipped/failed/elapsed/MB/s | Read console output at end of a run | A summary line/block with all six figures | — |
| AC-18 | Exit code non-zero if any file failed | Force a failure (e.g. unplug mid-run, B-1), read `$LASTEXITCODE` | `$LASTEXITCODE` ≠ 0 when ≥1 file is `failed`; `= 0` only on a fully clean run | R1, R7 |

**Pass condition for Section A:** every AC observed = Expected, with AC-3 recorded as the documented known gap (not a failure).

---

## B. Adversarial / Edge-Case Charter

Concrete attacks. Each lists the setup, the action, and the **pass bar**. Anything touching the device-write path is an automatic `severity:blocker` (§9.1).

> **Bound by SAFETY RULE #0 (top of this doc).** Every "attack" below is induced by the *host or physical environment* (unplug, Ctrl+C, lock, stop service, fill/lock the destination) or is *passive observation* / *static code inspection*. **No test here sends a write, delete, or rename to the iPhone.** The only real-device action is the read-only `copy` itself.

### B-1. USB unplugged mid-copy (R1, R7, R10)
- **Setup:** Start a full `copy` to a clean dest. Let several files complete.
- **Action:** Physically yank the USB cable mid-file.
- **Pass bar:** Tool catches the AFC error, flushes the journal, exits **non-zero**. The in-flight file is left as `<name>.partial` (or marked `failed`/`in_progress` in the journal) — **never** promoted to a final name. No completed final file is truncated. Re-plug + re-run → resumes, re-copies the interrupted file, size-verifies it, and `N_total` finals exist with **zero** `.partial` remaining. Manifest count == `N_total`.
- **Fail = blocker if:** any final file is half-written/truncated, or a `.partial` was renamed to final despite size mismatch.

### B-2. USB unplugged, then resume completeness (R1, R8, R10)
- After B-1's resume completes, re-run once more.
- **Pass bar:** copied = 0, skipped = total. Confirms resume produced a *complete* archive, not a silently short one.

### B-3. Ctrl+C mid-copy (R3, R10)
- **Setup:** Start a full `copy`.
- **Action:** Press Ctrl+C while a file is streaming.
- **Pass bar:** Clean shutdown — journal flushed, AFC handles closed (no resource-leak/hang), exit non-zero. In-flight file remains `.partial`. Journal has **no** file in a state that would be wrongly treated as `done`. Re-run resumes: interrupted file retried, done files skipped, end state 0 pending / 0 in_progress / 0 failed, count == `N_total`.

### B-4. Destination disk nearly full (R4, R15)
- **Setup:** Create a small volume (e.g. a small VHDX or a near-full USB stick) whose free space < `Bytes_total`.
- **Action:** `copy --dest <small-volume>`.
- **Pass bar:** **Pre-flight** computes estimated total and bails *before copying* with a message naming required (`total × 1.05`) vs available bytes. Non-zero exit. **No** files, no `.partial`, no half-built date tree written. (Also verify the writable-probe path R15: a read-only/locked dest yields a clear pre-flight write-permission error.)

### B-5. Filename collisions across DCIM buckets (R5, R17)
- **Context:** `/DCIM/` has multiple `NNNAPPLE` buckets; counter rollover can yield the same `IMG_NNNN` basename in different buckets, which may map to the same `YYYY-MM/` folder.
- **Action:** Full copy, then inspect (C-5). If the live device has no natural collision, document that and rely on the unit-test collision coverage (Task 14) — a **mocked `IPhoneClient` (NSubstitute)** that yields two same-named source descriptors. **No synthetic files are ever written to the device**; collisions are constructed in mocks or on the PC side only.
- **Pass bar:** Colliding files become `_2`, `_3`, …; **no `dest_path` duplicated** in the manifest; both distinct source files exist on disk (identity = source path + size, R17). **Never** an overwrite.

### B-6. Weird / missing / future EXIF dates (R16)
- **Context:** Screenshots & screen recordings have no `DateTimeOriginal`; some files have corrupt or future EXIF.
- **Action:** Full copy, then inspect (C-6).
- **Pass bar:** No-date files fall back to file mtime; genuinely undated → `unsorted/`. Dates outside `[1990, now+1day]` are not honored — file goes to `unsorted/` (clamp/reject). **No** file lands in an absurd year folder (e.g. `2099/`, `1970/`).

### B-7. R21 — Apple Devices service not running (port 27015 closed)
- **Setup:** Fresh boot **without** launching Apple Devices (or stop its background service). Confirm closed: `Test-NetConnection -ComputerName 127.0.0.1 -Port 27015` → `TcpTestSucceeded : False`. Plug in the iPhone.
- **Action:** `copy --dest D:\test`.
- **Pass bar:** Pre-flight probes 27015 and emits an **actionable** message — e.g. *"iPhone driver service not running. Open the Apple Devices app once, or install iTunes."* — **NOT** a generic "no device found". Then launch Apple Devices, confirm 27015 open, re-run → succeeds. The two failure modes (R21 service-down vs AC-13 no-device) must produce **distinct** messages.

### B-8. Re-run after a full clean copy (R8)
- **Action:** Run `copy` to completion, then run the identical command again.
- **Pass bar:** Second run: **0 copied, total skipped, 0 failed**, no `.partial`, manifest count unchanged. Fast (no re-streaming).

### B-9. Read-only proof at RUNTIME (R11) — the sacred check
- **Per SAFETY RULE #0 this is the ONLY real-device action in the whole plan, and it is a read-only `copy`.** The "proof" is entirely passive observation — we send no command intended to mutate anything; we only read and compare.
- **Goal:** Prove the iPhone is byte-for-byte unchanged by a full run.
- **Before:** Capture a full recursive `/DCIM/` listing — every file's **path, size (`st_size`), and mtime (`st_mtime`)** — via the tool's own `--dry-run` enumeration (read-only; opens no streams) into `dcim-before.txt` (sorted). Also record `N_total`, `Bytes_total`, and the top-level bucket mtimes.
- **Action:** Run a **full** `copy --dest D:\test` to completion.
- **After:** Capture the same listing → `dcim-after.txt` (sorted).
- **Pass bar:** `diff dcim-before.txt dcim-after.txt` is **empty (∅)**. No size changed, **no mtime changed** (a touch would prove a write), no file added/removed on the device. Device free space unchanged.
- **Any** difference = **`severity:blocker` + `safety:read-only-contract`**, no matter how small.

### B-10. §5.5 blocklist coverage — STATIC CODE-INSPECTION audit (R11, Stage 2 task 4)
- **This is a code-inspection audit, NOT a runtime test.** It never connects to a device, never runs a write, never sends a command. It is pure static analysis: read the test source and the symbols it reflects over.
- Inspect `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` and confirm its blocklist covers the **entire** §5.5 set (see Appendix 1). Diff the test's actual symbol list against Appendix 1 by eye / `grep`. If **any** write symbol from the canonical list is absent from the guard, that is a `severity:blocker` (`safety:read-only-contract`) — the guard has a hole even if no write is currently called.
- Also confirm the guard works by **reflection over assembly metadata** (not by invoking device APIs), per Rule #0.

### B-11. `--dry-run` writes nothing, opens no streams (R11)
- **Action:** `copy --dest D:\test --dry-run` against a **clean** dest.
- **Pass bar:** Console prints the plan; `D:\test` stays empty (no `get-and-see.db`, no `summary.txt`, no folders, no `.partial`); B-9 snapshot before/after dry-run is also identical (dry-run must not touch the device beyond read enumeration).

### B-12. Path-sanitization sanity (defense-in-depth, §9.3)
- **Action:** Inspect `dest_path` values in the manifest for any iPhone-supplied string used unsanitized (path traversal `..`, drive letters, reserved names).
- **Pass bar:** All dest paths are under the destination root; no `..` traversal; filenames sanitized. (Edge; note if anything looks off, file as `area:copy` `severity:major`.)

---

## C. Manifest & Summary Verification

Run with `sqlite3 "D:\test\get-and-see.db"`. The `files` schema (plan.md Task 7) and the `manifest` view (Session 3) are the contract.

### C-1. Manifest count == copied count (AC-9)
```sql
SELECT COUNT(*) FROM manifest;
```
**Expected:** equals the number of successfully copied files (`state = 'done'`), and equals the end-of-run "copied + previously-done" figure.

### C-2. State integrity after a clean run
```sql
SELECT state, COUNT(*) FROM files GROUP BY state;
```
**Expected:** only `done` (and possibly intentional `failed` in failure tests). After a *clean* full run: **0** `pending`, **0** `in_progress`, **0** `failed`.

### C-3. Manifest view shape (columns present)
```sql
PRAGMA table_info(manifest);
```
**Expected columns** (Session 3): `source_path`, `dest_path`, `size_bytes`, `source_mtime`, `exif_datetime_original`, `gps_latitude`, `gps_longitude`, `camera_make`, `camera_model`, `copied_at`, `sha256`. `live_photo_pair_id` **may be absent or always NULL in S1** (Live-Photo detection is Sprint 2 — see Section D; do not file a bug if missing). `sha256` is NULL unless `--verify-hash` (S3).

### C-4. `manifest` exposes only `done` rows (no internal state leak)
```sql
SELECT COUNT(*) FROM manifest
WHERE source_path IN (SELECT source_path FROM files WHERE state <> 'done');
```
**Expected:** `0` — the view filters `WHERE state = 'done'`.

### C-5. No overwrites / collision disambiguation (AC-16, R5, R17)
```sql
-- Must be EMPTY: a dest_path appearing twice means an overwrite or bad collision handling
SELECT dest_path, COUNT(*) c FROM manifest GROUP BY dest_path HAVING c > 1;

-- Evidence of disambiguation (expect some rows if natural collisions exist)
SELECT dest_path FROM manifest WHERE dest_path LIKE '%\_2.%' ESCAPE '\'
   OR dest_path LIKE '%\_3.%' ESCAPE '\';

-- Identity is source_path+size (R17): same name+size must NOT collapse to one row
SELECT source_path, source_size, COUNT(*) c FROM files
GROUP BY source_path, source_size HAVING c > 1;  -- expect EMPTY (each source is unique)
```
**Expected:** first query empty; second may list `_2/_3` files; third empty.

### C-6. Date organization & EXIF fallback (AC, R16)
```sql
-- Files routed to unsorted (no/invalid date) — spot-check these are screenshots/undated
SELECT source_path, dest_path FROM manifest WHERE dest_path LIKE 'unsorted%';

-- Date range of the archive
SELECT MIN(exif_datetime_original), MAX(exif_datetime_original)
FROM manifest WHERE exif_datetime_original IS NOT NULL;

-- Sanity: no dest folder outside a plausible year range (R16 clamp)
SELECT DISTINCT substr(dest_path,1,4) yr FROM manifest
WHERE dest_path GLOB '[0-9][0-9][0-9][0-9]*' ORDER BY yr;
```
**Expected:** `unsorted/` only holds genuinely undated files; min/max dates plausible; no year folder < 1990 or > current year + 1.

### C-7. Totals reconcile with summary.txt
```sql
SELECT COUNT(*) AS files, SUM(size_bytes) AS bytes FROM manifest;
```
**Expected:** matches the "Total: N files (X GB)" line in `summary.txt` (bytes → GB rounding allowed).

### C-8. Per-type counts (reconcile with summary.txt breakdown)
```sql
SELECT
  SUM(CASE WHEN upper(dest_path) LIKE '%.HEIC' THEN 1 ELSE 0 END) AS heic,
  SUM(CASE WHEN upper(dest_path) LIKE '%.JPG'  OR upper(dest_path) LIKE '%.JPEG' THEN 1 ELSE 0 END) AS jpg,
  SUM(CASE WHEN upper(dest_path) LIKE '%.MOV'  THEN 1 ELSE 0 END) AS mov,
  SUM(CASE WHEN upper(dest_path) LIKE '%.PNG'  THEN 1 ELSE 0 END) AS png
FROM manifest;
```
**Expected:** roughly matches the Photos/Videos/Screenshots breakdown in `summary.txt` (screenshot detection may be PNG-based; exact heuristic is the dev team's — verify it's *self-consistent*, not a specific number).

### C-9. Failed-run forensics (used in B-1/B-3)
```sql
SELECT source_path, state, error_message, started_at, finished_at
FROM files WHERE state IN ('failed','in_progress');
```
**Expected:** during/after an interrupted run, failed/in-flight rows carry an error/state; after a clean re-run this is **empty**.

### C-10. `summary.txt` expected shape (Session 3)
Verify it is human-readable and contains these elements (values illustrative):
```
get-and-see archive at D:\test
Last updated: <YYYY-MM-DD HH:MM:SS> UTC

Total: <N> files (<X> GB)
  Photos:        <n>  (HEIC: <n> · JPG: <n>)
  Videos:         <n>  (MOV: <n>)
  Live Photos:    <n> pairs          ← S2 feature; may be absent/0 in S1 (do not bug)
  Screenshots:    <n>

Date range: <YYYY-MM-DD> to <YYYY-MM-DD>
Devices: iPhone 12 Pro (<name>)        ← depends on S2 `devices` table; may be simplified in S1
Runs: <k> (...)                        ← depends on S2 `runs` table; may be simplified in S1

Last run: <copied> copied · <skipped> skipped (already done) · <failed> failed · <elapsed>
```
**S1 pass bar:** Header, "Last updated" (UTC), "Total" with byte size, per-type breakdown, "Date range", and the "Last run" line must be present, accurate, and match the manifest (C-7/C-8). The **Live Photos**, **Devices**, and **Runs** lines depend on Sprint 2 tables — if absent or simplified in S1, **note it, do not file a bug** (see Section D). Filing a "summary.txt incomplete" bug for an S2-gated line is out of scope.

---

## D. Out of Scope for Sprint 1 (do NOT file bugs against these)

These are unbuilt-by-plan. Filing bugs against them wastes triage. Source: `docs/sprint-1/plan.md` "What's NOT in This Sprint", PROJECT_BRIEF §7, Session 3 "Locked for Sprint 2/3".

| Deferred item | Lands in | Risk |
|---------------|----------|------|
| Spectre.Console live dashboard | Sprint 2 | — |
| `status` subcommand | Sprint 2 | — |
| `devices` table (and `summary.txt` "Devices:" detail) | Sprint 2 | — |
| `runs` table (and `summary.txt` "Runs:" detail) | Sprint 2 | — |
| Live Photo pair detection + `live_photo_pair_id` column (+ "Live Photos: N pairs" line) | Sprint 2 | — |
| Stall-detect / reconnect-and-retry on device sleep | Sprint 2 | R2 |
| Antivirus-quarantine resilience | Sprint 2 | R9 |
| PC-sleep / battery-power pre-flight warnings | Sprint 2 | R13, R14 |
| Network-drive destination handling | Sprint 2 | R18 |
| `--verify-hash` (SHA-256) | Sprint 3 | R12 |
| Long-path `\\?\` prefix | Sprint 3 | R6 |
| Apple-Photos-gap README disclaimer | Sprint 3 (docs) | R19 |
| Release workflow (EXE on tag) | Sprint 3 | — |
| Cross-platform CI matrix, code-signing, installer, GUI, HEIC conversion, XMP sidecars, geocoding, smart-folder views | v2 / out | — |
| **Branch protection enforced on `main`** | **Accepted gap** (private free tier) | — |

**Accepted/tracked (not testable here):** R20 (aging native deps — local-USB-only, read-only; accept for v1).

---

## Appendix 1 — Read-Only Contract Blocklist (canonical, for B-10)

`ReadOnlyContractTests` must fail the build if **any** of these symbols are referenced anywhere in `GetAndSee.Core` (or any shipping assembly). Source: `afc-library-decision.md` §5.5. In Stage 2 this is verified by **static code inspection only** (read the test source + reflect over assembly metadata) — **never** by calling any of these symbols against a device. Diff the test's actual blocklist against this list; **any missing entry = `severity:blocker` / `safety:read-only-contract`**.

**AFC write/mutate:**
```
afc_file_write
afc_truncate
afc_file_truncate
afc_make_directory
afc_make_link
afc_remove_path
afc_remove_path_and_contents
afc_rename_path
afc_set_file_time
afc_file_open  — in write/append modes: AfcFileMode.FopWr, FopRw, FopWrong, FopAppend, FopRdAppend
```
**lockdown state mutators (must never be called):**
```
lockdownd_set_value
lockdownd_remove_value
lockdownd_pair
lockdownd_unpair
lockdownd_activate
lockdownd_deactivate
```
**Allowed read surface (the only calls `IPhoneClient` may bind):**
```
idevice enumerate / idevice_new
lockdownd_client_new_with_handshake
lockdownd_start_service ("com.apple.afc")
afc_client_new
afc_read_directory
afc_get_file_info
afc_file_open (read-only: AfcFileMode.FopRdOnly) + afc_file_read   ← for OpenReadAsync streaming
```

---

## Appendix 2 — Risk Coverage Map (R1–R21)

| Risk | In S1? | Covered by |
|------|--------|-----------|
| R1 USB disconnect mid-stream | ✅ | AC-7, AC-18, B-1, B-2 |
| R2 iOS sleeps/locks → stall+retry | ❌ S2 | Section D |
| R3 Ctrl+C / crash | ✅ | AC-12, B-3 |
| R4 Destination disk fills | ✅ | AC-15, B-4 |
| R5 Filename collision across buckets | ✅ | AC-16, B-5, C-5 |
| R6 Windows MAX_PATH | ❌ S3 | Section D |
| R7 File truncated in transit | ✅ | AC-7, AC-18, B-1 |
| R8 Re-run skips done | ✅ | AC-11, B-2, B-8 |
| R9 Antivirus quarantine | ❌ S2 | Section D |
| R10 User pulls cable | ✅ | AC-7, B-1, B-3 |
| **R11 Accidental device write** | ✅ | AC-1, AC-2, AC-6, **B-9 (passive runtime proof)**, **B-10 (static blocklist audit)**, B-11, Appendix 1 |
| R12 Bit corruption (`--verify-hash`) | ❌ S3 | Section D |
| R13 PC sleeps | ❌ S2 | Section D |
| R14 Battery dies | ❌ S2 | Section D |
| R15 Dest permissions | ✅ | AC-15, B-4 |
| R16 Future/nonsense EXIF date | ✅ | B-6, C-6 |
| R17 Same name + size identity | ✅ | AC-16, B-5, C-5 |
| R18 Network-drive destination | ❌ S2 | Section D |
| R19 Albums-preserved expectation | ❌ S3 docs | Section D |
| R20 Aging native deps | ⏭ accept | Section D |
| R21 Apple Devices service lazy (27015) | ✅ | B-7 |
| R-device (no device / locked / untrusted) | ✅ | AC-13, AC-14 |

---

*Stage 2 will append observed results, the read-only-proof diff, and a PASS/BLOCKED verdict in `docs/qa/sprint-1-signoff.md`. Every defect is filed as a GitHub Issue per PROJECT_BRIEF §13 — QA files, dev fixes, Remy merges.*
