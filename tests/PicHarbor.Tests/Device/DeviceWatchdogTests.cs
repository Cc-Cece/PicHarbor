using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Device;
using PicHarbor.Core.Errors;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Device;

/// <summary>
/// Exercises the shared abandon-on-timeout watchdog that guards every blocking native AFC call
/// (open/stat/list/read). These tests park a real blocking call on a worker thread — the actual #25
/// failure mode — rather than mocking a stalling read, then drive the timeout with a fake clock.
/// </summary>
public sealed class DeviceWatchdogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_the_value_when_the_native_call_completes_before_the_timeout()
    {
        FakeTimeProvider clock = new();

        int value = await DeviceWatchdog.RunWithTimeoutAsync(() => 42, Timeout, clock, Token);

        value.ShouldBe(42);
    }

    [Fact]
    public async Task A_parked_native_call_times_out_with_DeviceStallException()
    {
        // THE #25 failure mode: a blocking native call (e.g. afc_file_open) parks forever on a
        // disconnect. Sprint 2 only guarded the byte read of an already-open file; this guards the
        // parked CALL itself. The worker thread blocks on the gate exactly as it would inside a hung
        // afc_file_open, so only the watchdog timeout can free the caller.
        FakeTimeProvider clock = new();
        TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> op = DeviceWatchdog.RunWithTimeoutAsync(
            () => gate.Task.GetAwaiter().GetResult(), Timeout, clock, Token);

        // The delay timer is already registered (the method runs synchronously up to WhenAny), so
        // advancing the fake clock fires the timeout deterministically — no wall-clock wait.
        clock.Advance(Timeout);

        DeviceStallException ex = await Should.ThrowAsync<DeviceStallException>(async () => await op);
        ex.Message.ShouldBe(DeviceStallException.DefaultMessage);

        gate.SetResult(0); // let the orphaned worker unwind
    }

    [Fact]
    public async Task An_abandoned_call_that_returns_late_disposes_its_result_so_no_handle_leaks()
    {
        FakeTimeProvider clock = new();
        TaskCompletionSource<TrackedHandle> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackedHandle handle = new();

        Task<TrackedHandle> op = DeviceWatchdog.RunWithTimeoutAsync(
            () => gate.Task.GetAwaiter().GetResult(),
            Timeout,
            clock,
            Token,
            onAbandoned: static settled =>
            {
                if (settled.Status == TaskStatus.RanToCompletion)
                {
                    settled.Result.Dispose();
                }
            });

        clock.Advance(Timeout);
        await Should.ThrowAsync<DeviceStallException>(async () => await op);
        handle.IsDisposed.ShouldBeFalse("the handle must not be disposed while the orphaned call is still parked");

        // The orphaned native open finally returns its handle → the abandon hook closes it (no leak).
        gate.SetResult(handle);
        await WaitForAsync(() => handle.IsDisposed, TimeSpan.FromSeconds(5));
        handle.IsDisposed.ShouldBeTrue("the abandoned call's late result must be disposed so the AFC handle is not leaked");
    }

    [Fact]
    public async Task Caller_cancellation_throws_OperationCanceled_not_stall()
    {
        FakeTimeProvider clock = new();
        TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new();

        Task<int> op = DeviceWatchdog.RunWithTimeoutAsync(
            () => gate.Task.GetAwaiter().GetResult(), TimeSpan.FromSeconds(30), clock, cts.Token);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await op);
        gate.SetResult(0);
    }

    [Fact]
    public async Task A_per_file_native_error_propagates_unchanged_and_is_not_turned_into_a_stall()
    {
        // A normal device error (one unreadable file) must surface as-is so the copier can fail just
        // that file and continue — only a real timeout becomes a DeviceStallException.
        FakeTimeProvider clock = new();

        DeviceException ex = await Should.ThrowAsync<DeviceException>(async () =>
            await DeviceWatchdog.RunWithTimeoutAsync<int>(
                () => throw new DeviceException("one bad file"), Timeout, clock, Token));

        ex.ShouldBeOfType<DeviceException>(); // not a DeviceStallException
        ex.Message.ShouldBe("one bad file");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
    }

    private sealed class TrackedHandle : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
