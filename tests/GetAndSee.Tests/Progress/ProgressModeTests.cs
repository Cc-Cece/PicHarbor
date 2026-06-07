using GetAndSee.Core.Progress;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Progress;

public sealed class ProgressModeTests
{
    [Theory]
    [InlineData(false, false, true)]  // interactive, not disabled → dashboard
    [InlineData(true, false, false)]  // --no-dashboard → text
    [InlineData(false, true, false)]  // redirected/piped → text
    [InlineData(true, true, false)]   // both → text
    public void ShouldUseDashboard_picks_dashboard_only_when_interactive_and_enabled(
        bool dashboardDisabled, bool outputRedirected, bool expected)
    {
        ProgressMode.ShouldUseDashboard(dashboardDisabled, outputRedirected).ShouldBe(expected);
    }
}
