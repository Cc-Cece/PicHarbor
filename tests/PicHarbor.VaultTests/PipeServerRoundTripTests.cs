// 管道端到端回归测试
//
// 覆盖三个曾经真实发生、且症状极具误导性的缺陷：
//
//  1) 顺序缺陷：`PipeServer.HandleConnectionAsync` 先调 `RunAsClient` 再读管道。
//     Win32 `ImpersonateNamedPipeClient` 要求服务端「先读后模拟」，
//     否则返回 ERROR_CANNOT_IMPERSONATE (1368)，.NET 抛 IOException。
//     症状：服务进程活着、日志里有 "waiting for connection"，但客户端
//     `Connect` 永远超时 / 拿不到响应（因为服务端一读出异常就 return，从不写响应）。
//
//  2) 异常逃逸：命令处理里抛出的异常（例如未提权时无法写受保护目录）
//     会穿透 `HandleConnectionAsync`，导致 `await using (pipe)` 在响应写出前
//     释放管道，客户端只看到 ERROR_BROKEN_PIPE (109)，服务端无任何错误日志。
//
//  3) 管道 DACL 缺失：删掉显式 PipeSecurity 后，服务（LocalSystem）创建的管道
//     使用「默认」安全描述符，交互式用户被挡在门外 —— 客户端 CreateFile 直接
//     ERROR_ACCESS_DENIED (5)，症状是 GUI 永远提示「服务已安装但未能连接」。
//     修复是显式授予 Authenticated Users 读写。
//
// 本测试驱动真实命名管道及实际 GUI 传输代码；仍需真实 LocalSystem/普通用户验收，
// 不能用同进程测试替代跨账户服务通信验证。

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PicHarbor.Vault;
using PicHarbor.Vault.Protocol;

namespace PicHarbor.VaultTests;

/// <summary>
/// 针对 <see cref="PipeServer"/> 的管道往返测试。
/// </summary>
public sealed class PipeServerRoundTripTests : IDisposable
{
    // 每个测试用独立管道名，避免与真实服务（PicHarborVault）及其他测试冲突。
    private readonly string _pipeName = "PicHarborTest_" + Guid.NewGuid().ToString("N");

    private readonly VaultTestScope _scope = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PipeServer _server;
    private readonly Task _serverTask;

    public PipeServerRoundTripTests()
    {
        var state = new VaultState();
        _server = new PipeServer(state, NullLogger<PipeServer>.Instance, _pipeName);
        _serverTask = _server.StartAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _server.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { _serverTask.GetAwaiter().GetResult(); } catch { }
        _cts.Dispose();
        _scope.Dispose();

        // 不删除固定共享目录；所有存储只使用本测试的随机隔离目录。
    }

