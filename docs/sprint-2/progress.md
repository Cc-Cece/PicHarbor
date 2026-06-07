# Sprint 2 — Progress (UX, Resumability & Pre-flight)

Branch: `feature/sprint-2` (off `main` `2fa0ce5`). Dev: Nova (app), Sage (systems), Kira (UX copy);
Ivy (QA), Quill (docs). Closes **#11** (USB-unplug hang) and **#10** (live current speed).

Two pillars first: (1) read-stall watchdog, (2) live dashboard. Update after every phase (§12).

---

## Phase 1 — Resilience: read-stall watchdog (Tasks 1, 2, 11-partial) ✅

**The #11 fix.** A yanked cable / sleeping device parks the native AFC read forever (Sprint 1 hang).
Now a per-read inactivity watchdog turns that into a clean, resumable stop.

**Built:**
- **`WatchdogReadStream`** (`GetAndSee.Core/Device`) — read-only `Stream` decorator. Races each
  `ReadAsync` against a `TimeProvider`-based delay; if no bytes arrive within the timeout it abandons
  the read and throws `DeviceStallException`. **Injectable seam:** `(Stream inner, TimeSpan timeout,
  TimeProvider)` — QA tests it with a mocked stalling stream, no cable-yank needed (Ivy's ask).
  - **No native handle leak:** on stall the caller is never blocked; disposal of the inner stream
    (closing the AFC handle) is scheduled for whenever the orphaned read finally returns/faults. If it
    never returns, the run is stopping and the OS reclaims it.
  - **No buffer-corruption hazard:** the watchdog reads into a private, reusable scratch buffer and
    copies out on success — so an orphaned read can never write into a caller buffer that `FileCopier`
    has already returned to the shared `ArrayPool`. One memcpy/chunk (~0.3%); reused buffer → zero
    hot-path allocation.
  - Distinguishes caller Ctrl+C (→ `OperationCanceledException`) from a genuine stall.
- **`DeviceStallException : DeviceException`** (Kira's copy): *"Device stopped responding (asleep or
  disconnected). Progress saved — reconnect and run the same command to resume."* Subtype so existing
  device-error handling catches it, distinct so the run stops cleanly instead of thrashing.
- **`FileCopier`** — new injectable `readTimeout`; wraps the source in `WatchdogReadStream` when set.
  On `DeviceStallException` it leaves the in-flight file **`in_progress`** (resumable), deletes the
  staging `.partial`, and **propagates** so the run stops (every subsequent read would also stall).
- **`copy --read-timeout <seconds>`** (default **30**, `0` disables). On stall the run stops cleanly,
  writes `summary.txt`, prints the actionable message + a "Run stopped early — N not yet copied"
  summary line, and **exits 3** (non-zero). `DeviceException` unsealed to allow the subtype.

**Tests (Task 11-partial):** `WatchdogReadStreamTests` (5) — returns bytes before timeout; throws
`DeviceStallException` on stall; actionable message; Ctrl+C → OCE not stall; **no-handle-leak**
(orphaned read completing disposes inner). `FileCopierTests` +1 — stall leaves file `in_progress`,
no `.partial` leak, no date folder, propagates. New `ControlledReadStream` test helper.

**Verified:** build 0/0; **42 tests** pass (40 unit + 2 safety); `ReadOnlyContractTests` still green
(watchdog adds no device-write symbol); `dotnet format` clean.

**Notes:** `InternalsVisibleTo(GetAndSee.Tests)` added so the internal watchdog is unit-testable;
CA2022 suppressed in the test project (controlled start-then-release-then-await read patterns).

## Phase 2 — Progress core (Tasks 3, 5) ✅

**UI-agnostic progress model + speed/ETA math** (engine-side), with the Sprint 1 text output now the
fallback reporter. The live dashboard (Phase 3) plugs into the same seam.

**Built (all in `GetAndSee.Core/Progress`):**
- **`TransferProgress`** — thread-safe live model. Hot path `RecordBytes(delta)` is a single
  `Interlocked.Add` (no locks, no allocation, no device I/O) so the readout cannot slow the copy.
  Current speed is a **rolling ~3s window** sampled when `Snapshot()` is called (dashboard does this at
  ≤4 Hz); avg = streamed/elapsed; ETA = remaining/rate. Injectable `TimeProvider` → deterministic
  tests. Handles slow-start (falls back to avg below a 0.5s window span — no spike) and idle gaps
  (current decays to 0, never negative). Closes **#10** (current speed).
- **`ProgressSnapshot`** — immutable view (counts, bytes, current/avg B/s, ETA, current file).
- **`IProgressReporter`** — `Start(TransferProgress)` + `OnFileCompleted(CopyResult)` + `Dispose`.
  Keeps the copy loop UI-agnostic.
- **`TextProgressReporter`** — the Sprint 1 per-file lines (`[done]`/`[skip]`/`[fail]`), now plain-text
  (ANSI-free, correct for piped/CI output). Writes to an injectable `TextWriter` for testability.
- **`ProgressMode.ShouldUseDashboard(disabled, redirected)`** — the fallback decision (used in Phase 3).
- **`FileCopier`** — new injectable `onBytesStreamed` per-chunk callback feeding `RecordBytes`.
- **`CopyCommand`** — copy loop now drives `TransferProgress` and routes per-file output through the
  reporter (text for now; dashboard selection lands in Phase 3).

**Tests:** `TransferProgressTests` (7, `FakeTimeProvider`) — avg, rolling current, ETA, ETA-null,
no slow-start spike, idle-gap decay, counts/processed-bytes. `TextProgressReporterTests` (4).
`ProgressModeTests` (4). Added `Microsoft.Extensions.TimeProvider.Testing`.

**Verified:** build 0/0; **57 tests** pass (55 unit + 2 safety); safety contract intact; format clean.

## Phase 3 — Dashboard (Tasks 4, 6) ✅

**Built:**
- **`LiveDashboard : IProgressReporter`** (`GetAndSee.Cli/Ui`) — Spectre.Console live panel: overall
  bar (% + bytes + file count), current file + its bar, **current MB/s + avg + ETA**, counts
  (done/skipped/failed). Renders on a **background task at ~4 Hz** (250 ms) reading only
  `TransferProgress.Snapshot()` — **no device I/O**, never blocks the copy. Device-supplied filenames
  are `Markup.Escape`d so a hostile name can't break rendering. A render error can never crash the run
  (caught); `Dispose` cancels the loop, draws a final frame, and tears down the live region.
- **`--no-dashboard`** flag + **graceful fallback:** `CopyCommand` selects the reporter via
  `ProgressMode.ShouldUseDashboard(noDashboard, Console.IsOutputRedirected)` — interactive TTY →
  dashboard; piped/redirected/`--no-dashboard` → `TextProgressReporter`. The reporter is disposed
  **before** the run summary so output is clean; the stall message prints after teardown.

**Tests:** `LiveDashboardTests` (3, Spectre `TestConsole`) render `RenderSnapshot` deterministically —
counts/speed/current file shown, **markup-hostile filename escaped without throwing**, failed count
shown. (Live-loop timing + no-slowdown are QA hardware checks.) Added `Spectre.Console.Testing`;
test project now references `GetAndSee.Cli` (`InternalsVisibleTo`).

**Verified:** build 0/0; **60 tests** pass (58 unit + 2 safety); safety intact; format clean;
`copy --help` shows `--read-timeout` and `--no-dashboard`.

## Phase 4 — Journal & status (Tasks 7, 8, 9) ✅

**Built:**
- **`runs` + `devices` tables (Task 8):** additive schema migration via `PRAGMA user_version`
  (bumped to 2). Opening a Sprint 1 (v1) db creates the two new tables **in place** without touching
  `files` data. `devices(udid, name, model, first_seen, last_seen)` with upsert (refreshes last_seen);
  `runs(id, started_at, finished_at, command, copied, skipped, failed, exit_code, device_udid)`.
  New journal methods: `UpsertDevice`, `RecordRun`, `ReadDevices`, `ReadRunsSummary`. `CopyCommand`
  records the device at run start and the run at the end.
- **`summary.txt` "Runs:"/"Devices:" lines (Session-3 gap closed):** `SummaryWriter` now renders from
  the journal's `ReadDevices()` + `ReadRunsSummary()` (devices line lists all devices; `Runs: N (first
  run …, latest …)`; `Last run:` from the latest run). Same renderer serves `copy` and `status`.
- **`status` subcommand (Task 7):** `status --dest <path>` opens `get-and-see.db` and prints archive
  totals + last-run summary **with no device attached**. Clear message + non-zero exit when no archive
  exists. Registered in `Program`.
- **Pre-flight battery warning (Task 9, R14) + sleep note (R13):** `HostPower` reads
  `GetSystemPowerStatus` via `[LibraryImport]` (guarded by `OperatingSystem.IsWindows()`);
  `PreflightChecks.GetHostPowerStatus()` with an **injectable** provider for tests. `copy` prints a
  one-line "disable PC sleep" tip and, on battery, an AC-power warning before the run.

**Tests:** `JournalSchemaV2Tests` (5) — devices upsert/read, runs history summary, empty history,
status-style summary, **in-place v1→v2 migration preserving files**. `PreflightPowerTests` (3,
injected provider). `SummaryWriterTests` updated to the journal-sourced signature.

**Verified:** build 0/0; **68 tests** pass (66 unit + 2 safety); safety contract intact; format clean;
`status` smoke-tested (friendly message + exit 2 when no archive).

**Notes:** `AllowUnsafeBlocks` enabled on Core (required by the `[LibraryImport]` source generator).

## Phase 5 — Stretch + tests + handoff (Tasks 10, 11-rest, 12, 13, 14) ⬜

---

## Bugs / Issues Found

_None._
