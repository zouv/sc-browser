using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 扩展安装链路：CRX3 验签与 ID 推导、商店链接解析、manifest 元数据。
/// CRX 用测试里现生成的 RSA 密钥签，不依赖外部样本。
/// </summary>
public class ExtensionTests : IDisposable
{
    private readonly string _dir;
    private readonly RSA _key;

    public ExtensionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scbrowser-ext-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _key = RSA.Create(2048);
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论。
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ //
    // 合成 CRX3
    // ------------------------------------------------------------------ //

    private byte[] BuildSignedCrx3(Dictionary<string, string> files)
    {
        var zip = BuildZip(files);
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        var signedHeaderData = Field(1, CrxPackage.IdBytes(publicKey));

        var signedData = Concat(
            Encoding.ASCII.GetBytes("CRX3 SignedData\0"),
            BitConverter.GetBytes((uint)signedHeaderData.Length),
            signedHeaderData,
            zip);

        var signature = _key.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var proof = Concat(Field(1, publicKey), Field(2, signature));
        var header = Concat(Field(2, proof), Field(10000, signedHeaderData));

        return Concat(
            "Cr24"u8.ToArray(),
            BitConverter.GetBytes(3u),
            BitConverter.GetBytes((uint)header.Length),
            header,
            zip);
    }

