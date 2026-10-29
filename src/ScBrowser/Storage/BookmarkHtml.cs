using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ScBrowser.Storage;

/// <summary>
/// Netscape Bookmark HTML（Chrome / Edge / Firefox 导出书签用的格式）的解析与生成。
/// 纯函数，供书签「导入 / 导出」使用；与数据库无关，树结构用 <see cref="Node"/> 表达。
/// <para>
/// 格式约定（各浏览器导出都遵守）：文件夹是 <c>&lt;DT&gt;&lt;H3&gt;名称&lt;/H3&gt;</c> 后跟一个
/// <c>&lt;DL&gt;&lt;p&gt;</c> 包裹子树；书签是 <c>&lt;DT&gt;&lt;A HREF="…"&gt;标题&lt;/A&gt;</c>。
/// 解析用**按行扫描**而不是完整 HTML 解析器——导出文件结构规整，行扫描足够，
/// 且对属性顺序、大小写、换行更宽容。
/// </para>
/// </summary>
public static class BookmarkHtml
{
    /// <summary>导入/导出树里的一个节点：文件夹或书签。</summary>
    public sealed record Node(string Title, string Url = "", IReadOnlyList<Node> Children = null!)
    {
        public bool IsFolder => Url.Length == 0;
        public IReadOnlyList<Node> Children { get; } = Children ?? [];
    }

    /// <summary>文件夹行：<c>&lt;DT&gt;&lt;H3&gt;Title&lt;/H3&gt;</c>。</summary>
    private const string H3Pattern = @"^\s*<DT>\s*<H3[^>]*>(.*)</H3>\s*$";

    /// <summary>书签行：<c>&lt;DT&gt;&lt;A HREF="…"&gt;Title&lt;/A&gt;</c>。</summary>
    private const string AnchorPattern = @"^\s*<DT>\s*<A\s[^>]*HREF\s*=\s*""([^""]*)""[^>]*>(.*)</A>";

    private static readonly Regex H3Regex = new(H3Pattern, RegexOptions.IgnoreCase);
    private static readonly Regex AnchorRegex = new(AnchorPattern, RegexOptions.IgnoreCase);

    /// <summary>解析导入的 HTML 文本；失败/空内容返回空列表（调用方据空列表提示）。</summary>
    public static IReadOnlyList<Node> Parse(string html)
    {
        var result = new List<Node>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        // 结构栈：当前打开的各级 <DL> 对应的「目标列表」。根之外每遇到 <DT><H3> 就把
        // 子树挂到当前列表，并把「子树列表」压栈；遇到 </DL> 出栈。
        var stack = new List<List<Node>> { result };

        foreach (var rawLine in html.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<META", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<TITLE>", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<H1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Equals("</DL><p>", StringComparison.OrdinalIgnoreCase)
                || line.Equals("</DL>", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count > 1)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            var folder = H3Regex.Match(line);
            if (folder.Success)
            {
                var children = new List<Node>();
                stack[^1].Add(new Node(Decode(folder.Groups[1].Value), Children: children));
                stack.Add(children);
                continue;
            }

            var bookmark = AnchorRegex.Match(line);
            if (bookmark.Success)
            {
                var url = Decode(bookmark.Groups[1].Value).Trim();
                if (url.Length > 0)
                {
                    stack[^1].Add(new Node(Decode(bookmark.Groups[2].Value), url));
                }
            }
            // 其余行（<DL><p> 起始、<HR>、空行）忽略。
        }

        return result;
    }

    /// <summary>生成可被各浏览器导入的 HTML（Chrome 导出文件的同款结构）。</summary>
    public static string Generate(IReadOnlyList<Node> roots)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        builder.AppendLine("<!-- This is an automatically generated file.");
        builder.AppendLine("     It will be read and overwritten.");
        builder.AppendLine("     DO NOT EDIT! -->");
        builder.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        builder.AppendLine("<TITLE>Bookmarks</TITLE>");
        builder.AppendLine("<H1>Bookmarks</H1>");
        builder.AppendLine("<DL><p>");

        foreach (var node in roots)
        {
            AppendNode(builder, node, 1);
        }

        builder.AppendLine("</DL><p>");
        return builder.ToString();
    }

    private static void AppendNode(StringBuilder builder, Node node, int depth)
    {
        var indent = new string(' ', depth * 4);
        if (node.IsFolder)
        {
            builder.Append(indent).Append("<DT><H3>").Append(Encode(node.Title)).AppendLine("</H3>");
            builder.Append(indent).AppendLine("<DL><p>");
            foreach (var child in node.Children)
            {
                AppendNode(builder, child, depth + 1);
            }

            builder.Append(indent).AppendLine("</DL><p>");
        }
        else
        {
            builder.Append(indent)
                .Append("<DT><A HREF=\"")
                .Append(Encode(node.Url))
                .Append("\">")
                .Append(Encode(node.Title))
                .AppendLine("</A>");
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Decode(string text) => WebUtility.HtmlDecode(text ?? string.Empty).Trim();
}
