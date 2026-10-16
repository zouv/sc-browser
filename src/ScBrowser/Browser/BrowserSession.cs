using Microsoft.Web.WebView2.Core;

namespace ScBrowser.Browser;

/// <summary>窗口的会话类型。</summary>
public enum BrowserSessionKind
{
    /// <summary>普通窗口：数据落在用户数据目录，写历史、参与会话保存。</summary>
    Normal,

    /// <summary>
    /// 无痕窗口：WebView2 的 InPrivate profile——Cookie/缓存/存储只存在于本次会话，
    /// 退出即销毁；同时本应用也不给它写历史、不保存会话。
    /// </summary>
    Incognito,
}

/// <summary>
/// 会话类型 → WebView2 profile 的桥接。
/// </summary>
public static class BrowserSession
{
    /// <summary>无痕用的 profile 名（与默认 profile 区分开，便于日志和排查）。</summary>
    private const string IncognitoProfileName = "incognito";

    /// <summary>
    /// 生成创建标签页用的控制器选项；普通会话返回 <c>null</c>（表示沿用默认 profile）。
    /// <para>
    /// 关键点：InPrivate 是<b>控制器/profile 级</b>能力，不是环境级的——所以无痕窗口仍然复用
    /// 全应用那唯一一个 <see cref="CoreWebView2Environment"/>（见
    /// <see cref="BrowserEnvironmentProvider"/> 的铁律），只是换一套内存 profile。
    /// </para>
    /// </summary>
    public static CoreWebView2ControllerOptions? CreateControllerOptions(
        CoreWebView2Environment environment,
        BrowserSessionKind kind)
    {
        if (kind == BrowserSessionKind.Normal)
        {
            return null;
        }

        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = IncognitoProfileName;
        options.IsInPrivateModeEnabled = true;
        return options;
    }
}
