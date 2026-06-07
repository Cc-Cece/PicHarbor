# Sprint 3.1 — Progress

> Live tracker (PROJECT_BRIEF §12). Branch: `fix/25-afc-open-watchdog` (dev clone).
> Plan: [`plan.md`](plan.md) · Issue: **#25** (unplug-safe AFC).

## Status

| # | Task | Status |
|---|------|--------|
| 1 | Extract one shared `DeviceWatchdog` (abandon-on-timeout + `DeviceStallException`); refactor `WatchdogReadStream` onto it | ✅ Done |
| 2 | Wrap `OpenReadAsync` / `ListDirectoryAsync` / `GetFileInfoAsync` native calls with the watchdog | ✅ Done |
| 3 | Stop advancing the loop on a connection-fatal device error (narrow "connection lost") | ✅ Done |
| 4 | Tests that **park a native open/stat/list** → `DeviceStallException` (fake clock); + connection-lost wiring | ✅ Done |
| 5 | `ReadOnlyContractTests` green; XML `<summary>` on new types; no new `var` | ✅ Done |
| 6 | `progress.md` + `done.md`; `Closes #25` in the commit | ✅ Done |

## Root cause (confirmed)

The Sprint 2 watchdog (`WatchdogReadStream`) only guarded the **byte read of an already-open file**.
The blocking native AFC calls (`afc_file_open`, `afc_read_directory`, `afc_get_file_info`) only checked
`cancellationToken` *before* the native call — a token can't interrupt a call already parked in native
code — so on a cable yank the **next file's `afc_file_open` parks forever** (194 s observed vs a 15 s
`--read-timeout`). The #11 hang didn't get fixed in Sprint 2; it **relocated** from the read to the open.

## Phase log

### Phase 1 — Extract the shared watchdog (behavior-preserving) ✅
- **New `GetAndSee.Core/Device/DeviceWatchdog.cs`** — the single abandon-on-timeout implementation:
  - `RaceAgainstTimeoutAsync<T>(Task<T> operation, timeout, TimeProvider, ct, Action<Task<T>>? onAbandoned)`
    — the shared core: races the operation against `Task.Delay(timeout, clock)`; on timeout it
    **abandons** the orphaned operation (schedules `onAbandoned` for whenever it settles, so any resource
    it produced is released — no handle leak) and throws `DeviceStallException`; caller cancellation →
    `OperationCanceledException` (not a stall).
  - `RunWithTimeoutAsync<T>(Func<T> blockingNativeCall, …)` — thin wrapper that runs the blocking native
    call on a worker (`Task.Run`) and delegates to the same core. Used by the 3 native calls.
- **`WatchdogReadStream` refactored onto `RaceAgainstTimeoutAsync`** — its private `AbandonRead` copy is
  **deleted**; the abandon-on-timeout logic now lives in exactly one place (resolves the review-profile
  drift item "Timeout-race / abandon-on-stall logic — about to be the right extraction"). The read still
  passes its own `inner.ReadAsync(…, ct)` task in, preserving the exact Sprint-2 cancellation/scratch
  semantics (every existing `WatchdogReadStreamTests` case stays green).
- **New `DeviceWatchdogTests`** — parks a **real blocking call** on a worker thread (the actual #25 mode,
  not a read mock), drives a `FakeTimeProvider` past the timeout → `DeviceStallException`; plus
  no-leak (late result disposed), cancellation → OCE, fast path returns value, per-file error propagates.

### Phase 2 — Guard the native open/stat/list + connection-lost ✅
- **`AfcIPhoneClient`** now takes `(TimeSpan readTimeout = default, TimeProvider? clock = null)` and routes
  `OpenReadAsync` / `ListDirectoryAsync` / `GetFileInfoAsync` through a private `GuardAsync<T>` →
  `DeviceWatchdog.RunWithTimeoutAsync`. The blocking native call runs on a worker; a stall → clean exit 3.
  - **`readTimeout == Zero` → unguarded inline native call (unchanged Sprint-1 behavior).** `CopyCommand`
    passes the existing `--read-timeout` (default 30 s) into the client, the same value `FileCopier` uses.
  - **No hot-path cost when healthy** — the timer is created per call but cancelled the instant the call
    returns (same pattern the read path already uses).
  - **Open abandon-hook** disposes a late-returning stream so the AFC file handle is never leaked.
- **Connection-lost (#3, conservative):** new `AfcErrors.IsConnectionFatal(AfcError)` → `true` only for
  `MuxError` / `ServiceNotConnected` / `ServiceClientFailed` (transport/socket gone). New
  `DeviceConnectionLostException : DeviceException`. `AfcErrors.ToException` maps a failed read-only AFC
  call → connection-lost (stop the run, resumable) **or** plain `DeviceException` (per-file fail-and-continue).
  Wired into all 4 read sites (open/read/list/stat). `FileCopier.CopyAsync` and `CopyCommand` now treat
  `DeviceStallException` **or** `DeviceConnectionLostException` identically: leave the file `in_progress`,
  stop the run cleanly, **exit 3**. A single unreadable file still `MarkFailed`s and continues.
- **Tests:** `AfcErrorsTests` (transport → fatal; per-file → not); `FileCopierTests` +2 (connection-lost
  stops the run resumably; a single unreadable file is failed-and-skipped, run not stopped).

## Read-only contract

The watchdog wraps the **same** read-only native calls (`afc_file_open` `FopenRdonly`,
`afc_read_directory`, `afc_get_file_info`, `afc_file_read`, `afc_file_close`). **No** AFC/lockdown
write/delete/rename/truncate/mkdir/link symbol added anywhere in `GetAndSee.Core`.
`ReadOnlyContractTests` (IL scan, both cases) **green**.

## Validation (local, mirrors CI)
- `dotnet build -c Release` ✅ **0 warn / 0 err**
- `dotnet test -c Release` ✅ **104 tests** (102 `GetAndSee.Tests` + 2 `ReadOnlyContractTests`)
- `dotnet format --verify-no-changes` ✅ (exit 0)

## Bugs / Issues Found
- None introduced. The fix is the #25 relocation closure; hardware re-acceptance is QA #21 Phase B.
