using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Transfer;
using GetAndSee.Tests.TestSupport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

/// <summary>
/// Phase 0 diagnosis of the #42 intra-file unplug spin. These tests characterize the <i>gap</i> in the
/// pre-3.3 code so the mechanism is on the record (see <c>docs/sprint-3.3/progress.md</c>): a device read
/// that returns <b>fast</b> (a premature <c>Success</c>+0) makes no forward progress yet bypasses the
/// per-read inactivity watchdog, and the run is not stopped at the run level — it is only ever handled as
/// a per-file failure. Sprint 3.3 adds a run-level forward-progress watchdog that closes this gap; the
/// manifestation tests that assert the fixed behavior live in <c>ForwardProgressWatchdogTests</c>
/// and the 3.3 additions to <c>FileCopierTests</c>.
/// </summary>
public sealed class Sprint33DiagnosisTests : IDisposable
{
    private readonly TempDirectory destination = new();
    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer = new();
    private readonly IMediaMetadataExtractor extractor = Substitute.For<IMediaMetadataExtractor>();
    private readonly IPhoneClient client = Substitute.For<IPhoneClient>();

    public Sprint33DiagnosisTests()
    {
        journal = TransferJournal.Open(destination.Path);
        extractor.Extract(Arg.Any<string>())
            .Returns(new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone"));
    }

    public void Dispose()
    {
        journal.Dispose();
        destination.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A device read modeled at the AfcReadStream boundary: overrides ONLY sync Read, so the async path
    // flows through base Stream.ReadAsync -> threadpool -> sync read, exactly like production.
    private sealed class FastZeroByteStream : Stream
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return 0; // premature EOF: the device returns fast with zero bytes (a yanked-cable signature)
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Fast_zero_byte_read_bypasses_the_per_read_watchdog_timer()
    {
        // The per-read watchdog only arms when a read does not complete promptly; a fast Success+0 sails
        // straight through. With a 10-MINUTE read timeout, a fast zero-byte read must still return at once
        // (the timer never materializes) rather than blocking — proving the per-read timer cannot see a
        // fast no-progress read. This is the structural escape hatch behind #42.
        FastZeroByteStream stream = new();
        client.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stream));

        RemoteFile file = new("/DCIM/100APPLE/IMG_6834.MOV", 392_323_980L, null);
        journal.EnsurePending(file);
        FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromMinutes(10));

        CopyResult result = await copier.CopyAsync(file, Token);

        // Pre-3.3 behavior: handled only as a per-file failure (size mismatch), never a run-level stop.
        result.Status.ShouldBe(CopyStatus.Failed);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("size mismatch");
        stream.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Premature_zero_byte_read_leaves_the_file_resumable_and_publishes_no_partial()
    {
        // Whatever the disconnect detection does, the data-safety invariants must already hold (they have
        // through all four manifestations): the in-flight file is left non-done (resumable) and no
        // .partial is ever promoted into the final tree.
        FastZeroByteStream stream = new();
        client.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stream));

        RemoteFile file = new("/DCIM/100APPLE/IMG_6834.MOV", 392_323_980L, null);
        journal.EnsurePending(file);
        FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromSeconds(30));

        await copier.CopyAsync(file, Token);

        journal.GetState(file.Path, file.Size).ShouldNotBe(FileState.Done);
        Directory.GetFiles(destination.Path, "*.MOV", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
