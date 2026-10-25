using System.IO;
using System.IO.Compression;
using System.Text;
using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// CRX 解包的用例。用<b>合成</b>的 CRX 文件测试——不依赖外部样本，
/// 也就能覆盖 v2/v3 两种头布局、坏文件、目录穿越等边界。
/// </summary>
public class CrxPackageTests : IDisposable
{
    private readonly string _workDir;

    public CrxPackageTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "scbrowser-crx-tests", Guid.NewGuid().ToString("N"));
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
            // 测试收尾清理失败不应让测试变红。
        }

        GC.SuppressFinalize(this);
    }

    // ---------------- 合成 CRX ----------------

    /// <summary>构造一个 v3 CRX：魔数 + 版本 + 头部长度 + 头部 + ZIP。</summary>
    private static byte[] BuildCrxV3(Dictionary<string, string> files, int headerLength = 16)
    {
        var zip = BuildZip(files);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write("Cr24"u8.ToArray());
        w.Write(3u);                       // 版本
        w.Write((uint)headerLength);       // 头部长度（内容本身对解包无意义）
        w.Write(new byte[headerLength]);   // 占位头部（真实文件里是 protobuf，含扩展 ID）
        w.Write(zip);
        w.Flush();

        return ms.ToArray();
    }

    /// <summary>构造一个 v2 CRX：魔数 + 版本 + 公钥长度 + 签名长度 + 公钥 + 签名 + ZIP。</summary>
    private static byte[] BuildCrxV2(Dictionary<string, string> files, int keyLength = 8, int sigLength = 12)
    {
        var zip = BuildZip(files);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write("Cr24"u8.ToArray());
        w.Write(2u);
        w.Write((uint)keyLength);
        w.Write((uint)sigLength);
        w.Write(new byte[keyLength]);
        w.Write(new byte[sigLength]);
        w.Write(zip);
        w.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildZip(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return ms.ToArray();
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---------------- 用例 ----------------

    [Fact]
    public void v3_包应能正确解包()
    {
        var crx = WriteFile("sample.crx", BuildCrxV3(new Dictionary<string, string>
        {
            ["manifest.json"] = """{"manifest_version":3,"name":"t","version":"1.0"}""",
            ["js/content.js"] = "console.log(1);",
        }));

        var target = Path.Combine(_workDir, "out-v3");
        CrxPackage.ExtractTo(crx, target);

        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(target, "js", "content.js")));
        Assert.Contains("console.log", File.ReadAllText(Path.Combine(target, "js", "content.js")));
    }

    [Fact]
    public void v2_包应能正确解包()
    {
        var crx = WriteFile("sample-v2.crx", BuildCrxV2(new Dictionary<string, string>
        {
            ["manifest.json"] = """{"manifest_version":2,"name":"t","version":"1.0"}""",
        }));

        var target = Path.Combine(_workDir, "out-v2");
        CrxPackage.ExtractTo(crx, target);

        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void 头部长度不同也应定位到正确偏移()
    {
        // 偏移算错的典型表现就是「ZIP 头对不上」，用一个非 16 字节对齐的头部来夹住这个 bug。
        var crx = WriteFile("odd-header.crx", BuildCrxV3(
            new Dictionary<string, string> { ["manifest.json"] = "{}" },
            headerLength: 37));

        var target = Path.Combine(_workDir, "out-odd");
        CrxPackage.ExtractTo(crx, target);

        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void 重新解包到同一目录应先清空旧内容()
    {
        var target = Path.Combine(_workDir, "out-reinstall");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "stale.txt"), "旧文件");

        var crx = WriteFile("reinstall.crx", BuildCrxV3(
            new Dictionary<string, string> { ["manifest.json"] = "{}" }));

        CrxPackage.ExtractTo(crx, target);

        Assert.False(File.Exists(Path.Combine(target, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void 非_CRX_文件应被拒绝()
    {
        var fake = WriteFile("not-a-crx.crx", Encoding.UTF8.GetBytes("PK\u0003\u0004 这是个普通 zip"));

        Assert.False(CrxPackage.LooksLikeCrx(fake));
        Assert.Throws<InvalidDataException>(() => CrxPackage.ExtractTo(fake, Path.Combine(_workDir, "x")));
    }

    [Fact]
    public void 不支持的_CRX_版本应报错()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("Cr24"u8.ToArray());
            w.Write(99u);
            w.Write(0u);
        }

        var crx = WriteFile("bad-version.crx", ms.ToArray());

        var ex = Assert.Throws<InvalidDataException>(
            () => CrxPackage.ExtractTo(crx, Path.Combine(_workDir, "y")));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void 含越界路径的包应被拒绝()
    {
        // 目录穿越：ZIP 里写 ../ 逃出目标目录，解包器必须拦住。
        var crx = WriteFile("traversal.crx", BuildCrxV3(new Dictionary<string, string>
        {
            ["../escaped.txt"] = "不该被写出去",
        }));

        var target = Path.Combine(_workDir, "out-traversal");
        Assert.Throws<InvalidDataException>(() => CrxPackage.ExtractTo(crx, target));
        Assert.False(File.Exists(Path.Combine(_workDir, "escaped.txt")));
    }

    [Fact]
    public void 写入同前缀兄弟目录的穿越也应被拒绝()
    {
        // 只比「目标目录」前缀的旧实现会放行它：...\out-sibling-evil 也以 ...\out-sibling 开头。
        var crx = WriteFile("sibling.crx", BuildCrxV3(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["../out-sibling-evil/x.js"] = "不该被写出去",
        }));

        var target = Path.Combine(_workDir, "out-sibling");
        Assert.Throws<InvalidDataException>(() => CrxPackage.ExtractTo(crx, target));
        Assert.False(Directory.Exists(Path.Combine(_workDir, "out-sibling-evil")));
    }

    [Fact]
    public void 坏包不应删掉已存在的旧目录()
    {
        // 校验先于删除：装坏包失败后，之前装好的同名扩展应原样保留。
        var target = Path.Combine(_workDir, "out-keep");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "manifest.json"), "旧版本");

        var crx = WriteFile("bad.crx", BuildCrxV3(new Dictionary<string, string>
        {
            ["../escaped.txt"] = "x",
        }));

        Assert.Throws<InvalidDataException>(() => CrxPackage.ExtractTo(crx, target));
        Assert.Equal("旧版本", File.ReadAllText(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void 条目数超限应被拒绝()
    {
        var files = Enumerable.Range(0, CrxPackage.MaxEntries + 1)
            .ToDictionary(i => $"f{i}.txt", _ => string.Empty);
        var crx = WriteFile("many.crx", BuildCrxV3(files));

        var ex = Assert.Throws<InvalidDataException>(
            () => CrxPackage.ExtractTo(crx, Path.Combine(_workDir, "out-many")));
        Assert.Contains("条目数", ex.Message);
    }

    [Theory]
    [InlineData("my-ext.crx", "my-ext")]
    [InlineData("My Ext v2.crx", "My-Ext-v2")]
    [InlineData("_underscore.crx", "ext_underscore")]   // WebView2 拒绝以 _ 开头的目录名
    [InlineData("...dots....crx", "dots")]
    [InlineData(".crx", "extension")]                    // 清理后为空 → 兜底名
    public void 扩展目录名应被规整为安全形式(string fileName, string expected)
    {
        Assert.Equal(expected, CrxPackage.DeriveFolderName(fileName));
    }

    [Fact]
    public void 规整后的目录名不应包含非法字符()
    {
        var name = CrxPackage.DeriveFolderName("a<b>c:d/e\\f|g?h*i.crx");
        var invalid = Path.GetInvalidFileNameChars();

        Assert.DoesNotContain(name, invalid);
        Assert.False(name.StartsWith('_'));
    }
}
