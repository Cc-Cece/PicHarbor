# PicHarbor

> 本项目主要基于[get-and-see](https://github.com/denis-a-evdokimov/get-and-see)并添加了更多功能。

[English](README.md) | [简体中文](README.zh-CN.md) | [项目主页](https://picharbor.akihito.dpdns.org/)

一款面向 Windows 的本地化**照片管理与备份工具**。支持通过 USB 快速无损备份移动端媒体文件，提供离线索引与高效检索，并支持跨设备同步与恢复至 iPhone、Android 及 Google 相册。

---

## 核心特性

### 📥 iPhone USB 无损备份 (AFC 协议)
- **原生 AFC 通信**：基于苹果原生 Apple File Conduit (AFC) 协议与设备直连，规避 Windows MTP / 资源管理器常见的传输断连与 0 字节损坏文件问题。
- **绝对只读安全保证**：架构层与编译期测试（`ReadOnlyContractTests`）双重约束。仅读取 iPhone 相机胶卷（`/DCIM/`），不向设备执行任何写入、修改或删除操作。
- **原子写入与断点续传**：基于 SQLite 事务日志与原子写入机制（`.partial` 写入 → 大小校验 → 命名就位）。传输中途拔线、休眠或中断后，重新运行自动跳过已备份项无缝续传。
- **灵活范围筛选**：支持全量相机胶卷备份、按拍摄日期区间筛选或指定 DCIM 子目录备份。

### 🔄 多端恢复与同步
- **恢复至 iPhone**：一键导出并整理为专用同步目录（平铺或 `YYYY-MM` 年月相册），通过 Apple Devices 或 iTunes 官方应用同步回 iOS「照片」，完整保留实况照片（Live Photo）成对关系。
- **恢复至 Android**：通过 FTP 协议将归档媒体直接上传至 Android / Pixel 设备，支持常规恢复以及基于 SQLite 历史记录的增量跳过。
- **同步至 Google 相册**：集成 `gpmc` 实现云端备份。支持内置窗口自动捕获 OAuth 凭据或 Android GmsCore 凭据，支持 Pixel 原画质（不计空间配额）或节省空间画质、自动/自定义相册分类、多线程并发及网络异常自动避让重试。

### 🔍 离线媒体检索与画廊
- **离线即时检索**：无需连接手机，通过本地 SQLite 数据库（`picharbor.db`，兼容旧版 `get-and-see.db`）快速检索。支持按拍摄时间、设备型号、媒体类型、GPS 定位、文件大小等多维度筛选。
- **多模式浏览**：提供表格视图与照片画廊视图，支持大图灯箱预览及内置视频直接播放。
- **智能关联配对**：自动识别并补全 Live Photo（实况照片 `.MOV`）、修图数据（`.AAE`）以及 RAW 预览（`.DNG` + `.JPG`）。

### 📁 归档管理与元数据维护
- **多种目录结构**：支持按月份（`YYYY-MM`）、年月层级（`YYYY/YYYY-MM`）、年份（`YYYY`）或平铺（`flat`）自动归档。
- **本地结构重整理**：提供 `reorganize` 工具，可直接在 PC 端无损重整现有归档目录的层级结构。
- **元数据与完整性**：完整保留拍摄 EXIF 与 GPS 信息，支持计算并校验 SHA-256 哈希；支持将照片 EXIF 拍摄时间一键同步至 Windows 文件的系统时间戳。
- **归档概览**：每次备份均生成直观易读的 `summary.txt` 概览报告与结构化数据库清单。

---

## 双端界面

PicHarbor 提供两套共享同一数据库清单与归档目录的交互界面：

1. **桌面图形界面 (`PicHarbor.Gui`)**：功能完备的桌面应用，包含“备份到电脑”、“恢复到 iPhone”、“恢复到 Android”、“Google 相册”、“归档状态”、“媒体检索”、“目录整理”和“设置”八大模块。
2. **命令行工具 (`picharbor.exe`)**：轻量级命令行程序，适合脚本化调用与自动化任务，具备实时交互式终端监控面板。（主要是[get-and-see](https://github.com/denis-a-evdokimov/get-and-see)）

---

## 运行环境与依赖

- **操作系统**：Windows 10（版本 2004 / 内部版本 19041 或更高版本，x64）或 Windows 11。
- **Apple 驱动服务**：需安装 **iTunes for Windows** 或微软商店的 **Apple Devices** 应用（提供 `usbmuxd` 守护进程）。
  
  > *提示：若使用微软商店的 Apple Devices，每次电脑重启后需打开该应用一次以启动后台服务。*
- **设备配置**：iPhone 连接电脑后解锁并点击**“信任此电脑”**。
- **编译依赖**：[.NET 10 SDK](https://dotnet.microsoft.com/download)（如从源码构建）。

---

## 快速上手

### 桌面客户端

从源码启动：
```pwsh
dotnet run --project src/PicHarbor.Gui -c Release
```
或发布单文件独立运行版：
```pwsh
dotnet publish src/PicHarbor.Gui -c Release -r win-x64 --self-contained true
```

### 命令行 (CLI)

```pwsh
# 增量备份到目标目录
picharbor copy --dest "D:\Photos"

# 指定归档目录层级结构 (month, year-month, year, flat)
picharbor copy --dest "D:\Photos" --organize-by year-month

# 试运行预览（仅扫描计划，不实际拷贝）
picharbor copy --dest "D:\Photos" --dry-run

# 检索归档媒体（无需连接设备）
picharbor search --dest "D:\Photos" --type video --from 2024-01-01 --has-gps

# 重整已有归档目录的层级结构
picharbor reorganize --dest "D:\Photos" --organize-by year-month

# 查看归档状态与统计信息
picharbor status --dest "D:\Photos"
```

---

## 命令行常用参数

### `copy`
| 参数 | 默认值 | 说明 |
|---|---|---|
| `--dest, -d` | *(必填)* | 归档媒体保存的目标路径。 |
| `--organize-by` | `month` | 目录结构：`month` (`YYYY-MM`)、`year-month`、`year` 或 `flat`。 |
| `--dry-run` | 关闭 | 仅枚举扫描文件并输出计划，不执行读取与写入。 |
| `--verify-hash` | 关闭 | 传输时计算文件 SHA-256 并记录至清单中。 |
| `--read-timeout` | `30` | 读取超时阈值（秒），超时自动中断并支持后续续传。 |
| `--no-dashboard` | 关闭 | 使用简明文本输出替代终端实时进度看板。 |

### `search`
| 参数 | 说明 |
|---|---|
| `--dest, -d` | *(必填)* 归档目标目录路径。 |
| `--from <YYYY-MM-DD>` | 筛选拍摄时间晚于或等于该日期的文件。 |
| `--to <YYYY-MM-DD>` | 筛选拍摄时间早于或等于该日期的文件。 |
| `--type <photo\|video\|screenshot\|other>` | 筛选媒体类型。 |
| `--camera <string>` | 按拍摄设备品牌或型号子字符串过滤。 |
| `--has-gps` | 仅显示包含 GPS 坐标的文件。 |
| `--open` | 在 Windows 资源管理器中打开匹配文件所在的文件夹。 |

---

## 文档参考

- [数据库清单说明 (Manifest Schema)](docs/user/manifest-schema.md)
- [常见问题与故障排查 (Troubleshooting)](docs/user/troubleshooting.md)

---

## 致谢 / 相关项目

本项目参考或使用了以下开源项目的代码与设计思路：

- [get-and-see](https://github.com/denis-a-evdokimov/get-and-see) (MIT)
- [gpmc](https://github.com/xob0t/gpmc) (MIT)
- [PCL](https://github.com/Meloong-Git/PCL) (other)
- [sweet-album](https://github.com/leuvi/sweet-album) (MIT)

---

## 开源协议

本项目采用 [GNU General Public License v3.0 (GNU GPLv3)](https://www.gnu.org/licenses/gpl-3.0.html) 协议发布。

