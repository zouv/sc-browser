using System.IO;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>
/// 内部页面注册表：把 <c>browser://xxx</c> 伪协议映射到随程序分发的本地 WebUI 页面。
/// 对应 Chrome 的 <c>chrome://</c> 页面机制——与普通网页不同，这些页面由应用自己提供。
/// </summary>
public static class InternalPageRegistry
{
    /// <summary>内部页面伪协议前缀。</summary>
    public const string Scheme = "browser://";

    /// <summary>
    /// 内部页面名 → WebUI 目录名。<c>browser://newtab</c> → <c>Assets/ui/newtab/index.html</c>。
    /// 新增内部页面时在此登记一行即可。
    /// </summary>
    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["newtab"] = "newtab",
        ["history"] = "history",
        ["bookmarks"] = "bookmarks",
        ["settings"] = "settings",
        ["extensions"] = "extensions",
        ["downloads"] = "downloads",
    };

    /// <summary>启动时使用的新标签页地址。</summary>
    public const string NewTabUrl = Scheme + "newtab";

    /// <summary>判断一个地址是否为内部页面。</summary>
    public static bool IsInternalPage(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把内部页面地址解析为本地文件的 <c>file://</c> URI；无法识别时返回 <c>null</c>。
    /// </summary>
    public static string? ResolveToFileUri(string url)
    {
        if (!IsInternalPage(url))
        {
            return null;
        }

        var name = url[Scheme.Length..].Trim('/');
        // 去掉可能的查询串（如 browser://newtab?x=1）
        var q = name.IndexOf('?');
        if (q >= 0)
        {
            name = name[..q];
        }

        if (!Pages.TryGetValue(name, out var folder))
        {
            return null;
        }

        var indexPath = Path.Combine(AppPaths.WebUiFolder, folder, "index.html");
        return File.Exists(indexPath) ? new Uri(indexPath).AbsoluteUri : null;
    }

    /// <summary>把内部页面显示名规整化，供地址栏显示（<c>browser://settings</c>）。</summary>
    public static string NormalizeDisplayUrl(string url)
    {
        var name = url[Scheme.Length..].Trim('/');
        var q = name.IndexOf('?');
        if (q >= 0)
        {
            name = name[..q];
        }

        return Scheme + name;
    }
}
