using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 标签「状态」与「引擎」解耦后的纯状态行为：没有 WebView2 也要能给出地址、标题与缩放。
/// 会话惰性恢复依赖这些行为（后台标签压根没有引擎）。
/// </summary>
public class TabStateTests
{
    [Fact]
    public void 未创建引擎的标签应给出地址与内部页面标题()
    {
        using var tab = new BrowserTab(1);
        tab.SetInitialLocation("browser://settings");

        Assert.False(tab.IsInitialized);
        Assert.Null(tab.View);
        Assert.Equal("browser://settings", tab.DisplayUrl);
        Assert.Equal("设置", tab.Title);
    }

    [Fact]
    public void 未创建引擎的标签对普通网址用地址作标题()
    {
        using var tab = new BrowserTab(2);
        tab.SetInitialLocation("https://example.com/a");

        Assert.Equal("https://example.com/a", tab.DisplayUrl);
        Assert.Equal("https://example.com/a", tab.Title);
    }

    [Fact]
    public void 未就绪时的导航应记为待加载而不是丢弃()
    {
        using var tab = new BrowserTab(3);
        tab.Navigate("https://example.com/pending");

        Assert.False(tab.IsInitialized);
        Assert.Equal("https://example.com/pending", tab.DisplayUrl);
    }

    [Fact]
    public void 未就绪时的缩放应记在标签状态里()
    {
        using var tab = new BrowserTab(4);
        tab.SetZoom(1.5);

        Assert.Equal(1.5, tab.ZoomFactor);
    }

    [Fact]
    public void 缩放应被限制在合理范围()
    {
        using var tab = new BrowserTab(5);
        tab.SetZoom(99);
        Assert.Equal(5.0, tab.ZoomFactor);

        tab.SetZoom(0.01);
        Assert.Equal(0.25, tab.ZoomFactor);
    }
}
