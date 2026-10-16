namespace ScBrowser.Browser;

/// <summary>
/// 最近关闭的标签页（后进先出），支撑 Ctrl+Shift+T。
/// <para>
/// 抽成独立小类是因为「容量上限 + 顺序」这两点最容易写错，而又完全不依赖 WebView2，
/// 可以直接单测；<see cref="TabManager"/> 只负责在关闭标签时往里塞一条。
/// </para>
/// </summary>
public sealed class ClosedTabHistory
{
    /// <summary>保留条数上限。够覆盖「手滑关掉几个」的常见场景，又不至于吃内存。</summary>
    public const int Capacity = 25;

    private readonly List<Entry> _entries = [];

    /// <summary>一条被关闭的标签记录。</summary>
    public readonly record struct Entry(string Url, string Title);

    public int Count => _entries.Count;

    /// <summary>记下一个被关闭的标签；空白地址（例如还没导航过的标签）不记录。</summary>
    public void Push(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _entries.Add(new Entry(url, title ?? string.Empty));
        if (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>取出最近关闭的一条（取走即出栈）。</summary>
    public bool TryPop(out Entry entry)
    {
        if (_entries.Count == 0)
        {
            entry = default;
            return false;
        }

        entry = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return true;
    }

    public void Clear() => _entries.Clear();
}
