// PicHarbor.Gui — 启用向导里「要保护的归档根」勾选条目
// 必须实现通知：PclCheckBox 双向绑定 IsSelected，且 ViewModel 的
// HasAnyRootSelected 依赖它变化后重新求值。

using CommunityToolkit.Mvvm.ComponentModel;

namespace PicHarbor.Gui.ViewModels;

/// <summary>向导第 2 步里的一个候选目录。</summary>
public partial class RootChoice : ObservableObject
{
    /// <summary>完整路径（提交给服务登记用）。</summary>
    public string Path { get; }

    /// <summary>界面上展示的一行文字。</summary>
    public string DisplayText { get; }

    /// <summary>是否勾选保护。</summary>
    [ObservableProperty] private bool isSelected;

    public RootChoice(string path, string displayText, bool isSelected)
    {
        Path = path;
        DisplayText = displayText;
        this.isSelected = isSelected;
    }
}
