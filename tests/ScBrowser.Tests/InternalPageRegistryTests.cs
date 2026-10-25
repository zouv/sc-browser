using System.IO;
using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 内部页面注册表：<c>browser://</c> 地址解析、消息白名单、资源路径解析（含目录穿越）。
/// 消息白名单是「网页能否调宿主能力」的判定点，务必有用例夹住。
/// </summary>
public class InternalPageRegistryTests : IDisposable
{
    private readonly string _webUi;

    public InternalPageRegistryTests()
    {
        _webUi = Path.Combine(Path.GetTempPath(), "scbrowser-webui-tests", Guid.NewGuid().ToString("N"), "ui");
        Directory.CreateDirectory(Path.Combine(_webUi, "newtab"));
        Directory.CreateDirectory(Path.Combine(_webUi, "common"));
        File.WriteAllText(Path.Combine(_webUi, "newtab", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_webUi, "newtab", "newtab.js"), "1");
        File.WriteAllText(Path.Combine(_webUi, "common", "base.css"), "body{}");

        // WebUI 根目录的「同前缀兄弟目录」：越界读取的典型目标。
        Directory.CreateDirectory(_webUi + "-secret");
        File.WriteAllText(Path.Combine(_webUi + "-secret", "x.txt"), "不该被读到");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_webUi)!, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不应让测试变红。
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("browser://settings", "settings")]
    [InlineData("browser://settings/", "settings")]
    [InlineData("browser://Settings/sub/page?x=1#y", "settings")]
    [InlineData("browser://newtab?x=1", "newtab")]
    public void 应取出内部页面名(string url, string expected)
    {
        Assert.True(InternalPageRegistry.TryGetPageName(url, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("file:///C:/x/index.html")]
    [InlineData("browser://")]
    [InlineData(null)]
    public void 非内部页面应取不到页面名(string? url)
    {
        Assert.False(InternalPageRegistry.TryGetPageName(url, out _));
    }

    [Fact]
    public void 导航地址应补尾部斜杠_未登记页面返回空()
    {
        Assert.Equal("browser://settings/", InternalPageRegistry.ToNavigableUri("browser://settings"));
        Assert.Equal("browser://history/", InternalPageRegistry.ToNavigableUri("browser://HISTORY/?q=1"));
        Assert.Null(InternalPageRegistry.ToNavigableUri("browser://not-a-page"));
    }

    [Fact]
    public void 显示地址应去掉尾部斜杠()
    {
        Assert.Equal("browser://settings", InternalPageRegistry.NormalizeDisplayUrl("browser://settings/"));
        Assert.Equal("https://a.com/", InternalPageRegistry.NormalizeDisplayUrl("https://a.com/"));
    }

    [Theory]
    [InlineData("browser://history/", "history.query")]
    [InlineData("browser://history/", "history.clear")]
    [InlineData("browser://history/", "navigation.navigate")]
    [InlineData("browser://downloads/", "downloads.openFile")]
    [InlineData("browser://settings/", "app.clearBrowsingData")]
    [InlineData("browser://newtab/", "history.topSites")]
    public void 内部页面应能调用自己白名单内的消息(string source, string type)
    {
        Assert.True(InternalPageRegistry.IsMessageAllowed(source, type));
    }

    [Theory]
    // 普通网页 / 本地文件：一律拒绝——这正是此前「任意网站可调宿主能力」的漏洞。
    [InlineData("https://evil.example/", "downloads.openFile")]
    [InlineData("https://evil.example/", "history.query")]
    [InlineData("file:///C:/Users/x/a.html", "settings.update")]
    [InlineData(null, "history.query")]
    // 内部页面也不能越权调别的页面的能力。
    [InlineData("browser://newtab/", "history.clear")]
    [InlineData("browser://newtab/", "history.topSitesX")]
    [InlineData("browser://history/", "downloads.openFile")]
    [InlineData("browser://downloads/", "settings.update")]
    // 未登记的 browser:// 页面同样拒绝。
    [InlineData("browser://unknown/", "navigation.navigate")]
    public void 越权消息应被拒绝(string? source, string type)
    {
        Assert.False(InternalPageRegistry.IsMessageAllowed(source, type));
    }

    [Fact]
    public void 资源路径_根路径解析到页面首页()
    {
        Assert.Equal(
            Path.Combine(_webUi, "newtab", "index.html"),
            InternalPageRegistry.ResolveResourcePath("browser://newtab/", _webUi));
    }

    [Fact]
    public void 资源路径_公共目录与页面目录()
    {
        Assert.Equal(
            Path.Combine(_webUi, "common", "base.css"),
            InternalPageRegistry.ResolveResourcePath("browser://newtab/common/base.css", _webUi));
        Assert.Equal(
            Path.Combine(_webUi, "newtab", "newtab.js"),
            InternalPageRegistry.ResolveResourcePath("browser://newtab/newtab.js", _webUi));
    }

    [Theory]
    [InlineData("browser://newtab/missing.js")]
    [InlineData("browser://unknown/")]
    [InlineData("browser://newtab/..%2F..%2Fui-secret%2Fx.txt")]
    [InlineData("browser://newtab/%2E%2E/%2E%2E/ui-secret/x.txt")]
    public void 资源路径_不存在或越界返回空(string uri)
    {
        Assert.Null(InternalPageRegistry.ResolveResourcePath(uri, _webUi));
    }
}
