using GetAndSee.Core.Device;
using GetAndSee.FakeDevice;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Harness;

/// <summary>
/// Unit tests for the opt-in EXE seam (<see cref="FakeDeviceSpecParser"/> + <see cref="FakeDeviceGate"/>):
/// the <c>GAS_FAKE_DEVICE</c> spec string parses into a working fake, and the gate activates only when the
/// env var is set — so a normal run (no env var) always falls back to the real client.
/// </summary>
public sealed class FakeDeviceSeamTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("small")]
    [InlineData("SMALL")] // case-insensitive
    public void Parses_the_small_library_preset(string spec)
    {
        FakeDeviceSpec result = FakeDeviceSpecParser.Parse(spec);

        result.Files.ShouldNotBeEmpty();
        result.DisconnectAfterOpenCount.ShouldBeNull();
    }

    [Fact]
    public void Parses_the_empty_preset()
    {
        FakeDeviceSpecParser.Parse("empty").Files.ShouldBeEmpty();
    }

    [Fact]
    public void Parses_the_disconnect_after_modifier()
    {
        FakeDeviceSpec result = FakeDeviceSpecParser.Parse("small;disconnect-after=2");

        result.DisconnectAfterOpenCount.ShouldBe(2);
    }

    [Fact]
    public void Parses_the_fail_every_modifier()
    {
        FakeDeviceSpec result = FakeDeviceSpecParser.Parse("small;fail-every");

        result.DefaultReadFault.ShouldNotBeNull();
        result.DefaultReadFault!.Kind.ShouldBe(ReadFaultKind.Empty);
    }

    [Theory]
    [InlineData("bogus")]                    // unknown preset
    [InlineData("small;unknown-mod")]        // unknown modifier
    [InlineData("small;disconnect-after=x")] // non-numeric count
    [InlineData("small;disconnect-after=-1")] // negative count
    public void Rejects_malformed_specs(string spec)
    {
        Should.Throw<FormatException>(() => FakeDeviceSpecParser.Parse(spec));
    }

    [Fact]
    public async Task A_parsed_spec_builds_a_connectable_device()
    {
        using FakeAfcDevice device = FakeDeviceSpecParser.Parse("small").Build();
        await device.ConnectAsync(Token);

        device.Device.ShouldNotBeNull();
        IReadOnlyList<string> dcim = await device.ListDirectoryAsync(DcimEnumerator.DefaultRoot, Token);
        dcim.ShouldNotBeEmpty();
    }

    [Fact]
    public void Gate_is_inactive_and_returns_null_when_the_env_var_is_unset()
    {
        WithEnv(null, () =>
        {
            FakeDeviceGate.IsActive.ShouldBeFalse();
            FakeDeviceGate.TryCreate().ShouldBeNull();
        });
    }

    [Fact]
    public void Gate_is_active_and_builds_the_fake_when_the_env_var_is_set()
    {
        WithEnv("small", () =>
        {
            FakeDeviceGate.IsActive.ShouldBeTrue();
            IPhoneClient? client = FakeDeviceGate.TryCreate();
            client.ShouldNotBeNull();
            client.ShouldBeOfType<FakeAfcDevice>();
        });
    }

    [Fact]
    public void Gate_propagates_a_clear_error_for_a_malformed_env_spec()
    {
        WithEnv("totally-bogus", () => Should.Throw<FormatException>(() => FakeDeviceGate.TryCreate()));
    }

    private static void WithEnv(string? value, Action body)
    {
        string? original = Environment.GetEnvironmentVariable(FakeDeviceGate.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FakeDeviceGate.EnvironmentVariable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeDeviceGate.EnvironmentVariable, original);
        }
    }
}
