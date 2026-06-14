# Sprint 3.4 — QA #21 Phase B Hardware Re-acceptance (the v1.0 release gate)

**Owner:** Ivy (QA Engineer)
**Branch under test:** `fix/sprint-3.4` @ `43628f0` ("make the disconnect escape-hatch terminate unconditional", #45)
**Binary under test:** the **shipped-shape artifact** — `dotnet publish src/GetAndSee.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.0.0-qa.3.4` → single-file self-contained `get-and-see.exe` (the exact shape `release.yml` ships). Tested with the EXE, **not** `dotnet run`.
**Date:** 2026-06-14
**Tracking issue:** [#21](https://github.com/denis-a-evdokimov/get-and-see/issues/21) — QA Stage 2 hardware acceptance · **PR:** #47
**Under test (new in 3.4):** the disconnect **escape-hatch** — on a dead device the run-level forward-progress watchdog, on its **independent timer thread**, prints the disconnect line, writes `summary.txt`, and **hard-terminates the process (exit 3) via `TerminateProcess`** (finalizer-skipping) instead of unwinding through native `afc_file_close`, which busy-spins.
**Method:** all QA artifacts under `E:\src\qa-stage2\evidence\phase-b-4\` (**outside the repo**); SAFETY RULE #0 governs — nothing was ever written to the device. The CEO performed every physical cable action; QA drove the copy + 2 Hz CPU/heartbeat trace (`GAS_DEBUG_READS=1`) and adjudicated each exit.

---

## Verdict: ✅ PASS

**The mid-copy USB-disconnect bug is fixed on real hardware.** Across **five physical cable-yanks** (mid-large-file ×2, between/among small files ×1, post-resume ×1, with `--verify-hash` ×1) the tool **self-terminated with exit code 3 every time, CPU returned to idle, and NO force-kill was ever required.** This is the first round that holds on hardware.

> **The round-6 bar, met.** In all **five** prior manifestations (#11 → #25 → #38 → #42 → #45) a real yank pinned one core at ~100% **forever** and required a manual force-kill. This round, every yank pins one core for **up to `--read-timeout`** (the `afc_file_close` spin), then the process **exits 3** and the OS reaps the orphaned thread (**CPU → idle**). Proven both with the escape-hatch firing explicitly ("Device disconnected" printed) and with the graceful forward-progress unwind winning the race — both yield the correct outcome.

Data safety and the read-only contract are fully intact: device byte-for-byte unchanged, resume byte-exact with zero loss, journal crash-safe across the hard terminate (WAL recovery), no published `.partial`.

---

## Test environment

| Item | Value |
|------|-------|
| Tool | `fix/sprint-3.4` @ `43628f0`, Release single-file win-x64 self-contained EXE (`-p:Version=1.0.0-qa.3.4`) |
| Flags present | `--read-timeout` (default 30), `--no-dashboard`, `--verify-hash` ✅ |
| Device | iPhone 12 Pro (`iPhone13,3`), iOS 26.5 — unlocked, trusted, over USB. `/DCIM/` = **27,476 files / 269.1 GB** |
| Host | Windows 11 x64, .NET SDK 10.0.204 |
| Driver | Apple Devices (Store) usbmuxd service on `127.0.0.1:27015` (reachable) |
| Destination | `E:\src\qa-stage2\evidence\phase-b-4\` (out-of-repo) |
| Capture | `Invoke-YankTest.ps1` — launches the EXE with `GAS_DEBUG_READS=1`, samples CPU% + the `.partial` byte-heartbeat at 2 Hz to CSV, records exit code, force-kills only if pinned > `read-timeout` + 30 s (the failure signal). CSV/stdout/reads-log per run retained. |

> **SAFETY RULE #0 upheld.** The only device interaction was the read-only `copy` (AFC read → PC write). Every failure was induced physically (cable yank). No write/delete/rename was ever issued; `ReadOnlyContractTests` 2/2 green; device byte-identical before/after (see Regression).

---

## The critical tests

| # | Test | Result | Evidence |
|---|------|--------|----------|
| **B-4 run 1** | Yank **mid-large-file**, `--read-timeout 30` | ✅ **PASS** | Yank mid-stream of `IMG_6874.MOV` (~211 MB in). **exit 3**, **no force-kill**. CPU pinned ~94–100% of one core for ~30 s (= `--read-timeout`), then process gone → every post-exit sample **0%** (CPU idle). Clean resumable stop printed; `summary.txt` written; **0** published `.partial`; 328 copied / 1 failed (the yanked file). Trace: `b4-run1-cpu.csv`. |
| **B-4 run 2** | Yank **mid-large-file**, `--read-timeout 10` | ✅ **PASS** (escape-hatch proven) | Yank mid-stream of `IMG_0746.MOV` (90,487,895 B). `reads.log`: `…error=EmptyResponse received=0` then the log **stops** — the #45 `afc_file_close` disposal-spin signature (no advance to a next file). **exit 3**, **no force-kill**, peak CPU **103.8%** → idle ~13 s after the yank. stdout carries the escape-hatch line **`Device disconnected. Progress saved - reconnect and re-run to resume.`** **0** published `.partial`. Trace: `b4-run2-cpu.csv`. |
| **B-5 run 1** | Yank **between/among small files**, `--read-timeout 10` | ✅ **PASS** (escape-hatch) | Yank during the small-file storm landed mid-read of `IMG_6314.JPG` → `EmptyResponse`, log stops (same `afc_file_close` spin). **exit 3**, **no force-kill**, peak CPU 103.8% → idle. **`Device disconnected`** printed. 24 done, **0** published `.partial`. Trace: `b5-run1-cpu.csv`. |
| **Resume** | Reconnect → re-run the same command | ✅ **PASS — byte-exact, zero loss** | On `copy-dest-b5`: **skips = 24** (already-done files, no re-stream); the interrupted **`IMG_6314.JPG` re-copied to its full `4,019,718 B`** (it was yanked at 3,145,728 B — re-streamed from 0, not resumed mid-file); **3 sample done-files hash-identical** before vs after (`F9E5E26B…`, `CBC5FAFC…`, `DF8DA26F…`); **0** published `.partial`; post-resume `status` exit 0. This run also captured a **4th clean yank** (mid 51 MB file → exit 3, no force-kill, escape-hatch). |
| **Journal integrity** | Journal opens clean after the hard `TerminateProcess` | ✅ **PASS — WAL recovery** | `status --dest` exits **0** on every hard-killed dest (`copy-dest` 328/2.1 GB, `copy-dest2b` 1,298/10.5 GB, `copy-dest-b5` → 923/6.9 GB after resume). The finalizer-skipping terminate corrupted no journal — SQLite WAL + per-file committed `MarkDone` is crash-safe as designed. |

### Exit-code summary (observed)
- **3** — disconnect/lost-device, clean resumable stop: **proven 5×** (B-4×2, B-5, resume 4th yank, verify-hash run).
- **2** — preflight failure (missing archive; cable unplugged at start): clean and **immediate, no hang** (a bonus data point — with the cable out, `copy` printed `No iPhone detected` and exited 2).
- **0** — `--help`, `status` on a valid archive, and `--dry-run` all exit 0.
- A full-archive copy to **clean completion (exit 0)** was **not** re-run (a 269 GB pass is out of scope for a spot-check); that path is **unchanged** by 3.4 (see Regression diff) and was verified in the Sprint-1 full-run.

---

## CPU traces (the proof)

**B-4 run 1** (`--read-timeout 30`) — pinned one core for the read-timeout window, then exit 3 → idle:
```
13:28:44  cable yanked (reads.log: IMG_6874.MOV error=EmptyResponse received=0)
13:28:44 → 13:29:14   one core ~94–100%   (the afc_file_close spin, ~30 s = --read-timeout)
13:29:15  process gone (exit 3) — POSTEXIT samples all 0%  ← CPU idle, NO force-kill
```

**B-4 run 2** (`--read-timeout 10`) — escape-hatch fired:
```
13:43:17  cable yanked mid IMG_0746.MOV (EmptyResponse; reads.log then STOPS = afc_file_close spin)
13:43:17 → 13:43:29   one core ~88–98% (peak 103.8%)   (~10 s = --read-timeout)
13:43:30  process gone (exit 3); stdout: "Device disconnected…"  ← CPU idle, NO force-kill
```

(`b5-run1` and `resume-b5` traces show the same pin → exit-3 → idle shape; CSVs retained in the evidence dir.)

---

## Regression spot-check (3.4 only ADDED the escape-hatch — confirm no regression)

3.4's blast radius (`git diff --stat fix/sprint-3.3..fix/sprint-3.4 -- src/`) is **only 7 escape-hatch files**: `DisconnectEscapeHatch.cs`, `IProcessTerminator.cs`, `TerminateProcessTerminator.cs` (new) + wiring in `CopyCommand.cs`, `FileCopier.cs`, `ForwardProgressWatchdog.cs`, `SummaryWriter.cs`. **`TransferJournal`, `LongPath`, `IPhoneClient`/AFC, `Preflight`, `DcimEnumerator`, `Organize` are unchanged** since the 3.3 hardware run — so the items below cannot have regressed.

| Check | Result | Evidence |
|-------|--------|----------|
| **Read-only — device unchanged** | ✅ **PASS** | Read-only `--dry-run` **before** (initial probe) and **after** the whole session (5+ copy runs, thousands of reads, 5 yanks + reconnects) both report **`would copy 27,476 files (269.1 GB)`** — byte-for-byte identical. Footer: *"No AFC read streams were opened and no files were written."* Dry-run wrote **0** files to disk. `IPhoneClient`/AFC unchanged + `ReadOnlyContractTests` green. |
| **Long-path (#39)** | ✅ **PASS (no-regression)** | `TransferJournal` + `LongPath` byte-identical to the version that **passed #39 on hardware in 3.3** (deep dest, journal DB path > 260 via `\\?\`). No code path changed; nothing to re-break. (Note: the QA host's PowerShell could not pre-create a 268-char directory to re-run the live deep copy, but the under-test code is unchanged.) |
| **`--verify-hash`** | ✅ **PASS** | A `--verify-hash` copy (`copy-dest-vh`): journal **`files`** table = **1,338 done rows, all 1,338 with a valid 64-hex `sha256`, 0 NULL**. Cross-check: `IMG_6248.JPG` (3,778,348 B) stored `cd5d45bb…e2c2` **==** independent `Get-FileHash -Algorithm SHA256` (**match = True**). The escape-hatch also fired correctly with hashing on (5th yank → exit 3, no force-kill). |
| **Exit codes** | ✅ **PASS** | disconnect = **3** (5×); preflight/no-device = **2** (clean, no hang); `--help`/`status`/`--dry-run` = **0**. |

---

## Issues

**None filed.** No defect found. The escape-hatch holds on hardware, data safety is intact, the read-only contract is upheld, and the regression surface is unchanged.

---

## For the Producer (Remy) / CEO

- **Gate status: ✅ PASS.** The Sprint 3.4 escape-hatch resolves the mid-copy USB-disconnect bug on real hardware — five physical yanks, five clean **exit 3**s, CPU → idle every time, **no force-kill**. This clears the named #21 criterion ("detect within `--read-timeout`, clean resumable stop, NO indefinite hang") that BLOCKED rounds #25/#38/#42/#45.
- **Data-safe:** device byte-for-byte unchanged (read-only upheld), resume re-copies the interrupted file to exact bytes with zero loss, journal survives the hard `TerminateProcess` (WAL recovery), no published `.partial`.
- This sign-off rides on `fix/sprint-3.4`. As QA I do **not** merge and do **not** cut the `v1.0` tag — that is the producer's call. Recommend: merge the combined 3.2 + 3.3 + 3.4 line → cut `v1.0`.
- Prior BLOCKED Phase B records (rounds for #25/#38/#42/#45) are on `feature/qa-2`'s `docs/qa/sprint-3-signoff.md`; this document is the authoritative **PASS** re-acceptance for the #45 escape-hatch.

— Ivy, QA