    private static byte[] Field(int number, byte[] data) =>
        Concat(Varint((ulong)((number << 3) | 2)), Varint((ulong)data.Length), data);

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(p => p.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
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
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ------------------------------------------------------------------ //
    // CRX 解析与验签
    // ------------------------------------------------------------------ //

    [Fact]
    public void 签名的_CRX3_应能通过校验并算出扩展_ID()
    {
        var crx = WriteFile("signed.crx", BuildSignedCrx3(new Dictionary<string, string>
        {
            ["manifest.json"] = """{"manifest_version":3,"name":"T","version":"1.2.3"}""",
        }));

        var info = CrxPackage.Read(crx);

        Assert.Equal(3, info.FormatVersion);
        Assert.True(CrxPackage.LooksLikeExtensionId(info.ExtensionId));
        Assert.Equal(CrxPackage.ComputeExtensionId(_key.ExportSubjectPublicKeyInfo()), info.ExtensionId);

        var target = Path.Combine(_dir, "out");
        CrxPackage.ExtractTo(crx, target, info);
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void 内容被篡改的_CRX3_应验签失败()
    {
        var bytes = BuildSignedCrx3(new Dictionary<string, string>
        {
            ["manifest.json"] = """{"manifest_version":3,"name":"T","version":"1"}""",
        });

        // 改 ZIP 段的最后一个字节（中央目录里的名字/内容都会受影响）。
        bytes[^1] ^= 0xFF;
        var crx = WriteFile("tampered.crx", bytes);

        var ex = Assert.Throws<InvalidDataException>(() => CrxPackage.Read(crx));
        Assert.Contains("签名", ex.Message);
    }

    [Fact]
    public void 未签名的_CRX3_应被拒绝()
    {
        // 没有 proof 的头部：ExtractTo 能解包，但 Read（安装走的就是它）必须拒绝。
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            var zip = BuildZip(new Dictionary<string, string> { ["manifest.json"] = "{}" });
            w.Write("Cr24"u8.ToArray());
            w.Write(3u);
            w.Write(0u);
            w.Write(zip);
        }

        var crx = WriteFile("unsigned.crx", ms.ToArray());
        Assert.Throws<InvalidDataException>(() => CrxPackage.Read(crx));
    }

    [Fact]
    public void 公钥与扩展_ID_的映射应符合_Chrome_规则()
    {
        // 独立复算一遍：SHA-256 前 16 字节，每个半字节映射到 a–p。
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(publicKey);
        var expected = new string(hash.Take(16)
            .SelectMany(b => new[]
            {
                (char)('a' + (b >> 4)),
                (char)('a' + (b & 0x0F)),
            })
            .ToArray());

        Assert.Equal(expected, CrxPackage.ComputeExtensionId(publicKey));
    }

    // ------------------------------------------------------------------ //
    // 商店链接 / ID
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", "abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin-lite/cimighlppcgcoapaliogpjjdehbnofhn",
        "cimighlppcgcoapaliogpjjdehbnofhn")]
    [InlineData("https://chrome.google.com/webstore/detail/abc/cimighlppcgcoapaliogpjjdehbnofhn?hl=zh-CN",
        "cimighlppcgcoapaliogpjjdehbnofhn")]
    [InlineData("https://microsoftedge.microsoft.com/addons/detail/x/abcdefghijklmnopabcdefghijklmnop",
        "abcdefghijklmnopabcdefghijklmnop")]
    public void 应从链接或纯_ID_中识别扩展_ID(string input, string expected)
    {
        Assert.Equal(expected, ExtensionStoreClient.ExtractId(input));
    }

    [Theory]
    [InlineData("https://example.com/no-id-here")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")]   // 31 位
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")] // 大写不是合法 ID
    [InlineData("")]
    public void 非法的商店输入应识别失败(string input)
    {
        Assert.Null(ExtensionStoreClient.ExtractId(input));
    }

    [Fact]
    public void 应识别_Edge_商店链接()
    {
        Assert.True(ExtensionStoreClient.IsEdgeStore("https://microsoftedge.microsoft.com/addons/detail/x/abc"));
        Assert.False(ExtensionStoreClient.IsEdgeStore("https://chromewebstore.google.com/detail/x/abc"));
    }

    // ------------------------------------------------------------------ //
    // manifest 元数据
    // ------------------------------------------------------------------ //

    [Fact]
    public void 应解析_manifest_的名称_权限_图标与弹窗()
    {
        var folder = Path.Combine(_dir, "ext");
        Directory.CreateDirectory(Path.Combine(folder, "_locales", "zh_CN"));
        Directory.CreateDirectory(Path.Combine(folder, "icons"));
        File.WriteAllBytes(Path.Combine(folder, "icons", "128.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(folder, "_locales", "zh_CN", "messages.json"),
            """{"extName":{"message":"示例扩展"},"extDesc":{"message":"说明文字"}}""");
        File.WriteAllText(Path.Combine(folder, "manifest.json"),
            """
            {
              "manifest_version": 3,
              "name": "__MSG_extName__",
              "description": "__MSG_extDesc__",
              "version": "2.0.1",
              "permissions": ["storage", "tabs"],
              "host_permissions": ["https://*.example.com/*"],
              "icons": { "16": "icons/16.png", "128": "icons/128.png" },
              "action": { "default_popup": "popup.html" },
              "options_page": "options.html",
              "update_url": "https://example.com/update.xml"
            }
            """);

        var metadata = ExtensionManifest.Read(folder);

        Assert.NotNull(metadata);
        Assert.Equal("示例扩展", metadata!.Name);
        Assert.Equal("说明文字", metadata.Description);
        Assert.Equal("2.0.1", metadata.Version);
        Assert.Equal(3, metadata.ManifestVersion);
        Assert.Equal(["storage", "tabs", "https://*.example.com/*"], metadata.Permissions);
        Assert.EndsWith(Path.Combine("icons", "128.png"), metadata.IconPath);
        Assert.Equal("popup.html", metadata.PopupPath);
        Assert.Equal("options.html", metadata.OptionsPath);
        Assert.Equal("https://example.com/update.xml", metadata.UpdateUrl);
    }

    [Fact]
    public void 写入_key_后应能由_manifest_反推扩展_ID()
    {
        var folder = Path.Combine(_dir, "key-ext");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"),
            """{"manifest_version":3,"name":"K","version":"1.0"}""");

        var publicKey = _key.ExportSubjectPublicKeyInfo();
        ExtensionManifest.EnsureKey(folder, Convert.ToBase64String(publicKey));

        var metadata = ExtensionManifest.Read(folder);
        Assert.Equal(CrxPackage.ComputeExtensionId(publicKey), metadata!.Id);
    }

    [Fact]
    public void 缺少_manifest_时应返回空()
    {
        var folder = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(folder);
        Assert.Null(ExtensionManifest.Read(folder));
    }

    [Fact]
    public void 损坏的_manifest_应返回空而不是抛异常()
    {
        var folder = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ not json");
        Assert.Null(ExtensionManifest.Read(folder));
    }
}
