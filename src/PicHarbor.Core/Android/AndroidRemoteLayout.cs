namespace PicHarbor.Core.Android;

/// <summary>
/// 安卓设备上的固定远端目录布局。
/// </summary>
/// <remarks>
/// <b>目录必须固定，不可由用户随意更改。</b> 识别文件（<c>.picharbor-serial</c>）的读写位置
/// 一旦随用户配置漂移，就会出现「写在 A 目录、去 B 目录找」的情况，导致识别文件形同丢失、
/// 设备身份退化为临时值。因此全流程统一使用 <see cref="RootDirectory"/>。
/// </remarks>
public static class AndroidRemoteLayout
{
    /// <summary>安卓侧唯一的目标根目录。</summary>
    public const string RootDirectory = "/DCIM/PicHarbor/";

    /// <summary>
    /// 把任意历史/用户输入的目录统一归一化为固定根目录。
    /// </summary>
    /// <remarks>
    /// 旧版本允许用户自填目录（含 <c>/DCIM/PicHarbor/iPhone/</c> 这类历史默认值），
    /// 一律收敛到 <see cref="RootDirectory"/>。
    /// </remarks>
    public static string Normalize(string? _) => RootDirectory;
}
