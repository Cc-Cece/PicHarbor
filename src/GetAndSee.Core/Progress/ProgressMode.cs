namespace GetAndSee.Core.Progress;

/// <summary>Decides which progress reporter a run should use.</summary>
public static class ProgressMode
{
    /// <summary>
    /// Returns <see langword="true"/> when the live dashboard should be used: the user has not passed
    /// <c>--no-dashboard</c> and the output is an interactive terminal (not redirected or piped).
    /// </summary>
    /// <param name="dashboardDisabled">Whether <c>--no-dashboard</c> was supplied.</param>
    /// <param name="outputRedirected">Whether console output is redirected/piped (non-interactive).</param>
    /// <returns><see langword="true"/> to use the live dashboard; <see langword="false"/> to use the text fallback.</returns>
    public static bool ShouldUseDashboard(bool dashboardDisabled, bool outputRedirected) =>
        !dashboardDisabled && !outputRedirected;
}
