# Sprint 4.1 QA Sign-off — `reorganize` (PR #68)

**QA:** Ivy · **Date:** 2026-07-04 · **PR:** [#68](https://github.com/denis-a-evdokimov/get-and-see/pull/68) — `feature/sprint-4.1` → `main`
**Type:** Light acceptance smoke (offline / PC-only — `reorganize` never touches the device; no iPhone needed)
**Verdict:** ✅ **PASS — no blockers, no majors, no minors.** Cleared for merge (Remy merges).

---

## What was tested

`reorganize` migrates an existing archive between `--organize-by` layouts by **moving files on the PC**. It
is the first operation in the product that moves/renames files, so this smoke targets the crux: **integrity
on a real-scale archive — no byte lost or corrupted, and the journal always points at where each file
actually is.**

### Test archives (built OFFLINE via the real copy pipeline, no hardware)

Because `reorganize` operates on an existing on-disk archive (media + `get-and-see.db`), I generated two
genuine archives by driving the **real** copy pipeline (`DcimEnumerator` → `TransferJournal` →
`DateFolderOrganizer` → `FileCopier`) against an in-memory `FakeDeviceSpec` — the same components the
shipped `copy` uses, so the archives are byte-for-byte what a real copy would have written:

| Archive | Files | Layout | Purpose |
|---|---|---|---|
| **golden** | **7,966** | nested `year-month` (2019–2024) | fast full matrix |
| **golden-big** | **32,446** | nested `year-month` (2016–2024) | wide crash-interrupt window |

Composition (both): thousands of dated photos across 60–108 month-folders, 16 videos, **6 Live-Photo pairs**,
**20 `unsorted/`** (no-date) files, **3 same-name/same-year** files (force `year` collisions), and **one
266-char (> MAX_PATH) path**. Schemes exercised: **`year-month`, `month`, `year`, `flat`**.

> Honest scope note: files are small (golden ≈ 93 MB), so this is a **multi-thousand-file
> COUNT-representative subset**, not a 269 GB byte-volume run. `reorganize` moves each file with a
> **same-volume atomic `File.Move` (OS rename)**, which is **size-independent** — a rename cannot truncate or
> corrupt bytes regardless of file size — so file **count + folder variety + crash timing** are what matter
> here, and all are at real scale.

### Independent integrity oracle (before/after every run)

- **Content multiset** — SHA-256 of every media file, sorted (path-independent). A reorganize must leave this
  **identical** (same bytes, just relocated). Long-path-aware (`\\?\`) so the 266-char file is hashed too.
- **Journal↔disk cross-check** (via the real read-only `TransferJournal`) — every manifest `dest_path` exists
  on disk at the recorded size, on-disk media count == manifest (no orphans/dupes), **no `.partial`**, plus
  the recorded `organize_scheme` and any in-flight `reorganize_target` marker.

---

## Results

| # | Area | Result | Evidence |
|---|---|---|---|
| 1 | **Round-trip byte-integrity** (`year-month`→`month`→`year-month`) | ✅ PASS | content multiset **IDENTICAL** both legs; final layout **IDENTICAL** to baseline; journal↔disk `OK`; 78 then 72 emptied folders removed (0 bare `YYYY` left); `organize_scheme` month→year-month; **no `.partial`, no dupes**, count conserved (7,966) |
| 2 | **Crash-resume** | ✅ PASS | **(a) graceful Ctrl-C → exit `130`**, journal fully consistent (0 missing), marker set, content identical. **(b) worst-case hard-kill** (32k archive): content **IDENTICAL** (0 loss), the single moved-but-not-yet-journaled file **healed on resume**; re-run completes **byte-identical AND layout-identical to a clean run** (determinism) |
| 3 | **`copy` guard** | ✅ PASS | `copy` while a reorganize is unfinished → **refuses, exit `2`**, message *"This archive has an unfinished reorganize (→ 'month'). Run … to finish it first."*; after resume, `copy` no longer refused (exit 0) |
| 4 | **No-op** | ✅ PASS | reorganize to the recorded scheme → *"Already organized as 'year-month'. Nothing to move."*, **0 moves, exit `0`**, media tree unchanged, journal↔disk `OK` |
| 5 | **`--dry-run`** | ✅ PASS | preview only; **`get-and-see.db` timestamp + bytes UNCHANGED**; media tree unchanged; no marker leaked; exit `0` |
| 6 | **Offline proof** | ✅ PASS | reorganize with **no device and `GAS_FAKE_DEVICE` unset** → exit `0`, zero device interaction; `ReadOnlyContractTests` **2/2 green** locally |
| 7 | **Sanity** | ✅ PASS | `--help` ok; **exit `2`** on a missing archive; **266-char long-path** file round-trips **byte-identical**; `year` coarsening → deterministic `SHARED`/`_2`/`_3` suffixes, **identical across two independent runs**, content identical |
| 8 | **Exit `1` / no-clobber** (bonus) | ✅ PASS | a foreign file occupying a target → that **one** move fails (**exit `1`**), foreign file **not clobbered**, journal row **not dropped** (original intact at old path), no loss, marker stays set; clear the blocker → resume **exit `0`**, content **IDENTICAL** |

Exit codes observed: **`0`** (ok / no-op), **`1`** (some files couldn't move — resumable), **`2`** (no
archive / copy-guard), **`130`** (Ctrl-C). All match the spec.

### Local test lanes (independent of CI)

- `GetAndSee.SafetyTests` (read-only device contract) — **2 / 2 green**.
- `reorganize` unit + E2E (`FullyQualifiedName~Reorganize`) — **36 / 36 green**.
- CI on PR #68 — **5 / 5 green** (build+test, published-EXE smoke, real-`.exe` E2E, gitleaks, coverage).

---

## Known nit (NOT filed — as directed)

A no-op `reorganize` writes **3 net-zero `settings` rows** (`reorganize_target` set → `organize_scheme`
re-set to the same value → marker cleared). Confirmed **cosmetic and idempotent — the media tree is
untouched** (Test 4). This is the one tracked gate nit; left as-is.

## Bugs filed

**None.** No defects found across 8 areas on two real-scale archives.

---

## Sign-off

**✅ PASS — no blockers.** `reorganize` preserves every byte across every layout transition (`year-month` ↔
`month` ↔ `year` ↔ `flat`), is crash-safe and deterministically resumable (both graceful Ctrl-C → 130 and
worst-case hard-kill), guards `copy` correctly, and is provably offline. Handing back to Remy for merge —
**I did not merge.**
