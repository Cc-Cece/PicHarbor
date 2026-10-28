<div align="center">

<img src="src/PicHarbor.Gui/Assets/PicHarbor.png" alt="PicHarbor Logo" width="96" />

# PicHarbor

Local-first photo management, backup, and synchronization application for Windows.

[![CI](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-0078D4?logo=windows&logoColor=white)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Release](https://img.shields.io/github/v/release/Cc-Cece/PicHarbor?color=007AFF&logo=github)](https://github.com/Cc-Cece/PicHarbor/releases)
[![License: GPL--3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)

<p align="center">
  <b>English</b> | <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/Cc-Cece/PicHarbor">GitHub Repository</a> · <a href="https://github.com/Cc-Cece/PicHarbor/releases/latest">Download Releases</a> · <a href="https://picharbor.akihito.dpdns.org/">Website</a>
</p>

<p align="center">
  Related: <a href="https://github.com/Cc-Cece/PicHarbor-iOS">iOS</a> · <a href="https://github.com/Cc-Cece/PicHarbor-Android">Android</a>
</p>

</div>

---

PicHarbor is a local-first photo management and backup tool for Windows. It archives iPhone media over USB, backs up Android media over FTP, and provides a local gallery with a searchable SQLite index. It supports managing collections across multiple disks, exporting media for other devices, and uploading selected photos to Google Photos.

## Features

| Capability | Description |
| --- | --- |
| **iPhone USB Backup** | Reads media from `/DCIM/` via native Apple File Conduit (AFC), with date/folder selection and resumable transfers. |
| **Android FTP Backup** | Scans and copies media folders over FTP, recording device identity and incremental history. |
| **Local Gallery & Search** | Table and gallery views, image preview, video playback, and metadata filters (date, camera, GPS, file size). |
| **Associated File Pairs** | Recognizes Live Photo pairs, `.AAE` edit sidecars, and RAW/JPEG pairs. |
| **Multi-Disk Library** | Groups multiple archive disks into one logical library, with offline member tracking, integrity checks, and duplicate recovery. |
| **Directory Layouts** | Supports `month`, `year-month`, `year`, or `flat` layouts, plus in-place reorganization of existing archives. |
| **Export & Sync** | Exports iPhone sync folders, uploads to Android via FTP, and uploads to Google Photos via bundled `gpmc`. |
| **Desktop & CLI** | WPF desktop application and standalone command-line tool sharing the same archive format. |

## Quick Start

### 1. Download

Download portable executables from [Releases](https://github.com/Cc-Cece/PicHarbor/releases/latest):
- **`PicHarbor.Gui.exe`**: Desktop GUI application (standalone, self-contained).
- **`picharbor.exe`**: Command-line tool.

Verify checksums using the `.sha256` files in the release assets:

```powershell
Get-FileHash .\PicHarbor.Gui.exe -Algorithm SHA256
```

### 2. Requirements

| Component | Requirements |
| --- | --- |
| **Desktop App** | Windows 10 (version 2004 / build 19041+) or Windows 11 (x64). Requires Microsoft Edge WebView2 Runtime for gallery/sign-in views. |
| **iPhone Backup** | Apple USB drivers (via iTunes for Windows or the Apple Devices app). Connect via USB, unlock device, and tap **Trust This Computer**. |
| **Android Backup** | Running FTP server on the device, network connectivity, and configured server credentials in PicHarbor. |
| **iPhone Restore** | Sync exported folders to iPhone Photos via Apple Devices or iTunes (AFC does not support direct import). |
| **Google Photos** | Python interpreter and Google credentials configured in the Google Photos view. |
| **Build from Source** | Windows with .NET 10 SDK. |

> [!TIP]
> If an iPhone is not detected after restarting the computer, launch the Apple Devices app once to initialize background USB services, then reconnect the unlocked device.

### 3. First Backup

1. Select a destination directory on a drive with sufficient free space (e.g., `D:\Photos`).
2. Open the iPhone or Android backup view and connect the device.
3. Choose the backup range, folder layout, and verification mode, then start the backup.
4. If interrupted, reconnecting and rerunning the backup automatically skips completed files and retries unfinished ones.

> [!NOTE]
> iPhone USB backup only reads from the camera roll; it does not modify or delete media on the device.

## Archive Storage & Verification

Media files are stored directly on disk without proprietary container formats. Each archive maintains its index and metadata under `.picharbor/` (database at `.picharbor/picharbor.db`).

### Folder Layouts

| Layout | Example Relative Path |
| --- | --- |
| `month` (default) | `2026-10/` |
| `year-month` | `2026/2026-10/` |
| `year` | `2026/` |
| `flat` | Root of archive |

To change layout on an existing archive, use the **Reorganize** tool rather than changing the copy layout option on subsequent runs.

### Verification Modes

| Mode | Behavior |
| --- | --- |
| **Fast** | Compares database records, file existence, and file size without reading content. |
| **Normal** (default) | Verifies file content on the computer before skipping. |
| **Strict** | Re-reads source device content over USB/FTP before skipping. |

### Multi-Disk Library & Duplicate Recovery

- Multiple archive directories can be registered as members of a single logical library for unified browsing and search.
- Verified duplicate copies can be moved to `.picharbor/recycle` (30-day default retention) and restored from Settings or CLI.

### App Lock & Credential Protection

- Optional App Lock protects registered archive directories using Windows permissions and a local service (does not encrypt raw media files).
- Account credentials (FTP, Google) are encrypted using Windows DPAPI under the current user account.

## Build from Source

Requires Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
git clone https://github.com/Cc-Cece/PicHarbor.git
cd PicHarbor
dotnet restore PicHarbor.sln
dotnet run --project src/PicHarbor.Gui -c Release
```

Run CLI from source:

```powershell
dotnet run --project src/PicHarbor.Cli -c Release -- --help
```

Publish standalone self-contained executables:

```powershell
dotnet publish src/PicHarbor.Gui -c Release -r win-x64 --self-contained true -o artifacts/gui
dotnet publish src/PicHarbor.Cli -c Release -r win-x64 --self-contained true -o artifacts/cli
```

Outputs are generated at `artifacts/gui/PicHarbor.Gui.exe` and `artifacts/cli/picharbor.exe`.

### Repository Structure

| Directory | Purpose |
| --- | --- |
| `src/PicHarbor.Gui` | WPF desktop application, gallery views, settings, and update checker. |
| `src/PicHarbor.Cli` | Command parser and terminal interface. |
| `src/PicHarbor.Core` | Device communication, transfers, SQLite indexing, metadata, and sync logic. |
| `src/PicHarbor.Vault` | App Lock service and Windows permission management. |
| `src/PicHarbor.FakeDevice` | Mock device harness for automated testing. |
| `tests/` | Unit tests, safety contract checks, and Vault tests. |

Run tests:

```powershell
dotnet test PicHarbor.sln -c Release --filter "Category!=E2E"
```

## Acknowledgements

PicHarbor builds upon work from [get-and-see](https://github.com/denis-a-evdokimov/get-and-see), [gpmc](https://github.com/xob0t/gpmc), [PCL](https://github.com/Meloong-Git/PCL), and [sweet-album](https://github.com/leuvi/sweet-album).

The author opposes using this project for marketing or unauthorized promotion. Please do not falsely claim official endorsement.
