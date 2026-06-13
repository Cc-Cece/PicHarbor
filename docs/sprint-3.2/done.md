# Sprint 3.2 — Done (hotfix: unplug busy-spin #38 + long-path journal #39)

**Branch:** `fix/sprint-3.2` (off `main` `205d095`) · **Status:** ✅ implemented, CI-green, **PR open —
stopped at the producer review gate (NOT merged).** · **Owner:** Dev (Sage — device + journal)
**Gates next:** producer independent review → QA #21 Phase B hardware re-acceptance → v1.0.

Two data-safe but gate-blocking defects from QA #21 Phase B. Two commits, one PR.

---

## What shipped

### #39 — Long-path journal crash (the quick one)
A deep destination crashed `copy`/`status` with an unhandled `SqliteException (SQLITE_CANTOPEN)` before
any copy, because `TransferJournal.Open`/`OpenReadOnly` built the SQLite `DataSource` from an unprefixed
`Path.Combine(dest, "get-and-see.db")`.

- `TransferJournal.Open` / `OpenReadOnly` — `DataSource = LongPath.ToExtended(databasePath)` in **both**;
  the `-wal`/`-shm` sidecars inherit the `\\?\` prefix (SQLite derives them from the `DataSource`).
- `OpenReadOnly` + `StatusCommand.Run` — also route their `File.Exists` probe through `ToExtended`
  (a non-prefixed `File.Exists` silently returns `false` past MAX_PATH, so `status` wrongly reported "no
  archive"). Now `status` works at depth, not just `copy`.
- `SummaryWriter.Write` — prefix the `summary.txt` write path (the same exposure the issue asked to
  sanity-check). The destination shown *inside* the text stays the clean form.
- **Verified:** the extended-length `DataSource` works for **short and long** paths alike — every
  existing short-path journal/summary/status test stayed green, so no healthy-path regression.

### #38 — USB-unplug busy-spin (the step-back one)
On a real cable-yank the native call **returns fast and wrong** (`afc_file_read` Success + 0 bytes, or a
non-allow-listed `AfcError`) instead of parking, so it sails **past** the inactivity watchdog; each file
fails fast and the copy loop spins at 100% CPU with no exit 3. A per-call timer **structurally cannot**
catch a run-level spin (the #11 stall → #25 park → #38 spin progression).

**Design — a forward-progress / connection-health circuit breaker (the plan's candidate (b)).** New
`ForwardProgressMonitor` counts **consecutive per-file failures**, resets on any copied/skipped file, and
at a limit (default **10**) presumes the device gone and throws `DeviceConnectionLostException` → the
existing `CopyCommand` catch → clean **exit 3**, in-flight file non-`done`/resumable, `summary.txt`
written. `FileCopier` owns one instance (`ThrowIfConnectionLost()` before each file; record success/
failure at outcomes). **`CopyCommand` unchanged** — no new drift on the already-escalated `ExecuteAsync`.

**Why (b), not (a)/(c):**
- **Manifestation-agnostic** — detects the *no-progress burst*, so it catches the 0-byte **and** the
  non-allow-listed-`AfcError` manifestations (and any future one) without enumerating native codes (the
  brittleness that let #38 ship).
- **(a) immediate truncation→connection-lost — rejected:** would false-positive on a legitimately
  changed/shrunk file and soft-wedge on re-run; requiring *N consecutive* is the robust signal (and the
  size-mismatch path already feeds the breaker).
- **(c) device-enumerable probe — rejected:** a native round-trip that can itself *park* + adds latency;
  the breaker needs no device round-trip.

This is **not another timer** — it is the cross-file complement the per-call watchdog cannot provide. The
watchdog still owns the **park** manifestation (untouched); the breaker owns the **fast-fail/spin** one.

**Known limit (by design; documented per producer review):** the breaker trips on **N consecutive**
failures (default 10), so ≥10 genuinely-corrupt-in-a-row files on a *healthy* device will stop the run as
if disconnected. This is pathological (real libraries don't have 10 unreadable files back-to-back), it is
**resumable** (re-run continues past them once they're journaled `failed`/skipped), and it is data-safe.
Making the threshold user-tunable (`--max-consecutive-failures`) is deferred post-v1.

---

## Tests — reproduce the SPIN, not a stall (the named blind spot)

- `FileCopierTests.A_device_that_fast_returns_zero_bytes_for_every_file_stops_the_run_instead_of_spinning`
  — fake yields a **0-byte stream** for every file in a tight loop (the `afc_file_read` Success+0
  signature); asserts the run **stops after `limit` files**, nothing published, no `.partial`.
- `…fast_throws_a_per_file_error_for_every_file…` — every file throws a fast **non-fatal
  `DeviceException`** in a tight loop; same clean stop (manifestation-agnostic proof).
- `Isolated_failures_between_successes_never_trip_the_breaker` — alternating fail/success (more than
  `limit` total, never consecutive) completes the whole run; no false trip.
- `ForwardProgressMonitorTests` — trip-after-N, reset-on-success, reject-non-positive-limit.
- `JournalLongPathTests` (#39) — open/migrate/round-trip + read-only reopen + `summary.txt` at a DB path
  **> 260 chars** (real WAL write at depth; asserts the path actually exceeds MAX_PATH).

The spin tests drive a **fast/0-byte/fast-error tight loop**, not a parked/stall mock — the exact gap the
issue called out. The parked-open/idle-stall suite (`Read_stall_…`, `Connection_lost_during_open_…`,
`DeviceWatchdogTests`) stays **green** — **#25 not regressed.**

---

## Verification (CI mirror, local)

| Gate | Result |
|------|--------|
| `dotnet format --verify-no-changes` (solution) | ✅ exit 0 |
| `dotnet build -c Release` (warnings-as-errors) | ✅ 0 warnings / 0 errors |
| `dotnet test -c Release` — `GetAndSee.Tests` | ✅ **112 / 112** |
| `dotnet test -c Release` — `GetAndSee.SafetyTests` (`ReadOnlyContractTests`) | ✅ **2 / 2** — read-only contract intact, no new device-write symbol |
| EXE smoke (`--help` exit 0; `status` friendly exit 2) | ✅ |

## Hard rules — held

- **Read-only contract:** the #38 fix is read-path only; `ReadOnlyContractTests` green; no AFC/lockdown
  write/delete/rename/etc. symbol added.
- **Data integrity:** interrupted file left non-`done` (resumable); no `.partial` published; manifest
  exposes only `done` rows (existing invariants untouched; asserted by the spin tests).
- **No new `var`** (explicit types in all new code; `.editorconfig` var rules still `:silent` pre-#23).
  XML `<summary>` on new Core types.

## Files changed

| # | File | Change |
|---|------|--------|
| 39 | `src/GetAndSee.Core/Journal/TransferJournal.cs` | `LongPath.ToExtended` on `DataSource` (Open + OpenReadOnly) + read-only existence probe; **and** the `Open` `Directory.CreateDirectory` (review delta) |
| 39 | `src/GetAndSee.Core/Preflight/PreflightChecks.cs` | **review delta** — prefix `EnsureDestinationWritable`'s dir-create + write-probe (the earliest MAX_PATH offender) |
| 39 | `src/GetAndSee.Core/Summary/SummaryWriter.cs` | prefix the `summary.txt` write path |
| 39 | `src/GetAndSee.Cli/Commands/StatusCommand.cs` | prefix the `File.Exists` probe |
| 39 | `tests/GetAndSee.Tests/Journal/JournalLongPathTests.cs` | **new** — long-path open/round-trip/read-only/summary |
| 39 | `tests/GetAndSee.Tests/Preflight/PreflightChecksTests.cs` | **review delta, +2** — long-path probe (stock-CI regression) + machine-independent trailing-dot wiring guard |
| 38 | `src/GetAndSee.Core/Transfer/ForwardProgressMonitor.cs` | **new** — consecutive-failure circuit breaker |
| 38 | `src/GetAndSee.Core/Transfer/FileCopier.cs` | own + drive the breaker (limit param, pre-file check, record outcomes) |
| 38 | `tests/GetAndSee.Tests/Transfer/FileCopierTests.cs` | **+3** spin/no-spin tests |
| 38 | `tests/GetAndSee.Tests/Transfer/ForwardProgressMonitorTests.cs` | **new** — breaker unit tests |
| — | `docs/sprint-3.2/progress.md`, `docs/sprint-3.2/done.md` | sprint docs |

Commits: `Closes #39` (`fa8255e`) · `Closes #38` (`0419012`) · review delta (long-path preflight symmetry).

---

## Handoff / next steps

1. **Producer independent review gate** — extra weight on Correctness/Reliability + the read-only
   contract, and a hard check that the #38 tests reproduce the **real spin** (fast/0-byte/fast-error tight
   loop), not a stall mock.
2. **QA #21 Phase B re-acceptance (hardware):** real cable-unplug (both fast-fail and parked
   manifestations if reproducible) ⇒ clean resumable stop, no hang, no spin, exit 3; long-path dest copies
   end-to-end. Ivy updates `docs/qa/sprint-3-signoff.md`.
3. On PASS → merge the PR → **update `PROJECT_BRIEF.md` §7/§8** (deliberately *not* updated here — the
   gate is pre-merge) → cut **v1.0**.

**Dev does NOT merge.** Stopped at the review gate. Never pushed to `main`.
