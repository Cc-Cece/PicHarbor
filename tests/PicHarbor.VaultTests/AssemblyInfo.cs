// 测试程序集配置：禁用并行执行。
// Vault 的密码/TOTP/登记根状态都存在 %ProgramData%\PicHarbor 的全局文件里，
// 并行跑会互相踩踏，必须串行。

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
