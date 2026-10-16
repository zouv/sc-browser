using CommunityToolkit.Mvvm.ComponentModel;
using ScBrowser.Storage;

namespace ScBrowser.Services;

/// <summary>地址栏下拉里一行的类型，决定图标与点击后的行为。</summary>
public enum SuggestionKind
{
    /// <summary>「使用 XX 搜索」。回车即把输入交给搜索引擎。</summary>
    Search,

    /// <summary>「打开 <地址>」。输入本身已像个地址。</summary>
    Url,

    /// <summary>历史记录命中。</summary>
    History,
}

/// <summary>地址栏下拉里的一行。</summary>
public sealed partial class SuggestionItem : ObservableObject
{
    public SuggestionItem(SuggestionKind kind, string title, string url, string displayUrl)
    {
        Kind = kind;
        Title = title;
        Url = url;
        DisplayUrl = displayUrl;
    }

    public SuggestionKind Kind { get; }

    /// <summary>主行文字。</summary>
    public string Title { get; }

    /// <summary>选中后实际导航到的地址。</summary>
    public string Url { get; }

    /// <summary>行右侧展示的地址（历史行才需要）。</summary>
    public string DisplayUrl { get; }

    /// <summary>是否键盘高亮（由视图模型维护，避免用 ListBox 的选中态）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>左侧图标（Segoe Fluent Icons 字形；用 \u 转义书写，避免源码出现私用区字符）。</summary>
    public string Glyph => Kind switch
    {
        SuggestionKind.Search => "\uE721",   // 放大镜
        SuggestionKind.History => "\uE81C",  // 时钟
        _ => "\uE774",                        // 地球
    };
}

/// <summary>
/// 地址栏下拉候选的构建逻辑（纯函数，便于单测）。
/// <para>
/// 内容来源刻意只取<b>本地历史</b>：不外发输入内容，也就没有额外隐私成本。
/// 第一行始终是「打开 / 搜索」候选——用户敲完直接回车就能走，不必先在下拉里选。
/// </para>
/// </summary>
public static class OmniboxSuggestions
{
    /// <summary>下拉最多显示几行。</summary>
    public const int MaxItems = 8;

    /// <summary>
    /// 生成候选列表。
    /// </summary>
    /// <param name="input">地址栏当前输入。</param>
    /// <param name="history">历史查询结果（调用方已按关键字过滤，这里再做排序与截断）。</param>
    /// <param name="searchTemplate">搜索模板，形如 <c>https://www.bing.com/search?q={0}</c>。</param>
    public static IReadOnlyList<SuggestionItem> Build(
        string? input,
        IReadOnlyList<HistoryEntry> history,
        string searchTemplate)
    {
        var text = input?.Trim() ?? string.Empty;
        var items = new List<SuggestionItem>();

        if (text.Length == 0)
        {
            // 空输入（刚聚焦时）：给「最近访问」，与 Chrome 聚焦时的观感一致。
            items.AddRange(history.Take(MaxItems).Select(ToHistoryItem));
            return items;
        }

        items.Add(BuildPrimaryItem(text, searchTemplate));

        foreach (var entry in Rank(history, text))
        {
            if (items.Count >= MaxItems)
            {
                break;
            }

            if (string.Equals(entry.Url, items[0].Url, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(ToHistoryItem(entry));
        }

        return items;
    }

    /// <summary>第一行：像是地址就「打开」，否则「使用 XX 搜索」。</summary>
    private static SuggestionItem BuildPrimaryItem(string text, string searchTemplate)
    {
        if (IsUrlLike(text))
        {
            var target = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
            return new SuggestionItem(SuggestionKind.Url, target, target, "直接打开");
        }

        var engine = EngineName(searchTemplate);
        var url = string.Format(searchTemplate, Uri.EscapeDataString(text));
        return new SuggestionItem(SuggestionKind.Search, $"使用 {engine} 搜索「{text}」", url, "搜索");
    }

    private static SuggestionItem ToHistoryItem(HistoryEntry entry)
    {
        var title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title;
        return new SuggestionItem(SuggestionKind.History, title, entry.Url, HostOf(entry.Url));
    }

    /// <summary>
    /// 匹配强度排序：地址（去协议）前缀 &gt; 标题前缀 &gt; 其它包含；
    /// 同档内按访问次数、再按最近访问时间——「常去且刚去过」的排前面。
    /// </summary>
    private static IEnumerable<HistoryEntry> Rank(IReadOnlyList<HistoryEntry> history, string text) =>
        history
            .Select(entry => (Entry: entry, Score: Score(entry, text)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.VisitCount)
            .ThenByDescending(x => x.Entry.LastVisit)
            .Select(x => x.Entry);

    private static int Score(HistoryEntry entry, string text)
    {
        // 用户敲的是站点名而不是「https://…」，所以比较地址前缀前先剥掉协议——
        // 否则输入「abc」永远匹配不上「https://abc.example/」这个最该排第一的行。
        var target = StripScheme(entry.Url);

        if (target.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (entry.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return target.Contains(text, StringComparison.OrdinalIgnoreCase)
               || entry.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static string StripScheme(string url)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        return separator >= 0 ? url[(separator + 3)..] : url;
    }

    /// <summary>
    /// 输入是否已经像个地址：带协议，或「不含空格且有点」。
    /// 只是启发式——真正决定走地址还是搜索的是 <c>AddressBarResolver</c>，
    /// 这里仅用于决定下拉第一行显示「打开」还是「搜索」。
    /// </summary>
    public static bool IsUrlLike(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Contains(' ') || text.Contains('　'))
        {
            return false;
        }

        var head = text.Split('/', '?', '#')[0];

        // localhost:8080 —— 端口形式也当作地址（本地开发常这么输）。
        var colon = head.IndexOf(':');
        if (colon > 0 && head[(colon + 1)..].All(char.IsAsciiDigit) && head[(colon + 1)..].Length > 0)
        {
            return true;
        }

        // example.com、192.168.1.1 算；「hello」「中文词」不算。
        return head.Contains('.') && !head.StartsWith('.') && !head.EndsWith('.');
    }

    /// <summary>从搜索模板里认出搜索引擎名，认不出就用它的主机名。</summary>
    public static string EngineName(string searchTemplate)
    {
        var host = HostOf(searchTemplate);
        foreach (var (needle, name) in KnownEngines)
        {
            if (host.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return string.IsNullOrEmpty(host) ? "搜索引擎" : host;
    }

    private static readonly (string Needle, string Name)[] KnownEngines =
    [
        ("bing", "Bing"),
        ("google", "Google"),
        ("baidu", "百度"),
        ("duckduckgo", "DuckDuckGo"),
        ("sogou", "搜狗"),
        ("so.com", "360 搜索"),
    ];

    /// <summary>取地址的主机名；解析不出来时退回原串（便于前缀比较）。</summary>
    private static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return url;
    }
}
