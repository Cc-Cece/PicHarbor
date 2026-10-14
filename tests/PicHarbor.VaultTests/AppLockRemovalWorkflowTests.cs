using PicHarbor.Gui.Util;
using Xunit;

namespace PicHarbor.VaultTests;

public sealed class AppLockRemovalWorkflowTests
{
    [Theory]
    [InlineData("", "restore,record-disabled,uninstall,purge", true)]
    [InlineData("restore", "restore", false)]
    [InlineData("uninstall", "restore,record-disabled,uninstall", false)]
    [InlineData("purge", "restore,record-disabled,uninstall,purge", false)]
    public async Task Removal_preserves_recovery_data_until_permissions_are_restored(string failAt, string expected, bool success)
    {
        var calls = new List<string>();
        Task<(bool ok, string message)> Step(string name)
        {
            calls.Add(name);
            return Task.FromResult((name != failAt, name));
        }
        var result = await AppLockRemovalWorkflow.RunAsync(
            () => Step("restore"), () => calls.Add("record-disabled"),
            () => Step("uninstall"), () => Step("purge"));
        Assert.Equal(success, result.ok);
        Assert.Equal(expected, string.Join(",", calls));
    }
}
