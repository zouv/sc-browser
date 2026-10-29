using ScBrowser.Storage;

namespace ScBrowser.Tests;

/// <summary>书签 HTML 导入导出的纯函数测试（Chrome / Edge / Firefox 同款 Netscape 格式）。</summary>
public class BookmarkHtmlTests
{
    private static BookmarkHtml.Node Folder(string title, params BookmarkHtml.Node[] children) =>
        new(title, Children: children);

    private static BookmarkHtml.Node Bookmark(string title, string url) => new(title, url);

    [Fact]
    public void 生成再解析应还原树结构()
    {
        var tree = new[]
        {
            Bookmark("ScBrowser", "https://github.com/zouv/sc-browser"),
            Folder("开发", Bookmark("dotnet", "https://dotnet.microsoft.com")),
            Folder("空文件夹"),
            Bookmark("带特殊字符 <&> \"' 的标题", "https://example.com/?a=1&b=<x>"),
        };

        var html = BookmarkHtml.Generate(tree);
        var parsed = BookmarkHtml.Parse(html);

        Assert.Equal(tree.Length, parsed.Count);
        Assert.False(parsed[0].IsFolder);
        Assert.Equal("ScBrowser", parsed[0].Title);
        Assert.Equal("https://github.com/zouv/sc-browser", parsed[0].Url);

        Assert.True(parsed[1].IsFolder);
        Assert.Equal("开发", parsed[1].Title);
        var child = Assert.Single(parsed[1].Children);
        Assert.Equal("https://dotnet.microsoft.com", child.Url);

        Assert.Empty(parsed[2].Children);

        // 特殊字符应完整往返（生成时转义、解析时还原）。
        Assert.Equal("带特殊字符 <&> \"' 的标题", parsed[3].Title);
        Assert.Equal("https://example.com/?a=1&b=<x>", parsed[3].Url);
    }

    [Fact]
    public void 应能解析Chrome导出的典型文件()
    {
        // Chrome 导出文件的真实结构：DOCTYPE / META / H1 / DL 嵌套 / ADD_DATE 属性。
        var html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <!-- This is an automatically generated file.
                 It will be read and overwritten.
                 DO NOT EDIT! -->
            <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
            <TITLE>Bookmarks</TITLE>
            <H1>Bookmarks</H1>
            <DL><p>
                <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="1700000000">书签栏</H3>
                <DL><p>
                    <DT><A HREF="https://example.com/" ADD_DATE="1700000000" ICON="data:image/png;base64,xxx">示例站点</A>
                    <DT><A HREF="https://example.org/路径?q=1&amp;r=2">中文路径与实体</A>
                </DL><p>
                <DT><A HREF="https://standalone.example/">根下的书签</A>
            </DL><p>
            """;

        var parsed = BookmarkHtml.Parse(html);

        Assert.Equal(2, parsed.Count);
        Assert.True(parsed[0].IsFolder);
        Assert.Equal("书签栏", parsed[0].Title);
        Assert.Equal(2, parsed[0].Children.Count);
        Assert.Equal("示例站点", parsed[0].Children[0].Title);
        Assert.Equal("https://example.com/", parsed[0].Children[0].Url);
        Assert.Equal("https://example.org/路径?q=1&r=2", parsed[0].Children[1].Url);
        Assert.Equal("https://standalone.example/", parsed[1].Url);
    }

    [Fact]
    public void 缺失闭合标签与杂散行应被宽容处理()
    {
        var html = """
            <DL><p>
                <DT><H3>文件夹（没有闭合 DL）</H3>
                <DL><p>
                    <DT><A HREF="https://a.example/">A</A>
                <HR>
                这是一行不该出现的垃圾文本
                <DT><A HREF="https://b.example/">B</A>
            """;

        var parsed = BookmarkHtml.Parse(html);

        var folder = Assert.Single(parsed);
        Assert.True(folder.IsFolder);
        Assert.Equal(2, folder.Children.Count);
        Assert.Equal("https://b.example/", folder.Children[1].Url);
    }

    [Fact]
    public void 空内容与纯头部应返回空列表()
    {
        Assert.Empty(BookmarkHtml.Parse(""));
        Assert.Empty(BookmarkHtml.Parse("   \n  "));
        Assert.Empty(BookmarkHtml.Parse("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<TITLE>Bookmarks</TITLE>"));
    }

    [Fact]
    public void 空文件夹应保持为空()
    {
        var parsed = BookmarkHtml.Parse("<DL><p>\n<DT><H3>空</H3>\n<DL><p>\n</DL><p>\n</DL><p>");
        var folder = Assert.Single(parsed);
        Assert.True(folder.IsFolder);
        Assert.Empty(folder.Children);
    }
}
