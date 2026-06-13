# Sprint 3.2 — Progress (hotfix: unplug busy-spin #38 + long-path journal #39)

Branch: `fix/sprint-3.2` off `main` (`205d095`). Owner: Dev (Sage — device + journal).
Two defects, two commits. Stops at the producer review gate (no merge).

---

## #39 — Long-path journal crash (the quick one) ✅

**Root cause (confirmed in source):** `TransferJournal.Open` and `OpenReadOnly` built the SQLite
`DataSource` from `Path.Combine(dest, "get-and-see.db")` with **no** `LongPath.ToExtended`. A deep
destination (DB path ≥ ~260 chars) crashed `SqliteConnection.Open()` with `SQLITE_CANTOPEN` before any
file was copied; a slightly shorter root still crashed at WAL-init because the `-wal`/`-shm` sidecars
push a few chars past the DB path. The media copy path was already `\\?\`-prefixed (R6), but the
journal opens first and was unprefixed.

**Fix:**
- `TransferJournal.Open` — `DataSource = LongPath.ToExtended(databasePath)`. `DatabasePath` keeps the
  clean, user-facing form for display/diagnostics.
- `TransferJournal.OpenReadOnly` — prefix **both** the `File.Exists` probe (a non-prefixed
  `File.Exists` on a > 260 path silently returns `false`, so `status` would wrongly report "no
  archive") **and** the `DataSource`. The `-wal`/`-shm` sidecars inherit `\\?\` because SQLite derives
  them from the `DataSource` (verified by the round-trip test, which performs a real WAL write at depth).
- `SummaryWriter.Write` — prefix the `summary.txt` write path (the same MAX_PATH exposure the issue
  asked us to sanity-check). The destination shown *inside* the text stays the clean form.
- `StatusCommand.Run` — prefix its `File.Exists` probe so a deep archive root is detected and falls
  through to the (now long-path-safe) read-only open.

**Test:** `tests/GetAndSee.Tests/Journal/JournalLongPathTests.cs`
- `Opens_migrates_and_round_trips_a_row_when_the_db_path_exceeds_260_chars` — builds a destination
  root that stays < 260 (so `Open`'s directory creation works) while `…/get-and-see.db` lands at ~272
  chars. Opens, migrates, `MarkDone` (a real WAL write at depth), reads the manifest, then reopens
  **read-only** and reads again. Asserts the DB path > 260 so the fix is genuinely exercised. Without
  the fix this throws `SQLITE_CANTOPEN` before the first assert.
- `Writes_summary_txt_when_the_destination_path_exceeds_260_chars` — `SummaryWriter.Write` at the same
  depth, read back via the `\\?\` prefix.
- Cleanup clears the SQLite connection pool (handles outlive `Dispose`) and removes the long-path
  artifacts via the extended prefix.

**Verified:** journal + summary + status suites green, including all **short-path** SQLite tests — the
extended-length `DataSource` works for both short and long paths (no healthy-path regression).

---

## #38 — USB-unplug busy-spin (the step-back one) ✅

**Root cause (confirmed in source + issue evidence):** the shared `DeviceWatchdog` (#25) only trips on a
native call that **parks** — an *inactivity* timer. A real cable-yank instead makes the native call
**return fast and wrong**: `afc_file_read` reports `Success` + `0` bytes (a premature EOF that fails the
size check), or an `AfcError` that is **not** in `AfcErrors.IsConnectionFatal`'s 3-code allow-list (so it
maps to a per-file `DeviceException`). Either way the call returns instantly, sails **past** the
inactivity timer, the file fails, and the **outer copy loop spins to the next file** — 100% CPU, no
stall message, no `summary.txt`, no exit 3 (the #11 stall → #25 park → #38 spin progression). A per-call
timer **structurally cannot** see this: no single call is slow.

**Design chosen — a forward-progress / connection-health circuit breaker (candidate (b)), and why:**

The spin is a **run-level** pattern, not a single-call problem, so the fix lives at the run level. New
`ForwardProgressMonitor` (`src/GetAndSee.Core/Transfer/ForwardProgressMonitor.cs`) counts **consecutive
per-file failures**; a copied/skipped file is forward progress and **resets** the streak; once the streak
reaches a limit (default **10**) the device is presumed gone and it throws `DeviceConnectionLostException`
→ the existing `CopyCommand` catch → clean **exit 3** + `summary.txt`, in-flight file non-`done`
(resumable). `FileCopier` owns one instance and calls `ThrowIfConnectionLost()` before each file and
`RecordSuccess()`/`RecordFailure()` at each outcome. **`CopyCommand` is unchanged** (no added drift to the
already-escalated `ExecuteAsync`).

Why this over the other candidates:
- **Manifestation-agnostic.** It detects the *pattern* (a no-progress burst), so it catches **both** the
  0-byte-EOF and the non-allow-listed-`AfcError` manifestations — and any future one — without having to
  enumerate native error codes (which is exactly the brittle gap that let #38 ship).
- **(a) truncation→connection-lost *immediately* — rejected.** Treating the first short read as a
  whole-run disconnect would false-positive on a legitimately changed/shrunk file (size known at
  enumerate > bytes readable at copy, connection fine) and soft-wedge the run on re-run. Requiring *N
  consecutive* failures is the robust signal; the existing size-mismatch path already fails the short
  file and feeds the breaker, so no separate immediate-abort is needed.
- **(c) device-enumerable health probe — rejected.** A probe is itself a native AFC round-trip that can
  *park* (needing yet another watchdog) and adds latency to every failure; the breaker infers
  "device gone" from the failure pattern with no device round-trip.

**Not just another timer:** the breaker is a cross-file progress monitor, the structural complement the
inactivity watchdog (per-call) cannot provide. The watchdog still handles the **park** manifestation
(unchanged); the breaker handles the **fast-fail / spin** manifestation.

**Tests — reproduce the SPIN, not a stall:** `FileCopierTests`
- `A_device_that_fast_returns_zero_bytes_for_every_file_stops_the_run_instead_of_spinning` — a fake whose
  every `OpenReadAsync` yields a **0-byte stream** (the `afc_file_read` Success+0 signature), driven in a
  tight loop; asserts the run **stops after `limit` files** (no spin), nothing published, no `.partial`.
- `A_device_that_fast_throws_a_per_file_error_for_every_file_stops_the_run_instead_of_spinning` — every
  file throws a fast **non-fatal `DeviceException`** in a tight loop; same clean stop (the
  manifestation-agnostic proof).
- `Isolated_failures_between_successes_never_trip_the_breaker` — alternating fail/success (more than
  `limit` total failures, never consecutive) completes the whole run with no false trip.
- `ForwardProgressMonitorTests` — unit-level trip-after-N, reset-on-success, reject-non-positive-limit.

These reproduce a **fast/0-byte/fast-error tight loop**, not a parked/stall mock — the gap the issue
named. The existing parked-open/idle-stall tests (`Read_stall_…`, `Connection_lost_during_open_…`,
`DeviceWatchdogTests`) stay **green** (untouched watchdog path), so #25 is not regressed.

**Verified:** full suite green in Release — `GetAndSee.Tests` 110/110, `ReadOnlyContractTests` 2/2 (no new
device-write symbol; the fix is read-path only), `dotnet format` clean, EXE `--help`/`status` smoke OK.

---

## Bugs / Issues Found

- (#39) Pre-existing latent gap beyond the DataSource: `OpenReadOnly`/`StatusCommand` `File.Exists`
  probes also fail past MAX_PATH (silent `false`). Fixed alongside so `status` works at depth, not just
  `copy`.
- (#38) The connection-fatal allow-list (`AfcErrors.IsConnectionFatal`, 3 codes) is necessarily
  incomplete for every real disconnect manifestation. Rather than chase codes, the breaker makes the fix
  independent of which `AfcError` a yank produces. Left the allow-list as-is (still the right fast-path
  classifier for the *parked*/known cases).

---

## Review-gate delta (producer PR #41 review = PASS-WITH-NITS) ✅

**Finding (verified in code):** #39's long-path fix was **asymmetric**. The SQLite `DataSource` was
prefixed, but the managed-filesystem touches that run *before* the journal were still unprefixed, so a
deep destination failed at **pre-flight** on a **stock** Windows machine (`LongPathsEnabled=0`) with a
misleading *"Destination is not writable"* (exit 2) — **before** the fixed journal was ever reached. Our
dev/QA box hides this because it has the long-paths registry key **on**; SQLite's native VFS ignores that
key, which is why only the `DataSource` crashed in QA. R6 must work on a stock machine.

**Fix (same branch, updates PR #41) — prefix the two remaining destination-root touches:**
- `PreflightChecks.EnsureDestinationWritable` — route **both** `Directory.CreateDirectory(destinationRoot)`
  and the write-probe path (`Path.Combine(destinationRoot, ".get-and-see-write-probe-….tmp")`) through
  `LongPath.ToExtended` (compute one `extendedRoot`, use it for the dir-create and the probe). The
  PreflightException message still shows the clean `destinationRoot`. **This is the earliest/worst
  offender:** the ~61-char probe filename crosses MAX_PATH at a *shallower* root than the 14-char
  `get-and-see.db`.
- `TransferJournal.Open` — wrap the `Directory.CreateDirectory(destinationRoot)` above the (already-fixed)
  `DataSource` line in `LongPath.ToExtended` too.
- **Scan result:** swept every managed FS touch in `src/` (`Directory.*`, `File.*`, `FileStream`). All
  `FileCopier` touches already derive from its constructor's `LongPath.ToExtended(destinationRoot)`
  (staging dir, final path, collision `File.Exists`, `SafeDelete`) — already prefixed. `SummaryWriter`,
  `OpenReadOnly`, `StatusCommand` were prefixed in the first #39 commit. `EnsureSufficientFreeSpace` uses
  the **drive root** (`DriveInfo`), not a deep path — no exposure. So preflight + journal-`Open` were the
  only two left. `ToExtended` is idempotent (no-op on short/already-prefixed paths), so shallow
  destinations are unaffected (confirmed: all existing short-path tests green).

**Tests (`PreflightChecksTests`, +2):**
- `Writable_check_passes_when_the_write_probe_path_exceeds_260_chars` — builds a root **< 260** (so the
  root dir is creatable even unprefixed, isolating the **probe** as the first offender) whose probe path
  is **> 260**; asserts `EnsureDestinationWritable` does not throw and the dir is created. On a **stock**
  box (e.g. the GitHub `windows-latest` CI runner, `LongPathsEnabled=0`) this **fails before the fix**
  (unprefixed probe write throws) and **passes after**; green on a long-paths box either way. Never relies
  on the registry key to *pass*.
- `Writable_check_routes_the_directory_create_through_the_long_path_prefix` — **machine-independent**
  wiring guard: passes a destination segment ending in `.` (a trailing dot). Windows normalization strips
  a trailing dot from an **unprefixed** create but the verbatim `\\?\` form **preserves** it (verified
  empirically; registry-**independent**), so the dot survives **iff** the method prefixes the path. Remove
  the `ToExtended` wiring and this goes **red on any machine** (including the long-paths dev/QA box).

**Verified:** `dotnet format` clean · Release build 0/0 · `GetAndSee.Tests` **112/112** ·
`GetAndSee.SafetyTests` **2/2** (`ReadOnlyContractTests` intact — no device-write symbol; FS-path change
only). Dev box confirmed `LongPathsEnabled=1` (matches the producer's note).

**Out of scope (not done, per the review):** `--max-consecutive-failures` flag (post-v1); the in-file
infinite-trickle shape. `CopyCommand` and the breaker logic untouched (reviewed clean).
