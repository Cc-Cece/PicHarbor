using GetAndSee.Core.Errors;
using GetAndSee.FakeDevice;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Harness;

/// <summary>
/// Unit tests for the one fault model (<see cref="ScriptedReadStream"/>) that folds in the three earlier
/// ad-hoc fakes. Each managed-observable read fault is injected deterministically and asserted directly,
/// preserving the exact park and spinning-close repros while delivering byte-faithful content otherwise.
/// </summary>
public sealed class ScriptedReadStreamTests
{
    private const int Seed = 12345;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_fault_delivers_the_full_content_then_clean_eof()
    {
        int size = (200 * 1024) + 7;
        ScriptedReadStream stream = new(Seed, size);

        byte[] read = await ReadAllAsync(stream);

        read.ShouldBe(FakeContent.Materialize(size, Seed));
    }

    [Fact]
    public async Task Slow_delivers_full_content_but_in_small_chunks()
    {
        int size = 200 * 1024; // > the 64 KiB slow-chunk cap, so several reads are needed.
        ScriptedReadStream stream = new(Seed, size, ReadFault.Slow);

        // A single read returns no more than the slow cap, even with a large buffer.
        int first = await stream.ReadAsync(new byte[size], Token);
        first.ShouldBe(64 * 1024);

        byte[] rest = await ReadAllAsync(stream);
        (first + rest.Length).ShouldBe(size);
    }

    [Fact]
    public async Task Empty_returns_zero_on_the_first_read()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 4096, ReadFault.Empty);

        int read = await stream.ReadAsync(new byte[4096], Token);

        read.ShouldBe(0);
    }

    [Fact]
    public async Task Premature_eof_short_returns_before_the_declared_size()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 5 * 1024 * 1024, ReadFault.PrematureEofAfter(1024));

        byte[] read = await ReadAllAsync(stream);

        read.Length.ShouldBe(1024); // short of the declared 5 MiB
        read.ShouldBe(FakeContent.Materialize(1024, Seed));
    }

    [Fact]
    public async Task Park_delivers_a_prefix_then_blocks_on_a_real_task_until_released()
    {
        const int prefix = 64 * 1024;
        ScriptedReadStream stream = new(Seed, declaredSize: 5 * 1024 * 1024, ReadFault.ParkAfter(prefix));

        Task<byte[]> readAll = ReadAllAsync(stream);
        await stream.ParkedReadStarted;
        readAll.IsCompleted.ShouldBeFalse("the parked read must really block, not resolve immediately");

        stream.ReleasePark();           // the orphaned read finally returns empty
        byte[] read = await readAll;
        read.Length.ShouldBe(prefix);   // only the prefix was delivered before the park
    }

    [Fact]
    public async Task Park_observing_cancellation_unblocks_when_the_token_is_cancelled()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 4096, ReadFault.ParkAfter(0, observeCancellation: true));
        using CancellationTokenSource cts = new();

        ValueTask<int> read = stream.ReadAsync(new byte[16], cts.Token);
        await stream.ParkedReadStarted;
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
    }

    [Fact]
    public async Task Connection_fatal_delivers_a_prefix_then_throws_connection_lost()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 5 * 1024 * 1024, ReadFault.ConnectionFatalAfter(2048));

        await Should.ThrowAsync<DeviceConnectionLostException>(async () => await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Per_file_error_delivers_a_prefix_then_throws_a_non_fatal_device_error()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 5 * 1024 * 1024, ReadFault.PerFileErrorAfter(2048));

        DeviceException error = await Should.ThrowAsync<DeviceException>(async () => await ReadAllAsync(stream));
        error.ShouldNotBeOfType<DeviceConnectionLostException>(); // a single-file error, not connection-fatal
    }

    [Fact]
    public async Task Spin_on_dispose_returns_premature_eof_then_busy_spins_until_released()
    {
        const int prefix = 1024 * 1024;
        ScriptedReadStream stream = new(Seed, declaredSize: 5 * 1024 * 1024, ReadFault.SpinOnDisposeAfter(prefix));

        byte[] read = await ReadAllAsync(stream); // delivers the prefix then a premature zero-byte read
        read.Length.ShouldBe(prefix);

        Task dispose = Task.Run(() => stream.Dispose(), Token); // the synchronous close-spin blocks a thread
        await stream.DisposeSpinStarted;
        dispose.IsCompleted.ShouldBeFalse("the close must busy-spin until released (the #45 core-pin shape)");

        stream.ReleaseSpin();
        await dispose;
        stream.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void Synchronous_read_and_all_writes_are_refused_so_the_stream_stays_async_and_read_only()
    {
        ScriptedReadStream stream = new(Seed, declaredSize: 16);

        Should.Throw<NotSupportedException>(() => stream.Read(new byte[16], 0, 16));
        Should.Throw<NotSupportedException>(() => stream.Write(new byte[16], 0, 16));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream sink = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, Token)) > 0)
        {
            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }
}
