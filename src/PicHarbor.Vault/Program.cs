// PicHarbor.Vault — 入口点
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicHarbor.Vault;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<VaultState>();
builder.Services.AddHostedService<PipeServer>();

// 以 Windows 服务方式运行（dotnet publish 后安装为服务）
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PicHarborVault";
});

var host = builder.Build();

// 在 IHost 完全构建后，立刻执行首次收回
// （BackgroundService.ExecuteAsync 里也会再做一次，双重保险）
using (var scope = host.Services.CreateScope())
{
    var state = scope.ServiceProvider.GetRequiredService<VaultState>();
    state.RevokeAllOnStartup();
}

await host.RunAsync();