    // ──────────────────────────────────────────────────────────────────────
    // 往返
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 最基本的往返：发一条 QueryStatus，必须拿到 ok=true 的响应。
    /// 这条测试在「先模拟后读」缺陷存在时会超时/返回 null。
    /// </summary>
    [Fact]
    public async Task QueryStatus_returns_ok_response()
    {
        var resp = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus });
        Assert.NotNull(resp);
        Assert.True(resp!.Ok, resp.Error);
        Assert.NotNull(resp.Status);
        Assert.False(resp.Status!.HasPassword);
    }

    [Fact]
    public async Task Actual_GUI_client_returns_complete_status_with_explicit_pipe_security()
    {
        var response = await PicHarbor.Gui.Vault.VaultClient.QueryStatusOnPipeAsync(_pipeName, TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        Assert.True(response.Ok, response.Error);
        Assert.NotNull(response.Status);
    }

    [Fact]
    public void Communication_failure_is_not_reported_as_an_incorrect_password()
    {
        var response=new PicHarbor.Gui.Vault.VaultResponse {Ok=false,Error="服务身份校验失败"};
        Assert.Equal(response.Error,PicHarbor.Gui.Vault.VaultClient.DescribeUnlockFailure(response));
        Assert.Equal("密码错误。",PicHarbor.Gui.Vault.VaultClient.DescribeUnlockFailure(new(){Ok=false,Error="Wrong password."}));
    }

    [Fact]
    public async Task Slow_unlock_reports_progress_keeps_status_responsive_and_does_not_repeat_revoke()
    {
        string root=Path.Combine(Path.GetPathRoot(_scope.Directory)!,"PicHarborTestArchives","Slow",Guid.NewGuid().ToString("N"));
        RootStore.Save([root]);PasswordStore.SetPassword("isolated-test-password");
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        int locks=0,grants=0;
        using var state=new VaultState(_=>Interlocked.Increment(ref locks),(_,_)=>
        {Interlocked.Increment(ref grants);entered.Set();release.Wait();});
        state.RevokeAllOnStartup();
        string name="SlowUnlock_"+Guid.NewGuid().ToString("N");
        using var stopping=new CancellationTokenSource();using var server=new PipeServer(state,NullLogger<PipeServer>.Instance,name);
        await server.StartAsync(stopping.Token);
        try
        {
            var unlock=PicHarbor.Gui.Vault.VaultClient.SendOnPipeAsync(new(){Command="Unlock",Password="isolated-test-password"},name,TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken));
            var status=await PicHarbor.Gui.Vault.VaultClient.QueryStatusOnPipeAsync(name,TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2),TestContext.Current.CancellationToken);
            Assert.NotNull(status);Assert.True(status.Ok);Assert.NotNull(status.Status);
            Assert.True(status.Status.OperationInProgress);Assert.False(status.Status.IsUnlocked);
            await Task.Delay(3500,TestContext.Current.CancellationToken);
            Assert.False(unlock.IsCompleted);
            release.Set();var result=await unlock;Assert.NotNull(result);Assert.True(result.Ok,result.Error);
            var repeated=await PicHarbor.Gui.Vault.VaultClient.SendOnPipeAsync(new(){Command="Unlock",Password="isolated-test-password"},name,TestContext.Current.CancellationToken);
            Assert.NotNull(repeated);Assert.True(repeated.Ok,repeated.Error);
            Assert.Equal(1,locks);Assert.Equal(1,grants);
            var locked=await PicHarbor.Gui.Vault.VaultClient.SendOnPipeAsync(new(){Command="Lock"},name,TestContext.Current.CancellationToken);
            Assert.NotNull(locked);Assert.True(locked.Ok);Assert.Equal(2,locks);
            Assert.False(state.ReadPermissionStatus().InProgress);Assert.False(state.IsUnlocked);
        }
        finally {release.Set();stopping.Cancel();await server.StopAsync(CancellationToken.None);}
    }

    [Fact]
    public async Task Startup_protection_does_not_block_pipe_status_or_report_unlocked()
    {
        string root=Path.Combine(Path.GetPathRoot(_scope.Directory)!,"PicHarborTestArchives","Startup",Guid.NewGuid().ToString("N"));
        RootStore.Save([root]);using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        using var state=new VaultState(_=>{entered.Set();release.Wait();},(_,_)=>{});
        using var stopping=new CancellationTokenSource();
        string name="StatusStartup_"+Guid.NewGuid().ToString("N");
        using var statusServer=new PipeServer(state,NullLogger<PipeServer>.Instance,name);
        await statusServer.StartAsync(stopping.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken));
            var result=await PicHarbor.Gui.Vault.VaultClient.QueryStatusOnPipeAsync(name,TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2),TestContext.Current.CancellationToken);
            Assert.NotNull(result);Assert.True(result.Ok);Assert.NotNull(result.Status);
            Assert.True(result.Status.OperationInProgress);Assert.False(result.Status.IsUnlocked);Assert.Single(result.Status.RegisteredRoots);
        }
        finally {release.Set();stopping.Cancel();await statusServer.StopAsync(CancellationToken.None);}
    }

    [Fact]
    public async Task Legacy_server_without_progress_can_finish_a_permission_request_after_three_seconds()
    {
        string name="LegacySlow_"+Guid.NewGuid().ToString("N");
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var waiting=pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var responseTask=PicHarbor.Gui.Vault.VaultClient.SendOnPipeAsync(new(){Command="Lock"},name,TestContext.Current.CancellationToken);
        await waiting;using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
        Assert.NotNull(await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        await Task.Delay(3500,TestContext.Current.CancellationToken);
        using var writer=new StreamWriter(pipe,Encoding.UTF8,leaveOpen:true){AutoFlush=true};
        await writer.WriteLineAsync("{\"ok\":true}");
        var response=await responseTask;Assert.NotNull(response);Assert.True(response.Ok,response.Error);
    }

    [Fact]
    public void Failed_revoke_does_not_grant_access_or_report_unlocked()
    {
        string root=Path.Combine(Path.GetPathRoot(_scope.Directory)!,"PicHarborTestArchives","Denied",Guid.NewGuid().ToString("N"));
        RootStore.Save([root]);PasswordStore.SetPassword("isolated-test-password");int grants=0;
        using var state=new VaultState(_=>throw new IOException("ACL failed"),(_,_)=>grants++);
        state.RevokeAllOnStartup();using var user=WindowsIdentity.GetCurrent();
        Assert.False(state.TryUnlock("isolated-test-password",user.User!,Environment.ProcessId,out _));
        Assert.Equal(0,grants);Assert.False(state.IsUnlocked);
        Assert.Contains(root,state.ReadPermissionStatus().Errors.Keys);
        Assert.False(state.ReadPermissionStatus().InProgress);
    }

    [Fact]
    public async Task Actual_GUI_client_preserves_connection_failure_details()
    {
        var response = await PicHarbor.Gui.Vault.VaultClient.QueryStatusOnPipeAsync("Missing_" + Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Contains("连接命名管道超时", response.Error);
    }

    /// <summary>连续多次往返必须都成功——验证 accept 循环在每次连接后都重建管道实例。</summary>
    [Fact]
    public async Task Multiple_sequential_calls_all_succeed()
    {
        for (int i = 0; i < 5; i++)
        {
            var resp = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus });
            Assert.NotNull(resp);
            Assert.True(resp!.Ok, $"第 {i + 1} 次往返失败：{resp.Error}");
        }
    }

    /// <summary>非法 JSON 必须得到一条错误响应，而不是断管。</summary>
    [Fact]
    public async Task Invalid_json_returns_error_not_broken_pipe()
    {
        var resp = await SendRawAsync("this is not json");
        Assert.NotNull(resp);
        Assert.False(resp!.Ok);
        Assert.Contains("Invalid JSON", resp.Error);
    }

    /// <summary>
    /// 未知命令必须得到错误响应（走 switch 的 default 分支），且连接不断。
    /// </summary>
    [Fact]
    public async Task Unknown_command_returns_error_and_pipe_stays_healthy()
    {
        var resp = await SendAsync(new VaultRequest { Command = "NoSuchCommand" });
        Assert.NotNull(resp);
        Assert.False(resp!.Ok);

        // 再来一次正常调用，证明服务没被打崩。
        var again = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus });
        Assert.NotNull(again);
        Assert.True(again!.Ok);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 异常不逃逸（缺陷 2 的回归）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 命令处理抛异常时，客户端必须收到一条结构化错误响应
    /// （而不是 ERROR_BROKEN_PIPE），且服务能继续处理后续请求。
    /// <para>
    /// 这里用 RegisterRoot 指向一个非法路径触发 handler 内的失败路径。
    /// 无论失败发生在参数校验还是文件系统层，都必须转成响应。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Handler_failure_returns_structured_error_and_keeps_serving()
    {
        // 空路径 -> 参数校验失败路径
        var bad = await SendAsync(new VaultRequest { Command = VaultCommand.RegisterRoot, Path = "" });
        Assert.NotNull(bad);
        Assert.False(bad!.Ok);
        Assert.False(string.IsNullOrWhiteSpace(bad.Error));

        // 服务仍然健康
        var ok = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus });
        Assert.NotNull(ok);
        Assert.True(ok!.Ok);
    }

    /// <summary>
    /// 引导命令被放行后仍须通过路径白名单。使用被保护的测试目录验证结构化拒绝，
    /// 不创建共享归档根、不修改真实归档 ACL。完整 ACL 成功路径需独立管理员验收。
    /// </summary>
    [Fact]
    public async Task Bootstrap_register_requires_elevation()
    {
        var response = await SendAsync(new VaultRequest { Command = VaultCommand.RegisterRoot, Path = _scope.Directory });
        Assert.NotNull(response);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Assert.False(response.Ok); Assert.Equal("Access denied.",response.Error); }
        var status = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus });
        Assert.True(status!.Ok);
    }

    [Fact]
    public async Task Bootstrap_set_password_requires_elevation()
    {
        var response = await SendAsync(new VaultRequest { Command = VaultCommand.SetPassword, Password = "Test-Pw-123!" });
        Assert.NotNull(response);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Assert.False(response.Ok); Assert.Equal("Access denied.",response.Error); }
        else Assert.True(response.Ok,response.Error);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 工具
    // ──────────────────────────────────────────────────────────────────────

    private Task<VaultResponse?> SendAsync(VaultRequest request)
        => SendRawAsync(JsonSerializer.Serialize(request));

    private async Task<VaultResponse?> SendRawAsync(string payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var pipe = new NamedPipeClientStream(".", _pipeName,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation, System.IO.HandleInheritability.None);

        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 连不上：在「先模拟后读」缺陷下会走到这里（服务端 accept 了但不响应）。
            Assert.Fail("连接命名管道超时——PipeServer 未在监听或未正确处理连接。");
            return null;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("等待响应超时——服务端接受了连接但没有写出响应。");
            return null;
        }
        catch (IOException ex)
        {
            Assert.Fail($"读取响应时管道断裂（ERROR_BROKEN_PIPE）：{ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(line)) return null;
        return JsonSerializer.Deserialize<VaultResponse>(line);
    }
}

