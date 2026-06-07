using System.Diagnostics;
using System.Reflection;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Device;

public sealed class WatchdogReadStreamTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Returns_bytes_when_read_completes_before_timeout()
    {
        var inner = new ControlledReadStream();
        await using var watchdog = new WatchdogReadStream(inner, TimeSpan.FromSeconds(5));
        var buffer = new byte[16];

        ValueTask<int> read = watchdog.ReadAsync(buffer, TestContext.Current.CancellationToken);
        inner.Release(8);
        int bytes = await read;

        bytes.ShouldBe(8);
        buffer[..8].ShouldAllBe(b => b == 1);
    }

    [Fact]
    public async Task Throws_DeviceStallException_when_no_bytes_within_timeout()
    {
        var inner = new ControlledReadStream();
        await using var watchdog = new WatchdogReadStream(inner, ShortTimeout);
        var buffer = new byte[16];

        var stopwatch = Stopwatch.StartNew();
        // Never release the read → it stalls.
        await Should.ThrowAsync<DeviceStallException>(async () =>
            await watchdog.ReadAsync(buffer, TestContext.Current.CancellationToken));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stall_message_is_the_actionable_copy()
    {
        var inner = new ControlledReadStream();
        await using var watchdog = new WatchdogReadStream(inner, ShortTimeout);

        DeviceStallException ex = await Should.ThrowAsync<DeviceStallException>(async () =>
            await watchdog.ReadAsync(new byte[8], TestContext.Current.CancellationToken));

        ex.Message.ShouldBe(DeviceStallException.DefaultMessage);
    }

    [Fact]
    public async Task Caller_cancellation_throws_OperationCanceled_not_stall()
    {
        var inner = new ControlledReadStream();
        await using var watchdog = new WatchdogReadStream(inner, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        ValueTask<int> read = watchdog.ReadAsync(new byte[8], cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
    }

    [Fact]
    public async Task Abandoned_read_disposes_inner_when_it_finally_completes_no_handle_leak()
    {
        var inner = new ControlledReadStream();
        var watchdog = new WatchdogReadStream(inner, ShortTimeout);

        await Should.ThrowAsync<DeviceStallException>(async () =>
            await watchdog.ReadAsync(new byte[8], TestContext.Current.CancellationToken));

        // Disposing the watchdog after a stall must not block waiting for the orphaned read.
        await watchdog.DisposeAsync();
        inner.IsDisposed.ShouldBeFalse("inner must not be disposed while the orphaned read is still in flight");

        // When the orphaned native read finally returns, the inner stream (native handle) is closed.
        inner.Release(0);
        await WaitForAsync(() => inner.IsDisposed, TimeSpan.FromSeconds(5));
        inner.IsDisposed.ShouldBeTrue("the orphaned read completing must close the native handle (no leak)");
    }

    [Fact]
    public async Task Scratch_buffer_is_zeroed_after_a_read_so_no_device_bytes_linger()
    {
        var inner = new ControlledReadStream();
        await using var watchdog = new WatchdogReadStream(inner, TimeSpan.FromSeconds(5));
        var buffer = new byte[16];

        ValueTask<int> read = watchdog.ReadAsync(buffer, TestContext.Current.CancellationToken);
        inner.Release(8);
        int bytes = await read;

        bytes.ShouldBe(8);
        buffer[..8].ShouldAllBe(b => b == 1); // the caller received its bytes

        // The private reusable scratch buffer must retain no device bytes at rest (defense in depth).
        byte[]? scratch = GetScratch(watchdog);
        scratch.ShouldNotBeNull();
        scratch.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void Synchronous_Read_is_unsupported_so_it_cannot_bypass_the_watchdog()
    {
        var inner = new ControlledReadStream();
        using var watchdog = new WatchdogReadStream(inner, ShortTimeout);

        // A sync read would skip the inactivity timeout entirely; it must be refused, not fall through.
        Should.Throw<NotSupportedException>(() => watchdog.Read(new byte[8], 0, 8));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
    }

    private static byte[]? GetScratch(WatchdogReadStream watchdog)
    {
        FieldInfo field = typeof(WatchdogReadStream)
            .GetField("scratch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[]?)field.GetValue(watchdog);
    }
}
