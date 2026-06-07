# Sprint 2 — UX, Resumability & Pre-flight

**Sprint Goal:** Make a multi-hour copy *trustworthy and recoverable*: a read-stall watchdog that turns the USB-unplug/​sleep hang (#11, R2) into a clean, resumable stop; and a live Spectre.Console dashboard with current speed (#10) — without slowing the transfer.

**Branch:** `feature/sprint-2`

> **Read first:** `PROJECT_BRIEF.md` (§9 Safety — still governs; §7 scope), `docs/brainstorm/sprint-2-consilium.md` (the agreed cut + ordering), `docs/sprint-1/afc-library-decision.md` §5 (native read caveats), `docs/risk-register.md` (R2, R9, R13, R14).
>
> **Carry-over from Sprint 1:** open issues **#11** (USB-unplug hang, `severity:major`) and **#10** (live current-speed, `enhancement`). Both resolved here.

## Prioritized Task List

| # | Task | Owner | Est | Description |
|---|------|-------|-----|-------------|
| 1 | **Read-stall watchdog (#11, R2)** | Sage | L | Wrap each AFC chunk read so that **no bytes for `--read-timeout` seconds (default 30)** cancels the read, raises `DeviceException`, marks the in-flight file non-`done` (resumable), **stops the run cleanly** (no thrash), exits non-zero. Native read runs on a cancellable/abandonable path — **no native handle leak**. **Build an injectable timeout seam** (`TimeProvider` + configurable timeout) so QA can test against a mocked stalling stream. **No mid-run auto-reconnect** (Sprint 3). Closes #11. |
| 2 | Watchdog error copy | Kira | S | User-facing message on stall: e.g. *"Device stopped responding (asleep or disconnected). Progress saved — reconnect and run the same command to resume."* Distinct from AC-13 (no device) and B-7 (service down). |
| 3 | `IProgressReporter` abstraction | Nova | S | Extract progress reporting behind an interface so the copy engine is UI-agnostic and unit-testable. Sprint 1 per-file text output becomes the **fallback** `TextProgressReporter`. |
| 4 | **Live dashboard (Spectre.Console)** | Nova | L | `LiveDashboard : IProgressReporter`. Shows overall %/bytes, current file + its bar, **current MB/s + avg MB/s + ETA**, counts (done/skipped/failed/elapsed). Refresh **≤4 Hz**, computed only from data the copy loop already has — **no extra device I/O**. Must not measurably slow the copy. |
| 5 | **Current + avg speed + ETA (#10)** | Nova | M | Rolling ~3s window over bytes already counted in `FileCopier.CopyStreamAsync` → `current MB/s`; cumulative → `avg`; ETA from current. Survives slow-start/idle gaps without 0/spike artifacts. Feeds both per-file and overall. Closes #10. |
| 6 | **Graceful fallback + `--no-dashboard`** | Nova | M | Auto-detect non-interactive/redirected/piped output (`Console.IsOutputRedirected`) → fall back to `TextProgressReporter`. `--no-dashboard` flag forces text. CI and piping must still work cleanly. |
| 7 | **`status` subcommand** | Nova | M | `status --dest <path>` opens `get-and-see.db` **read-only**, prints last-run summary + archive totals **with no device attached**. Reuses `SummaryWriter`. Clear message if no db/dest. |
| 8 | **`runs` + `devices` journal tables** | Sage | M | Additive schema (bump `schema_version`, migrate existing dbs in place). `runs`: per-invocation row (start/end, command, copied/skipped/failed, exit). `devices`: udid/name/model/first+last seen. Populate each run. Feed `status` + the `summary.txt` "Runs:"/"Devices:" lines (closes a Session-3 gap). |
| 9 | Pre-flight: on-battery warning (R14) + sleep note (R13) | Sage | S | Warn (not block) if the host is on battery power before a long run. Print a one-line "disable PC sleep for long runs" note. (Actual keep-awake API is Sprint 3.) |
| 10 | **Live-Photo pair detection** (CUTTABLE) | Nova | M | Link `IMG_NNNN.HEIC`/`.JPG` + `IMG_NNNN.MOV` by basename + close mtime → populate `live_photo_pair_id`; add `summary.txt` "Live Photos: N pairs". **Cut first if Tasks 1/4 are at risk.** |
| 11 | Unit tests | Ivy | M | Watchdog (injected timeout → cancels + marks non-done + resumable, via mocked stalling stream); speed/ETA math (rolling window, idle gaps); `IProgressReporter` fallback selection; `status` against a fixture db; `runs`/`devices` schema + migration; Live-Photo pairing (if shipped). |
| 12 | XML doc comments on new public API | Nova / Sage | S | Every new public class/method in `GetAndSee.Core` gets a one-line `<summary>`. |
| 13 | Stub docs | Quill | S | `--help` review for `status` + `--no-dashboard`/`--read-timeout`; README note (status exists, dashboard default); collect new error strings for the Sprint 3 troubleshooting guide. |
| 14 | `docs/sprint-2/progress.md` + `done.md` + handoff | Nova | S | Per PROJECT_BRIEF §12. |

**Size:** S = <1hr, M = 1–3hr, L = 3–5hr.

## Work Schedule

1. **Phase 1 — Resilience** (Tasks 1, 2, 11-partial): watchdog + message + its tests. **The #11 fix. Commit + push.** (This unblocks Ivy's full re-run and makes long copies safe.)
2. **Phase 2 — Progress core** (Tasks 3, 5): `IProgressReporter` + speed/ETA math (engine-side, UI-agnostic). **Commit.**
3. **Phase 3 — Dashboard** (Tasks 4, 6): Spectre live view + fallback/`--no-dashboard`. **Commit.**
4. **Phase 4 — Journal & status** (Tasks 7, 8, 9): `runs`/`devices` tables, `status` verb, pre-flight warnings. **Commit.**
5. **Phase 5 — Stretch + tests + handoff** (Tasks 10, 11-rest, 12, 13, 14): Live-Photo pairing (if time), remaining tests, XML docs, stub docs, handoff. **Final commit + push + PR.**

Update `docs/sprint-2/progress.md` after each phase.

## Success Criteria

- [ ] `dotnet test` green incl. `ReadOnlyContractTests` and new watchdog/speed/status tests
- [ ] CI green on PR (real build + format + tests on `windows-latest`)
- [ ] **#11 closed:** USB unplug (or read stall) during copy → tool detects within `--read-timeout`, prints the actionable message, marks in-flight file non-`done`, exits non-zero, and a re-run resumes cleanly — **no indefinite hang**
- [ ] Watchdog is unit-tested via an injected timeout + mocked stalling stream (no cable-yank required to test)
- [ ] **#10 closed:** during a copy the dashboard shows live **current MB/s**, avg MB/s, and ETA, updating ≥ every ~2s
- [ ] Dashboard does **not** measurably slow the copy (MB/s with vs without on the same subset within noise)
- [ ] Piped/redirected output and `--no-dashboard` fall back to text cleanly; CI output is readable
- [ ] `status --dest <path>` prints last-run + totals **with no iPhone connected**
- [ ] `get-and-see.db` has `runs` (and `devices`) populated; `summary.txt` "Runs:"/"Devices:" lines render
- [ ] On-battery start prints a warning; PC-sleep note shown for long runs
- [ ] **Read-only contract intact** — no new device-write symbol; `ReadOnlyContractTests` still passes
- [ ] **QA full-archive re-run** (Ivy, hardware): one uninterrupted 269 GB run completes, live `summary.txt` produced (closes S1 AC-7/AC-17 deferrals)

## What's NOT in This Sprint

| Cut | Reason |
|-----|--------|
| Auto-reconnect-and-retry mid-run (true R2 soft recovery) | Watchdog "detect → stop clean → resume on re-run" is safer for v1; reconnection is more state/failure modes → Sprint 3 |
| `SetThreadExecutionState` keep-awake (R13 prevention) | Sprint 3; Sprint 2 only warns |
| `--verify-hash` (R12), long-path `\\?\` (R6) | Sprint 3 |
| Release workflow / single-file EXE on tag | Sprint 3 |
| Full user docs (README/troubleshooting/manifest-schema) | Sprint 3 (Quill) — Sprint 2 ships only stubs |
| Live-Photo pairing **if** the dashboard/watchdog are at risk | Designated release valve → Sprint 3 |
| HEIC convert, geocoding, smart-folder views, GUI | v2 backlog |

## Dev + QA Team Prompt

```
You are the get-and-see dev team — Nova (app), Sage (systems), Kira (UX copy),
with Ivy (QA) and Quill (docs). Execute Sprint 2.

First, in e:\src\get-and-see-dev:
  git checkout main && git fetch origin && git reset --hard origin/main
  git checkout -b feature/sprint-2

Read: PROJECT_BRIEF.md (§9 Safety still governs), docs/brainstorm/sprint-2-consilium.md,
docs/sprint-2/plan.md, docs/sprint-1/afc-library-decision.md §5, docs/risk-register.md.

Two pillars, in order:
  1. Read-stall WATCHDOG (#11 + R2) — per-chunk read timeout (--read-timeout, default 30s)
     → cancel → DeviceException → mark in-flight file non-done (resumable) → stop the run
     cleanly → non-zero exit → actionable message. NO native handle leak. NO mid-run
     auto-reconnect (Sprint 3). Build an INJECTABLE timeout seam so QA can test it with a
     mocked stalling stream (no cable-yank needed).
  2. Live DASHBOARD (Spectre.Console) + current speed (#10) — behind an IProgressReporter
     interface (Sprint 1 text output = fallback). current MB/s (rolling ~3s) + avg + ETA +
     counts. Refresh <=4 Hz, NO extra device I/O, must not slow the copy. Graceful fallback
     on piped/redirected/non-TTY output and --no-dashboard.

Then: status subcommand, runs/devices journal tables (additive migration), pre-flight
battery warning. Live-Photo pairing is the CUTTABLE stretch — drop it first if pillars slip.

HARD RULES (unchanged): IPhoneClient stays read-only; no new device-write symbols anywhere
in GetAndSee.Core; ReadOnlyContractTests must still pass. Any reconnect/new device code
re-opens READ-ONLY. No CLI flag mutates the device.

Process: all work on feature/sprint-2; commit + update docs/sprint-2/progress.md after each
phase; use the PR template; reference issues ("Closes #11", "Closes #10"); CI green before
the final PR. Never push to main. When done, push and open ONE PR
"sprint-2: UX, resumability & pre-flight" and stop for producer (Remy) review.

QA (Ivy), after the dev PR is up: re-run the FULL 269 GB acceptance now that the watchdog
makes long runs safe (closes S1 AC-7/AC-17), test the watchdog (injected + real unplug),
verify dashboard adds no slowdown and the fallback works, re-affirm the read-only contract.
SAFETY RULE #0 still governs — never issue a device write, ever.

Take your time — correctness and the read-only contract beat speed.
```
