# 安卓设备唯一码识别 — 方案与实施清单

**状态：** ⚠️ **方案已于 2026-10-13 修订并实施完成** —— 放弃 USB 序列号路径，改为「PC 生成短唯一码」为唯一身份来源。
第二~五章的 USB/SN 方案**已废弃**（保留作为决策记录），请以**第十一章**为准。
**初版定稿：** 2026-10-12 ｜ **修订并实施：** 2026-10-13
**相关文件：** `src/PicHarbor.Core/Device/UsbDeviceSerialProbe.cs`、`DeviceSerialValidator.cs`、
`src/PicHarbor.Core/Android/AndroidDeviceDetector.cs`、`src/PicHarbor.Gui/ViewModels/AndroidBackupViewModel.cs`、
`src/PicHarbor.Gui/DeviceBackupHistoryDialog.xaml.cs`

---

## 一、要解决的问题

安卓设备通过 FTP 备份/回传时，需要一个**稳定、唯一**的设备标识来：

1. 把备份历史正确归到同一台设备下（`devices.udid`）
2. 给回传记录分区，避免重复传输（`android_sync_records.device_id`）
3. 在"连错设备"时给出提醒

---

## 二、核心结论：唯一码从哪来

### 可用的只有硬件序列号（SN）

安卓上真正"硬件烧录"的标识只有 `ro.serialno`。**免 ADB 读它的唯一途径**是：

```
USB 描述符 iSerialNumber  ≡  MTP DeviceInfo.SerialNumber  ≡  Windows WPD_DEVICE_SERIAL_NUMBER
```

三者同源，都来自 `ro.serialno`，且**恢复出厂设置不变**。

### 其余候选全部出局

| 候选 | 出局原因 |
|---|---|
| `ANDROID_ID` (SSAID) | Android 8+ 按「应用签名 + 用户 + 设备」隔离；**恢复出厂会变** |
| IMEI / `Build.getSerial()` | Android 10+ 需 `READ_PRIVILEGED_PHONE_STATE`（仅系统应用） |
| Wi-Fi / BT MAC | Android 6+ 起随机化，App 侧 `getHardwareAddress()` 直接返回 null |
| `Build.FINGERPRINT` / 型号 | 同型号所有机器完全相同 |

### 但 SN 不可 100% 依赖

1. 部分白牌 / 低端 MTK 机型出厂**未烧唯一 SN** → 空值、通用串、或同型号所有机器相同
2. `iSerialNumber` 由 USB gadget 驱动配置，**切换 USB 模式（MTP/PTP/RNDIS/仅充电）时可能变或消失**
3. MTK 平台可在 autoconfig 改 SN，刷第三方 ROM 可能变
4. 锁屏状态下部分机型拒绝 MTP 枚举

---

## 三、权威判据：怎么判断"设备有没有提供 SN"

**判据：设备实例 ID 的最后一段若含 `&`，即为 Windows 用「集线器 / 端口路径」合成**
（此时 `DEVICE_CAPABILITIES.UniqueID = FALSE`），**换一个 USB 口就会变，不是设备身份**。

```
USB#VID_2717&PID_FF40#a1b2c3d4e5f6        → 真 SN
USB#VID_2717&PID_FF40#5&2b1c4d3&0&1       → 合成值，判为「无 SN」
```

> **禁止用字符串启发式去猜垃圾值。** 旧实现的正则 `#([^#&]+)` 用 `[^#&]` 排除了 `&`，
> 会把 `5&2b1c4d3&0&1` 截成 `"5"` 当成真序列号写进库。合成值长什么样属于 Windows 实现细节，
> 靠特征反推必然漏判。

### 叠加校验（`DeviceSerialValidator`）

| 判据 | 拦截 |
|---|---|
| 含 `&` | Windows 端口路径合成值 |
| `android:` 前缀 | 本项目自造的 UDID 前缀，里面装的是配置项 |
| 长度 < 4 或 > 64 | 截断残留 / 异常串 |
| 全相同字符 | 占位填充（`00000000`、`FFFFFFFF`） |
| 已知占位串 | `0123456789ABCDEF`、`unknown` 等 |
| **与已登记设备重复** | **同型号所有机器返回同一个通用串**（单看一台设备查不出来） |

