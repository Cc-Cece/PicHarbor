using CommunityToolkit.Mvvm.ComponentModel;

namespace GetAndSee.Gui.ViewModels;

public partial class TransferItemDetail : ObservableObject
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileSizeText { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
