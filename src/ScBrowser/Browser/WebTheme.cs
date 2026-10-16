using Microsoft.Web.WebView2.Core;

namespace ScBrowser.Browser;

/// <summary>
/// 把应用主题（<c>System</c> / <c>Light</c> / <c>Dark</c>）映射成 WebView2 的颜色方案偏好。
/// </summary>
/// <remarks>
/// 深色「影响网页」的做法是<b>顺着站点</b>走：把 profile 的
/// <see cref="CoreWebView2Profile.PreferredColorScheme"/> 设成 <c>Dark</c>，
/// 页面里的 <c>prefers-color-scheme</c> 媒体查询就会报 dark——自己声明了深色样式的站点随之变深，
/// 没声明的站点保持原样。这与 Chrome/Edge 的行为一致，不会把排版、图片、图表拧坏。
/// <para>
/// 另有一个顺带的好处：内部页面（<c>browser://*</c>）的 <c>common/base.css</c> 也用同一个媒体查询
/// 切换令牌，于是「应用主题 = 内部页面主题」，不必再单独给 WebUI 推一套主题消息。
/// </para>
/// <para>
/// <c>System</c> 映射为 <c>Auto</c>（跟随系统），而不是硬转成当时的深浅——
/// 这样系统主题在应用运行期间变化时，网页仍能跟着变。
/// </para>
/// </remarks>
public static class WebTheme
{
    /// <summary>
    /// 网页的「默认底色」——页面自己还没画出来之前那一瞬间显示的颜色。
    /// <para>
    /// 必须跟着主题走：WebView2 的默认值是白色，深色主题下新建标签/新建窗口会先闪一下白底，
    /// 非常刺眼。这里给成与外壳一致的颜色，闪的就变成"看不出变化"。
    /// </para>
    /// </summary>
    public static System.Drawing.Color DefaultBackground(bool dark) => dark
        ? System.Drawing.Color.FromArgb(0x20, 0x21, 0x24)   // 与 Theme.Dark.xaml 的 WindowBackgroundBrush 一致
        : System.Drawing.Color.White;

    public static CoreWebView2PreferredColorScheme Resolve(string theme) => theme switch
    {
        "Dark" => CoreWebView2PreferredColorScheme.Dark,
        "Light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };
}
