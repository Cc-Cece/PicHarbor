# Manifest & database schema

Every destination has a SQLite file at **`.picharbor/get-and-see.db`**. It is both the
**in-progress journal** (what enables resume) and the long-term **manifest** (a queryable index of your
archive). Keep the entire `.picharbor` directory with the original photos when moving a member.
The portable `library.json` describes the logical library and its registered storage members.
Legacy root-level databases remain readable and are relocated through a verified SQLite backup during upgrade.

The database is the "substrate": the archive is plain files on disk, and this is the index over them. You
can query it with **any** SQLite tool — no part of `get-and-see` is required to read it.

```pwsh
# e.g. with the sqlite3 CLI
sqlite3 -readonly "D:\Photos\.picharbor\get-and-see.db" "SELECT COUNT(*) FROM manifest;"
```

> **Read-only tip:** open it read-only so a query can never interfere with a running copy:
> `sqlite3 -readonly "D:\Photos\.picharbor\get-and-see.db" "..."`

The schema version is tracked in `PRAGMA user_version` (currently **8**). Future versions are refused
before any migration. Direct external writes are unsupported; writer guards reject older applications.

---

## The `manifest` view (start here)

The `manifest` view is the user-facing surface: it exposes the columns you care about for **completed**
physical files only (`state = 'done'`, `replica_state = 'Available'`), with one row per local path.
External proof, missing and recycled locations do not claim a completed local file. Use the library
commands or GUI for combined logical totals across storage members.