---

## 四、身份模型

```
唯一码（设备标识）
  ├─ 优先：USB/MTP 读到的 SN
  ├─ 失败：用户手动填入
  └─ 都不可用：用户点「随机」→ PC 生成 PH-{10 位大写 hex}

devices.udid（安卓侧） = "android:" + 唯一码
```

**关键原则：**

- **PC 侧存储是身份真相**（`AppConfig.AndroidBackupDeviceId` + `devices` 表 + `android_devices` 表）。
  不依赖"手机文件还在"，也不试图从特征派生。
- **手机端 `.picharbor-serial` 用于核对**，不是身份来源。
- **目标目录固定为 `/DCIM/PicHarbor/`**（UI 只读展示）。目录不固定会导致识别文件读写位置漂移，
  是身份漂移的根源。
- **改唯一码必须迁移 `udid`**（`devices.udid` + `runs.device_udid` + `backup_sessions.device_uid`），
  否则下次备份会新建一条记录、历史批次脱钩。目标 udid 被占用时拒绝迁移。
- **回传不做限制**：A 设备备份到电脑的照片，允许回传给 A 或 B；唯一码只用于给同步记录分区。

---

## 五、回传（PC → 安卓）的识别逻辑

比对的两个值：**期望值** = PC 侧已确认唯一码；**实际值** = 手机 `.picharbor-serial`。

| 手机 `.picharbor-serial` | PC 侧唯一码 | deviceId 取值 | 动作 |
|---|---|---|---|
| 有，且相同 | 有 | 唯一码 | 正常回传 |
| 有，但不同 | 有 | 手机的值 | 警告「本机确认目标是 A，当前连到的是 B」，用户选中止或继续 |
| **无** | 有 | PC 侧唯一码 | **无法核对**，必须明确告知 |
| 有 | 无 | 手机的值 | 用手机值并回填 PC 侧配置 |
| 无 | 无 | — | 走新设备分支 |

**为什么"手机无文件"这一格必须提示而不能静默**：

```
连错设备（连到 B，B 上没文件）→ 静默按 A 的唯一码记录
  → 文件传到 B 的手机上，但 android_sync_records 记成 A
  → 下次回传给 A 时这些文件被判定「已同步」而跳过
  → 但 A 手机上其实没有 → 数据静默不一致
```

---

## 六、已完成（2026-10-12）

### Core

- `UsbDeviceSerialProbe` 重写：新增 `UsbSerialAvailability` 枚举与 `TryParseDeviceInstance(path, out serial)`；
  按「末段含 `&`」权威判定，无 SN 时返回 `null` 而非垃圾值
- `DeviceSerialValidator` 新增：`DeviceSerialRejection` 枚举 + `Validate` / `IsUsable`
- `TransferJournal` 新增三个方法：
  - `UpdateDeviceHardwareSerial(udid, serial)` — 仅改目标设备
  - `IsHardwareSerialTakenByOther(serial, excludeUdid)` — 跨设备重复检测
  - `MigrateDeviceUdid(old, new)` — 事务内迁移三张表，目标被占用则拒绝
- `ArchiveRepository` 新增对应三个 async 包装
- `AppConfig` 新增 `AndroidBackupDeviceIdConfirmed`

### GUI

- **安卓备份 FTP 弹窗**：新增「设备标识」行（TextBox + 「自动识别」按钮 + 状态提示）；
  打开弹窗时静默自动探测一次（`AutoProbeDeviceIdentityAsync`，仅未确认时）
- **设备备份历史弹窗**：新增「修正标识」按钮 + 内联编辑（输入框 / 保存 / 取消）+ 提示行；
  占位值显示为红色「未识别」
- 三份 `StringResources` 各新增 24 键，**918 × 3 键集一致**

### 验证

- build 0 警告 0 错误
- 测试 484 + 90 + 2 全过（新增 34 例：`UsbDeviceSerialProbeTests` / `DeviceSerialValidatorTests` /
  `DeviceIdentityMigrationTests`）

---

## 七、实施清单（已全部完成）

### 第一组：身份地基

