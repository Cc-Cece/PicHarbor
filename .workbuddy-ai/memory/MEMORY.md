# PicHarbor 项目长期记忆

## 交付形态（已与用户拍板）
- **单文件便携 exe**：用户只拿一个 `PicHarbor.Gui.exe`，双击即用。
- 启用应用锁时，**同一个 exe 带 `--vault-service` 自注册为 LocalSystem 服务**，
  不额外分发第二个文件，弹一次 UAC，用完即弃；日常 GUI 不提权。
- 关闭锁后服务保留空转；设置页提供「彻底移除应用锁组件」。
- TOTP 只用于「忘记密码后重置密码」，**不是**第二种解锁方式；重置成功≠解锁。

## 工程约定（必须遵守）
- 交给用户在 **Windows PowerShell 5.1** 里直接执行的 `.ps1` 脚本**一律写纯 ASCII**，
  不要写中文/非 ASCII 字符：PS 5.1 会把无 BOM 的 UTF-8 文件按系统 ANSI(GBK) 解码，
  非 ASCII 文本会变乱码并导致 `MissingExpressionAfterOperator` / `字符串缺少终止符` 等解析错误。
  写完后用 `[System.Management.Automation.Language.Parser]::ParseFile(...)` 校验。
- **提权子命令必须两处注册**：`Program.Main` 的 `Matches` 分支 + `VaultServiceManager.ExecuteCommandInline`。
  `ExecuteCommandInline` 对**无参**命令用整串精确匹配；**带参**命令必须先 `SplitArguments` 再转发到
  `Program` 的 `internal static` 处理函数（缺任一处 → 已提权环境下静默返回 1）。
