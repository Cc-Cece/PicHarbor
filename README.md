<div align="center">

<img src="src/PicHarbor.Gui/Assets/PicHarbor.png" alt="PicHarbor logo" width="96" />

# PicHarbor

**Keep your photos local. Back them up, browse them, and take them with you.**

[![CI](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml)
![Platform](https://img.shields.io/badge/platform-Windows_x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

[Download](https://github.com/Cc-Cece/PicHarbor/releases/latest) · [Website](https://picharbor.akihito.dpdns.org/) · [简体中文](README.zh-CN.md)

[Features](#features) · [Get started](#get-started) · [Command line](#command-line) · [Build from source](#build-from-source) · [Documentation](#documentation)

</div>

PicHarbor is a local-first photo management and backup app for Windows. Archive iPhone media over USB, back up Android media over FTP, and browse your collection through a local gallery and searchable SQLite index. Keep archives across multiple disks, prepare photos for another device, or upload selected media to Google Photos.

This project is primarily based on [get-and-see](https://github.com/denis-a-evdokimov/get-and-see), with additional desktop, library management, and synchronization features.

## Features

| What you want to do | What PicHarbor provides |
| --- | --- |
| Back up an iPhone | Read media from `/DCIM/` through native Apple File Conduit (AFC), with date/folder selection in the GUI and resumable backup runs. |
| Back up an Android device | Scan and copy selected media folders through an FTP connection, with device identity and incremental backup history. |
| Browse without a phone | Table and gallery views, image previews, video playback, and filters for capture date, media type, camera, GPS availability, and file size. |
| Keep related files together | Recognition of Live Photo components, `.AAE` edit sidecars, and RAW/JPEG pairs. |
| Manage a growing collection | Multiple archive disks in one logical library, offline member tracking, integrity checks, and recoverable duplicate copies. |
| Organize files on disk | Month, nested year/month, year, or flat layouts, plus in-place reorganization of existing archives. |
| Copy media to another destination | iPhone sync-folder export, Android FTP uploads, and Google Photos uploads through the bundled `gpmc` integration. |
| Control the desktop experience | Light/dark themes, English and Chinese interfaces, optional app lock, and desktop updates from GitHub Releases. |

The GUI covers backup, browsing, synchronization, and settings. The CLI covers iPhone backup, archive search/status, reorganization, and library maintenance; both use the same archive metadata.

## Get started

### 1. Download the desktop app

Open the [latest release](https://github.com/Cc-Cece/PicHarbor/releases/latest) and download **`PicHarbor.Gui.exe`**. Save it in a writable folder and launch it. Release executables are portable and self-contained; a separate .NET installation is not required.

For terminal use, download **`picharbor.exe`** instead. Each executable has a matching `.sha256` file in the release assets. Compare the published checksum with:

```powershell
Get-FileHash .\PicHarbor.Gui.exe -Algorithm SHA256
```

### 2. Prepare the features you need

| Feature | Requirements |
| --- | --- |
| Desktop app | Windows 10 version 2004 / build 19041 or newer, or Windows 11; x64. The gallery and embedded sign-in use Microsoft Edge WebView2 Runtime. Media playback depends on installed Windows codecs. |
| iPhone USB backup | Apple USB drivers from Apple Devices or iTunes for Windows. Connect with a USB cable, unlock the iPhone, and accept **Trust This Computer**. |
| Android backup or restore | An FTP server running on the phone, a reachable connection, and the server address/credentials configured in PicHarbor. Use the device pairing flow to bind its identity. |
| iPhone restore | Apple Devices or iTunes to sync the folder exported by PicHarbor into Photos. PicHarbor does not import it directly over AFC. |
| Google Photos | A working Python interpreter and Google credentials configured in the Google Photos view. The app bundles the `gpmc` bridge/package; an external `gpmc` directory can also be configured. |
| Build from source | .NET 10 SDK on Windows. |

> [!TIP]
> If your iPhone is not detected after a PC reboot, open Apple Devices once to start its background service, then reconnect the unlocked phone.

### 3. Make your first backup

1. Choose a destination folder on a disk with enough free space, such as `D:\Photos`.
2. Select the iPhone or Android backup view and connect the device. For Android, test the FTP connection and select the media folders to include.
3. Choose the backup scope and folder layout, then start the backup. **Normal** verification is the default; see [verification modes](#verification-modes).
4. Browse the archive or search it after the run. If a transfer is interrupted, reconnect the device and run the backup again to retry unfinished files and skip completed ones.

> [!NOTE]
> iPhone USB backup reads the camera roll; it does not write, modify, or delete media on the iPhone. Android pairing and restore/upload operations have their own write behavior. This is a media archive, not a full device or iCloud backup.

## How your archive works

Photos remain ordinary files on disk. Each archive keeps its journal and index under `.picharbor/`; the current database is `.picharbor/picharbor.db`, with support for legacy root-level databases. Keep the **whole archive folder**, including `.picharbor`, together when moving it, then open the new location in PicHarbor.

| Layout | Example relative folder |
| --- | --- |
| `month` (default) | `2026-10/` |
| `year-month` | `2026/2026-10/` |
| `year` | `2026/` |
| `flat` | Archive root |

An existing archive retains its recorded layout. Use **reorganize** to change it rather than changing the layout option on a later copy run.

### Verification modes

New copies record SHA-256 while streaming, check the transferred size, and flush the staged file before publishing it to its final path. An interrupted staging file is retried on the next run; individual files do not resume from a byte offset.

For existing backups, choose a mode in **Settings → advanced backup settings**:

| Mode | Check before skipping a completed backup |
| --- | --- |
| Fast | Use backup records, file existence, and size without reading file content. Cannot detect same-size content changes. |
| Normal (default) | Verify the backup content on the computer. |
| Strict | Also reread the source device content before skipping. Slower, especially over USB. |

The CLI uses normal verification by default and offers `--strict-verify`. The legacy `--verify-hash` option remains accepted for compatibility; new copies already record hashes.

### Multiple disks and duplicate recovery

Register archive folders as members of one library to browse and search them together. Offline members retain their indexed records, but unavailable copies are not treated as proof that a new backup can be skipped.

Library self-checks can run at startup, after backup, and when a member reconnects. They operate on registered media and can move verified duplicate copies into the member's `.picharbor/recycle` area. The default retention is 30 days; expired copies are removed only after the surviving copy is verified again. Recoverable copies can be restored from Settings or the CLI.

See the [multi-disk library guide (Chinese)](docs/user/multi-disk-library.md) for member relocation, deletion/rebackup preferences, recovery, and writer coordination. Finish tasks on one PC before moving member disks to another; simultaneous disconnected writes from multiple PCs are unsupported.

### App lock and synchronization

The optional app lock protects registered folders through Windows permissions and a local service. It **does not encrypt original photos** or protect against administrators or offline disk access. FTP and Google credentials are stored using the current Windows user's DPAPI protection.

Android transfers use FTP, which does not add transport encryption. Google Photos uploads require network access and account authentication; local backup, indexing, and browsing do not require a Google account.

## Command line

Run these PowerShell examples from the folder containing the downloaded `picharbor.exe`:

```powershell
# Preview an iPhone backup without copying files
.\picharbor.exe copy --dest "D:\Photos" --dry-run

# Start an incremental backup; choose the layout when creating the archive
.\picharbor.exe copy --dest "D:\Photos" --organize-by year-month

# Verify source content before skipping an existing backup
.\picharbor.exe copy --dest "D:\Photos" --strict-verify

# Search the archive without connecting the phone
.\picharbor.exe search --dest "D:\Photos" --type video --from 2026-01-01 --has-gps

# Show archive status
.\picharbor.exe status --dest "D:\Photos"

# Preview a layout change; remove --dry-run to apply it
.\picharbor.exe reorganize --dest "D:\Photos" --organize-by month --dry-run

# List recoverable duplicate copies
.\picharbor.exe library recycled --dest "D:\Photos"
```

Use `--help` on any command for its full options:

```powershell
.\picharbor.exe --help
.\picharbor.exe copy --help
.\picharbor.exe search --help
.\picharbor.exe library --help
```

For scripts, `copy --no-dashboard` enables plain text output. The default read-stall timeout is 30 seconds; `--read-timeout 60` allows more time for a slow device.

## Build from source

Clone this repository, then run these commands on Windows with the .NET 10 SDK installed:

```powershell
git clone https://github.com/Cc-Cece/PicHarbor.git
cd PicHarbor
dotnet restore PicHarbor.sln
dotnet run --project src/PicHarbor.Gui -c Release
```

To run the CLI from source:

```powershell
dotnet run --project src/PicHarbor.Cli -c Release -- --help
```

To publish the same self-contained Windows x64 executable format used by releases:

```powershell
dotnet publish src/PicHarbor.Gui -c Release -r win-x64 --self-contained true -o artifacts/gui
dotnet publish src/PicHarbor.Cli -c Release -r win-x64 --self-contained true -o artifacts/cli
```

The outputs are `artifacts/gui/PicHarbor.Gui.exe` and `artifacts/cli/picharbor.exe`. Apple drivers, WebView2, and feature-specific dependencies still apply.

### Repository map and checks

| Directory | Purpose |
| --- | --- |
| `src/PicHarbor.Gui` | WPF desktop app, gallery, settings, and updates. |
| `src/PicHarbor.Cli` | Command parser and terminal interface. |
| `src/PicHarbor.Core` | Device access, transfers, SQLite journal, metadata, library management, and sync. |
| `src/PicHarbor.Vault` | App-lock service and Windows permission protection. |
| `src/PicHarbor.FakeDevice` | Opt-in fake-device harness for testing. |
| `tests/` | Core, safety-contract, Vault, and supporting JavaScript/Python tests. |
| `docs/` | User guides and engineering records. |
| `website/` | Static project website. |

For the .NET build and tests, including compiled-code checks for prohibited iPhone device mutation calls:

```powershell
dotnet build PicHarbor.sln -c Release
dotnet test PicHarbor.sln -c Release --no-build --filter "Category!=E2E"
```

The separate executable E2E lane and published-executable smoke checks are defined in [CI](.github/workflows/ci.yml).

## Acknowledgements

PicHarbor builds on work from [get-and-see](https://github.com/denis-a-evdokimov/get-and-see), [gpmc](https://github.com/xob0t/gpmc), [PCL](https://github.com/Meloong-Git/PCL), and [sweet-album](https://github.com/leuvi/sweet-album).

The author opposes using this project to drive traffic or for marketing. Please do not impersonate the official project or falsely claim the author's endorsement.