1. **固定同步目标目录为 `/DCIM/PicHarbor/`** ✅
   - 新增 `AndroidRemoteLayout.RootDirectory` 常量 + `Normalize()`（一律收敛）
   - UI 从可编辑 TextBox 改为**只读展示**（`IsReadOnly` + `IsTabStop=False` + ToolTip 说明）
   - 三处默认值统一；`AppSettings.Load()` 对历史值做一次性收敛

2. **合并两套设备标识配置** ✅
   - `AndroidSyncViewModel` 与 `BackupViewModel` 均改读写 `AppConfig.AndroidBackupDeviceId`
   - `AppConfig.AndroidDeviceId` 退役；`AppSettings.Load()` 做一次性迁移（旧值非空且新值为空时搬过去）

3. **回传识别改造** ✅
   - 新增 `DeviceIdentityResult` / `DeviceIdentitySource` / `DeviceIdentityMatch`（Matched / Mismatched /
     Unverifiable / NoBaseline），纯决策函数 `ResolveFromValues` 与 I/O 分离
   - `AndroidSyncEngine.SyncAsync` 新增 `confirmIdentity` 回调；不一致或无法核对且未确认时
     **不传输任何文件**，返回 `AndroidSyncResult.Aborted`
   - **修复漏洞**：`.picharbor-serial` 路径不再绕过比对

### 第二组：功能补齐

4. **「随机」按钮** ✅ — FTP 弹窗 + 设备历史弹窗；`DeviceSerialGenerator.CreateInternalId()` → `PH-` + 10 位大写 hex
5. **写识别文件到手机** ✅ — `AndroidBackupViewModel.TryWriteIdentityToPhoneAsync`（best-effort，失败只提示不阻塞）
6. **历史弹窗「读取 SN」** ✅ — 复用 `UsbDeviceSerialProbe` + 校验，结果填入编辑框待用户确认
7. **历史弹窗「重新绑定到手机」** ✅ — 读 `AppConfig` 的 FTP 参数连接并把当前标识写回设备端固定目录；
   iPhone 设备自动隐藏该按钮
8. **修 `hardware_serial` 未同步 bug** ✅ — `ApplyDeviceIdentityAsync` 补 `UpdateDeviceHardwareSerialAsync`；
   同时修 `MigrateDeviceUdid` 在源记录不存在时谎报成功的问题（改为统计影响行数）

---

## 八、已定取舍

1. **手机无文件 + PC 侧有码** → **阻塞确认**（弹框，用户必须选择才继续）。理由：这是唯一无法自动判定的情况，
   且静默继续会导致「文件传到 B 却记录成 A」的数据不一致。
2. **随机序号格式** → `PH-` + 10 位大写 hex（如 `PH-3F9A2C7E15`）。

---

## 九、已评估并否决的方案

### 9.1 自研安卓 App（随 PC 软件分发，经 FTP 传到手机）

**设想**：App 获取手机信息（含 SN）写入固定文件夹供 PC 识别，并提供配对与 FTP 功能。

**否决理由**：

- **技术硬伤**：Android 10+ 普通应用拿不到 SN（`Build.getSerial()` 需 `READ_PRIVILEGED_PHONE_STATE`）；
  官方《唯一标识符最佳实践》明确：*"尽可能选择用户可重置的标识符……避免使用硬件标识符"*、
  *"在 Android 6 及更高版本中，MAC 地址的访问权仅限于系统应用。第三方应用无法访问这些地址。"*
  —— **Android 有意不向普通应用提供不可重置的硬件标识符**。
- **真正增量只有两点**：① 自带 FTP 服务（免装第三方 FTP App）；② 手机文件被误删时可多位置冗余 + `ANDROID_ID` 辅助自动恢复。
- **成本不成比例**：新工程 + Gradle + 签名 keystore + CI 需装 Android SDK（现项目纯 .NET）；
  用户需开「未知来源安装」，小米纯净模式 / 华为鸿蒙会拦截；Android 11+ 写公共目录需
  `MANAGE_EXTERNAL_STORAGE` 敏感权限；**国产 ROM 省电策略会杀后台导致 FTP 不稳定（自研也躲不掉）**；
  Android 版本碎片化长期维护。
- **注意**：真正需要覆盖的"极端情况"其实是「手机上的识别文件被误删」；
  **恢复出厂设置本就应当重新绑定**（用户数据全清、照片也没了，备份关系理应重建），不算需要覆盖的异常。

