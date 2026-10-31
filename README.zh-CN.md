<div align="center">

<img src="src/PicHarbor.Gui/Assets/PicHarbor.png" alt="PicHarbor 图标" width="96" />

# PicHarbor

面向 Windows 的本地照片管理、备份与同步工具。

[![CI](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/Cc-Cece/PicHarbor/actions/workflows/ci.yml)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-0078D4?logo=windows&logoColor=white)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Release](https://img.shields.io/github/v/release/Cc-Cece/PicHarbor?color=007AFF&logo=github)](https://github.com/Cc-Cece/PicHarbor/releases)
[![License: GPL--3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)

<p align="center">
  <a href="README.md">English</a> | <b>简体中文</b>
</p>

<p align="center">
  <a href="https://github.com/Cc-Cece/PicHarbor">GitHub 仓库</a> · <a href="https://github.com/Cc-Cece/PicHarbor/releases/latest">下载发布版本</a> · <a href="https://picharbor.akihito.dpdns.org/">项目主页</a>
</p>

<p align="center">
  相关项目：<a href="https://github.com/Cc-Cece/PicHarbor-iOS">iOS</a> · <a href="https://github.com/Cc-Cece/PicHarbor-Android">Android</a>
</p>

</div>

---

PicHarbor 是一款面向 Windows 的本地照片管理与备份工具。通过 USB 归档 iPhone 媒体，通过 FTP 备份 Android 媒体，提供本地画廊与 SQLite 索引检索。支持跨磁盘管理图库、导出供其他设备同步的照片，以及上传媒体至 Google 相册。

## 功能特性

| 功能 | 说明 |
| --- | --- |
| **iPhone USB 备份** | 通过原生 Apple File Conduit（AFC）读取 `/DCIM/` 媒体，支持按日期/文件夹选择范围与断点重试。 |
| **Android FTP 备份** | 通过 FTP 扫描并复制媒体文件夹，记录设备身份与增量历史。 |
| **本地画廊与检索** | 提供表格与画廊视图、图片预览、视频播放，支持按拍摄日期、相机、GPS 与文件大小筛选。 |
| **关联文件配对** | 自动识别实况照片（Live Photo）组件、`.AAE` 调色修图文件及 RAW/JPEG 配对。 |
| **多磁盘图库** | 将多个归档磁盘纳入统一逻辑图库，支持离线成员跟踪、完整性检查与重复副本回收恢复。 |
| **目录结构布局** | 支持 `month`、`year-month`、`year` 或 `flat` 平铺布局，支持对已有归档原地重整目录。 |
| **导出与同步** | 导出 iPhone 同步目录、通过 FTP 上传至 Android，或通过内置 `gpmc` 上传至 Google 相册。 |
| **桌面与命令行** | 提供 WPF 桌面端应用与独立命令行工具，共享同一套归档格式与元数据。 |

## 快速上手

### 1. 下载程序

从 [Releases](https://github.com/Cc-Cece/PicHarbor/releases/latest) 下载便携版单文件程序（无需另行安装 .NET）：
- **`PicHarbor.Gui.exe`**：桌面图形界面程序。
- **`picharbor.exe`**：命令行工具。

可使用 Release 附件中的 `.sha256` 文件进行校验：

```powershell
Get-FileHash .\PicHarbor.Gui.exe -Algorithm SHA256
```

### 2. 运行环境要求

| 组件 | 要求 |
| --- | --- |
| **桌面端应用** | Windows 10（版本 2004 / 内部版本 19041+）或 Windows 11（x64）。画廊和内嵌登录需 Microsoft Edge WebView2 Runtime。 |
| **iPhone 备份** | Apple USB 驱动（通过 iTunes for Windows 或微软商店「Apple 设备」获取）。数据线连接并信任此电脑。 |
| **Android 备份** | 手机端运行 FTP 服务，局域网连接可达，并在 PicHarbor 中配置对应凭据。 |
| **iPhone 恢复** | 将 PicHarbor 导出的同步目录通过 Apple 设备或 iTunes 同步至手机（AFC 协议不支持反向直接写入相册）。 |
| **Google 相册** | 在 Google 相册页面完成账号授权。同步引擎使用 PicHarbor 内置的 Python 运行时，无需另行安装 Python。 |
| **源码构建** | Windows 环境与 .NET 10 SDK。 |

> [!TIP]
> 电脑重启后若未识别 iPhone，可先启动一次「Apple 设备」应用激活底层后台驱动，再重新连接已解锁手机。

### 3. 首次备份

1. 在剩余空间充足的磁盘上选择目标目录（如 `D:\Photos`）。
2. 打开 iPhone 或 Android 备份页面并连接设备。
3. 选择备份范围、目录布局与校验模式，启动备份。
4. 传输若中断，重新连接设备再次运行即可自动跳过已备份文件，重试未完成文件。

> [!NOTE]
> iPhone USB 备份仅读取相机胶卷，不会修改或删除设备上的原文件。

## 归档存储与校验

照片以普通原始文件保存在磁盘上，不使用专有容器格式。每个归档的日志与索引位于 `.picharbor/`（数据库文件为 `.picharbor/picharbor.db`）。

### 目录布局

| 布局 | 相对路径示例 |
| --- | --- |
| `month`（默认） | `2026-10/` |
| `year-month` | `2026/2026-10/` |
| `year` | `2026/` |
| `flat` | 归档根目录 |

已有归档如需修改布局，请使用**重整目录（Reorganize）**功能，无需重新传输。

### 校验模式

| 模式 | 判定依据 |
| --- | --- |
| **极速模式** | 依据数据库记录、文件存在与大小判断，不读取磁盘内容。 |
| **普通模式**（默认） | 校验电脑端已保存的文件内容。 |
| **严格模式** | 跳过前重新读取源设备内容进行比对。 |

### 多磁盘图库与重复回收

- 支持将多个归档文件夹注册为同一图库的成员，实现跨磁盘合并检索。
- 已验证的重复副本可移入 `.picharbor/recycle` 区域（默认保留 30 天），可在设置或命令行中按需恢复。

### 应用锁与凭据保护

- 可选应用锁通过 Windows 文件权限与本地服务保护归档目录（不对照片原文件加密）。
- FTP 与 Google 凭据均使用当前 Windows 用户的 DPAPI 进行加密保护。

## 从源码构建

需要 Windows 系统与 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
git clone https://github.com/Cc-Cece/PicHarbor.git
cd PicHarbor
dotnet restore PicHarbor.sln
dotnet run --project src/PicHarbor.Gui -c Release
```

运行命令行端：

```powershell
dotnet run --project src/PicHarbor.Cli -c Release -- --help
```

发布独立便携版可执行文件：

```powershell
dotnet publish src/PicHarbor.Gui -c Release -r win-x64 --self-contained true -o artifacts/gui
dotnet publish src/PicHarbor.Cli -c Release -r win-x64 --self-contained true -o artifacts/cli
```

输出路径为 `artifacts/gui/PicHarbor.Gui.exe` 与 `artifacts/cli/picharbor.exe`。

### 代码结构

| 目录 | 职责 |
| --- | --- |
| `src/PicHarbor.Gui` | WPF 桌面应用、画廊界面、设置与自动更新。 |
| `src/PicHarbor.Cli` | 命令行解析与终端交互。 |
| `src/PicHarbor.Core` | 设备通信、传输调度、SQLite 索引与元数据处理。 |
| `src/PicHarbor.Vault` | 应用锁服务与 Windows 权限控制。 |
| `src/PicHarbor.FakeDevice` | 自动化测试专用的模拟设备桩。 |
| `tests/` | 单元测试、安全契约校验与 Vault 测试。 |

运行测试：

```powershell
dotnet test PicHarbor.sln -c Release --filter "Category!=E2E"
```

## 致谢

PicHarbor 基于以下开源项目的工作：[get-and-see](https://github.com/denis-a-evdokimov/get-and-see)、[gpmc](https://github.com/xob0t/gpmc)、[PCL](https://github.com/Meloong-Git/PCL) 与 [sweet-album](https://github.com/leuvi/sweet-album)。

作者反对利用本项目进行引流或商业营销。请勿冒充官方或虚称作者背书。
