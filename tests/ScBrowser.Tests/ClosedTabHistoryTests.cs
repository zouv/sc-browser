using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 「最近关闭的标签」栈：Ctrl+Shift+T 的还原顺序与容量上限。
/// </summary>
public class ClosedTabHistoryTests
{
    [Fact]
    public void 后进先出地还原()
    {
        var history = new ClosedTabHistory();
        history.Push("https://a.example/", "A");
        history.Push("https://b.example/", "B");

        Assert.True(history.TryPop(out var first));
        Assert.Equal("https://b.example/", first.Url);

        Assert.True(history.TryPop(out var second));
        Assert.Equal("https://a.example/", second.Url);

        Assert.False(history.TryPop(out _));
    }

    [Fact]
    public void 空白地址不入栈()
    {
        var history = new ClosedTabHistory();
        history.Push(null, "无地址");
        history.Push("   ", "空白");

        Assert.Equal(0, history.Count);
        Assert.False(history.TryPop(out _));
    }

    [Fact]
    public void 超出容量时丢弃最早的记录()
    {
        var history = new ClosedTabHistory();
        for (var i = 0; i < ClosedTabHistory.Capacity + 5; i++)
        {
            history.Push($"https://example.com/{i}", $"第 {i} 个");
        }

        Assert.Equal(ClosedTabHistory.Capacity, history.Count);

        // 栈顶仍是最新一条；最早的那几条已被丢弃。
        Assert.True(history.TryPop(out var newest));
        Assert.Equal($"https://example.com/{ClosedTabHistory.Capacity + 4}", newest.Url);

        var urls = new List<string>();
        while (history.TryPop(out var entry))
        {
            urls.Add(entry.Url);
        }

        Assert.DoesNotContain("https://example.com/0", urls);
        Assert.Contains("https://example.com/5", urls);
    }

    [Fact]
    public void 标题为空时不应抛异常()
    {
        var history = new ClosedTabHistory();
        history.Push("https://a.example/", null);

        Assert.True(history.TryPop(out var entry));
        Assert.Equal(string.Empty, entry.Title);
    }
}
