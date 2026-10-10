using System.Security.Cryptography;
using System.Text;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Reorganize;
using PicHarbor.Core.Summary;
using PicHarbor.Core.Util;
using PicHarbor.FakeDevice;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Reorganize;

/// <summary>
/// End-to-end tests for the <see cref="Reorganizer"/> engine: build a real archive with the
/// <see cref="CopyPipelineHarness"/> + <see cref="FakeAfcDevice"/> (no hardware), then move it between
/// layouts on disk and assert the sacred invariants — byte-identity, crash-resume, deterministic collisions,
/// journal accuracy, and cleanup. Integrity dominates because this is the first thing in the product that
/// moves files on the PC.
/// </summary>
public sealed class ReorganizerTests
{
    private static readonly DateTimeOffset Aug2024 = new(2024, 8, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sep2024 = new(2024, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Area 1 + 8: byte-integrity and journal accuracy ----------------------------------------------

    [Fact]
    public async Task Reorganize_preserves_every_byte_and_leaves_the_journal_accurate()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        List<string> before = ShaMultiset(dest.Path);

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Month);

        report.Failed.ShouldBe(0);
        report.Moved.ShouldBeGreaterThan(0);
        // Byte-identity: the exact multiset of file hashes is conserved — nothing lost, changed, or added.
        ShaMultiset(dest.Path).ShouldBe(before);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
        using TransferJournal journal = TransferJournal.OpenReadOnly(dest.Path);
        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.Month);
        journal.GetReorganizeTarget().ShouldBeNull(); // marker cleared on a clean run
    }

    // ---- Area 2 (HIGHEST): crash-resume between the File.Move and the journal update -------------------

    [Fact]
    public async Task A_move_interrupted_before_the_journal_update_is_healed_on_resume()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        List<string> before = ShaMultiset(dest.Path);

        // Simulate the exact crash window: one file is physically moved to its target, but the process died
        // before UpdateDestPath ran — so the journal still points at the old path and the marker is set.
        PlannedMove pending = FirstPlannedMove(dest.Path, OrganizeScheme.Month);
        using (TransferJournal journal = TransferJournal.Open(dest.Path))
        {
            journal.SetReorganizeTarget(OrganizeScheme.Month);
        }

        MovePhysicallyOnly(dest.Path, pending);

        // Resume: the reconcile path must detect the file already at its target (source gone, size matches)
        // and heal the journal, then finish the rest — byte-identical, nothing double-moved or lost.
        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Month);

        report.Failed.ShouldBe(0);
        ShaMultiset(dest.Path).ShouldBe(before);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
        using TransferJournal check = TransferJournal.OpenReadOnly(dest.Path);
        check.GetReorganizeTarget().ShouldBeNull();
        check.GetOrganizeScheme().ShouldBe(OrganizeScheme.Month);
    }

    [Fact]
    public async Task Resuming_after_a_subset_of_moves_finishes_identically_to_a_clean_run()
    {
        using TempDirectory dest = new();
        using TempDirectory reference = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        await BuildArchive(reference.Path, spec, OrganizeScheme.YearMonth);

        // Reference: a single clean reorganize to month.
        Reorganize(reference.Path, OrganizeScheme.Month);
        Dictionary<string, string> referenceLayout = RelativePathToSha(reference.Path);

        // Under test: apply the first two moves for real (move + journal update), set the marker, leave the
        // rest — a clean interruption partway — then resume.
        ApplyFirstMovesForReal(dest.Path, OrganizeScheme.Month, take: 2);
        ReorganizeReport resume = Reorganize(dest.Path, OrganizeScheme.Month);

        resume.Failed.ShouldBe(0);
        // Interruption point is invisible in the outcome: same files at the same paths with the same bytes.
        RelativePathToSha(dest.Path).ShouldBe(referenceLayout);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
    }

    // ---- Area 3: round-trip -----------------------------------------------------------------------------

    [Fact]
    public async Task Round_trip_year_month_to_month_and_back_restores_the_original_layout()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        Dictionary<string, string> original = RelativePathToSha(dest.Path);

        Reorganize(dest.Path, OrganizeScheme.Month);
        Reorganize(dest.Path, OrganizeScheme.YearMonth);

        // Every file is back at its original relative path with identical bytes.
        RelativePathToSha(dest.Path).ShouldBe(original);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
    }

    [Fact]
    public async Task Round_trip_through_a_coarsening_scheme_still_restores_the_original_layout()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        Dictionary<string, string> original = RelativePathToSha(dest.Path);

        // year-month → year (coarsens) → month → year-month. No name collisions in this library, so the
        // round-trip is exact.
        Reorganize(dest.Path, OrganizeScheme.Year);
        Reorganize(dest.Path, OrganizeScheme.Month);
        Reorganize(dest.Path, OrganizeScheme.YearMonth);

        RelativePathToSha(dest.Path).ShouldBe(original);
    }

    // ---- Area 4: deterministic new collisions -----------------------------------------------------------

    [Fact]
    public async Task Coarsening_creates_a_deterministic_numeric_suffix_stable_across_reruns()
    {
        using TempDirectory dest = new();
        // Two same-named files from different months collide when coarsened to a single year folder.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/101APPLE/IMG_0001.HEIC", Sep2024);
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);

        Reorganize(dest.Path, OrganizeScheme.Year);

        // Deterministic by source_path order: 100APPLE keeps the plain name, 101APPLE gets _2.
        File.Exists(Path.Combine(dest.Path, "2024", "IMG_0001.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, "2024", "IMG_0001_2.HEIC")).ShouldBeTrue();
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
        Dictionary<string, string> afterFirst = RelativePathToSha(dest.Path);

        // Re-running to the same scheme is an idempotent no-op — the suffix does not drift.
        ReorganizeReport second = Reorganize(dest.Path, OrganizeScheme.Year);
        second.Moved.ShouldBe(0);
        RelativePathToSha(dest.Path).ShouldBe(afterFirst);
    }

    [Fact]
    public async Task A_collided_move_interrupted_before_journaling_heals_to_the_same_suffix()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/101APPLE/IMG_0001.HEIC", Sep2024);
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        List<string> before = ShaMultiset(dest.Path);

        // The SECOND (suffixed) mover is physically moved to its `_2` target but not yet journaled.
        ReorganizePlan plan = ComputePlan(dest.Path, OrganizeScheme.Year);
        PlannedMove suffixed = plan.Moves.First(move => move.TargetDestPath.Contains("_2"));
        using (TransferJournal journal = TransferJournal.Open(dest.Path))
        {
            journal.SetReorganizeTarget(OrganizeScheme.Year);
        }

        MovePhysicallyOnly(dest.Path, suffixed);

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Year);

        report.Failed.ShouldBe(0);
        ShaMultiset(dest.Path).ShouldBe(before);
        // The healed file kept the SAME deterministic _2 suffix (no third-level bump, no duplicate).
        File.Exists(Path.Combine(dest.Path, "2024", "IMG_0001_2.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, "2024", "IMG_0001_3.HEIC")).ShouldBeFalse();
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
    }

    // ---- Area 5: no-op ----------------------------------------------------------------------------------

    [Fact]
    public async Task Reorganize_to_the_current_scheme_is_a_no_op()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.Month);
        Dictionary<string, string> before = RelativePathToSha(dest.Path);

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Month);

        report.Moved.ShouldBe(0);
        report.Failed.ShouldBe(0);
        report.AlreadyPlaced.ShouldBe(spec.Files.Count);
        RelativePathToSha(dest.Path).ShouldBe(before); // disk untouched
    }

    // ---- Area 6: unsorted never moves; Live-Photo pair co-locates and moves together -------------------

    [Fact]
    public async Task Unsorted_files_never_move_and_a_live_photo_pair_moves_together()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_AUG.HEIC", Aug2024)
            .AddLivePhotoPair("/DCIM/101APPLE/IMG_LIVE", Sep2024)
            .AddUnsorted("/DCIM/100APPLE/NODATE.DAT");
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);

        Reorganize(dest.Path, OrganizeScheme.Month);

        // Unsorted is identical under every scheme, so it never moved.
        File.Exists(Path.Combine(dest.Path, "unsorted", "NODATE.DAT")).ShouldBeTrue();
        // The Live-Photo still + clip share a capture date, so both landed in the same month folder.
        File.Exists(Path.Combine(dest.Path, "2024-09", "IMG_LIVE.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, "2024-09", "IMG_LIVE.MOV")).ShouldBeTrue();
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
    }

    [Fact]
    public void A_file_recorded_as_unsorted_never_moves_even_if_its_stored_date_resolves_sane()
    {
        using TempDirectory dest = new();
        // A file copy placed in unsorted/ (no trustworthy date at the time — e.g. a device clock skew that
        // put its capture time beyond copy's "now + 1 day" sanity bound), but whose stored timestamp now
        // resolves as a sane 2024 date. Reorganize must respect copy's original decision and leave it put —
        // never re-classify unsorted → dated (which would make placement clock-dependent).
        RemoteFile file = new("/DCIM/100APPLE/SKEWED.HEIC", 10, new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero));
        using (TransferJournal journal = TransferJournal.Open(dest.Path))
        {
            journal.EnsurePending(file);
            journal.MarkDone(
                file.Path, file.Size, Path.Combine(DateFolderOrganizer.UnsortedFolder, "SKEWED.HEIC"),
                new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, null, null), DateTimeOffset.UtcNow);
        }

        ReorganizePlan plan = ComputePlan(dest.Path, OrganizeScheme.YearMonth);

        plan.Moves.ShouldBeEmpty();
        plan.AlreadyPlaced.ShouldBe(1);
    }

    [Fact]
    public async Task A_foreign_file_blocking_a_target_fails_that_move_without_clobbering()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", Aug2024);
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);

        // Pre-place a foreign (user's own) file exactly where the single planned move wants to land.
        PlannedMove move = FirstPlannedMove(dest.Path, OrganizeScheme.Month);
        string targetAbs = Path.Combine(dest.Path, move.TargetDestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetAbs)!);
        byte[] foreign = Encoding.UTF8.GetBytes("a user's own file — must never be clobbered");
        File.WriteAllBytes(targetAbs, foreign);

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Month);

        report.Failed.ShouldBe(1);
        report.Moved.ShouldBe(0);
        File.ReadAllBytes(targetAbs).ShouldBe(foreign, "the foreign file must not be overwritten");
        using (TransferJournal journal = TransferJournal.OpenReadOnly(dest.Path))
        {
            // The row is never dropped — still done at its original path — and the run stays resumable:
            // the marker is left set (so copy refuses) and the recorded scheme is unchanged.
            journal.GetState(spec.Files[0].Path, spec.Files[0].Size).ShouldBe(FileState.Done);
            journal.EnumerateDoneForReorganize().ShouldHaveSingleItem().DestPath.ShouldBe(move.CurrentDestPath);
            journal.GetReorganizeTarget().ShouldBe(OrganizeScheme.Month);
            journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.YearMonth);
        }

        // Once the blocker is removed, a re-run completes cleanly and finalizes the migration.
        File.Delete(targetAbs);
        ReorganizeReport rerun = Reorganize(dest.Path, OrganizeScheme.Month);
        rerun.Failed.ShouldBe(0);
        rerun.Moved.ShouldBe(1);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
        using (TransferJournal journal = TransferJournal.OpenReadOnly(dest.Path))
        {
            journal.GetReorganizeTarget().ShouldBeNull();
            journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.Month);
        }
    }

    // ---- Area 7: long path (R6) -------------------------------------------------------------------------

    [Fact]
    public async Task Reorganize_moves_a_file_through_a_path_beyond_max_path()
    {
        using TempDirectory dest = new();
        // A 230-char leaf pushes the nested year-month destination past the legacy 260-char limit.
        string longLeaf = new string('L', 230) + ".HEIC";
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto($"/DCIM/100APPLE/{longLeaf}", Aug2024);
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        List<string> before = ShaMultiset(dest.Path);

        // Reorganize to flat — the source is a > 260 nested path, so both endpoints exercise \\?\.
        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Flat);

        report.Failed.ShouldBe(0);
        ShaMultiset(dest.Path).ShouldBe(before);
        string finalExtended = LongPath.ToExtended(Path.Combine(dest.Path, longLeaf));
        File.Exists(finalExtended).ShouldBeTrue();
        // Remove the > 260 file via the extended path so TempDirectory (non-extended) cleanup succeeds.
        File.Delete(finalExtended);
    }

    // ---- Area 11: empty-folder cleanup ------------------------------------------------------------------

    [Fact]
    public async Task Emptied_scheme_folders_are_removed_and_protected_entries_are_kept()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_AUG.HEIC", Aug2024)
            .AddUnsorted("/DCIM/100APPLE/NODATE.DAT");
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        Directory.Exists(Path.Combine(dest.Path, "2024", "2024-08")).ShouldBeTrue();

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.Month);

        report.DirectoriesRemoved.ShouldBeGreaterThan(0);
        // The emptied nested folders are gone…
        Directory.Exists(Path.Combine(dest.Path, "2024", "2024-08")).ShouldBeFalse();
        Directory.Exists(Path.Combine(dest.Path, "2024")).ShouldBeFalse();
        // …and everything that must survive does.
        Directory.Exists(Path.Combine(dest.Path, "unsorted")).ShouldBeTrue();
        Directory.Exists(Path.Combine(dest.Path, "2024-08")).ShouldBeTrue();
        File.Exists(TransferJournal.ResolveDatabasePath(dest.Path)).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, SummaryWriter.FileName)).ShouldBeTrue();
    }

    // ---- Cancellation: side-effect-bounded and resumable -----------------------------------------------

    [Fact]
    public async Task Cancellation_before_any_move_leaves_the_archive_resumable()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dest.Path, spec, OrganizeScheme.YearMonth);
        List<string> before = ShaMultiset(dest.Path);

        using CancellationTokenSource cts = new();
        cts.Cancel();
        using (TransferJournal journal = TransferJournal.Open(dest.Path))
        {
            Reorganizer reorganizer = new(journal, new DateFolderOrganizer(), dest.Path);
            ReorganizePlan plan = reorganizer.Plan(OrganizeScheme.Month);
            Should.Throw<OperationCanceledException>(() => reorganizer.Execute(plan, cts.Token));
        }

        // No file moved; the in-flight marker is set (resumable). A fresh run completes byte-identical.
        ShaMultiset(dest.Path).ShouldBe(before);
        using (TransferJournal journal = TransferJournal.OpenReadOnly(dest.Path))
        {
            journal.GetReorganizeTarget().ShouldBe(OrganizeScheme.Month);
        }

        Reorganize(dest.Path, OrganizeScheme.Month).Failed.ShouldBe(0);
        ShaMultiset(dest.Path).ShouldBe(before);
        AssertJournalMatchesDiskAndContent(dest.Path, spec);
    }

    // ---- Area 9: offline / read-only --------------------------------------------------------------------

    [Fact]
    public void The_engine_has_no_device_dependency()
    {
        // Offline by construction: the reorganize engine must never hold or accept the device client, so it
        // cannot make an AFC call. (The whole-assembly device-write scan lives in ReadOnlyContractTests.)
        Type client = typeof(PicHarbor.Core.Device.IPhoneClient);
        Type engine = typeof(Reorganizer);

        engine.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => client.IsAssignableFrom(parameter.ParameterType))
            .ShouldBeFalse("no constructor parameter may be the device client");
        engine.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Any(field => client.IsAssignableFrom(field.FieldType))
            .ShouldBeFalse("no field may hold the device client");
        engine.GetMethods()
            .Any(method => client.IsAssignableFrom(method.ReturnType)
                || method.GetParameters().Any(parameter => client.IsAssignableFrom(parameter.ParameterType)))
            .ShouldBeFalse("no method may take or return the device client");
    }

    [Theory]
    [InlineData(@"15Plus\2026-09\IMG_2403.JPG", "15Plus", false)]
    [InlineData(@"15Plus\2026\2026-09\IMG_2403.JPG", "15Plus", false)]
    [InlineData(@"15Plus\2026\IMG_2403.JPG", "15Plus", false)]
    [InlineData(@"15Plus\IMG_2403.JPG", "15Plus", false)]
    [InlineData(@"15Plus\unsorted\IMG_2403.JPG", "15Plus", true)]
    [InlineData(@"2026-09\IMG_2403.JPG", null, false)]
    [InlineData(@"2026\2026-09\IMG_2403.JPG", null, false)]
    [InlineData(@"2026\IMG_2403.JPG", null, false)]
    [InlineData(@"IMG_2403.JPG", null, false)]
    [InlineData(@"unsorted\IMG_2403.JPG", null, true)]
    [InlineData(@"iPhone 15 Pro\2024-05\PXL_001.JPG", "iPhone 15 Pro", false)]
    [InlineData(@"MyPhone\unsorted\IMG_0001.JPG", "MyPhone", true)]
    public void InspectPath_correctly_detects_device_prefix_and_unsorted(string path, string? expectedPrefix, bool expectedUnsorted)
    {
        var (devicePrefix, isUnsorted) = Reorganizer.InspectPath(path);
        devicePrefix.ShouldBe(expectedPrefix);
        isUnsorted.ShouldBe(expectedUnsorted);
    }

    [Fact]
    public void Reorganize_preserves_device_subfolder_prefix()
    {
        using TempDirectory dest = new();
        string relativeSrc = Path.Combine("15Plus", "2026-09", "IMG_2403.JPG");
        string fullSrc = Path.Combine(dest.Path, relativeSrc);
        Directory.CreateDirectory(Path.GetDirectoryName(fullSrc)!);
        File.WriteAllBytes(fullSrc, new byte[] { 1, 2, 3, 4 });

        using (TransferJournal journal = TransferJournal.Open(dest.Path))
        {
            journal.SetOrganizeScheme(OrganizeScheme.Month);
            RemoteFile remote = new("/DCIM/100APPLE/IMG_2403.JPG", 4, new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
            journal.EnsurePending(remote);
            journal.MarkDone(
                remote.Path, remote.Size, relativeSrc,
                new MediaMetadata(new DateTime(2026, 9, 10, 12, 0, 0), null, null, "Apple", "iPhone 15 Plus"),
                DateTimeOffset.UtcNow);
        }

        ReorganizePlan plan = ComputePlan(dest.Path, OrganizeScheme.YearMonth);
        plan.Moves.Count.ShouldBe(1);
        string expectedTarget = Path.Combine("15Plus", "2026", "2026-09", "IMG_2403.JPG");
        plan.Moves[0].TargetDestPath.ShouldBe(expectedTarget);

        ReorganizeReport report = Reorganize(dest.Path, OrganizeScheme.YearMonth);
        report.Failed.ShouldBe(0);
        report.Moved.ShouldBe(1);

        string fullTarget = Path.Combine(dest.Path, expectedTarget);
        File.Exists(fullTarget).ShouldBeTrue("File should be moved preserving device prefix");
        File.Exists(fullSrc).ShouldBeFalse("Original path should be moved");
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static async Task BuildArchive(string dest, FakeDeviceSpec spec, OrganizeScheme scheme)
    {
        HarnessResult result = await new CopyPipelineHarness(
            dest, spec.Build(), requestedScheme: scheme, schemeExplicit: true).RunAsync(Token);
        result.ExitCode.ShouldBe(0);
        result.Failed.ShouldBe(0);
    }

    private static ReorganizeReport Reorganize(string dest, OrganizeScheme target)
    {
        using TransferJournal journal = TransferJournal.Open(dest);
        Reorganizer reorganizer = new(journal, new DateFolderOrganizer(), dest);
        ReorganizePlan plan = reorganizer.Plan(target);
        return reorganizer.Execute(plan, Token);
    }

    private static ReorganizePlan ComputePlan(string dest, OrganizeScheme target)
    {
        using TransferJournal journal = TransferJournal.Open(dest);
        return new Reorganizer(journal, new DateFolderOrganizer(), dest).Plan(target);
    }

    private static PlannedMove FirstPlannedMove(string dest, OrganizeScheme target) =>
        ComputePlan(dest, target).Moves[0];

    // Physically performs a move (as the atomic rename would) WITHOUT the journal update — the crash window.
    private static void MovePhysicallyOnly(string dest, PlannedMove move)
    {
        string currentExtended = LongPath.ToExtended(Path.Combine(dest, move.CurrentDestPath));
        string targetExtended = LongPath.ToExtended(Path.Combine(dest, move.TargetDestPath));
        Directory.CreateDirectory(LongPath.ToExtended(Path.GetDirectoryName(Path.Combine(dest, move.TargetDestPath))!));
        File.Move(currentExtended, targetExtended);
    }

    // Applies the first `take` planned moves fully (move + journal update) and sets the in-flight marker —
    // a clean interruption after some moves completed.
    private static void ApplyFirstMovesForReal(string dest, OrganizeScheme target, int take)
    {
        using TransferJournal journal = TransferJournal.Open(dest);
        ReorganizePlan plan = new Reorganizer(journal, new DateFolderOrganizer(), dest).Plan(target);
        journal.SetReorganizeTarget(target);
        foreach (PlannedMove move in plan.Moves.Take(take))
        {
            MovePhysicallyOnly(dest, move);
            journal.UpdateDestPath(move.SourcePath, move.SourceSize, move.TargetDestPath);
        }
    }

    private static void AssertJournalMatchesDiskAndContent(string dest, FakeDeviceSpec spec)
    {
        using TransferJournal journal = TransferJournal.OpenReadOnly(dest);
        Dictionary<string, ReorganizeEntry> rows =
            journal.EnumerateDoneForReorganize().ToDictionary(entry => entry.SourcePath);

        foreach (FakeDeviceFile file in spec.Files)
        {
            rows.ShouldContainKey(file.Path);
            string relative = rows[file.Path].DestPath;
            string full = LongPath.ToExtended(Path.Combine(dest, relative));
            File.Exists(full).ShouldBeTrue($"{file.Path} should exist at its recorded path {relative}");
            byte[] expected = FakeContent.Materialize((int)file.Size, file.ContentSeed);
            File.ReadAllBytes(full).ShouldBe(expected, $"{file.Path} must be byte-identical after reorganize");
        }
    }

    private static List<string> ShaMultiset(string dest) =>
        ArchiveFiles(dest)
            .Select(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, string> RelativePathToSha(string dest)
    {
        string rootExtended = LongPath.ToExtended(dest);
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ArchiveFiles(dest))
        {
            string relative = Path.GetRelativePath(rootExtended, path);
            map[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }

        return map;
    }

    // Every media file in the archive, excluding the journal DB (+ WAL/SHM sidecars), summary.txt, and the
    // staging directory.
    private static IEnumerable<string> ArchiveFiles(string dest) =>
        Directory.EnumerateFiles(LongPath.ToExtended(dest), "*", SearchOption.AllDirectories)
            .Where(path => !IsInfrastructure(path));

    private static bool IsInfrastructure(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(TransferJournal.DatabaseFileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SummaryWriter.FileName, StringComparison.OrdinalIgnoreCase)
            || path.Contains(TransferJournal.MetadataFolderName, StringComparison.OrdinalIgnoreCase)
            || path.Contains(".picharbor-tmp", StringComparison.OrdinalIgnoreCase);
    }
}