- **提权结果只靠退出码 + `--report <json>` 文件回传**，不依赖标准输出；密码等敏感值走
  **临时文件**（`%TEMP%\PicHarborElevate\`），提权进程读完即删、GUI 侧 `finally` 兜底。
- **多值路径参数用 `|` 分隔的单字符串**：`UseShellExecute=true` 下 Shell 会吃引号，
  空格/反斜杠路径用 `"..."` 包裹并整体作为一个参数。
- `AppSettings.Save(AppConfig)` 是**整文件覆盖**：任何单字段持久化都必须 `Load()` → 改字段 → `Save()`，
  否则会抹掉语言、目录、OAuth 等全部设置。
- 三份 `StringResources.{zh-CN,en-US,zh-HK}.xaml` 必须**键集完全一致**（缺一即运行回退英文）；
  改动后用 `grep -o 'x:Key="[^"]*"' | sort | diff` 校验。
- WPF `CheckBox` 双向绑定与 `Checked/Unchecked` 事件**二选一**（并存会互相触发）；本项目选双向绑定。
- **WPF 步骤互斥禁止用 `InverseBooleanToVisibilityConverter`**：多步共存时各步的 Inverse 会同时为 true 而「串台」
  （实测向导步 0 同时显示 6 个按钮）。正确做法：VM 侧暴露**显式单步布尔属性**（如 `ShowWizardBack` / `ShowWizardNext`），
  XAML 一律绑单一布尔 + `BooleanToVisibilityConverter`。**派生属性必须逐一声明在 `[NotifyPropertyChangedFor]`**，
  否则切步不刷新。
- **`WindowStyle="None"` + `WindowChrome` 的自绘标题栏**：任何放在 `CaptionHeight` 带内的交互控件
  **必须** 设 `WindowChrome.IsHitTestVisibleInChrome="True"`，否则点击被拖动窗口吞掉。图标按钮建议 `Path` 手绘
  （`M0,0 H10` 横线=最小化；`M0,0 L10,10 M10,0 L0,10` 叉=关闭），`Stretch="None"` 保证线宽不被拉伸。
- **模态窗关闭 = 取消**：`DialogResult=false` 让调用方 `if (ok == true) Show(); else Close();` 走退出分支；
  写 `DialogResult` 要包 `try/catch(InvalidOperationException)`（非 `ShowDialog` 时不可写）。
- **沙箱禁止 `Add-Type` / `Assembly::LoadFrom`**（判定为执行任意 .NET 代码）。XAML 卡片/窗口验证改用
  「BAML 编译产物存在 + `<Type>.g.cs` 挂接核对 + 启动无 crash.log」三重替代，不要试图反射加载程序集。
- **安全页（设置页 SUB-VIEW 5）是应用锁的唯一入口**，设置页不再保留应用锁卡片。
  向导只做「启用」（步 1~4）；管理模式已删除，管理职责全在安全页。
- **改密码有两条既有服务端命令，勿新增**：`ChangePassword(old,new)` 走旧密码；
  `ResetPasswordWithTotp(code,new)` 走 TOTP（不要求旧密码）。二者均为特权命令，非管理员须提权。
  提权子命令 `--vault-change-password`，两处注册；两个密码都走临时文件（错误路径也要删）。
- **`ServiceInstaller.IsInstalled()` 是现成的服务安装探测**（P/Invoke 版），
  不要再 new `ServiceController`；探测失败保守返回 true（宁多显示「移除组件」入口，不误藏）。

## 服务运维约束
- 仓库内的 Release 二进制（`PicHarbor.Gui.exe` / `PicHarbor.Vault.dll`）
  **在 `PicHarborVault` 服务运行期间会被锁定**，无法覆盖。
  更新应先隔离发布到新路径，再 UAC 更新服务配置、确认旧进程停止后启动新exe。
  `ChangeServiceConfig`和1056成功不等于已加载新版本。
  验收必须普通用户实际GUI客户端完整QueryStatus，核对服务SID=S-1-5-18/PID/构建MVID；管理员CreateFile成功不代表GUI成功。
  脚本：`.docs/rebuild-and-restart-vault.ps1`，诊断子命令 `--vault-status --report <path>`，一键更新 `--repair-vault-service`。

## 命名管道要点（踩过坑，别重犯）
- 管道名 `PicHarborVault`。服务以 LocalSystem 运行。
- **必须显式设 DACL 并放行 Authenticated Users**，否则交互式用户
  `CreateFile` 得 ERROR_ACCESS_DENIED (5)，GUI 表现「已安装但未能连接」。
- **纠正此前错误结论**：创建时仅设置 DACL（不设 SACL/任意所有者）不需要 `SeSecurityPrivilege`；生产和测试统一 `NamedPipeServerStreamAcl.Create`，不可用默认ACL降级隐藏失败。
- **禁止 Deny Everyone FullControl**：会覆盖所有 Allow。断继承用保护DACL；AU只给 ReadWrite+Synchronize，不给CreateNewInstance。
- GUI 不能用 PipeDirection.InOut 默认 GENERIC_WRITE（隐含CreateNewInstance）；显式 PipeAccessRights.ReadWrite+Synchronize，与DACL匹配。
- **身份模拟必须先读后模拟**：Win32 `ImpersonateNamedPipeClient` 要求服务端
  已先从管道读过数据，否则 ERROR_CANNOT_IMPERSONATE (1368) → .NET IOException。
- 命令处理异常**必须**转成结构化响应，绝不逃出 handler（否则管道中途释放，
  客户端只见 ERROR_BROKEN_PIPE (109)）。
- 传输模式用 Byte；协议为行分隔 UTF-8 JSON，请求字段名是 `cmd`（不是 `Command`）。

## 安全边界
- 权限判定在**命令层**：`IsDailyCommand`（QueryStatus/Unlock/Lock/ResetPasswordWithTotp 允许普通用户）
  vs 特权命令（SetPassword/ChangePassword/RegisterRoot/UnregisterRoot/ClearPassword 仅管理员/SYSTEM）。
- 引导例外：尚无密码时允许普通用户 RegisterRoot/SetPassword（首个启用者即所有者）。
- 登记根即锁定（`TryRegister` 调 `AclEngine.Lock`），启用当场生效。
