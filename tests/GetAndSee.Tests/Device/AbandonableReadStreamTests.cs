using System.Diagnostics;
using System.Reflection;
using GetAndSee.Core.Device;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Device;

public sealed class AbandonableReadStreamTests
{
    [Fact]
    public async Task Returns_bytes_when_read_completes_before_cancellation()
    {
        var inner = new ControlledReadStream();
        await using var shield = new AbandonableReadStream(inner);
        var buffer = new byte[16];

        ValueTask<int> read = shield.ReadAsync(buffer, TestContext.Current.CancellationToken);
        inner.Release(8);
        int bytes = await read;

        bytes.ShouldBe(8);
        buffer[..8].ShouldAllBe(b => b == 1);
    }

    [Fact]
    public async Task Cancelling_the_token_abandons_the_read_and_throws_OperationCanceled()
    {
        // The run-level forward-progress watchdog trips by cancelling the token. A stuck read must then
        // surface OperationCanceledException promptly instead of blocking on the orphaned native read.
        var inner = new ControlledReadStream();
        await using var shield = new AbandonableReadStream(inner);
        using var cts = new CancellationTokenSource();

        ValueTask<int> read = shield.ReadAsync(new byte[8], cts.Token);
        await inner.Started; // ensure the read is in flight
        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Abandoned_read_disposes_inner_when_it_finally_completes_no_handle_leak()
    {
        // Model the real native read: it ignores the cancellation token, so abandoning it leaves it in
        // flight. The shield must not block on it, must not dispose the handle while it is still running,
        // and must close the handle once the orphaned read finally returns.
        var inner = new ControlledReadStream(observeCancellation: false);
        var shield = new AbandonableReadStream(inner);
        using var cts = new CancellationTokenSource();

        ValueTask<int> read = shield.ReadAsync(new byte[8], cts.Token);
        await inner.Started;
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await read);

        // Disposing the shield after an abandon must not block waiting for the orphaned read.
        await shield.DisposeAsync();
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
        await using var shield = new AbandonableReadStream(inner);
        var buffer = new byte[16];

        ValueTask<int> read = shield.ReadAsync(buffer, TestContext.Current.CancellationToken);
        inner.Release(8);
        int bytes = await read;

        bytes.ShouldBe(8);
        buffer[..8].ShouldAllBe(b => b == 1); // the caller received its bytes

        // The private reusable scratch buffer must retain no device bytes at rest (defense in depth).
        byte[]? scratch = GetScratch(shield);
        scratch.ShouldNotBeNull();
        scratch.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void Synchronous_Read_is_unsupported_so_a_stuck_read_can_always_be_abandoned()
    {
        var inner = new ControlledReadStream();
        using var shield = new AbandonableReadStream(inner);

        // A sync read would block on the inner native read with no way to abandon it; it must be refused.
        Should.Throw<NotSupportedException>(() => shield.Read(new byte[8], 0, 8));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
    }

    private static byte[]? GetScratch(AbandonableReadStream shield)
    {
        FieldInfo field = typeof(AbandonableReadStream)
            .GetField("scratch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[]?)field.GetValue(shield);
    }
}
