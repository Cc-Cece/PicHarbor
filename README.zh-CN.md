<div align="center">
<img src="src/PicHarbor.Gui/Assets/PicHarbor.png" alt="PicHarbor 图标" width="96" />

# PicHarbor

**把照片留在本地，安心备份，随时浏览，随身携带。**

[![CI](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml)
![平台](https://img.shields.io/badge/platform-Windows_x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

[下载](https://github.com/Cc-Cece/PicHarbor/releases/latest) · [项目主页](https://picharbor.akihito.dpdns.org/) · [English](README.md)

[功能速览](#功能速览) · [快速上手](#快速上手) · [命令行](#命令行) · [从源码构建](#从源码构建) · [文档](#文档)

</div>

PicHarbor 是一款面向 Windows、以本地存储为核心的照片管理与备份应用。通过 USB 归档 iPhone 媒体，通过 FTP 备份 Android 媒体，使用本地画廊和可检索的 SQLite 索引浏览照片。支持跨磁盘管理归档、为其他设备准备照片，以及将选定媒体上传至 Google 相册。

本项目主要基于 [get-and-see](https://github.com/denis-a-evdokimov/get-and-see)，增加了桌面界面、图库管理和同步功能。

## 功能速览

| 你想做什么 | PicHarbor 提供的功能 |
| --- | --- |
| 备份 iPhone | 通过原生 Apple File Conduit（AFC）读取 `/DCIM/` 媒体；图形界面支持按日期或文件夹选择范围，中断后可再次运行继续备份。 |
| 备份 Android 设备 | 通过 FTP 扫描并复制选定的媒体文件夹，记录设备身份和增量备份历史。 |
| 不连接手机也能浏览 | 表格与画廊视图、图片预览、视频播放，以及拍摄日期、媒体类型、相机、是否含 GPS 和文件大小等筛选条件。 |
| 保持关联文件的配对 | 识别实况照片（Live Photo）组件、`.AAE` 编辑附属文件和 RAW/JPEG 配对。 |
| 管理不断增长的照片库 | 将多个归档磁盘纳入同一逻辑图库，跟踪离线成员，检查完整性，并支持恢复被回收的重复副本。 |
| 整理磁盘上的文件 | 支持按月、年月嵌套、按年或平铺布局，以及对已有归档原地重整目录结构。 |
| 将媒体复制到其他位置 | 导出 iPhone 同步目录、通过 FTP 上传至 Android，以及通过内置 `gpmc` 集成上传至 Google 相册。 |
| 调整桌面使用体验 | 浅色/深色主题、中英文界面、可选应用锁，以及通过 GitHub Releases 更新桌面端。 |

图形界面涵盖备份、浏览、同步和设置。命令行涵盖 iPhone 备份、归档检索与状态查询、目录重整和图库维护；两者共享同一套归档元数据。

## 快速上手

### 1. 下载桌面应用

打开[最新发布版本](https://github.com/Cc-Cece/PicHarbor/releases/latest)，下载 **`PicHarbor.Gui.exe`**，将其保存在可写文件夹中并启动。发布的可执行文件为便携式独立运行版，无需另行安装 .NET。

如需使用终端，下载 **`picharbor.exe`**。每个可执行文件在发布附件中都有对应的 `.sha256` 文件。可用以下命令计算哈希，与发布的校验值比较：

```powershell
Get-FileHash .\PicHarbor.Gui.exe -Algorithm SHA256
```

### 2. 按需准备运行环境

| 功能 | 要求 |
| --- | --- |
| 桌面应用 | Windows 10 版本 2004 / 内部版本 19041 或更新版本，或 Windows 11；x64。画廊和内嵌登录使用 Microsoft Edge WebView2 Runtime。媒体播放依赖已安装的 Windows 编解码器。 |
| iPhone USB 备份 | 通过 Apple Devices 或 iTunes for Windows 安装 Apple USB 驱动。用 USB 线连接并解锁 iPhone，确认**“信任此电脑”**。 |
| Android 备份或恢复 | 手机运行 FTP 服务器，连接可达，并在 PicHarbor 中配置服务器地址和凭据。通过设备配对流程绑定设备身份。 |
| iPhone 恢复 | 使用 Apple Devices 或 iTunes，将 PicHarbor 导出的目录同步到“照片”。PicHarbor 不通过 AFC 直接导入。 |
| Google 相册 | 在 Google 相册页面配置可用的 Python 解释器和 Google 凭据。应用内置 `gpmc` 桥接脚本及包，也可配置外部 `gpmc` 目录。 |
| 从源码构建 | Windows 环境及 .NET 10 SDK。 |

> [!TIP]
> 如果电脑重启后无法识别 iPhone，先打开一次 Apple Devices 以启动后台服务，再重新连接已解锁的手机。

### 3. 完成首次备份

1. 在空间充足的磁盘上选择目标文件夹，例如 `D:\Photos`。
2. 选择 iPhone 或 Android 备份页面并连接设备。Android 设备需先测试 FTP 连接，再选择要包含的媒体文件夹。
3. 选择备份范围和目录布局，然后开始备份。默认使用**普通模式**，详见[校验模式](#校验模式)。
4. 备份完成后浏览或检索归档。如果传输中断，重新连接设备并再次运行备份，即可重试未完成文件并跳过已完成项。

> [!NOTE]
> iPhone USB 备份只读取相机胶卷，不会写入、修改或删除 iPhone 上的媒体。Android 配对以及恢复、上传操作各有相应的写入行为。本项目提供媒体归档，不是整机或 iCloud 备份工具。

## 归档如何保存

照片仍以普通文件保存在磁盘上。每个归档的日志和索引位于 `.picharbor/`，当前数据库路径为 `.picharbor/picharbor.db`，同时兼容旧版根目录数据库。移动归档时，请保留**整个归档文件夹**（包括 `.picharbor`），然后在 PicHarbor 中打开新位置。

| 布局 | 相对目录示例 |
| --- | --- |
| `month`（默认） | `2026-10/` |
| `year-month` | `2026/2026-10/` |
| `year` | `2026/` |
| `flat` | 归档根目录 |

已有归档会沿用记录的布局。更改布局请使用 **reorganize**，而不是在后续复制时修改布局选项。

### 校验模式

新复制的文件会在流式传输过程中记录 SHA-256，核对传输大小，并在发布到最终路径前将暂存文件刷新落盘。中断的暂存文件会在下次运行时重新传输；单个文件不支持从中断的字节位置续传。

对于已有备份，可在**设置 → 备份高级设置**中选择模式：

| 模式 | 跳过已完成备份前的检查 |
| --- | --- |
| 极速 | 根据备份记录、文件是否存在及大小判断，不读取文件内容。无法检测大小相同的内容变化。 |
| 普通（默认） | 校验电脑上的备份内容。 |
| 严格 | 跳过前还会重新读取源设备的内容。速度较慢，尤其是 USB 连接。 |

命令行默认使用普通校验，并提供 `--strict-verify`。旧参数 `--verify-hash` 仍保留以兼容已有用法；所有新复制本来就会记录哈希。

### 多磁盘图库与重复副本恢复

将归档文件夹登记为同一图库的成员，即可统一浏览和检索。成员离线后仍保留索引记录，但不可用的副本不能作为跳过新备份的依据。

图库自检可在应用启动、备份完成和成员重连时运行。自检仅处理已登记的媒体，可能将已验证的重复副本移入成员的 `.picharbor/recycle` 区域。默认保留 30 天；到期后，只有再次验证保留副本有效，才会删除回收副本。可在设置或命令行中恢复仍可恢复的副本。

成员迁移、删除后的重新备份偏好、恢复和写入协调详见[多磁盘图库指南（中文）](docs/user/multi-disk-library.md)。将成员磁盘转移到另一台电脑前，应先结束原电脑上的任务；不支持多台电脑各持离线成员盘同时写入同一图库。

### 应用锁与同步

可选应用锁通过 Windows 权限和本地服务保护已登记文件夹。它**不会加密照片原文件**，也不防范管理员或离线硬盘访问。FTP 和 Google 凭据使用当前 Windows 用户的 DPAPI 保护保存。

Android 传输使用 FTP，不提供额外的传输加密。上传至 Google 相册需要联网和账号认证；本地备份、索引和浏览无需 Google 账号。

## 命令行

在下载的 `picharbor.exe` 所在文件夹中运行以下 PowerShell 示例：

```powershell
# 预览 iPhone 备份，不复制文件
.\picharbor.exe copy --dest "D:\Photos" --dry-run

# 开始增量备份；创建归档时选择布局
.\picharbor.exe copy --dest "D:\Photos" --organize-by year-month

# 跳过已有备份前校验源设备内容
.\picharbor.exe copy --dest "D:\Photos" --strict-verify

# 无需连接手机即可检索归档
.\picharbor.exe search --dest "D:\Photos" --type video --from 2026-01-01 --has-gps

# 查看归档状态
.\picharbor.exe status --dest "D:\Photos"

# 预览布局变更；移除 --dry-run 后实际执行
.\picharbor.exe reorganize --dest "D:\Photos" --organize-by month --dry-run

# 列出可恢复的重复副本
.\picharbor.exe library recycled --dest "D:\Photos"
```

在任意命令后使用 `--help` 查看完整选项：

```powershell
.\picharbor.exe --help
.\picharbor.exe copy --help
.\picharbor.exe search --help
.\picharbor.exe library --help
```

用于脚本时，`copy --no-dashboard` 可启用纯文本输出。默认读取停滞超时为 30 秒；对于较慢的设备，可用 `--read-timeout 60` 延长等待时间。

## 从源码构建

在已安装 .NET 10 SDK 的 Windows 环境中，克隆仓库并运行以下命令：

```powershell
git clone https://github.com/Cc-Cece/PicHarbor.git
cd PicHarbor
dotnet restore PicHarbor.sln
dotnet run --project src/PicHarbor.Gui -c Release
```

从源码运行命令行工具：

```powershell
dotnet run --project src/PicHarbor.Cli -c Release -- --help
```

发布与正式版本相同格式的 Windows x64 独立运行版可执行文件：

```powershell
dotnet publish src/PicHarbor.Gui -c Release -r win-x64 --self-contained true -o artifacts/gui
dotnet publish src/PicHarbor.Cli -c Release -r win-x64 --self-contained true -o artifacts/cli
```

输出为 `artifacts/gui/PicHarbor.Gui.exe` 和 `artifacts/cli/picharbor.exe`。仍需满足 Apple 驱动、WebView2 及各功能的专用依赖要求。

### 仓库结构与检查

| 目录 | 用途 |
| --- | --- |
| `src/PicHarbor.Gui` | WPF 桌面应用、画廊、设置和更新。 |
| `src/PicHarbor.Cli` | 命令解析和终端界面。 |
| `src/PicHarbor.Core` | 设备访问、传输、SQLite 日志、元数据、图库管理和同步。 |
| `src/PicHarbor.Vault` | 应用锁服务和 Windows 权限保护。 |
| `src/PicHarbor.FakeDevice` | 供测试按需启用的模拟设备工具。 |
| `tests/` | 核心逻辑、安全约束、Vault 测试，以及辅助 JavaScript/Python 测试。 |
| `docs/` | 用户指南和工程记录。 |
| `website/` | 静态项目网站。 |

运行 .NET 构建和测试，其中包括检查编译产物是否调用了禁止使用的 iPhone 设备修改接口：

```powershell
dotnet build PicHarbor.sln -c Release
dotnet test PicHarbor.sln -c Release --no-build --filter "Category!=E2E"
```

独立的可执行文件端到端测试任务和发布产物冒烟检查定义在 [CI](.github/workflows/ci.yml) 中。

## 致谢

PicHarbor 基于以下项目的工作构建：[get-and-see](https://github.com/denis-a-evdokimov/get-and-see)、[gpmc](https://github.com/xob0t/gpmc)、[PCL](https://github.com/Meloong-Git/PCL) 和 [sweet-album](https://github.com/leuvi/sweet-album)。

作者反对利用本项目引流、营销。请勿冒充官方或虚称作者背书。