### 9.2 由 PC 从「已有特征」确定性派生唯一码

**设想**：不依赖存储，从手机已有特征算出唯一码，重新生成也不变。

**否决理由**：派生输入必须同时满足「稳定」+「能区分个体」。核过 PC 免 ADB 可观测的全部特征：

| 特征 | 区分个体 | 稳定 |
|---|---|---|
| USB `iSerialNumber` | 是 | 是 |
| VID / PID | 否（同型号共享） | 否（随 USB 模式变） |
| 型号串（iManufacturer / iProduct / `WPD_DEVICE_MODEL`） | 否 | 是 |
| `WPD_DEVICE_FIRMWARE_VERSION` | 否 | 否（系统升级即变） |
| Wi-Fi MAC（PC 可经 ARP 读） | 是（单网内） | **否：per-SSID，换网即变**；Android 12+ 开发者选项可轮换 |
| IP 地址 | 是（单网内） | 否（DHCP 会变） |
| FTP banner / 主机名 | 弱 | 否（用户可改） |

→ 除 SN 外全部不满足，派生结果退化为「型号指纹」（同型号两台手机冲突）。

**另需澄清**：派生 ID 也解决不了「手机无文件时无法核对」——它只能提供**期望值**，
而期望值 PC 侧本来就有；真正缺的是手机端的**实际值**。

---

## 十、结论（初版）

**回到纯 PC 方案。** 身份以 PC 侧存储的唯一码为准，手机端文件仅用于核对；
"手机文件误删"通过历史记录里的「重新绑定到手机」手动修复——这是成本最低且已足够覆盖实际场景的做法。

---

# 十一、方案修订（2026-10-13）：放弃 USB 序列号路径

## 11.1 起因：一次真实事故

用户报告：FTP 弹窗自动识别得到 `90218962415663`，而设备历史显示 `0000802000067D2C14A3002E`，
且未插 USB 也能读到值。

**根因（已实测确认）：**

1. `UsbDeviceSerialProbe` 读的是注册表 `HKLM\SOFTWARE\Microsoft\Windows Portable Devices\Devices`，
   **该键包含历史条目**——设备拔掉后条目长期保留。实测该键有 17 个子键 / 10 个 USB 条目，
   涉及 iPhone、iPad、Hongsha M60、M2012K11AC、MI 8 等多台**曾经连接过**的设备。
2. 调用方**盲取第一个通过校验的候选**（`break`），于是选中了「Hongsha M60」的 `90218962415663`。
3. 用户手机（Redmi K40，`adb` 权威确认）真实序列号是 **`691A42FE`**，与选中值完全无关。
4. 更严重：该 K40 当前枚举为 `USB\VID_18D1&PID_4EE7\691A42FE`，**ClassGUID = WinUSB，根本不是 WPD 设备**，
   WPD 注册表里只有它更早的 `VID_18D1&PID_4EE1` 条目 → **该路线对这台手机只能读到历史，读不到当前状态**。

> 数据已修复（含备份 `E:\Pictures\PicHarbor\.picharbor\backup-20261013\`），
> `devices` / `runs` / `backup_sessions` / `config.json` / 手机端 `.picharbor-serial` 均已更正为 `691A42FE`。

## 11.2 结论：SN 读取是纯冗余

**SN 读取带来的功能增益基本为零。** 本设计只需三个能力，生成的码全部具备：

| 目的 | 生成的码能否满足 |
|---|---|
| 让备份历史挂到同一台设备 | ✅ |
| 给回传记录分区、避免重复传输 | ✅ |
| **检测"连错了设备"** | ✅ **效果完全一致** |

第三条是关键：**码是我们自己写进手机的，手机报什么就是什么。**
连到 B 手机会读到 B 的码 → 与基准不符 → `Mismatched` 拦截。**SN 没有任何独有能力。**

而 SN 路径要付的代价：注册表含历史、USB 模式变化导致 PID/实例 ID 不同、白牌机不烧码、
多设备无法区分、需要 SetupAPI P/Invoke。**上面那起事故就是这些代价的兑现。**

## 11.3 新方向：PC 生成短唯一码

```
首次绑定：生成 PH-XXXXXXXXXX
         → 写入手机 /DCIM/PicHarbor/.picharbor-serial
         → 同时存 PC 侧（AppConfig + devices 表）