/// <summary>
/// 针对管道 DACL 构造的回归测试。
/// <para>
/// 背景：早期为了修「服务启动即崩」，把显式 PipeSecurity 整个删掉了，
/// 结果服务以 LocalSystem 创建的管道落到「默认」安全描述符，
/// <b>交互式登录用户被拒绝</b>：客户端 CreateFile 返回 ERROR_ACCESS_DENIED (5)，
/// GUI 表现为「服务已安装但未能连接」。
/// 修复必须显式放行 Authenticated Users，否则 GUI（非提权）永远连不上。
/// </para>
/// </summary>
public sealed class PipeSecurityTests
{
    [Fact]
    public async Task System_DACL_allows_GUI_round_trip_but_rejects_client_server_creation()
    {
        // 用生产 SYSTEM 身份构造 DACL，而不是让当前测试用户拿到创建者 FullControl。
        // 首个服务器句柄由创建者持有；客户端每次打开均由内核按此 DACL 检查。
        string pipeName = "SystemDacl_" + Guid.NewGuid().ToString("N");
        var security = PipeServer.BuildPipeSecurity(LocalSystem);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
        var client = PicHarbor.Gui.Vault.VaultClient.QueryStatusOnPipeAsync(pipeName, timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        var request = await reader.ReadLineAsync(timeout.Token);
        Assert.Equal("QueryStatus", JsonDocument.Parse(request!).RootElement.GetProperty("cmd").GetString());
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            Assert.NotNull(identity.User);
        });
        await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"status\":{}}\n"), timeout.Token);
        await server.FlushAsync(timeout.Token);
        var response = await client;
        Assert.True(response!.Ok, response.Error);
        Assert.NotNull(response.Status);

        using var current = WindowsIdentity.GetCurrent();
        if (!PipeServer.IsAuthorizedIdentity(current))
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var impostor = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            });
    }

    [Fact]
    public void Administrative_authorization_matches_effective_token_not_group_list()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(expected, PipeServer.IsAuthorizedIdentity(identity));
    }

    private static readonly SecurityIdentifier AuthenticatedUsers =
        new(WellKnownSidType.AuthenticatedUserSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [Fact]
    public void PipeSecurity_does_not_deny_everyone()
    {
        var rules = PipeServer.BuildPipeSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>();
        Assert.DoesNotContain(rules, r => r.AccessControlType == AccessControlType.Deny
            && r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [Fact]
    public void PipeSecurity_is_protected_and_clients_cannot_create_server_instances()
    {
        var security = PipeServer.BuildPipeSecurity();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(AuthenticatedUsers)
            && (r.PipeAccessRights & (PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions
                | PipeAccessRights.TakeOwnership)) != 0);
    }

    /// <summary>DACL 必须显式放行「已认证用户」——这是 GUI 能连接的前提。</summary>
    [Fact]
    public void PipeSecurity_grants_authenticated_users()
    {
        var security = PipeServer.BuildPipeSecurity();

        bool granted = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                   && r.IdentityReference.Equals(AuthenticatedUsers)
                   && (r.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);

        granted.ShouldBeTrue(
            "管道 DACL 必须显式授予 Authenticated Users 读写，" +
            "否则未提权的 GUI 会 CreateFile ERROR_ACCESS_DENIED (5)。");
    }

    /// <summary>SYSTEM 与 Administrators 必须有完全控制。</summary>
    [Theory]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public void PipeSecurity_grants_full_control_to_privileged_identities(WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        var security = PipeServer.BuildPipeSecurity();

        bool granted = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                   && r.IdentityReference.Equals(sid)
                   && (r.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl);

        granted.ShouldBeTrue($"{sidType} 应被授予管道完全控制。");
    }

    /// <summary>
    /// DACL 构造本身不应依赖特权（构造是纯内存对象），
    /// 这样在非提权环境也能测试，不会因为缺 SeSecurityPrivilege 而失败。
    /// </summary>
    [Fact]
    public void PipeSecurity_can_be_built_without_elevation()
    {
        PipeServer.BuildPipeSecurity().ShouldNotBeNull();
    }
}
