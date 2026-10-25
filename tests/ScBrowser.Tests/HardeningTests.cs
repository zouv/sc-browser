using System.IO;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;
using ScBrowser.Services;

namespace ScBrowser.Tests;

/// <summary>阶段性加固的小件：命令行地址解析、原子写、路径判定、设置合并。</summary>
public class HardeningTests : IDisposable
{
    private readonly string _workDir;

    public HardeningTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "scbrowser-hardening-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不应让测试变红。
        }

        GC.SuppressFinalize(this);
    }

    // ---------------- 命令行地址 ----------------

    [Fact]
    public void 命令行_地址与本地文件应被识别_选项与扩展路径应被跳过()
    {
        var page = Path.Combine(_workDir, "page.html");
        File.WriteAllText(page, "<html></html>");

        var urls = StartupCommandRunner.ExtractUrls(
        [
            "https://example.com/a",
            "--install-extension", @"D:\path\to\ext",
            "--some-flag",
            "example.org",
            page,
        ]);

        Assert.Equal(
            ["https://example.com/a", "https://example.org", new Uri(page).AbsoluteUri],
            urls);
    }

    [Fact]
    public void 命令行_没有地址时返回空()
    {
        Assert.Empty(StartupCommandRunner.ExtractUrls([]));
        Assert.Empty(StartupCommandRunner.ExtractUrls(["--install-extension=D:\\x"]));
        Assert.True(StartupCommandRunner.HasCommands(["--install-extension=D:\\x"]));
        Assert.False(StartupCommandRunner.HasCommands(["https://a.com"]));
    }

    // ---------------- 原子写 ----------------

    [Fact]
    public void 原子写_覆盖旧内容且不留临时文件()
    {
        var path = Path.Combine(_workDir, "sub", "settings.json");
        AtomicFile.WriteAllText(path, "旧");
        AtomicFile.WriteAllText(path, "新");

        Assert.Equal("新", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    // ---------------- 路径判定 ----------------

    [Theory]
    [InlineData(@"C:\data\Extensions\foo\a.js", @"C:\data\Extensions\foo", true)]
    [InlineData(@"C:\data\Extensions\foo\a.js", @"C:\data\Extensions\foo\", true)]
    [InlineData(@"C:\data\Extensions\foo-evil\a.js", @"C:\data\Extensions\foo", false)]
    [InlineData(@"C:\data\Extensions\foo", @"C:\data\Extensions\foo", false)]
    public void 路径判定应补分隔符比较(string path, string directory, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsUnderDirectory(path, directory));
    }

    // ---------------- 设置合并 ----------------

    [Fact]
    public void 设置页保存只合并偏好字段_不清空窗口几何()
    {
        var current = new AppSettings { WindowWidth = 1200, WindowLeft = 10, WindowMaximized = true };
        var fromPage = new AppSettings { Theme = "Dark", HomePage = InternalPageRegistry.NewTabUrl, EnableExtensions = false };

        current.CopyPreferencesFrom(fromPage);

        Assert.Equal("Dark", current.Theme);
        Assert.False(current.EnableExtensions);
        Assert.Equal(1200, current.WindowWidth);
        Assert.Equal(10, current.WindowLeft);
        Assert.True(current.WindowMaximized);
    }
}
