# GetAndSee

> This project is primarily based on [get-and-see](https://github.com/denis-a-evdokimov/get-and-see) with additional features.

[English](README.md) | [简体中文](README.zh-CN.md)

A local-first **photo management and backup tool** for Windows. It provides reliable, lossless media archiving from mobile devices via USB, offline indexing and search, and multi-destination synchronization across iPhone, Android, and Google Photos.

---

## Key Features

### 📥 Lossless iPhone USB Archiving (AFC Protocol)
- **Native AFC Connection**: Communicates directly with iOS devices over Apple File Conduit (AFC) via USB, avoiding connection drops and 0-byte corrupted files commonly seen with Windows MTP / Explorer.
- **Strictly Read-Only Guarantee**: Enforced by architectural design and compile-time contract tests (`ReadOnlyContractTests`). The tool only reads the iPhone camera roll (`/DCIM/`) and does not write, modify, or delete files on the device.
- **Atomic & Resumable**: Employs SQLite journaling and atomic writes (`.partial` → size verification → atomic rename). Interrupted transfers (unplugging, sleep, Ctrl+C) resume cleanly without duplicate writes or corrupt files.
- **Flexible Scope**: Back up the entire camera roll, a specific capture date range, or selected DCIM folders.

### 🔄 Multi-Destination Restore & Sync
- **Restore to iPhone**: Prepares structured sync folders (flat or `YYYY-MM` albums) for synchronization via Apple Devices or iTunes into iOS Photos, keeping Live Photo pairs intact.
- **Restore to Android**: Directly uploads media to Android / Pixel devices over FTP, with support for full copy or historical incremental sync (tracking transferred files via SQLite).
- **Google Photos Sync**: Integrates cloud backup via `gpmc`. Supports automatic OAuth token capture or Android GmsCore credentials, Pixel original quality (unlimited quota) or Storage Saver, custom/auto albums, multi-threaded uploads, and automatic retry with jitter backoff.

### 🔍 Offline Media Indexing & Gallery
- **Offline Search**: Query media using the local SQLite manifest (`get-and-see.db`) without connecting devices. Filter by capture date, camera make/model, media type, GPS coordinates, and file size.
- **Visual Browser**: Browse archives in table or gallery view with built-in lightbox preview and direct video playback.
- **Smart Pairing**: Automatically pairs and manages Live Photos (`.HEIC`/`.JPG` + `.MOV`), `.AAE` sidecar edits, and RAW pairs (`.DNG` + `.JPG`).

### 📁 Archive Management & Integrity
- **Configurable Layouts**: Automatically organizes files into date structures: `month` (`YYYY-MM`), `year-month` (`YYYY/YYYY-MM`), `year`, or `flat`.
- **Lossless Reorganization**: In-place directory reorganization tool (`reorganize`) to switch folder layouts on the PC without re-copying.
- **Metadata Integrity**: Preserves original EXIF and metadata. Optional SHA-256 hash calculation and verification. Provides one-click synchronization of EXIF capture timestamps to Windows file system dates.
- **Reports**: Generates structured SQLite database manifests and human-readable `summary.txt` logs after each backup run.

---

## Interfaces

GetAndSee provides two interfaces sharing the same SQLite manifest and destination folder:

1. **Desktop GUI (`GetAndSee.Gui`)**: Full-featured graphical interface with tabs for Backup to PC, Restore to iPhone, Restore to Android, Google Photos, Archive Status, Media Search, Layout Reorganize, and Settings.
2. **Command Line CLI (`get-and-see.exe`)**: Lightweight CLI designed for scriptable calls and automated tasks, featuring a real-time interactive terminal dashboard (primarily [get-and-see](https://github.com/denis-a-evdokimov/get-and-see)).

---

## Prerequisites

- **OS**: Windows 10 (version 2004 / build 19041 or newer, x64) or Windows 11.
- **Apple Drivers**: Apple USB driver installed via **iTunes for Windows** or the **Apple Devices** app from Microsoft Store (provides `usbmuxd`).
  
  > *Tip: If using Apple Devices from the Microsoft Store, launch it once after each PC reboot so its background service is running.*
- **Device Setup**: Unlock the iPhone and tap **"Trust This Computer"**.
- **For Build**: [.NET 10 SDK](https://dotnet.microsoft.com/download).

---

## Quick Start

### Desktop Application

Run from source:
```pwsh
dotnet run --project src/GetAndSee.Gui -c Release
```
Or build a self-contained portable executable:
```pwsh
dotnet publish src/GetAndSee.Gui -c Release -r win-x64 --self-contained true
```

### Command Line (CLI)

```pwsh
# Incremental backup to target directory
get-and-see copy --dest "D:\Photos"

# Backup with specified folder structure (month, year-month, year, flat)
get-and-see copy --dest "D:\Photos" --organize-by year-month

# Dry-run preview (scans files without copying)
get-and-see copy --dest "D:\Photos" --dry-run

# Search archived media (without device connected)
get-and-see search --dest "D:\Photos" --type video --from 2024-01-01 --has-gps

# Reorganize folder layout of existing archive
get-and-see reorganize --dest "D:\Photos" --organize-by year-month

# View archive summary and stats
get-and-see status --dest "D:\Photos"
```

---

## CLI Options

### `copy`
| Option | Default | Description |
|---|---|---|
| `--dest, -d` | *(Required)* | Destination directory for the archive. |
| `--organize-by` | `month` | Organization layout: `month` (`YYYY-MM`), `year-month`, `year`, or `flat`. |
| `--dry-run` | off | Enumerate files and plan transfer without copying. |
| `--verify-hash` | off | Calculate and store SHA-256 hashes during transfer. |
| `--read-timeout` | `30` | Seconds without data before timing out (auto-resumable). |
| `--no-dashboard` | off | Plain text output instead of the interactive terminal dashboard. |

### `search`
| Option | Description |
|---|---|
| `--dest, -d` | *(Required)* Target archive directory. |
| `--from <YYYY-MM-DD>` | Filter by capture date on or after this date. |
| `--to <YYYY-MM-DD>` | Filter by capture date on or before this date. |
| `--type <photo\|video\|screenshot\|other>` | Filter by media type. |
| `--camera <string>` | Filter by camera model or brand substring. |
| `--has-gps` | Only include items with GPS coordinates. |
| `--open` | Open matched items' folders in Windows Explorer. |

---

## Documentation

- [Manifest Schema](docs/user/manifest-schema.md)
- [Troubleshooting Guide](docs/user/troubleshooting.md)

---

## Acknowledgements

This project makes use of ideas and components from the following open-source projects:

- [get-and-see](https://github.com/denis-a-evdokimov/get-and-see) (MIT)
- [gpmc](https://github.com/xob0t/gpmc) (MIT)

---

## License

This project is licensed under the [GNU General Public License v3.0 (GNU GPLv3)](https://www.gnu.org/licenses/gpl-3.0.html).
