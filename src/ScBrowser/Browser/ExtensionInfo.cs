namespace ScBrowser.Browser;

/// <summary>
/// 已安装扩展的展示信息。
/// <para>
/// 注意：WebView2 的能力边界决定了这里<b>拿不到</b>扩展的来源目录、
/// 图标、权限、描述——平台只暴露 id / name / isEnabled 三项。
/// </para>
/// </summary>
public sealed record ExtensionInfo(string Id, string Name, bool IsEnabled);
