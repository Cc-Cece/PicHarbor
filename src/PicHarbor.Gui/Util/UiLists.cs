using System.Collections.ObjectModel;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Replaces combo-box labels in place so the selected index stays put.
/// </summary>
internal static class UiLists
{
    public static void Replace(ObservableCollection<string> target, params string[] labels)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            if (i < target.Count)
            {
                if (!string.Equals(target[i], labels[i], StringComparison.Ordinal))
                {
                    target[i] = labels[i];
                }
            }
            else
            {
                target.Add(labels[i]);
            }
        }

        while (target.Count > labels.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
