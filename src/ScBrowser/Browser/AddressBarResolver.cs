namespace ScBrowser.Browser;

/// <summary>
/// 把地址栏里的用户输入解析成可导航的目标地址。
/// 规则对齐主流浏览器：内部页面 &gt; 显式协议 &gt; 裸域名 &gt; 搜索词。
/// </summary>
public static class AddressBarResolver
{
    /// <summary>
    /// 默认搜索引擎模板。<c>{0}</c> 会被替换为 URL 编码后的查询词。
    /// 后续接入「设置」页后可改为用户自定义值（见 SettingsService）。
    /// </summary>
    public static string SearchTemplate { get; set; } = "https://www.bing.com/search?q={0}";

    /// <summary>可被直接放行的协议前缀（用户显式输入时不做任何改写）。</summary>
    private static readonly string[] KnownSchemes =
    [
        "http://", "https://", "file://", "about:", "data:", "view-source:",
        "edge://", "chrome://", "browser://", "ftp://",
    ];

    /// <summary>
    /// 解析用户输入。
    /// </summary>
    /// <param name="input">地址栏原始文本。</param>
    /// <returns>可直接交给 <c>CoreWebView2.Navigate</c> 的地址。</returns>
    public static string Resolve(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return InternalPageRegistry.NewTabUrl;
        }

        // 1) 内部页面：browser://xxx
        if (InternalPageRegistry.IsInternalPage(text))
        {
            return text;
        }

        // 2) 显式协议：原样放行
        foreach (var scheme in KnownSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        // 3) 形如 example.com / localhost:3000 / 192.168.1.1 的裸地址 → 补 https
        if (LooksLikeHost(text))
        {
            return "https://" + text;
        }

        // 4) 其余一律当搜索词
        return string.Format(SearchTemplate, Uri.EscapeDataString(text));
    }

    /// <summary>
    /// 判断文本是否像「主机名或 IP[:端口][/路径]」。
    /// 刻意保守：只有明确像域名的才补协议，避免把 "hello world" 这类查询词误判成域名。
    /// </summary>
    private static bool LooksLikeHost(string text)
    {
        // 含空格 → 视为搜索词
        if (text.Contains(' '))
        {
            return false;
        }

        // 取主机部分（去掉路径与查询）
        var host = text;
        var slash = host.IndexOfAny(['/', '?', '#']);
        if (slash >= 0)
        {
            host = host[..slash];
        }

        // 去掉端口
        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            var port = host[(colon + 1)..];
            // 端口必须是数字，否则不算主机（例如 "note:hello" 不是地址）
            if (port.Length == 0 || !port.All(char.IsDigit))
            {
                return false;
            }

            host = host[..colon];
        }

        if (host.Length == 0)
        {
            return false;
        }

        // localhost 特判
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // IPv4 字面量
        if (host.Count(c => c == '.') == 3 && host.All(c => char.IsDigit(c) || c == '.'))
        {
            return true;
        }

        // 域名：至少一个点，且点两侧都是合法的域名标签字符；顶级标签必须是字母且长度 >= 2
        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
        }

        var tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter);
    }
}
