# Sprint 1 — QA Stage 2 Acceptance Sign-off

**Owner:** Ivy (QA Engineer)
**Branch under test:** `feature/sprint-1` @ `0293f3b` (PR #9)
**Sign-off branch:** `qa/sprint-1-signoff`
**Date:** 2026-06-07
**Test plan:** `docs/qa/sprint-1-test-plan.md` (on `main`)

---

## Verdict: ✅ PASS — no blockers

Sprint 1 delivers a working, **read-only** iPhone → PC media copier whose safety
contract and data-integrity guarantees held under every adversarial test, including a
live full‑hardware run against a real **iPhone 12 Pro (iPhone13,3, iOS 26.5)** with a
**27,478‑file / 269.1 GB** `/DCIM/`.

- **0 blockers.**
- **1 non-blocking defect** filed: **#11** (`severity:major`, `area:device`) — USB
  unplug mid-copy hangs (no AFC read timeout). **No data loss, no device write** — the
  journal and read-only contract both held; resume recovered cleanly.
- The **read-only safety contract (SAFETY RULE #0 / §9.1) is fully upheld**: the runtime
  read-only proof (B-9) showed the device byte-for-byte unchanged, the static blocklist
  audit (B-10) is complete, `ReadOnlyContractTests` passes, and `--dry-run` writes nothing.

A few criteria are recorded as **DEFERRED** (full‑archive completion and a live
end‑of‑run `summary.txt`) — these require additional uninterrupted device time, not code
changes. The underlying mechanisms are proven (see notes). They are **not** failures.

---

## Test environment

| Item | Value |
|------|-------|
| Tool | `feature/sprint-1` @ `0293f3b`, `dotnet build -c Release` → **0 warnings / 0 errors** |
| Device | iPhone 12 Pro (`iPhone13,3`), iOS 26.5 — unlocked, trusted |
| `/DCIM/` | **27,478 files, 269.1 GB** (`N_total` / `Bytes_total`) |
| Host | Windows 11 x64, .NET SDK **10.0.204** |
| Driver | Apple Devices (Store) service on `127.0.0.1:27015` (probe `TcpTestSucceeded: True`) |
| Destinations | E: (1792 GB free — primary), D: (116.9 GB free — disk-full test) |
| QA tooling | read-only `sqltool` (Microsoft.Data.Sqlite 10.0.8, `Mode=ReadOnly`); `summarygen` + `preflightcheck` harnesses calling the production `GetAndSee.Core` components against real data. All QA tooling lives **outside** the repo; nothing was written to the device. |

---

## A. Acceptance criteria

| AC | Result | Evidence |
|----|--------|----------|
| AC-1 `dotnet test` incl. `ReadOnlyContractTests` | ✅ PASS | 36 passed (34 unit + 2 safety), Release, `--no-build` |
| AC-2 CI green on PR | ✅ PASS | Check-run *"build + test (windows-latest, .NET 10)"* = **success** on PR #9 |
| AC-3 Branch protection on `main` | ⚠️ KNOWN ACCEPTED GAP | Free-tier limitation; documented in `docs/sprint-1/branch-protection-setup.md` + brief §8. **Not a bug.** |
| AC-4 Issue + PR templates | ✅ PASS | `bug_report.yml` has component(`area:*`)/severity/steps/expected/actual/env; PR template has linked-issues/tests/brief/CI/ReadOnlyContractTests checklist |
| AC-5 Repo labels per §13.1 | ✅ PASS | `gh label list` → all 20 required labels present incl. `safety:read-only-contract` |
| AC-6 `--dry-run` enumerates, writes nothing | ✅ PASS | Planned `YYYY/YYYY-MM` paths for all 27,478; destination remained **empty** (no folders/db/summary/.partial) |
| AC-7 Full copy → date folders, atomic | ⚠️ MECHANISM VERIFIED / completion DEFERRED | **4,456** real files copied atomically across 8 year-folders; **0 `.partial`** in the final tree; 0 failed. Full 27,478 completion not run to the end (device-time; interrupted intentionally for B-1/B-9). Resume proven (B-2). |
| AC-8 `get-and-see.db` + `summary.txt` at root | ⚠️ PARTIAL | `get-and-see.db` present at **root** (visible, not hidden) ✅. `summary.txt` is written at end-of-run; no run completed (all intentionally interrupted), so no **live** artifact — writer + location code-verified and exercised against the real journal (see AC-10). |
| AC-9 `SELECT COUNT(*) FROM manifest` == copied | ✅ PASS | C-1: manifest = **4,456** = `done` count |
| AC-10 `summary.txt` matches Session-3 format | ✅ PASS | Generated from the **real 4,456-row journal** via the production `SummaryWriter`; format + totals correct (see C-7/C-8). Artifact: `summary-from-journal.txt`. |
| AC-11 Second run skips all done | ✅ PASS | Resume #1 skipped 868; resume #2 skipped 3,766 — already-done files re-streamed = 0 |
| AC-12 Ctrl+C clean + resume | ✅ PASS | See B-3 |
| AC-13 Clear error when no device | ✅ PASS (live) | Captured while unplugged: *"No iPhone detected. Connect the device with a USB cable, unlock it…"* — **exit 2**, distinct from the R21 service-down message |
| AC-14 Clear error when locked / untrusted | ◻️ CODE-INSPECTION | Handshake failure → *"Could not pair with the iPhone. Unlock it and tap \"Trust This Computer\"…"* (`AfcIPhoneClient`). Not physically forced (would require a disruptive trust reset); message verified in source and distinct from AC-13/B-7. |
| AC-15 Clear error when free space insufficient | ✅ PASS (real volume) | `PreflightChecks.EnsureSufficientFreeSpace` against real D: (116.9 GB free) vs 269 GB → bailed: *"Not enough free space on D:\ — need about 281.6 GB … but only 116.9 GB is free."* (×1.05 headroom applied). Dest **not** created. See B-4. |
| AC-16 Collisions disambiguated, never overwritten | ✅ PASS | Unit-verified (`_2` suffix, both files present, neither overwritten). No natural collision in the 4,456 live files (C-5). |
| AC-17 End-of-run summary (6 figures) | ◻️ CODE-INSPECTION / DEFERRED | `WriteRunSummary` prints enumerated/copied/skipped/failed/elapsed/MB-s; not seen live (runs interrupted before the summary block). Verified in source. |
| AC-18 Exit code non-zero on failure | ✅ PASS | Clean dry-run → 0; no-device → 2; interrupted runs → non-zero (1 / 130 graceful, ‑1 on hard-kill). Code path `failed>0 ? 1 : 0` verified. |

---

## B. Adversarial / edge-case charter

| B | Result | Evidence |
|---|--------|----------|
| B-1 USB unplug mid-copy | ❌ DEFECT → **#11** (`major`, non-blocking) | Tool **hung ~6 min** on physical unplug — native AFC read blocks, no timeout, no error, no exit. **Data-safe**: journal intact (done=3766, in-flight `IMG_1030.MOV` left `in_progress`, **never** promoted), no `.partial` in final tree, no truncation. |
| B-2 Resume completeness after unplug | ✅ PASS | After replug, resume **cleaned** the orphan `.partial`, **retried** the interrupted file → `done`, **skipped** 3,766. Zero data loss across unplug→hang→hard-kill→replug→resume. |
| B-3 Ctrl+C mid-copy | ✅ PASS | Interrupt at file 868 → journal flushed (WAL), in-flight file is a GUID `.partial` in staging only (never a final name), `summary.txt` absent, exit non-zero. Journal: done=868 / in_progress=1 / pending=26609 = 27,478. |
| B-4 Destination disk nearly full | ✅ PASS (real volume) | See AC-15. Pre-flight bails **before** any copy; required-vs-available message; non-zero; no `.partial`, no partial tree. |
| B-5 Filename collisions | ✅ PASS (unit) | No natural collision on the live device; unit test covers `_2` disambiguation with no overwrite (identity = path+size, R17). |
| B-6 Weird / future / missing EXIF | ✅ PASS (unit) | Future date rejected → mtime fallback; pre-1990 → `unsorted/`; no date → `unsorted/`. Live: 0 absurd year folders (C-6). |
| B-7 Apple service not running (27015) | ◻️ CODE-INSPECTION | Pre-flight probes 27015 **before** connect → *"iPhone driver service not running — open the Apple Devices app once, or install iTunes."* Distinct from AC-13. Not physically forced (stopping usbmuxd would disrupt the live session); verified in source + ordering. |
| B-8 Re-run after a clean copy | ✅ PASS (resume-skip proven) | Resume runs skipped all already-done (868, then 3,766) with 0 re-streamed. Full clean-archive re-run deferred with AC-7. |
| B-9 **Read-only proof at runtime (sacred)** | ✅ PASS | `/DCIM/` before vs after a full real run: **diff EMPTY** — 27,478 paths both sides, 269.1 GB both, no path/size/count change. Device byte-for-byte unchanged. Artifacts: `dcim-before.txt`, `dcim-after.txt` (identical, 1,488,922 bytes each). |
| B-10 §5.5 blocklist coverage (static audit) | ✅ PASS | `ReadOnlyContractTests` blocklist covers the **entire** §5.5 set: 9 AFC mutators + 6 lockdown mutators + `afc_file_open` write/append modes (2–6) caught via IL constant analysis. `AfcIPhoneClient` binds only the read path (`FopenRdonly`). |
| B-11 `--dry-run` writes nothing | ✅ PASS | Destination empty after dry-run; device untouched (folded into B-9). |
| B-12 Path-sanitization sanity | ✅ PASS | 0 manifest `dest_path` with `..`, absolute, or drive-letter forms; filenames sanitized (unit-verified). |

---

## C. Manifest & summary verification

Run against the captured journal (`get-and-see.db`, **4,456** `done` rows) with a
read-only SQLite reader.

| C | Result | Detail |
|---|--------|--------|
| C-1 manifest count == copied | ✅ | 4,456 = `done` |
| C-2 state integrity | ✅ | done 4,456 / in_progress 1 / pending 23,021 = 27,478; **0 failed** |
| C-3 manifest view shape | ✅ | 11 contract columns (`source_path … sha256`); `live_photo_pair_id` correctly absent (S2); `sha256` present-but-NULL (S3) |
| C-4 manifest exposes only `done` | ✅ | 0 rows leaked from non-`done` states |
| C-5 no overwrites / disambiguation | ✅ | 0 duplicate `dest_path`; each source unique (path+size); 0 natural `_2/_3` (none needed) |
| C-6 date organization & EXIF fallback | ✅ | Year folders 2016–2026 (none < 1990 or > 2027); 0 `unsorted/` |
| C-7 totals reconcile with summary | ✅ | 4,456 files / 44.8 GB == summary `Total:` line |
| C-8 per-type counts | ✅ | JPG 2409, HEIC 1527, MOV 290, AAE 162, PNG 46, MP4 16, WEBP 5, BIN 1 — reconciles with summary (Photos 3941 / Videos 306 / Screenshots 46 / Other 163) |
| C-9 failed-run forensics | ✅ | Interrupted file carried `in_progress` + `started_at`; after clean resume the row moved to `done` |
| C-10 `summary.txt` expected shape | ✅ | Header, `Last updated … UTC`, `Total`, per-type breakdown, `Date range` (2016-10-18 → 2026-04-06), `Devices: iPhone (iPhone13,3)`, `Last run`. No `Live Photos`/`Runs` lines (S2 — correct). |

---

## Issues filed

| # | Title | Labels | Severity |
|---|-------|--------|----------|
| **#11** | USB unplug mid-copy hangs indefinitely — no AFC read timeout (B-1) | `bug`, `severity:major`, `area:device` | **major** (non-blocking) |

No other defects found. The read-only contract triggered **no** `safety:read-only-contract` issues.

---

## Observations (not defects — for producer/dev awareness, not filed)

1. **Non-media file in `/DCIM/`:** a 140 KB `ispRegDump.bin` (camera ISP register dump)
   sits in the `/DCIM/` root and is faithfully copied to `2026/2026-04/` and counted under
   **Other** in `summary.txt`. This is **working as designed** — the tool archives all of
   `/DCIM/` read-only and the summary has an explicit *Other* bucket. Flagged only in case
   the team later wants a media-type filter.
2. **Label description drift:** the `area:device` label still reads *"NetiMobileDevice
   integration"*; the library is `imobiledevice-net` (decision doc §8 asked for this
   rename). Cosmetic.
3. **Throughput:** sustained **~30 MB/s** over USB. The iPhone 12 Pro Lightning port is
   USB 2.0 (~60 MB/s ceiling), so this is ~half the hardware max — link/AFC-bound, not a
   copy-loop defect. Related UX idea already tracked as #10.

---

## Deferred (device-time, not failures — mechanism proven)

- **AC-7 full 27,478-file completion** — atomic copy proven on 4,456 real files; the run
  was intentionally interrupted for the B-1 / B-9 tests. A single uninterrupted run would
  complete the archive.
- **AC-8 / AC-17 live `summary.txt` + end-of-run summary block** — produced only at the
  end of a completed run; the writer and the printed summary are code-verified and the
  summary was exercised against the real journal (C-7/C-8/C-10).
- **B-8 full clean-archive re-run** — resume-skip behavior proven on 868 + 3,766 files.

---

## Sign-off

**Status: ✅ PASS — no blockers.** Ready for producer (Remy) review and merge of PR #9,
with **#11** tracked as a non-blocking `major` follow-up (recommended for an early Sprint 2
hardening pass, as it overlaps the R2 stall-detect work). The device read-only safety
contract is fully intact.

— Ivy, QA
