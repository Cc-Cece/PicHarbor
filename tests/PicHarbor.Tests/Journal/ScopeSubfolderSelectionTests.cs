using PicHarbor.Core.Journal;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

/// <summary>
/// CRUD coverage for the scope_subfolder_selections table (unified subfolder-filter persistence,
/// shared by the iPhone / Android / Google Photos scope pickers).
/// </summary>
public sealed class ScopeSubfolderSelectionTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Fresh_journal_returns_empty_selection()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.GetScopeSubfolderSelections("iphone_restore", "dev-1").ShouldBeEmpty();
    }

    [Fact]
    public void Set_then_get_round_trips_checked_paths()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "2024", "2024/10-巴黎", "2025/旅行" });

        journal.GetScopeSubfolderSelections("iphone_restore", "dev-1")
            .ShouldBe(new[] { "2024", "2024/10-巴黎", "2025/旅行" }, ignoreOrder: true);
    }

    [Fact]
    public void Set_overwrites_previous_selection_for_same_scope_and_device()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "a", "b" });
        journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "c" });

        journal.GetScopeSubfolderSelections("iphone_restore", "dev-1").ShouldBe(new[] { "c" });
    }

    [Fact]
    public void Selections_are_isolated_per_scope_kind_and_device_key()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "a" });
        journal.SetScopeSubfolderSelections("android_sync", "dev-1", new[] { "b" });
        journal.SetScopeSubfolderSelections("google_upload", "default", new[] { "c" });

        journal.GetScopeSubfolderSelections("iphone_restore", "dev-1").ShouldBe(new[] { "a" });
        journal.GetScopeSubfolderSelections("android_sync", "dev-1").ShouldBe(new[] { "b" });
        journal.GetScopeSubfolderSelections("google_upload", "default").ShouldBe(new[] { "c" });
        journal.GetScopeSubfolderSelections("iphone_restore", "dev-2").ShouldBeEmpty();
    }

    [Fact]
    public void Empty_selection_can_be_persisted_via_sentinel_row()
    {
        // The UI stores a sentinel marker row so an intentionally disabled filter (nothing checked)
        // survives reloads; the marker must round-trip like any other path.
        const string sentinel = "__PicHarborSelectionNone__";
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetScopeSubfolderSelections("google_upload", "default", new[] { sentinel });

        journal.GetScopeSubfolderSelections("google_upload", "default").ShouldBe(new[] { sentinel });
    }

    [Fact]
    public void Clear_removes_all_rows_for_the_scope_and_device()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "a", "b" });
        journal.ClearScopeSubfolderSelections("iphone_restore", "dev-1");

        journal.GetScopeSubfolderSelections("iphone_restore", "dev-1").ShouldBeEmpty();
    }

    [Fact]
    public void Get_on_legacy_journal_without_the_table_returns_empty_set()
    {
        // Simulate an old journal: create the archive, drop the table, then query.
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.SetScopeSubfolderSelections("iphone_restore", "dev-1", new[] { "a" });
        }

        using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={TransferJournal.ResolveDatabasePath(dir.Path)}"))
        {
            connection.Open(); connection.CreateFunction("picharbor_writer_version",()=>8L);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE scope_subfolder_selections;";
            command.ExecuteNonQuery();
        }

        using TransferJournal reopened = TransferJournal.Open(dir.Path);
        reopened.GetScopeSubfolderSelections("iphone_restore", "dev-1").ShouldBeEmpty();
    }
}
