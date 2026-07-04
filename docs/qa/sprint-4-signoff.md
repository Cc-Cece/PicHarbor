# Sprint 4 — QA Light-Hardware Acceptance (organize & find)

**Owner:** Ivy (QA Engineer)
**Branch under test:** `feature/sprint-4` @ `37db5ae` · **PR:** [#65](https://github.com/denis-a-evdokimov/get-and-see/pull/65) ("sprint-4: organize & find (flat YYYY-MM default + search)")
**Binary under test:** the **shipped-shape artifact** — `dotnet publish src/GetAndSee.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` → single-file self-contained `get-and-see.exe`. Tested with the EXE, **not** `dotnet run`.
**Date:** 2026-07-04
**Method:** all QA artifacts under `E:\src\qa-stage4\` (**outside the repo**); the read-only SAFETY RULE governs — nothing was ever written to the device. Journal state read with a `Mode=ReadOnly` SQLite connection.

---

## Verdict: ✅ PASS — no blockers

Sprint 4's three deliverables hold on real hardware. **The upgrade path is byte-stable:** pointing the new EXE at a genuine existing v2 nested (`YYYY\YYYY-MM`) archive migrated the journal to schema v3, stamped `organize_scheme = 'year-month'`, and **left every already-copied file untouched** (328/328 byte-identical, zero re-shuffled, zero duplicated, zero re-copied) while resumed files continued into the **existing nested layout** — never flat. The new flat `YYYY-MM` default, the four `--organize-by` schemes, the explicit-conflict warning, and the read-only `search` command all behave as specified. The device is byte-for-byte unchanged and `search` provably makes **no** device connection.

**No new defects were found. No GitHub Issues were filed.** The three already-known, producer-tracked items were not re-filed (see *Known items*).

---

## Test environment

| Item | Value |
|------|-------|
| Tool | `feature/sprint-4` @ `37db5ae`, Release single-file win-x64 self-contained EXE |
| Device | iPhone 12 Pro (`iPhone13,3`) — unlocked, trusted, over USB. `/DCIM/` = **27,515 files / 269.9 GB** |
| Host | Windows 10 Pro for Workstations, .NET SDK 10.0.204 |
| Driver | Apple Devices (Store) usbmuxd service on `127.0.0.1:27015` (reachable) |
| Destinations | `E:\src\qa-stage4\…` (out-of-repo); upgrade source = a genuine v2 nested archive (`user_version = 2`, tables `devices/files/runs`, 328 `done` files across `2024\2024-04` + `2026\2026-04`) copied to a working dir so the original stayed a pristine baseline |

> **Read-only proof upheld.** The only device interaction was read-only `copy` (AFC read → PC write), `copy --dry-run` (enumerate only), and `search` (local journal only). No write/delete/rename was ever issued; `--dry-run` reports "No AFC read streams were opened and no files were written"; device file-count + total-size identical before/after (see area 5).

---

## Results by test area

| # | Area | Result | Evidence |
|---|------|--------|----------|
| **1** | **Upgrade path — byte-stable resume of an existing v1.0/v2 nested archive** (highest priority) | ✅ **PASS** | Ran `copy --dest <v2-nested-archive>` (no `--organize-by`). Journal migrated **`user_version` 2 → 3**; `settings.organize_scheme = 'year-month'` stamped. **328/328** pre-existing files remained **byte-identical** at their exact nested paths (SHA-256 before/after: 328 identical, **0 changed, 0 missing**) — no re-shuffle, no duplication, no re-copy. Resumed files landed in **nested `YYYY\YYYY-MM`** (`2024\2024-04\IMG_6003.JPG …`), the staging `.partial` appeared under `.get-and-see-tmp\`, and **zero flat `YYYY-MM` folders** were created at the root. (A full-archive no-op run — "all skipped, exit 0" — was not possible: the prior complete Stage-2 archive had its files reclaimed, so a genuine **subset** was used and the stronger per-file byte-stability + nested-resume proof substituted. This also *is* the "new files land nested" bonus: the 313 resumed files were new-to-the-archive and every one went nested.) |
| **2** | **Explicit conflict** `--organize-by month` on the `year-month` archive | ✅ **PASS** | `copy --dest <that archive> --organize-by month --dry-run` printed **`warning: This archive is organized as 'year-month'. Ignoring --organize-by 'month'. Use 'reorganize' to change it.`** and previewed **27,515 nested paths / 0 flat** — the flag was ignored, the recorded scheme kept, nothing written (no re-shuffle). |
| **3** | **Fresh archive, new default + scheme spot-checks** | ✅ **PASS** | Fresh `copy --dest <new>` (no flag) → **flat `2024-04\`, `2026-04\`** at the root, `organize_scheme = 'month'` recorded at v3. **Live Photo pair co-located:** `IMG_6247.HEIC` + `IMG_6247.MOV` both in `2024-04\`. Spot-checks on fresh dests: `--organize-by year-month` → `2024\2024-04\name` (nested); `year` → `2024\name` (no month subfolder); `flat` → `name` at the root (only `.get-and-see-tmp` beside it). *`unsorted\` not exercised on hardware — this device has **0** undated files (every file carries an EXIF or mtime date); it is covered by `DateFolderOrganizerTests`.* |
| **4** | **`search` — read-only, no device** | ✅ **PASS** | Over a real 641-file manifest, every filter returned correct counts, **independently cross-checked against direct manifest SQL**: type partition `photo 582 + video 23 + screenshot 5 + other 31 = 641`; `--has-gps` = **576** (= SQL); `--min-size 100000000` = **9** (= SQL); `--type video --min-size 200000000` = **3** (= SQL); `--from/--to` narrows correctly; all exit `0`. `--open` launched an Explorer window at the matching folder (verified an Explorer process started), exit `0` (the 10-folder cap is unit-tested). **Device-independence proven:** with the **iPhone physically unplugged**, `copy` failed to connect (`error: No iPhone detected`, exit 2) while `search` returned correct results in **0.19 s, exit 0** — no device enumeration, no connection. |
| **5** | **Read-only device proof** (sacred, non-negotiable) | ✅ **PASS** | After all copy runs, a fresh read-only re-enumeration reported **27,515 files / 269.9 GB — identical to the pre-test baseline**, with "No AFC read streams were opened and no files were written." Nothing added, removed, or modified on the device. |
| **6** | **Sanity** | ✅ **PASS** | `copy --help` shows `--organize-by <flat\|month\|year\|year-month> … [default: month]`; `search --help` shows the full filter set (`--from --to --type --camera --min-size --max-size --has-gps --open`); every clean run exited `0`. |

---

## Known items (producer-tracked — confirmed, NOT re-filed)

Per the QA brief, these three were already logged by the producer and are **not** blockers:

- **(a)** `FileCopier.organizeScheme` is an optional parameter defaulting to `YearMonth` (mechanism-vs-policy; the one production caller always passes the resolved scheme). Hardening deferred to Sprint 4.1. — *Confirmed by inspection; working as designed for v4.*
- **(b)** `search` date filters can be off by a day for **mtime-dated** files at a day boundary in a non-UTC timezone. — *Low-priority polish; not reproduced/pursued this pass.*
- **(c)** `progress.md` test count vs `done.md` (262 vs 266) and an overloaded `search` exit code. — *Cosmetic; the observed exit codes were correct for every case tested (`0` success, `2` no-archive/no-device).*

---

## Scope notes (what a *light* pass deliberately did **not** do)

- **No full 269 GB run.** Copy runs were bounded (started, verified, then stopped with a data-safe `Stop-Process -Force`, which leaves at most one expected `.partial` orphan under `.get-and-see-tmp\`). The healthy full-copy-to-completion path is unchanged by Sprint 4 (destination-side only) and was proven in earlier hardware acceptances.
- **No disconnect/unplug resilience testing** — Sprint 4 does not touch the device/AFC path (the read-only contract and watchdog are untouched); the one unplug performed was solely to prove `search` needs no device.
- **`unsorted\`** could not be exercised (this device has no undated files) — unit-test covered.

---

## Sign-off

**✅ PASS — no blockers.** Sprint 4 is accepted from a QA standpoint. Handing back to the Producer (Remy): merge PR #65 once CI is fully green (the ~17-min E2E lane included) **and** this sign-off is on record. QA does not merge.
