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

## Bugs / Issues Found

- (#39) Pre-existing latent gap beyond the DataSource: `OpenReadOnly`/`StatusCommand` `File.Exists`
  probes also fail past MAX_PATH (silent `false`). Fixed alongside so `status` works at depth, not just
  `copy`.