后续识别：读手机文件 → 与 PC 侧基准比对 → Matched / Mismatched / Unverifiable
文件丢失：从设备历史重新配对，把 PC 侧的值写回手机
```

**附带收益：首次绑定不再需要数据线。** USB 整条链路可从产品中移除——
不用提醒插线、不用管 MTP 模式、不用管"仅充电不枚举"、不用管 ADB 开关。
备份与回传本来就都依赖 FTP，FTP 不通时插线也无用。

**唯一代价**：手机文件与 PC 配置同时丢失时无法从硬件重新认出。属"双灾难"场景，
且**有 SN 也救不了**（那正是 SN 最不可靠的时候）。

## 11.4 配对流程（用户明确要做的功能）

**核心难点：手机文件丢失时，PC 怎么知道连的是哪台手机？**
答案取决于入口——**配对必须绑定到一条明确的设备记录**。

| 入口 | 目标码来源 | 安全性 |
|---|---|---|
| **设备历史弹窗**（推荐） | 该条记录已存的码 | ✅ 明确知道是哪台 |
| **FTP 弹窗**（首次/通用） | 手机无文件 → 新生成；有文件 → 需判断 | ⚠️ 必须防护 |

**三条分支（读手机文件后）：**

1. **未找到文件** → 生成新码 → 写入 → **回读验证** → 落库。这是"文件被删"的正常路径。
2. **已存在且一致** → 已完成配对，**不重复写**。
3. **已存在但不一致** → **必须停下警告用户**：可能是连错了手机，或该手机已绑给另一条记录。
   给用户两个出口：① 到设备历史为它建立独立记录（不覆盖）② 确认覆盖。
   **没有这条分支，配对就会变成"把 A 的身份随手写到 B 手机上"——与本次事故同类错误。**

**实时反馈**：配对做成**分步可见**的对话框，而不是一句最终提示。

```
目标设备：K40          使用标识：PH-3F9A2C7E15
─────────────────────────────────
✅ 连接 FTP              192.168.10.35:2121
✅ 读取手机端识别文件      未找到（需要配对）
✅ 写入 .picharbor-serial 141 字节
⏳ 回读验证...
```

**「回读验证」是配对成功的唯一判据**——FTP 的 `STOR` 可能返回成功但文件未真正落盘，
必须 `STOR` 后再 `RETR` 读回逐字节比对。

技术上：Core 加 `PairAsync(ftp, targetCode, IProgress<PairStep>)`，回调每步状态；
GUI 渲染成列表。符合项目现有 `onLog` / `onItemProgress` 回调风格。
现有的「重新绑定到手机」只有一句最终提示、失败时看不出卡在哪，正好一并升级。

**手机端可见性补偿**：不装 App 无法实时反馈，但可额外写一个人类可读的
`.picharbor-paired.txt`（设备名 / 标识 / 配对时间），用户可在手机文件管理器查看。

## 11.5 手动输入的定位

**保留，但降级为「高级 / 兜底」入口，不作为引导路径。** 剩余用途：

1. 极端情况：PC 重装**且**归档库也丢失（归档库就在归档目录内，很难同时丢）
2. 用户偏好：想把真 SN 或自己的命名当码用
3. 写手机失败时，PC 侧至少先有个记录

**风险提示**：手动输入可能给同一台手机造出**第二个身份**（手打 `my-phone` 而手机里是 `PH-XXX`），
下次配对会 mismatch 拦截、让用户困惑。因此必须在 UI 上标明其为兜底入口。

## 11.6 实施清单（已全部完成）

1. **删除 USB 路径** ✅ — `UsbDeviceSerialProbe.cs` 及其测试已删除；FTP 弹窗「自动识别」与历史弹窗「读取 SN」按钮已移除；
   相关 12 个语言键已清理
2. **生成码提为主路径** ✅ — FTP 弹窗按钮改为「生成并绑定」（`GenerateAndBindDeviceIdCommand`）
3. **新增配对对话框** ✅ — `AndroidPairingDialog`：分步实时反馈 + 回读验证 + 覆盖保护；
   Core 侧 `AndroidPairingService.PairAsync` 上报 `IProgress<PairStep>`
4. **升级「重新绑定到手机」** ✅ — 改为打开同一个配对对话框（目标码由设备记录带入）
5. **移除 WPD 注册表读取** ✅ — 探针整体删除，不再有任何回退到历史条目的路径
6. **Crockford Base32** ✅ — `DeviceSerialGenerator` 改用无 `I/L/O/U` 的字母表 + 密码学随机，`PH-` + 8 位（共 11 字符）
7. **保留** ✅ — `DeviceSerialValidator`（校验手动输入与生成码）、`DeviceIdentityMatch` 逻辑（只认文件值，未改动）
8. **同步侧标识合并** ✅ — `android_devices` / `android_sync_records` / `android_manual_selections` 的
   `GAS-A7368C46`、`GAS-0E8C798E`、`K40`、`Android Device` 四个 id 合并为 `691A42FE`；
   `android_sync_records` 7290 → 5519 条（两组 GAS-* 是 K40 的子集，无信息损失），
   **保住了 5519 条"已回传"记录，避免下次回传全量重传**。备份：`backup-20261013b/`

**验证**：build 0 警告 0 错误；三份语言资源 **944 × 3 键集一致**；CRLF 归一；
新增/更新测试覆盖生成器格式与字母表、目录收敛、决策矩阵、配对路径规范化。

---

# 十二、设备命名模型（2026-10-13 补充）

## 12.1 两个名字，分工明确

| | 可变性 | 用途 | 存哪 |
|---|---|---|---|
| **显示名称** | 随时可改 | 界面显示 + 归档库 `devices.name` | `AppConfig.AndroidBackupDeviceName` / `IPhoneDeviceName` |
| **归档文件夹名** | **首次备份时定死，之后不可变** | 文件系统布局（参与 manifest 的相对路径） | `AppConfig.AndroidCustomSubdir` / `IPhoneCustomSubdir` |

**归档文件夹名绝不能跟着显示名改**——它参与 manifest 的 `DestPath`，改动会让已有文件留在旧目录、
新备份写进新目录，形成**归档目录分裂**。

## 12.2 修复的既有缺陷

1. **`DeviceSubdir` 从未持久化**：`AppConfig.AndroidCustomSubdir` / `IPhoneCustomSubdir` 两个字段一直存在但
   **从未被读写**；两个 VM 的 `deviceSubdir` 都是硬编码初值（`"Pixel 8"` / `"iPhone"`），
   且用「等于默认值」这种隐式判断决定是否跟随显示名 → 每次重启归档目录名都会漂移。
2. **iPhone 改名会改归档文件夹名**：`SaveRenameModal` 的 iPhone 分支直接 `BackupVM.DeviceSubdir = clean`，
   比安卓更糟（安卓至少不动目录名）。
3. **显示名是两套字段**：`DetectedDeviceModel`（重命名编辑）与 `AndroidDeviceName`（无 UI，备份时写库）
   互不同步 → 改的名字活不过下一次备份。

## 12.3 现行行为

- **首次备份前**：若归档文件夹名未确定，弹出 `ArchiveFolderDialog`（默认值 = 当前显示名），
  说明「这是唯一一次修改机会」，并在确认前**校验重名**（不得与既有设备名冲突）。确认后写入配置并锁定。
- **之后**：归档文件夹名只读；备份页与重命名弹窗均以只读方式展示。
- **重命名**：只改显示名 → 更新配置 + 归档库 `devices.name`（带重名拒绝）；**不触碰归档文件夹名**。
- **iPhone 显示名**：默认取设备自报的名字；一旦用户重命名即锁定，不再被设备名覆盖。
- **安卓显示名**：由 `DetectedDeviceModel` 单一持有（`AndroidDeviceName` 已退役），
  识别到唯一码后**用码查库回填**，避免用户重新起名。

## 12.4 已知遗留

- iPhone 同步侧仍以**设备型号**（`iphone_devices.device_model`）为主键 → 两台同型号 iPhone 会撞车。
  本次未处理，属独立问题。
- 同步页（PC → 手机）的 `AppConfig.AndroidDeviceName` 与备份页的 `AndroidBackupDeviceName` 仍是两个字段；
  前者目前已无实际用途，未清理。
