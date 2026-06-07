using GetAndSee.Core.Preflight;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Preflight;

public sealed class PreflightPowerTests
{
    [Theory]
    [InlineData(HostPowerStatus.Battery)]
    [InlineData(HostPowerStatus.Ac)]
    [InlineData(HostPowerStatus.Unknown)]
    public void GetHostPowerStatus_uses_injected_provider(HostPowerStatus status)
    {
        var checks = new PreflightChecks(powerStatusProvider: () => status);

        checks.GetHostPowerStatus().ShouldBe(status);
    }
}
