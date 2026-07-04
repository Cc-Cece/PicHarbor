using GetAndSee.Cli.Commands;
using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Search;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace GetAndSee.Tests.Cli;

public sealed class SearchCommandTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Returns_exit_2_when_no_archive_exists()
    {
        var opener = new RecordingFolderOpener();

        int exit = SearchCommand.Run(dir.Path, new MediaSearchCriteria(), open: false, opener, NewConsole());

        exit.ShouldBe(2);
        // Read-only: searching a missing archive must not create a database.
        File.Exists(Path.Combine(dir.Path, TransferJournal.DatabaseFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Open_reveals_each_distinct_containing_folder_once()
    {
        SeedPhotos();
        var opener = new RecordingFolderOpener();

        int exit = SearchCommand.Run(dir.Path, new MediaSearchCriteria(Type: MediaType.Photo), open: true, opener, NewConsole());

        exit.ShouldBe(0);
        string root = Path.GetFullPath(dir.Path);
        // Three photos across two months → two distinct folders, in capture-date order, absolute paths.
        opener.OpenedFolders.ShouldBe([Path.Combine(root, "2024-08"), Path.Combine(root, "2024-09")]);
    }

    [Fact]
    public void Open_does_nothing_when_there_are_no_matches()
    {
        SeedPhotos();
        var opener = new RecordingFolderOpener();

        SearchCommand.Run(dir.Path, new MediaSearchCriteria(Type: MediaType.Video), open: true, opener, NewConsole());

        opener.CallCount.ShouldBe(0);
    }

    [Fact]
    public void Open_caps_the_number_of_folders_revealed()
    {
        SeedManyMonths(12);   // 12 distinct month folders → more than the cap
        var opener = new RecordingFolderOpener();

        SearchCommand.Run(dir.Path, new MediaSearchCriteria(), open: true, opener, NewConsole());

        opener.OpenedFolders.Count.ShouldBe(SearchCommand.MaxFoldersToOpen);
    }

    [Fact]
    public void Search_does_not_modify_the_database()
    {
        SeedPhotos();
        string before = SchemaSnapshot(dir.Path);

        SearchCommand.Run(dir.Path, new MediaSearchCriteria(), open: false, new RecordingFolderOpener(), NewConsole());

        SchemaSnapshot(dir.Path).ShouldBe(before);
    }

    [Fact]
    public void Builds_inclusive_date_bounds()
    {
        bool ok = SearchCommand.TryBuildCriteria(
            "2024-08-01", "2024-08-31", null, null, null, null, false,
            out MediaSearchCriteria criteria, out string? error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        criteria.From.ShouldBe(new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero));
        // --to is inclusive of the whole day, so its bound is the end of that day.
        criteria.To.ShouldBe(new DateTimeOffset(new DateOnly(2024, 8, 31).ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero));
    }

    [Fact]
    public void Maps_type_token_to_media_type()
    {
        SearchCommand.TryBuildCriteria(null, null, "video", null, null, null, false, out MediaSearchCriteria criteria, out _)
            .ShouldBeTrue();

        criteria.Type.ShouldBe(MediaType.Video);
    }

    [Theory]
    [InlineData("not-a-date", null)]
    [InlineData(null, "2024-13-40")]
    public void Rejects_an_invalid_date(string? from, string? to)
    {
        SearchCommand.TryBuildCriteria(from, to, null, null, null, null, false, out _, out string? error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Rejects_a_reversed_date_range()
    {
        SearchCommand.TryBuildCriteria("2024-09-01", "2024-08-01", null, null, null, null, false, out _, out string? error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Rejects_a_reversed_size_range()
    {
        SearchCommand.TryBuildCriteria(null, null, null, null, 100, 10, false, out _, out string? error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    private void SeedPhotos()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        Add(journal, "/DCIM/100APPLE/A.HEIC", Path.Combine("2024-08", "A.HEIC"), new DateTime(2024, 8, 10, 9, 0, 0));
        Add(journal, "/DCIM/100APPLE/B.HEIC", Path.Combine("2024-08", "B.HEIC"), new DateTime(2024, 8, 20, 9, 0, 0));
        Add(journal, "/DCIM/101APPLE/C.HEIC", Path.Combine("2024-09", "C.HEIC"), new DateTime(2024, 9, 1, 9, 0, 0));
    }

    private void SeedManyMonths(int count)
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        for (int i = 0; i < count; i++)
        {
            int month = (i % 12) + 1;
            int year = 2020 + (i / 12);
            string folder = $"{year:D4}-{month:D2}";
            Add(journal, $"/DCIM/100APPLE/IMG_{i}.HEIC", Path.Combine(folder, $"IMG_{i}.HEIC"), new DateTime(year, month, 1, 9, 0, 0));
        }
    }

    private static void Add(TransferJournal journal, string sourcePath, string dest, DateTime captured)
    {
        var file = new RemoteFile(sourcePath, 1024, null);
        journal.EnsurePending(file);
        journal.MarkDone(file.Path, file.Size, dest, new MediaMetadata(captured, null, null, "Apple", "iPhone"), DateTimeOffset.UtcNow);
    }

    private static IAnsiConsole NewConsole() =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });

    private static string SchemaSnapshot(string root)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, TransferJournal.DatabaseFileName) }.ConnectionString);
        connection.Open();

        using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        long userVersion = (long)version.ExecuteScalar()!;

        using SqliteCommand objects = connection.CreateCommand();
        objects.CommandText = "SELECT type || ':' || name FROM sqlite_master ORDER BY name;";
        var names = new List<string>();
        using SqliteDataReader reader = objects.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return $"v{userVersion};{string.Join(",", names)}";
    }
}
