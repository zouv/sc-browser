using ScBrowser.Services;
using ScBrowser.Storage;

namespace ScBrowser.Tests;

/// <summary>
/// 地址栏下拉的候选构建：排序、去重、上限，以及「这串输入像不像地址」的启发式。
/// </summary>
public class OmniboxSuggestionsTests
{
    private const string Template = "https://www.bing.com/search?q={0}";

    private static HistoryEntry Entry(string url, string title, int visits = 1, int daysAgo = 0) =>
        new(1, url, title, visits, DateTimeOffset.UtcNow.AddDays(-daysAgo));

    [Fact]
    public void 空输入应列出最近访问()
    {
        var history = new[] { Entry("https://a.example/", "A"), Entry("https://b.example/", "B") };

        var items = OmniboxSuggestions.Build("", history, Template);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(SuggestionKind.History, i.Kind));
        Assert.Equal("https://a.example/", items[0].Url);
    }

    [Fact]
    public void 关键词输入的第一行应是搜索项()
    {
        var items = OmniboxSuggestions.Build("google map", [], Template);

        Assert.Single(items);
        Assert.Equal(SuggestionKind.Search, items[0].Kind);
        Assert.Contains("Bing", items[0].Title);
        // 搜索模板里的 {0} 必须被替换成 URL 编码后的查询词。
        Assert.Equal("https://www.bing.com/search?q=google%20map", items[0].Url);
    }

    [Fact]
    public void 地址样输入的第一行应是直接打开()
    {
        var items = OmniboxSuggestions.Build("example.com/path", [], Template);

        Assert.Single(items);
        Assert.Equal(SuggestionKind.Url, items[0].Kind);
        Assert.Equal("https://example.com/path", items[0].Url);
    }

    [Fact]
    public void 带协议前缀的输入不应被重复加协议()
    {
        var items = OmniboxSuggestions.Build("http://example.com/", [], Template);

        Assert.Equal("http://example.com/", items[0].Url);
    }

    [Fact]
    public void 历史项应按匹配强度排序且同档优先访问次数()
    {
        var history = new[]
        {
            Entry("https://other.example/x", "无关标题", visits: 99),
            Entry("https://x.example/abc", "标题", visits: 7),            // 仅包含
            Entry("https://abc-shop.example/", "随便", visits: 1),         // 地址前缀（最强）
            Entry("https://cdn.abc.example/", "随便", visits: 3),          // 仅包含，访问次数居中
        };

        var items = OmniboxSuggestions.Build("abc", history, Template);

        // 第一行是搜索/打开候选，其后按强度；同档按访问次数降序。
        Assert.Equal("https://abc-shop.example/", items[1].Url);
        Assert.Equal("https://x.example/abc", items[2].Url);
        Assert.Equal("https://cdn.abc.example/", items[3].Url);
    }

    [Fact]
    public void 标题前缀应强于纯包含()
    {
        var history = new[]
        {
            Entry("https://yyy.example/abc", "无关", visits: 50),          // 仅包含
            Entry("https://zzz.example/page", "abc 开头的标题", visits: 1), // 标题前缀
        };

        var items = OmniboxSuggestions.Build("abc", history, Template);

        Assert.Equal("https://zzz.example/page", items[1].Url);
    }

    [Fact]
    public void 与首行地址相同的历史项不应重复出现()
    {
        var history = new[] { Entry("https://example.com/path", "Example") };

        var items = OmniboxSuggestions.Build("example.com/path", history, Template);

        Assert.Single(items);
    }

    [Fact]
    public void 候选数量不超过上限()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => Entry($"https://site{i}.example/page", $"站点 {i}"))
            .ToArray();

        var items = OmniboxSuggestions.Build("site", history, Template);

        Assert.Equal(OmniboxSuggestions.MaxItems, items.Count);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("example.com", true)]
    [InlineData("example.com/a/b?q=1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("hello world", false)]
    [InlineData("google map", false)]
    [InlineData("中文关键词", false)]
    [InlineData("", false)]
    public void 像不像地址的判定(string input, bool expected)
    {
        Assert.Equal(expected, OmniboxSuggestions.IsUrlLike(input));
    }

    [Theory]
    [InlineData("https://www.bing.com/search?q={0}", "Bing")]
    [InlineData("https://www.google.com/search?q={0}", "Google")]
    [InlineData("https://www.baidu.com/s?wd={0}", "百度")]
    [InlineData("https://search.marcia.dev/?q={0}", "search.marcia.dev")]
    public void 搜索行应显示搜索引擎名(string template, string expected)
    {
        Assert.Equal(expected, OmniboxSuggestions.EngineName(template));
    }
}
