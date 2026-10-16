using System.IO;
using System.IO.Compression;
using System.Text;

namespace ScBrowser.Browser;

/// <summary>
/// CRX（Chrome 扩展包）解包工具。
/// <para>
/// CRX 结构：<c>"Cr24"</c> 魔数(4) + 版本(4) + 头长度/密钥签名长度 + 头部 + <b>一段完整 ZIP</b>。
/// 头部只描述签名与扩展 ID，真正的内容是尾部那段 ZIP，所以解包 = 定位 ZIP 起始偏移后
/// 用标准 ZIP 解压。
/// </para>
/// </summary>
public static class CrxPackage
{
    private static readonly byte[] Magic = "Cr24"u8.ToArray();

    /// <summary>判断文件是否像 CRX 包（只读前 4 字节，不做全量校验）。</summary>
    public static bool LooksLikeCrx(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
                   && head.SequenceEqual(Magic);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把 CRX 解包到 <paramref name="targetDirectory"/>。目录已存在时会先清空（重装语义）。
    /// </summary>
    public static void ExtractTo(string crxPath, string targetDirectory)
    {
        using var stream = File.OpenRead(crxPath);
        var zipOffset = FindZipOffset(stream);

        // 为什么要把 ZIP 段切到独立流，而不是直接 stream.Position = zipOffset 后交给 ZipArchive：
        // ZIP 的中央目录偏移是「相对归档起点」记录的，ZipArchive 从非零位置起读会算错偏移，
        // 报 "End Of Central Directory ... does not correspond"。
        // 切出独立流（位置归零）是唯一稳妥的做法。代价是整包进内存——扩展包通常很小，可接受。
        var zipLength = checked((int)(stream.Length - zipOffset));
        var zipBytes = new byte[zipLength];
        stream.Position = zipOffset;
        stream.ReadExactly(zipBytes);

        using var zipStream = new MemoryStream(zipBytes, writable: false);

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        var root = Path.GetFullPath(targetDirectory);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            // 目录条目（以 / 结尾）没有内容，只需建目录。
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));

            // 防目录穿越：确保解出的路径仍在目标目录内。
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"CRX 内含越界路径，已中止解包：{entry.FullName}");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>定位内嵌 ZIP 的起始偏移。</summary>
    private static long FindZipOffset(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(4);
        if (magic.Length < 4 || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是有效的 CRX 文件（缺少 Cr24 标识）。");
        }

        var version = reader.ReadUInt32();
        return version switch
        {
            // v2：版本(4) + 公钥长度(4) + 签名长度(4) + 公钥 + 签名
            2 => 16 + reader.ReadUInt32() + reader.ReadUInt32(),
            // v3：版本(4) + 头部长度(4) + 头部(protobuf)
            3 => 12 + reader.ReadUInt32(),
            _ => throw new InvalidDataException($"不支持的 CRX 版本：{version}。仅支持 v2 / v3。"),
        };
    }

    /// <summary>
    /// 由 CRX 文件名派生一个安全的扩展目录名。
    /// 名称不得以 <c>_</c> 开头——WebView2 会以 E_ACCESSDENIED 拒绝加载。
    /// </summary>
    public static string DeriveFolderName(string crxPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(crxPath);
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string(baseName
            .Select(c => invalid.Contains(c) || c == ' ' ? '-' : c)
            .ToArray())
            .Trim('-', '.');

        if (cleaned.Length == 0)
        {
            cleaned = "extension";
        }

        if (cleaned.StartsWith('_'))
        {
            cleaned = "ext" + cleaned;
        }

        return cleaned;
    }
}
