using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Reorganize;
using GetAndSee.Core.Search;

namespace GetAndSee.Gui.ViewModels;

public class ReorganizeItem
{
    public string OriginalPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
}

public partial class ReorganizeViewModel : ObservableObject
{
    [ObservableProperty]
    private string archivePath = MainViewModel.DefaultArchivePath;

    [ObservableProperty]
    private string currentScheme = "month (YYYY-MM)";

    [ObservableProperty]
    private string targetScheme = "year-month (YYYY\\YYYY-MM)";

    public ObservableCollection<string> Schemes { get; } = new()
    {
        "month (YYYY-MM)",
        "year-month (YYYY\\YYYY-MM)",
        "year (YYYY)",
        "flat"
    };

    [ObservableProperty]
    private string reorgSummaryText = "";

    [ObservableProperty]
    private ObservableCollection<ReorganizeItem> previewItems = new();

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ReorgSummaryText = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            PreviewItems = new();
            return;
        }

        try
        {
            OrganizeScheme scheme = ParseSchemeToken(TargetScheme);
            var (items, summary, targetSchemeStr) = await Task.Run(() =>
            {
                using var journal = TransferJournal.OpenReadOnly(ArchivePath);
                var reorganizer = new Reorganizer(journal, new DateFolderOrganizer(), ArchivePath);
                ReorganizePlan plan = reorganizer.Plan(scheme);

                const int maxDisplay = 1000;
                int countToTake = Math.Min(plan.Moves.Count, maxDisplay);
                var list = new List<ReorganizeItem>(countToTake);

                for (int i = 0; i < countToTake; i++)
                {
                    PlannedMove move = plan.Moves[i];
                    list.Add(new ReorganizeItem
                    {
                        OriginalPath = move.CurrentDestPath,
                        TargetPath = move.TargetDestPath
                    });
                }

                string summaryText = plan.Moves.Count > maxDisplay
                    ? $"需要移动重构 {plan.Moves.Count:N0} 项 (仅预览前 {maxDisplay:N0} 项)"
                    : $"需要移动重构 {plan.Moves.Count:N0} 项";

                return (list, summaryText, plan.TargetScheme.ToString().ToLowerInvariant());
            }).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentScheme = targetSchemeStr;
                PreviewItems = new ObservableCollection<ReorganizeItem>(items);
                ReorgSummaryText = summary;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reorganize preview error: {ex.Message}");
            ReorgSummaryText = $"预览失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExecuteReorganizeAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            return;
        }

        try
        {
            OrganizeScheme scheme = ParseSchemeToken(TargetScheme);
            await Task.Run(() =>
            {
                using var journal = TransferJournal.Open(ArchivePath);
                var reorganizer = new Reorganizer(journal, new DateFolderOrganizer(), ArchivePath);
                ReorganizePlan plan = reorganizer.Plan(scheme);
                reorganizer.Execute(plan);
            }).ConfigureAwait(false);

            await DryRunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reorganize execute error: {ex.Message}");
        }
    }

    private static OrganizeScheme ParseSchemeToken(string schemeText)
    {
        if (schemeText.StartsWith("year-month")) return OrganizeScheme.YearMonth;
        if (schemeText.StartsWith("year")) return OrganizeScheme.Year;
        if (schemeText.StartsWith("flat")) return OrganizeScheme.Flat;
        return OrganizeScheme.Month;
    }
}
