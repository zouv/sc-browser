using Microsoft.Web.WebView2.Core;
using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 应用主题 → 网页颜色方案的映射。纯函数，不需要 WebView2 运行时。
/// </summary>
public class WebThemeTests
{
    [Theory]
    [InlineData("Dark", CoreWebView2PreferredColorScheme.Dark)]
    [InlineData("Light", CoreWebView2PreferredColorScheme.Light)]
    // System 交给 Auto 跟随系统，而不是固化成本次的深浅：
    // 这样系统主题在应用运行期间变化时，网页仍能跟着变。
    [InlineData("System", CoreWebView2PreferredColorScheme.Auto)]
    // 未知取值（设置文件被手改成非法值时的兜底）同样回落到 Auto。
    [InlineData("", CoreWebView2PreferredColorScheme.Auto)]
    [InlineData("Solarized", CoreWebView2PreferredColorScheme.Auto)]
    public void Resolve_MapsThemeToPreferredColorScheme(
        string theme,
        CoreWebView2PreferredColorScheme expected)
    {
        Assert.Equal(expected, WebTheme.Resolve(theme));
    }
}

/// <summary>
/// 网页默认底色：深色主题下必须是深色，否则新建标签会先闪一下白底。
/// </summary>
public class WebThemeBackgroundTests
{
    [Fact]
    public void 深色主题的默认底色应接近外壳的深灰()
    {
        var color = WebTheme.DefaultBackground(dark: true);

        Assert.Equal(0x20, color.R);
        Assert.Equal(0x21, color.G);
        Assert.Equal(0x24, color.B);
        // 不能是白色——那正是要修掉的「闪一下白」
        Assert.NotEqual(System.Drawing.Color.White.ToArgb(), color.ToArgb());
    }

    [Fact]
    public void 浅色主题用白色()
    {
        Assert.Equal(System.Drawing.Color.White.ToArgb(), WebTheme.DefaultBackground(dark: false).ToArgb());
    }
}
