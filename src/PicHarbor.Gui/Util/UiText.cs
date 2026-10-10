namespace PicHarbor.Gui.Util;

/// <summary>
/// Formats user-visible sentences from the active language pack.
/// </summary>
internal static class UiText
{
    public static string Format(string key, string fallback, params object[] args)
    {
        string template = App.GetString(key, fallback);
        return args.Length == 0 ? template : string.Format(template, args);
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return Format("FmtDurationHms", "{0}小时{1:D2}分{2:D2}秒", (int)span.TotalHours, span.Minutes, span.Seconds);
        }

        if (span.TotalMinutes >= 1)
        {
            return Format("FmtDurationMs", "{0:D2}分{1:D2}秒", span.Minutes, span.Seconds);
        }

        return Format("FmtDurationS", "{0}秒", span.Seconds);
    }

    public static string Eta(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return Format("FmtEtaHm", "{0}时{1}分", (int)span.TotalHours, span.Minutes);
        }

        return Format("FmtEtaMs", "{0}分{1}秒", span.Minutes, span.Seconds);
    }

    public static string MissingFiles(int count, IEnumerable<string> paths)
    {
        string list = string.Join(Environment.NewLine, paths.Take(3));
        if (count > 3)
        {
            list += Environment.NewLine + "...";
        }

        return Format("FmtMissingSelectedFiles", "有 {0} 个选中的文件在磁盘上不存在:\n{1}", count, list);
    }
}

/// <summary>
/// Remembers the last formatted sentence so a language switch can rebuild it.
/// </summary>
internal sealed class StickyText
{
    private string key = "";
    private string fallback = "";
    private object[] args = [];

    public bool HasValue => key.Length > 0;

    public void Clear()
    {
        key = "";
        fallback = "";
        args = [];
    }

    public string Set(string resourceKey, string resourceFallback, params object[] resourceArgs)
    {
        key = resourceKey;
        fallback = resourceFallback;
        args = resourceArgs;
        return Current;
    }

    public string Current =>
        key.Length == 0
            ? ""
            : args.Length == 0
                ? App.GetString(key, fallback)
                : string.Format(App.GetString(key, fallback), args);
}
