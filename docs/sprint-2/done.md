# Sprint 2 (Phases 1–5) — Done

**Branch:** `feature/sprint-2` · **PR:** _sprint-2: UX, resumability & pre-flight_
**Team:** Nova (app), Sage (systems), Kira (UX copy); Ivy (QA), Quill (docs).
**Closes:** #11 (USB-unplug hang) · #10 (live current speed).

---

## What was built

Sprint 2 makes a multi-hour copy **trustworthy and recoverable**.

| Area | Type(s) | Notes |
|------|---------|-------|
| Read-stall watchdog (#11/R2) | `WatchdogReadStream`, `DeviceStallException` | Per-read inactivity timeout → abandon read → stall → run stops cleanly, resumable, exit 3. Injectable `TimeProvider`. No native handle leak; private scratch buffer (no ArrayPool corruption from the orphaned read). |
| Progress model (#10) | `TransferProgress`, `ProgressSnapshot` | Lock-free hot path; rolling ~3s current speed + avg + ETA; injectable clock. |
| Reporters | `IProgressReporter`, `TextProgressReporter`, `ProgressMode`, `LiveDashboard` | UI-agnostic seam; Spectre live dashboard (≤4 Hz, no device I/O) with graceful text fallback + `--no-dashboard`. |
| Journal v2 | `runs` + `devices` tables, migration | Additive `PRAGMA user_version` migration; `UpsertDevice`/`RecordRun`/`ReadDevices`/`ReadRunsSummary`. |
| Status | `StatusCommand` (`status --dest`) | Reads `get-and-see.db`, prints totals + last-run with no device. |
| Summary | `SummaryWriter` (journal-sourced) | Adds `Devices:`/`Runs:`/`Live Photos: N pairs` lines (Session-3 format). |
| Pre-flight | `HostPower`, `PreflightChecks.GetHostPowerStatus` | On-battery warning (R14) + PC-sleep note (R13). |
| Live Photos | `LivePhotoDetector` | Detection + summary line. |
| CLI flags | `--read-timeout` (30s), `--no-dashboard` | On `copy`. |

## Read-only contract — still enforced

`ReadOnlyContractTests` passes unchanged. No new device-write symbol was introduced anywhere in
`GetAndSee.Core`; the watchdog, dashboard, journal, and pre-flight code are all read-only. The
watchdog re-uses the existing read path (it only adds a timeout); there is no reconnect/new-device
code this sprint, so nothing re-opens the device.

## Verification (local)

- `dotnet build -c Release` → **0 warnings, 0 errors**.
- `dotnet test -c Release` → **74 passed** (72 unit + 2 safety, incl. `ReadOnlyContractTests`).
- `dotnet format --verify-no-changes` → clean (CI gate).
- `copy --help` shows `--read-timeout` + `--no-dashboard`; `status` smoke-tested (friendly message + exit 2 when no archive).

## Exit codes

`0` success · `1` one or more files failed · `2` pre-flight/device error · `3` read stall (resumable) · `130` Ctrl+C.

## New user-facing strings (for the Sprint 3 troubleshooting guide — Quill)

- **Stall (#11):** "Device stopped responding (asleep or disconnected). Progress saved — reconnect and run the same command to resume."
- **Run stopped early:** "Run stopped early — N file(s) not yet copied. Reconnect and run the same command to resume."
- **On battery (R14):** "Warning: running on battery — connect AC power before a large transfer."
- **Sleep note (R13):** "Tip: disable PC sleep so a long transfer isn't interrupted."
- **Status, no archive:** "No get-and-see archive found at <path>. Run get-and-see copy --dest "<path>" first."

## Not done in this PR (by design)

- **On-device acceptance** (full uninterrupted 269 GB run; real cable-unplug; dashboard no-slowdown;
  fallback on a real TTY) — **QA (Ivy)**, hardware. The watchdog (unit-tested via a mocked stalling
  stream) makes the long run safe; this closes the Sprint 1 AC-7/AC-17 deferrals.
- **Auto-reconnect-and-retry mid-run** (true R2 soft recovery) — Sprint 3. Watchdog "detect → stop
  clean → resume on re-run" is the Sprint 2 contract.
- **Queryable `live_photo_pair_id` column + manifest-view change** — Sprint 3 (the cuttable part of
  Task 10). Detection + the `Live Photos: N pairs` summary line ship now.
- `SetThreadExecutionState` keep-awake (R13 prevention), `--verify-hash`, long-path `\\?\`, release
  workflow, full user docs — Sprint 3.

## Notes for the next chat

- `DeviceException` is now **unsealed**; `DeviceStallException` derives from it.
- Core has `AllowUnsafeBlocks=true` (required by the `[LibraryImport]` power-status P/Invoke).
- Test project references `GetAndSee.Cli` (for the dashboard render test) + `Spectre.Console.Testing`
  + `Microsoft.Extensions.TimeProvider.Testing`; `InternalsVisibleTo(GetAndSee.Tests)` on Core and Cli.
- Journal schema is at `user_version = 2`. The deferred Live-Photo column would be a v3 migration
  (add `live_photo_pair_id` to `files` via guarded `ALTER`, recreate the `manifest` view).
