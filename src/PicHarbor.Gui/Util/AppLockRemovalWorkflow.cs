namespace PicHarbor.Gui.Util;

/// <summary>Restores protected directories before removing their only recovery mechanism.</summary>
internal static class AppLockRemovalWorkflow
{
    internal static async Task<(bool ok, string message)> RunAsync(
        Func<Task<(bool ok, string message)>> disable,
        Action recordDisabled,
        Func<Task<(bool ok, string message)>> uninstall,
        Func<Task<(bool ok, string message)>> purge)
    {
        var result = await disable();
        if (!result.ok) return result;
        recordDisabled();
        result = await uninstall();
        if (!result.ok) return result;
        return await purge();
    }
}
