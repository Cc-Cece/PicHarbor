using System.CommandLine;
using GetAndSee.Cli.Commands;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.FakeDevice;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace GetAndSee.Tests.Cli;

public sealed class ReorganizeCommandTests
{
    private static readonly DateTimeOffset Aug2024 = new(2024, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- parse --------------------------------------------------------------------------------------

    [Fact]
    public void Dest_is_required()
    {
        ParseResult result = ReorganizeCommand.Build().Parse(["--organize-by", "month"]);

        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Organize_by_is_required()
    {
        ParseResult result = ReorganizeCommand.Build().Parse(["--dest", "X"]);

        result.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("month")]
    [InlineData("year-month")]
    [InlineData("year")]
    [InlineData("flat")]
    public void Accepts_each_known_target_scheme(string token)
    {
        ParseResult result = ReorganizeCommand.Build().Parse(["--dest", "X", "--organize-by", token]);

        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Rejects_an_unknown_target_scheme()
    {
        ParseResult result = ReorganizeCommand.Build().Parse(["--dest", "X", "--organize-by", "weekly"]);

        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parses_dry_run()
    {
        ParseResult result = ReorganizeCommand.Build().Parse(["--dest", "X", "--organize-by", "month", "--dry-run"]);

        result.Errors.ShouldBeEmpty();
        result.GetValue<bool>("--dry-run").ShouldBeTrue();
    }

    // ---- Run ----------------------------------------------------------------------------------------

    [Fact]
    public void Returns_exit_2_and_creates_nothing_when_no_archive_exists()
    {
        using TempDirectory dir = new();

        int exit = ReorganizeCommand.Run(dir.Path, OrganizeScheme.Month, dryRun: false, NewConsole(), Token);

        exit.ShouldBe(2);
        File.Exists(Path.Combine(dir.Path, TransferJournal.DatabaseFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Dry_run_previews_and_is_provably_side_effect_free()
    {
        using TempDirectory dir = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        await BuildArchive(dir.Path, spec, OrganizeScheme.YearMonth);
        List<string> filesBefore = ArchiveRelativePaths(dir.Path);

        int exit = ReorganizeCommand.Run(dir.Path, OrganizeScheme.Month, dryRun: true, NewConsole(), Token);

        exit.ShouldBe(0);
        // Nothing moved: the exact set of files on disk is unchanged, the new layout was never created.
        ArchiveRelativePaths(dir.Path).ShouldBe(filesBefore);
        Directory.Exists(Path.Combine(dir.Path, "2024", "2024-08")).ShouldBeTrue();  // still nested
        File.Exists(Path.Combine(dir.Path, "2024-08", "IMG_0001.HEIC")).ShouldBeFalse();
        using TransferJournal journal = TransferJournal.OpenReadOnly(dir.Path);
        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.YearMonth); // recorded scheme untouched
        journal.GetReorganizeTarget().ShouldBeNull();                    // no in-flight marker written
    }

    [Fact]
    public async Task Run_moves_files_to_the_new_layout_and_records_it()
    {
        using TempDirectory dir = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", Aug2024);
        await BuildArchive(dir.Path, spec, OrganizeScheme.YearMonth);

        int exit = ReorganizeCommand.Run(dir.Path, OrganizeScheme.Month, dryRun: false, NewConsole(), Token);

        exit.ShouldBe(0);
        File.Exists(Path.Combine(dir.Path, "2024-08", "IMG_0001.HEIC")).ShouldBeTrue();
        Directory.Exists(Path.Combine(dir.Path, "2024")).ShouldBeFalse();
        using TransferJournal journal = TransferJournal.OpenReadOnly(dir.Path);
        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.Month);
        journal.GetReorganizeTarget().ShouldBeNull();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static async Task BuildArchive(string dest, FakeDeviceSpec spec, OrganizeScheme scheme)
    {
        HarnessResult result = await new CopyPipelineHarness(
            dest, spec.Build(), requestedScheme: scheme, schemeExplicit: true).RunAsync(Token);
        result.ExitCode.ShouldBe(0);
    }

    private static List<string> ArchiveRelativePaths(string dest)
    {
        string root = Path.GetFullPath(dest);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Where(relative => !relative.StartsWith(TransferJournal.DatabaseFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();
    }

    private static IAnsiConsole NewConsole() =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });
}
