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

## Phase 2 — Progress core (Tasks 3, 5) ⬜

## Phase 3 — Dashboard (Tasks 4, 6) ⬜

## Phase 4 — Journal & status (Tasks 7, 8, 9) ⬜

## Phase 5 — Stretch + tests + handoff (Tasks 10, 11-rest, 12, 13, 14) ⬜

---

## Bugs / Issues Found

_None._
