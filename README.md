# get-and-see

A single-file, read-only Windows CLI that copies your **entire iPhone camera roll** (photos, videos,
Live Photos, screenshots, screen recordings) to a PC over USB — built for a worst-case ~400 GB library:
resumable, atomic, verifiable, and organized by date.

It talks to the iPhone with Apple's native **AFC** protocol (the same family Finder, iTunes, and iMazing
use) — deliberately **not** Windows MTP / Explorer, which drops connections and silently produces 0-byte
files at scale.

> ## 🔒 This tool never writes to or deletes from your iPhone.
> The device is read-only **by architectural design** — there is no write, delete, rename, or move code
> path against the iPhone anywhere in the codebase, and a build-time test ([`ReadOnlyContractTests`](tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs))
> fails the build if anyone ever adds one. See [Safety](#safety).

## Quick start

```pwsh
# 1. Plug in the iPhone, unlock it, tap "Trust This Computer".
# 2. If you use the Microsoft Store "Apple Devices" app, open it once first.
# 3. Copy the whole camera roll to D:\Photos:
get-and-see copy --dest "D:\Photos"
```

That's it. Leave it running — it shows live progress, you can stop or unplug at any time, and re-running
the same command resumes exactly where it left off. Nothing is ever written to or deleted from the iPhone.

> **Using iCloud Photos with "Optimize iPhone Storage"?** Read
> [iCloud and Optimize iPhone Storage](#icloud-and-optimize-iphone-storage) first — some full-resolution
> originals may live in iCloud rather than on the device.

## What you get

- **Every file** in `/DCIM/` — copied faithfully, with all original EXIF metadata preserved.
- **Organized by date**: flat `YYYY-MM/filename.ext` by default (e.g. `2024-08/IMG_4821.HEIC`) — pick the
  layout with `--organize-by` (`month` · `year-month` · `year` · `flat`); files with no trustworthy capture
  date go to `unsorted/`.
- **A SQLite manifest** (`get-and-see.db`) at the destination root — a queryable index of everything
  copied (date, GPS, camera, size, paths). See [manifest schema](docs/user/manifest-schema.md).
- **A human-readable `summary.txt`** regenerated after every run.
- **Resume**: re-run the same command and it skips what's done and continues where it stopped.
- **Search** the archive by date, type, size, camera, or GPS with `search` — optionally `--open` the
  matches in Explorer. Read-only; no iPhone needed.

## What this tool does **NOT** do

`get-and-see` copies the iPhone's raw camera roll (the `/DCIM/` directory). It does **not** preserve
information that lives inside the Apple Photos app:

- Albums and Smart Albums
- User-added keywords or descriptions
- Favorites / hearts
- People and Face tags
- iCloud Shared Album membership
- Memories and auto-curated collections

That information lives in Apple's internal photo database, which is not accessible via the read-only
file protocol this tool uses. To export those, a different tool that accesses the Photos library
directly would be needed (different scope, different safety profile).

What you get is **complete and faithful at the raw-file level**: every photo, video, Live Photo, screen
recording, and screenshot in the camera roll, with original EXIF preserved, organized by date — plus the
manifest and summary.

## Prerequisites

1. **Windows** (x64).
2. **Apple device USB drivers** — installed by **either** iTunes for Windows **or** the **"Apple Devices"**
   app from the Microsoft Store. One of these must be present (it provides the `usbmuxd` service).
3. **iPhone connected via USB, unlocked**, with **"Trust This Computer"** tapped at least once.

> If you use the Microsoft Store **"Apple Devices"** app, **open it once** after a reboot — its background
> service is lazy and stays off until the app has been launched, which otherwise looks exactly like
> "no iPhone found". See [troubleshooting](docs/user/troubleshooting.md).

## iCloud and Optimize iPhone Storage

`get-and-see` copies the files **physically present on the iPhone**. If you use **iCloud Photos** with
**Settings → Photos → Optimize iPhone Storage** turned on, the device may keep only smaller, space-saving
versions of some photos and videos — the full-resolution originals live in iCloud, not on the phone — and
those originals are **not reachable** over the read-only USB file protocol this tool uses.

**To copy true originals:** on the iPhone, choose **Settings → Photos → Download and Keep Originals**, then
wait (on Wi-Fi, plugged in) until everything has finished downloading to the device. Only then is the
camera roll complete on-device for `get-and-see` to copy.

This is an Apple storage setting, not something the tool can work around — the originals simply aren't on
the device until you download them. If you keep **Download and Keep Originals** on, everything is always
local and this never applies.

## Install

**Option A — download the release (recommended).** Grab `get-and-see.exe` from the
[Releases](../../releases) page. It is a single self-contained file (~84 MB) — no install, no admin
rights, no .NET runtime needed. Verify the download against the published checksum:

```pwsh
(Get-FileHash .\get-and-see.exe -Algorithm SHA256).Hash
# compare to get-and-see.exe.sha256 from the release
```

**Option B — build from source.** Requires the .NET 10 SDK:

```pwsh
git clone https://github.com/denis-a-evdokimov/get-and-see.git
cd get-and-see
dotnet publish src/GetAndSee.Cli -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# output: src/GetAndSee.Cli/bin/Release/net10.0/win-x64/publish/get-and-see.exe
```

## Usage

```pwsh
# Copy everything to D:\Photos (organized into flat YYYY-MM folders by default)
get-and-see copy --dest "D:\Photos"

# Choose the folder layout (recorded per-archive on the first copy)
get-and-see copy --dest "D:\Photos" --organize-by year-month

# Plan only — enumerate and show what WOULD be copied; opens no read streams, writes nothing
get-and-see copy --dest "D:\Photos" --dry-run

# Also record a SHA-256 of every file in the manifest (slower; for the cautious)
get-and-see copy --dest "D:\Photos" --verify-hash

# Show archive totals and the last-run summary — no iPhone needed
get-and-see status --dest "D:\Photos"

# Search the archive (read-only, no iPhone needed) — e.g. by camera, and open the matches' folders
get-and-see search --dest "D:\Photos" --camera "iPhone 12" --open

# Reorganize an existing archive into a different folder layout (offline; moves files on the PC)
get-and-see reorganize --dest "D:\Photos" --organize-by year-month

get-and-see --help
```

### `copy` options

| Option | Default | Description |
|--------|---------|-------------|
| `--dest, -d` | *(required)* | Destination root folder for the date-organized archive. |
| `--organize-by <scheme>` | `month` | Folder layout, **recorded per-archive on the first copy**: `month` (flat `YYYY-MM`), `year-month` (nested `YYYY\YYYY-MM`), `year`, or `flat`. A later run keeps the recorded layout; use [`reorganize`](#reorganize) to change it. |
| `--dry-run` | off | Enumerate and plan only — no read streams opened, nothing written. |
| `--verify-hash` | off | Also compute each file's SHA-256 while it copies and record it in the manifest. Read-only; slower. |
| `--read-timeout <seconds>` | `30` | Seconds with no bytes from the device before a read is treated as a stall and the run stops cleanly (resumable). `0` disables the watchdog. |
| `--no-dashboard` | off | Disable the live dashboard; use plain per-file text output (also auto-used when output is redirected). |

A multi-hour transfer shows a **live dashboard** (illustrative, redacted):

```text
╭───────────────────────── get-and-see — copying ─────────────────────────╮
│ Overall  [######################------] 78.5%   210.4 GB / 269.0 GB   (21,402/27,478 files) │
│ Current  [###############-------------] IMG_4821.HEIC                                       │
│ Speed    28.9 MB/s (avg 30.1)   ETA 33m 12s                                                 │
│ Files    21,380 done · 18 skipped · 0 failed                                                │
╰────────────────────────────────────────────────────────────────────────╯
```

> Throughput is bounded by the iPhone's USB-2.0 Lightning link (~30 MB/s on an iPhone 12 Pro), not by
> the tool. A full ~270 GB library takes a few hours; leave it running (and disable PC sleep).

After every run, `summary.txt` at the destination looks like (illustrative, redacted):

```text
get-and-see archive at D:\Photos
Last updated: 2026-06-07 14:32:11 UTC

Total: 27,478 files (269.0 GB)
  Photos:      19,233  (HEIC: 18,400 · JPG: 833)
  Videos:      6,210  (MOV: 6,210)
  Live Photos: 1,204 pairs
  Screenshots: 1,800
  Other:       35

Date range: 2014-08-03 to 2026-06-05
Devices: Sample iPhone (iPhone13,3)
Runs: 3 (first run 2026-06-01, latest 2026-06-07)

Last run: 21,402 copied · 6,072 skipped (already done) · 0 failed · 2h 41m
```

### `search`

Find files in an existing archive **without the iPhone** — `search` reads the manifest (`get-and-see.db`)
only, never the device, and never changes anything on disk.

```pwsh
# Large 2024 videos that have GPS — and open their folders in Explorer
get-and-see search --dest "D:\Photos" --type video --from 2024-01-01 --min-size 104857600 --has-gps --open
```

| Option | Description |
|--------|-------------|
| `--dest, -d` | *(required)* Destination root of an existing archive. |
| `--from <yyyy-MM-dd>` | Only files captured on or after this date. |
| `--to <yyyy-MM-dd>` | Only files captured on or before this date (the whole day is included). |
| `--type <photo\|video\|screenshot\|other>` | Media type, derived from the file extension. |
| `--camera <substr>` | Case-insensitive substring of the camera make or model. |
| `--min-size <bytes>` / `--max-size <bytes>` | File-size bounds, in bytes. |
| `--has-gps` | Only files that carry GPS coordinates. |
| `--open` | Open the matches' containing folders in Explorer (capped at 10 folders). |

Results print as a table (path, capture date, size, type) with a count. An empty result is not an error.

### `reorganize`

Change an **existing** archive's folder layout by moving files on the PC — the sanctioned way to switch
`--organize-by` after the fact (a `copy` re-run with a different `--organize-by` keeps the recorded layout).
`reorganize` is **offline** (never touches the iPhone), **atomic** (each file is an all-or-nothing move on
the same drive), and **resumable** (interrupt it and re-run the same command to finish). It never loses or
corrupts a byte, keeps each file's name (adding `_2`/`_3` only if a coarser layout would collide two files),
leaves `unsorted\` files where they are, and removes the folders it empties.

```pwsh
# Preview the moves first, then migrate D:\Photos to nested YYYY\YYYY-MM folders
get-and-see reorganize --dest "D:\Photos" --organize-by year-month --dry-run
get-and-see reorganize --dest "D:\Photos" --organize-by year-month
```

| Option | Description |
|--------|-------------|
| `--dest, -d` | *(required)* Destination root of an existing archive. |
| `--organize-by <month\|year-month\|year\|flat>` | *(required)* The target layout to migrate to. |
| `--dry-run` | Show the planned moves and write nothing (no move, no journal change). |

While a reorganize is unfinished (e.g. it was interrupted), `copy` refuses to run and tells you to finish the
reorganize first, so new files are never copied into a half-migrated tree.

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | Success — all files copied (and verified) or already done. |
| `1` | Completed, but some files failed — re-run to retry. |
| `2` | Pre-flight/device error (no device, not trusted, driver service off, disk full). |
| `3` | Device stalled (asleep/disconnected) — progress saved; reconnect and re-run to resume. |
| `130` | Interrupted (Ctrl+C) — progress saved; re-run to resume. |

## Stopping, unplugging and resuming

You can stop a run at any time — press **Ctrl+C**, unplug the cable, or let the iPhone sleep. Because every
file is journaled and only published after it is fully written and size-checked, an interrupted run **never
loses or corrupts data**: re-run the **same `copy` command** and it skips everything already done and
continues from the exact file it stopped on.

If the cable is pulled mid-copy, the tool notices the device is gone (within `--read-timeout`, default
30s), prints `Device disconnected. Progress saved — reconnect and re-run to resume.`, and exits cleanly
with code **3**. Your data on the PC is always safe and your iPhone is never touched, so it is perfectly
fine to just pull the cable when you need to.

## How it works (integrity)

Each file is streamed to a staging `.partial`, flushed to disk (`fsync`), **size-verified against the
AFC-reported size**, and only then **atomically renamed** into place — so a yanked cable can never leave
a half-written final file. Every file's state is tracked in the SQLite journal, making the whole transfer
resumable. Filename collisions across DCIM subfolders are disambiguated (`_2`, `_3`, …) — never
overwritten. With `--verify-hash`, a SHA-256 is computed in the same streaming pass and stored in the
manifest. Long destination paths (beyond the legacy 260-character limit) are handled transparently.

## Safety

- The iPhone is **read-only by design** — the AFC client binds only read operations (`ListDirectory`,
  `GetFileInfo`, `OpenRead`); no write/delete/rename/mkdir symbol exists in the code.
- A build-failing test ([`ReadOnlyContractTests`](tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs))
  scans the compiled assembly and fails the build if any device-mutating symbol is ever referenced.
- There is **no** `--delete-after-copy`, `--move`, or `--cleanup`. Adding one is a design conversation,
  not a code change.
- **No network, no telemetry, no cloud.** Local USB only.
- Your photos and the manifest are written **only** to the destination folder you choose.

## FAQ

**Does this modify or delete anything on my iPhone?**
No — never. The iPhone is read-only by design, and a build-time test fails the build if any write/delete
code is ever added. It only reads `/DCIM/` and writes to the destination folder on your PC. See [Safety](#safety).

**Can I unplug the cable or stop mid-copy?**
Yes. Stop with Ctrl+C or just unplug — nothing is lost or corrupted, and re-running the same command resumes
exactly where it left off. See [Stopping, unplugging and resuming](#stopping-unplugging-and-resuming).

**Some originals are missing or look low-resolution.**
You most likely have iCloud **Optimize iPhone Storage** on, so the originals are in iCloud, not on the
device. See [iCloud and Optimize iPhone Storage](#icloud-and-optimize-iphone-storage).

**Where are my albums, favorites, and face tags?**
Those live in the Apple Photos database, not in the camera-roll files, so they aren't copied. See
[What this tool does **NOT** do](#what-this-tool-does-not-do).

**Will it copy the same photo twice if I run it again?**
No. The journal at the destination tracks every copied file, so re-runs skip what's already done. Re-run as
often as you like to pick up newly taken photos.

**How long does it take?**
Throughput is limited by the iPhone's USB link (~30 MB/s on an iPhone 12 Pro), not the tool — a full
~270 GB library takes a few hours. Disable PC sleep and leave it running; you can resume if interrupted.

**Can I copy to an external or network drive?**
Yes, but prefer a local disk for a large run. Each destination keeps its own journal and resumes
independently, so you can even split a library across several drives.

**Is there any network access or telemetry?**
None. It is local USB only — no cloud, no analytics.

## Troubleshooting

See **[docs/user/troubleshooting.md](docs/user/troubleshooting.md)** for the common failure modes
("no iPhone detected", "Trust This Computer", the Apple Devices service, disk-full, stalls, long paths)
with the exact messages the tool prints and how to fix each.

## Documentation

- [Troubleshooting](docs/user/troubleshooting.md)
- [Manifest / database schema](docs/user/manifest-schema.md) — query your archive with any `sqlite3` tool.
- [Release notes](docs/release-notes/) · [release-notes template](docs/release-notes-template.md)

## License

[MIT](LICENSE).
