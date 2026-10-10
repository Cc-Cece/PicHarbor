using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Reorganize;
using PicHarbor.Core.Search;
using PicHarbor.Gui;

namespace PicHarbor.Gui.ViewModels;

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
    private string reorgSummaryText = App.GetString("ReorgSummaryInitial", "点击“模拟预览”分析目录结构重排变动。");

    private int lastMoveCount = -1;

    private const int ReorgPreviewCap = 1000;

    [ObservableProperty]
    private ObservableCollection<ReorganizeItem> previewItems = new();

    public ReorganizeViewModel()
    {
        RefreshCurrentScheme();
    }

    partial void OnArchivePathChanged(string value)
    {
        RefreshCurrentScheme();
    }

    public void RefreshCurrentScheme()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            CurrentScheme = App.GetString("ReorgNotReady", "未就绪");
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            OrganizeScheme? scheme = journal.GetOrganizeScheme();
            CurrentScheme = scheme.HasValue ? FormatSchemeName(scheme.Value) : "month (YYYY-MM)";
        }
        catch
        {
            CurrentScheme = "month (YYYY-MM)";
        }
    }

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ReorgSummaryText = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            PreviewItems = new();
            lastMoveCount = -1;
            return;
        }

        try
        {
            OrganizeScheme scheme = ParseSchemeToken(TargetScheme);
            var (items, summary, currentSchemeDisplay, moveCount) = await Task.Run(() =>
            {
                using var journal = TransferJournal.OpenReadOnly(ArchivePath);
                OrganizeScheme? existingScheme = journal.GetOrganizeScheme();
                string currentDisplay = existingScheme.HasValue ? FormatSchemeName(existingScheme.Value) : "month (YYYY-MM)";

                var reorganizer = new Reorganizer(journal, new DateFolderOrganizer(), ArchivePath);
                ReorganizePlan plan = reorganizer.Plan(scheme);

                int countToTake = Math.Min(plan.Moves.Count, ReorgPreviewCap);
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

                string summaryText = FormatMoveSummary(plan.Moves.Count);

                return (list, summaryText, currentDisplay, plan.Moves.Count);
            }).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentScheme = currentSchemeDisplay;
                PreviewItems = new ObservableCollection<ReorganizeItem>(items);
                lastMoveCount = moveCount;
                ReorgSummaryText = summary;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reorganize preview error: {ex.Message}");
            ReorgSummaryText = string.Format(App.GetString("FmtReorgPreviewFailed", "预览失败: {0}"), ex.Message);
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

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentScheme = FormatSchemeName(scheme);
            });

            await DryRunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reorganize execute error: {ex.Message}");
        }
    }

    public void OnLanguageChanged()
    {
        RefreshCurrentScheme();
        ReorgSummaryText = lastMoveCount < 0
            ? App.GetString("ReorgSummaryInitial", "点击“模拟预览”分析目录结构重排变动。")
            : FormatMoveSummary(lastMoveCount);
    }

    private static string FormatMoveSummary(int moveCount)
    {
        return moveCount > ReorgPreviewCap
            ? string.Format(App.GetString("FmtReorgMoveCountPreview", "需要移动重构 {0:N0} 项 (仅预览前 {1:N0} 项)"), moveCount, ReorgPreviewCap)
            : string.Format(App.GetString("FmtReorgMoveCount", "需要移动重构 {0:N0} 项"), moveCount);
    }

    private static string FormatSchemeName(OrganizeScheme scheme) => scheme switch
    {
        OrganizeScheme.Month => "month (YYYY-MM)",
        OrganizeScheme.YearMonth => "year-month (YYYY\\YYYY-MM)",
        OrganizeScheme.Year => "year (YYYY)",
        OrganizeScheme.Flat => "flat",
        _ => scheme.ToString().ToLowerInvariant()
    };

    private static OrganizeScheme ParseSchemeToken(string schemeText)
    {
        if (schemeText.StartsWith("year-month")) return OrganizeScheme.YearMonth;
        if (schemeText.StartsWith("year")) return OrganizeScheme.Year;
        if (schemeText.StartsWith("flat")) return OrganizeScheme.Flat;
        return OrganizeScheme.Month;
    }
}
