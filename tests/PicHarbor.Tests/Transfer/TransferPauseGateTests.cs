using PicHarbor.Core.Transfer;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Transfer;

public sealed class TransferPauseGateTests
{
    [Fact]
    public async Task Pause_waits_for_a_checkpoint_and_resume_releases_the_same_task()
    {
        var gate = new TransferPauseGate();
        gate.Pause();
        gate.IsPauseRequested.ShouldBeTrue();
        gate.IsPaused.ShouldBeFalse();
        var wait = gate.WaitAsync(TestContext.Current.CancellationToken);
        gate.IsPaused.ShouldBeTrue();
        wait.IsCompleted.ShouldBeFalse();
        gate.Resume();
        await wait;
        gate.IsPaused.ShouldBeFalse();
        gate.IsPauseRequested.ShouldBeFalse();
        await gate.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_cancels_a_paused_wait_and_does_not_need_resume()
    {
        var gate = new TransferPauseGate();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        gate.Pause();
        var wait = gate.WaitAsync(stop.Token);
        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => wait);
        gate.Resume();
        await gate.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Repeated_pause_resume_cycles_preserve_all_remaining_work()
    {
        var gate = new TransferPauseGate();
        var completed = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            gate.Pause();
            gate.Pause();
            async Task Work()
            {
                await gate.WaitAsync(TestContext.Current.CancellationToken);
                completed.Add(i);
            }
            var task = Work();
            completed.Count.ShouldBe(i);
            gate.Resume();
            await task;
        }
        completed.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Resuming_before_checkpoint_does_not_leave_a_stale_pause()
    {
        var gate = new TransferPauseGate();
        gate.Pause();
        gate.Resume();
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        gate.IsPaused.ShouldBeFalse();
    }
}
