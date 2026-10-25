using System.IO;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>
/// 内部页面注册表：<c>browser://xxx/</c> 自定义 scheme → 随程序分发的本地 WebUI 文件。
/// 对应 Chrome 的 <c>chrome://</c> 页面机制——与普通网页不同，这些页面由应用自己提供。
/// <para>
/// <b>为什么是自定义 scheme 而不是 <c>file://</c></b>：<c>file://</c> 页面与用户打开的任意本地
/// HTML 同源，没法据此判断「消息是不是内部页面发的」；而 <c>browser://&lt;页面名&gt;</c>
/// 每页一个独立来源，消息桥可以按来源做权限判定（见 <see cref="IsMessageAllowed"/>）。
/// 另外扩展内容脚本的 <c>&lt;all_urls&gt;</c> 不覆盖自定义 scheme，扩展也就无法注入内部页面、
/// 借它越权调用宿主——这也是不用 <c>https</c> 虚拟主机映射的原因。
/// </para>
/// </summary>
public static class InternalPageRegistry
{
    /// <summary>自定义 scheme 名（向 WebView2 环境注册时用）。</summary>
    public const string SchemeName = "browser";

    /// <summary>内部页面地址前缀。</summary>
    public const string Scheme = SchemeName + "://";

    /// <summary>所有页面共用的资源目录（<c>browser://&lt;页面&gt;/common/...</c>）。</summary>
    private const string CommonFolder = "common";

    /// <summary>一个内部页面：WebUI 目录 + 标签标题 + 它能调用的宿主消息（按前缀放行）。</summary>
    private sealed record PageEntry(string Folder, string Title, string[] AllowedMessagePrefixes);

    /// <summary>
    /// 内部页面名 → WebUI 目录与消息白名单。<c>browser://newtab/</c> → <c>Assets/ui/newtab/index.html</c>。
    /// 新增内部页面时在此登记一行，并只给它<b>确实需要</b>的消息前缀——
    /// 白名单是第二道防线：即使某个页面被注入了脚本，也调不到别的页面的能力。
    /// </summary>
    private static readonly Dictionary<string, PageEntry> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["newtab"] = new("newtab", "新标签页", ["navigation.", "history.topSites"]),
        ["history"] = new("history", "历史记录", ["navigation.", "history."]),
        ["bookmarks"] = new("bookmarks", "书签", ["navigation.", "bookmarks."]),
        ["settings"] = new("settings", "设置", ["navigation.", "settings.", "app."]),
        ["extensions"] = new("extensions", "扩展程序", ["navigation.", "extensions."]),
        ["downloads"] = new("downloads", "下载内容", ["navigation.", "downloads."]),
    };

    /// <summary>启动时使用的新标签页地址。</summary>
    public const string NewTabUrl = Scheme + "newtab";

    /// <summary>判断一个地址是否为内部页面。</summary>
    public static bool IsInternalPage(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>取内部页面名（<c>browser://settings/x?y</c> → <c>settings</c>）；不是内部页面返回 <c>false</c>。</summary>
    public static bool TryGetPageName(string? url, out string name)
    {
        name = string.Empty;
        if (!IsInternalPage(url))
        {
            return false;
        }

        var rest = url![Scheme.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        name = (end >= 0 ? rest[..end] : rest).ToLowerInvariant();
        return name.Length > 0;
    }

    /// <summary>是否为已登记的内部页面。</summary>
    public static bool IsKnownPage(string? url) =>
        TryGetPageName(url, out var name) && Pages.ContainsKey(name);

    /// <summary>
    /// 把内部页面地址规整成可交给 WebView2 导航的形式（<c>browser://settings</c> → <c>browser://settings/</c>）；
    /// 未登记的页面返回 <c>null</c>。
    /// </summary>
    public static string? ToNavigableUri(string url) =>
        TryGetPageName(url, out var name) && Pages.ContainsKey(name)
            ? Scheme + name + "/"
            : null;

    /// <summary>把内部页面地址规整化，供地址栏显示（<c>browser://settings/</c> → <c>browser://settings</c>）。</summary>
    public static string NormalizeDisplayUrl(string url) =>
        TryGetPageName(url, out var name) ? Scheme + name : url;

    /// <summary>
    /// 内部页面的显示标题（标签条用）。尚未加载引擎的标签也能据此显示「设置」而不是地址。
    /// </summary>
    public static string? GetPageTitle(string url) =>
        TryGetPageName(url, out var name) && Pages.TryGetValue(name, out var page) ? page.Title : null;

    /// <summary>
    /// 某个内部页面能否调用某条宿主消息。来源不是已登记的内部页面（即普通网页、本地文件）一律拒绝。
    /// </summary>
    /// <param name="sourceUrl">发消息的文档地址（WebView2 给的 <c>Source</c>，网页无法伪造）。</param>
    /// <param name="messageType">消息类型，如 <c>history.query</c>。</param>
    public static bool IsMessageAllowed(string? sourceUrl, string messageType)
    {
        if (!TryGetPageName(sourceUrl, out var name) || !Pages.TryGetValue(name, out var page))
        {
            return false;
        }

        foreach (var prefix in page.AllowedMessagePrefixes)
        {
            // 以 . 结尾的是「整个领域」，否则要求完全相等（避免 history.topSites 放行 history.topSitesX）。
            var allowed = prefix.EndsWith('.')
                ? messageType.StartsWith(prefix, StringComparison.Ordinal)
                : string.Equals(messageType, prefix, StringComparison.Ordinal);
            if (allowed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把一次 <c>browser://</c> 资源请求解析成本地文件路径；不存在、越界或页面未登记时返回 <c>null</c>。
    /// <para>
    /// 路径规则：<c>/</c> → 页面的 <c>index.html</c>；<c>/common/*</c> → 公共目录；其余在页面目录内找。
    /// WebUI 里写的 <c>../common/base.css</c> 在 <c>browser://newtab/</c> 下会被浏览器规整成
    /// <c>browser://newtab/common/base.css</c>（根路径之上没有 ..），所以页面无需改引用。
    /// </para>
    /// </summary>
    public static string? ResolveResourcePath(string requestUri) =>
        ResolveResourcePath(requestUri, AppPaths.WebUiFolder);

    /// <summary>同 <see cref="ResolveResourcePath(string)"/>，可指定 WebUI 根目录（单测用）。</summary>
    internal static string? ResolveResourcePath(string requestUri, string webUiRoot)
    {
        if (!TryGetPageName(requestUri, out var name)
            || !Pages.TryGetValue(name, out var page)
            || !Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        string relative;
        if (path.Length == 0 || path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.Combine(page.Folder, "index.html");
        }
        else if (path.StartsWith(CommonFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            relative = path;
        }
        else
        {
            relative = Path.Combine(page.Folder, path);
        }

        var root = Path.GetFullPath(webUiRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        // 防目录穿越：结果必须仍在 WebUI 根目录之内。
        if (!PathSafety.IsUnderDirectory(full, root))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    /// <summary>按扩展名给出响应的 Content-Type。</summary>
    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
