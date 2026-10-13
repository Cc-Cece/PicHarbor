# PicHarbor 改造验证记录

日期：2026-10-13。所有写入、回收和 ACL 场景使用随机临时库或 `C:\PicHarborTestArchives`
内的隔离测试目录。未启动应用处理用户真实图库，未安装或更新真实 Windows 服务。

验证命令：

```powershell
dotnet build PicHarbor.sln --no-restore -v quiet
dotnet test PicHarbor.sln --no-build --no-restore -v minimal
dotnet test tests/PicHarbor.Tests/PicHarbor.Tests.csproj --no-restore -v minimal
dotnet list PicHarbor.sln package --vulnerable --include-transitive --format json
git diff --check
```

最后一次整体构建：0 个警告、0 个错误。依赖审计未报告已知漏洞，差异检查通过。

普通权限测试中需要提权的 11 项不计为通过；同一 Vault 测试程序集在隔离提权进程
中重新运行，108 项执行、108 项通过、0 项跳过，原始记录为本地 `elevated.trx`。
后续媒体关系改动经过完整 Core 回归：532 项通过，0 项跳过，见
本地 `library-final_net10.0_20261013183042.trx`。
Safety 测试另外 2 项通过；连同提权 Vault 测试，共 642 项通过。
原始 TRX 和执行日志仅留在本机，不纳入 Git；仓库保存验证摘要及可复现命令。

新增场景包括：设备同路径同大小但不同内容、严格校验、实际成员断开/重连、取消删除
询问、回收恢复及过期延期、准备操作中断、复制成员身份、编辑版内容、同名导出、组件
跨成员移动及改名、旧写入者拒绝、未来版本保护、关系约束、TOTP 并发及写入失败、
管道授权与连接故障、服务重启权限收回、权限恢复失败保留登记、服务镜像目录保护。

发布前仍需实机验收：真实手机与 FTP/Google 账号、正式安装后 LocalSystem 服务与
普通 Windows 用户的通信、GUI 完整交互及不同物理磁盘。提权测试不替代跨账户服务
安装验收，本次没有把这些未执行的流程报告为通过。

## 应用锁响应超时回归

本机诊断确认服务能够响应状态查询，且客户端超时后服务已经进入解锁状态，指向
客户端 3 秒统一期限与同步权限操作之间的结构性冲突。

新增隔离测试覆盖：超过 3 秒的解锁仍等待最终结果、操作期间状态查询保持响应、
启动收权期间不报告解锁、重复验证不重复收权、旧服务无进度帧的慢操作兼容、
收权失败时不授予访问权限。普通权限 Vault 回归 101 项通过、11 项权限测试跳过。
本轮提权验证及已安装服务更新需完成 Windows UAC，未完成前不计为通过或已部署。