| Column | Type | Meaning |
|--------|------|---------|
| `source_path` | TEXT | Absolute path on the device, e.g. `/DCIM/100APPLE/IMG_4821.HEIC`. |
| `dest_path` | TEXT | Path on the PC **relative to the destination root**, e.g. `2024-08\IMG_4821.HEIC` (the folder layout depends on the archive's `--organize-by` scheme). |
| `size_bytes` | INTEGER | File size in bytes (the verified size). |
| `source_mtime` | TEXT | Device last-modified time (ISO-8601), or `NULL`. |
| `exif_datetime_original` | TEXT | EXIF capture time (`DateTimeOriginal`) when available, else `NULL`. |
| `gps_latitude` | REAL | GPS latitude from EXIF, or `NULL`. |
| `gps_longitude` | REAL | GPS longitude from EXIF, or `NULL`. |
| `camera_make` | TEXT | EXIF camera make (e.g. `Apple`), or `NULL`. |
| `camera_model` | TEXT | EXIF camera model (e.g. `iPhone 12 Pro`), or `NULL`. |
| `copied_at` | TEXT | UTC time this file was copied (ISO-8601). |
| `sha256` | TEXT | Lowercase SHA-256 content digest, recorded for every new copy. Older rows are filled by self-check. |

---

## Tables

### `files` — every file's transfer state (the journal)

A source record's identity includes **device ID + source path + source size**. Modification time
participates in fast incremental checks; strict verification rereads source content. Unknown historical
device provenance cannot prove that another device's file is already backed up.

| Column | Type | Notes |
|--------|------|-------|
| `id` | INTEGER | Primary key. |
| `device_id` | TEXT | Source device identity; empty for unresolved historical provenance. |
| `source_path` | TEXT | Device path. Part of the unique identity. |
| `source_size` | INTEGER | Device-reported size. Part of the unique identity. |
| `source_mtime` | TEXT | Device modified time (ISO-8601) or `NULL`. |
| `dest_path` | TEXT | Relative destination path once known, else `NULL`. |
| `state` | TEXT | `pending`, `in_progress`, `done`, `failed`, `missing`, or `external` (verified on another member). |
| `media_id` | TEXT | Verified content identity, independent of source name. |
| `replica_state` | TEXT | Availability/maintenance state of this physical location. |
| `replica_root` | TEXT | Surviving member location for retired or externally verified content. |
| `error_message` | TEXT | Last failure reason when `failed`, else `NULL`. |
| `started_at` | TEXT | When the file last started copying. |
| `finished_at` | TEXT | When it finished (or failed). |
| `exif_datetime_original` | TEXT | EXIF capture time, or `NULL`. |
| `gps_latitude` / `gps_longitude` | REAL | EXIF GPS, or `NULL`. |
| `camera_make` / `camera_model` | TEXT | EXIF camera, or `NULL`. |
| `copied_at` | TEXT | UTC completion time. |
| `sha256` | TEXT | SHA-256 hex; nullable only for older unverified records. |

Indexes include device-scoped source identity, `ix_files_state`, and `ix_files_dest_path`.

Multiple source rows can refer to one physical path without duplicating the manifest entry.

### Library and recovery records (v8)

| Record | Purpose |
|--------|---------|
| `library_identity` | Stable logical library ID and member storage ID. |
| `media_content` | Size and SHA-256 of verified content. |
| `source_exclusions` | Device-scoped deletion preferences, including source modification time. |
| `maintenance_operations` | Prepared, recycled, restoring, restored, cancelled or purged duplicate operations. |
| `migration_quarantine` / `migration_anomalies` | Preserved invalid legacy relationships and review evidence. |

Source provenance and replica locations are represented by the device-scoped `files` records;
the portable catalog carries combined metadata and histories for offline display. Manual selections
use `#media/<digest>` when verified so moving the file does not invalidate selection.

### `devices` — devices seen by this archive

| Column | Type | Notes |
|--------|------|-------|
| `udid` | TEXT | Primary key — the device UDID. |
| `name` | TEXT | User-assigned device name (e.g. `Sample iPhone`), or `NULL`. |
| `model` | TEXT | Apple product type (e.g. `iPhone13,3`), or `NULL`. |
| `first_seen` / `last_seen` | TEXT | First and most-recent time this device was used here. |

### `runs` — the audit trail of every run

| Column | Type | Notes |
|--------|------|-------|
| `id` | INTEGER | Primary key. |
| `started_at` / `finished_at` | TEXT | UTC run start/end. |
| `command` | TEXT | The verb that ran (e.g. `copy`). |
| `copied` / `skipped` / `failed` | INTEGER | Per-run counts. |
| `exit_code` | INTEGER | Process exit code for that run. |
| `device_udid` | TEXT | UDID of the device used, or `NULL`. |

### `settings` — per-archive settings (key/value)

| Column | Type | Notes |
|--------|------|-------|
| `key` | TEXT | Primary key. Keys: `organize_scheme`, and — only while a `reorganize` is in flight — `reorganize_target`. |
| `value` | TEXT | The setting's value. |

`organize_scheme` records the archive's folder layout (`month`, `year-month`, `year`, or `flat`), chosen on
the first `copy` and honored by every later run so an archive is never silently re-shuffled.

`reorganize_target` is a transient marker present **only while a `reorganize` is underway**: it records the
layout being migrated to so an interrupted reorganize is detected and resumed, and so `copy` refuses to run
into a half-migrated archive. It is removed when the reorganize completes. It is a settings key only, so it
needs no schema-version bump.

### Schema migrations

Schema upgrades use `PRAGMA user_version` and transaction validation:

- **v1 → v2** added the `devices` and `runs` tables.
- **v2 → v3** added the `settings` table. An archive that already had files when it was upgraded is stamped
  `organize_scheme = 'year-month'` (the only layout that existed before v3), so existing nested archives keep
  resolving to their existing paths and **resume byte-stable** — the upgrade never moves or re-copies a file.
- **v4 → v7** added backup history and device restore/manual selection features.
- **v7 → v8** adds stable library/member identities, device-scoped source records, media/recovery metadata,
  relationship validation and old-writer guards. A consistent `.pre-v8` SQLite backup is kept before
  structural migration. Invalid history relationships are preserved in quarantine rather than discarded.
  `integrity_check` and `foreign_key_check` must pass before committing the new version.

See [multi-disk library and recovery](multi-disk-library.md) for operational behavior and safe recovery.

---

## Example queries

**How many files and how big is the archive?**

```sql
SELECT COUNT(*) AS files, printf('%.1f GB', SUM(size_bytes) / 1024.0 / 1024 / 1024) AS total
FROM manifest;
```

**Files per month:**

```sql
SELECT substr(exif_datetime_original, 1, 7) AS month, COUNT(*) AS files
FROM manifest
WHERE exif_datetime_original IS NOT NULL
GROUP BY month
ORDER BY month;
```

**Largest 10 files:**

```sql
SELECT dest_path, printf('%.1f MB', size_bytes / 1024.0 / 1024) AS size
FROM manifest
ORDER BY size_bytes DESC
LIMIT 10;
```

**Everything with GPS coordinates** (e.g. to build a map):

```sql
SELECT dest_path, gps_latitude, gps_longitude
FROM manifest
WHERE gps_latitude IS NOT NULL AND gps_longitude IS NOT NULL;
```

**Verify integrity** (after a `--verify-hash` run) — list files and their digests:

```sql
SELECT dest_path, sha256
FROM manifest
WHERE sha256 IS NOT NULL
ORDER BY dest_path;
```

You can re-hash a file on disk and compare to its recorded digest:

```pwsh
(Get-FileHash "D:\Photos\2024-08\IMG_4821.HEIC" -Algorithm SHA256).Hash.ToLower()
# compare to the sha256 value in the manifest for that dest_path
```

**Anything that didn't complete** (look in the full `files` table, not the `manifest` view):

```sql
SELECT source_path, state, error_message
FROM files
WHERE state <> 'done';
```

**Run history:**

```sql
SELECT started_at, command, copied, skipped, failed, exit_code
FROM runs
ORDER BY id DESC;
```

> **Privacy note:** this database contains personal metadata — capture times, **GPS locations**, device
> name, and the on-device paths of your media. It lives only in your chosen destination folder. Treat
> `get-and-see.db` (and `summary.txt`) as sensitive if you share or back up the folder.
