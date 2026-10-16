using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 地址栏输入解析的边界用例。
/// 这块的规则稍有不慎就会把「搜索词」误判成「域名」，或反之——是本项目最容易回归的地方之一。
/// </summary>
public class AddressBarResolverTests
{
    public AddressBarResolverTests()
    {
        // 全局静态配置，测试里显式固定，避免受本机设置影响。
        AddressBarResolver.SearchTemplate = "https://www.bing.com/search?q={0}";
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://example.com/a?b=c", "http://example.com/a?b=c")]
    [InlineData("file:///C:/tmp/x.html", "file:///C:/tmp/x.html")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("browser://settings", "browser://settings")]
    public void 显式协议的输入应原样放行(string input, string expected)
    {
        Assert.Equal(expected, AddressBarResolver.Resolve(input));
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("www.example.com/path", "https://www.example.com/path")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("localhost", "https://localhost")]
    [InlineData("192.168.1.1", "https://192.168.1.1")]
    [InlineData("192.168.1.1:8080/admin", "https://192.168.1.1:8080/admin")]
    [InlineData("sub.domain.co.uk", "https://sub.domain.co.uk")]
    public void 形如主机名的输入应补全为_https(string input, string expected)
    {
        Assert.Equal(expected, AddressBarResolver.Resolve(input));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("这是个中文查询")]
    [InlineData("how to write c#")]
    [InlineData("note:hello")]          // 冒号后不是端口 → 不是主机
    [InlineData("justtext")]            // 没有点 → 不是域名
    [InlineData("trailing.")]           // 空标签 → 不是域名
    [InlineData("a.b")]                 // 顶级标签只有 1 位 → 视为搜索词
    [InlineData("1.2.3")]               // 不是四段 IPv4，也不是合法域名
    public void 不像地址的输入应走搜索(string input)
    {
        var resolved = AddressBarResolver.Resolve(input);

        Assert.StartsWith("https://www.bing.com/search?q=", resolved);
        Assert.Contains(Uri.EscapeDataString(input), resolved);
    }

    [Fact]
    public void 空输入应回到新标签页()
    {
        Assert.Equal(InternalPageRegistry.NewTabUrl, AddressBarResolver.Resolve(""));
        Assert.Equal(InternalPageRegistry.NewTabUrl, AddressBarResolver.Resolve("   "));
    }

    [Fact]
    public void 搜索词应做_URL_编码()
    {
        var resolved = AddressBarResolver.Resolve("c# & .net");

        // 不能把 & 原样带进查询串，否则会被解析成额外的查询参数。
        Assert.DoesNotContain(" & ", resolved);
        Assert.Contains("%20%26%20", resolved);
    }

    [Fact]
    public void 自定义搜索模板应生效()
    {
        AddressBarResolver.SearchTemplate = "https://duckduckgo.com/?q={0}";

        try
        {
            Assert.Equal("https://duckduckgo.com/?q=hello", AddressBarResolver.Resolve("hello"));
        }
        finally
        {
            AddressBarResolver.SearchTemplate = "https://www.bing.com/search?q={0}";
        }
    }
}
