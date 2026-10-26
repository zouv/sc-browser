using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>CRX 包解析结果。</summary>
/// <param name="FormatVersion">CRX 格式版本（2 / 3）。</param>
/// <param name="ExtensionId">由公钥算出的扩展 ID（32 个 a-p 字母）。</param>
/// <param name="PublicKey">DER 编码的公钥（SubjectPublicKeyInfo）。</param>
/// <param name="ZipOffset">内嵌 ZIP 的起始偏移。</param>
public sealed record CrxPackageInfo(int FormatVersion, string ExtensionId, byte[] PublicKey, long ZipOffset);

/// <summary>
/// CRX（Chrome 扩展包）解析与解包。
/// <para>
/// 结构：<c>"Cr24"</c> + 版本 + 头长度 + 头部 + <b>一段完整 ZIP</b>。
/// v3 的头部是 protobuf，里面带公钥与签名；v2 是「公钥长度 + 签名长度 + 公钥 + 签名」。
/// </para>
/// <para>
/// <b>为什么要校验签名</b>：本项目没有商店审核，安装 CRX 等于信任其代码。至少确认
/// 「包确实是它所声称的那把私钥签的」——公钥与签名都在包里，改内容必然导致验签失败。
/// 同时用公钥算出扩展 ID，把公钥写回 manifest 的 <c>key</c>，装进 WebView2 后 ID 才能与 Chrome 一致。
/// </para>
/// </summary>
public static class CrxPackage
{
    private static readonly byte[] Magic = "Cr24"u8.ToArray();

    /// <summary>CRX3 签名前缀（含结尾的 NUL，共 16 字节）。</summary>
    private static readonly byte[] Crx3SignatureContext = "CRX3 SignedData\0"u8.ToArray();

    /// <summary>单个扩展包允许的最大条目数。</summary>
    internal const int MaxEntries = 20_000;

    /// <summary>单个扩展包解压后允许的最大总字节数。</summary>
    internal const int MaxExtractedBytes = 512 * 1024 * 1024;

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
    /// 解析并<b>校验</b> CRX：定位 ZIP、验证签名、算出扩展 ID。
    /// 签名不通过会抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static CrxPackageInfo Read(string crxPath)
    {
        var bytes = File.ReadAllBytes(crxPath);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是有效的 CRX 文件（缺少 Cr24 标识）。");
        }

        var version = BitConverter.ToUInt32(bytes, 4);
        return version switch
        {
            3 => ReadV3(bytes),
            2 => ReadV2(bytes),
            _ => throw new InvalidDataException($"不支持的 CRX 版本：{version}。仅支持 v2 / v3。"),
        };
    }

    /// <summary>把 CRX 解包到 <paramref name="targetDirectory"/>。目录已存在时会先清空（重装语义）。</summary>
    public static void ExtractTo(string crxPath, string targetDirectory)
    {
        var bytes = File.ReadAllBytes(crxPath);
        var offset = FindZipOffset(new MemoryStream(bytes));
        ExtractZip(bytes, offset, targetDirectory);
    }

    /// <summary>按已解析的偏移解包（避免重复解析）。</summary>
    public static void ExtractTo(string crxPath, string targetDirectory, CrxPackageInfo info) =>
        ExtractZip(File.ReadAllBytes(crxPath), info.ZipOffset, targetDirectory);

    private static void ExtractZip(byte[] bytes, long offset, string targetDirectory)
    {
        var zipLength = checked((int)(bytes.Length - offset));
        using var zipStream = new MemoryStream(bytes, (int)offset, zipLength, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // 先整体校验再动磁盘：越界路径、条目数、解压后总大小（防 zip 炸弹）。
        // 放在删除旧目录之前，坏包不会把已装好的同名扩展先删掉。
        var root = Path.GetFullPath(targetDirectory);
        ValidateEntries(archive, root);

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // 目录条目（以 / 结尾）没有内容，只需建目录。
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// 解包前的整体校验：越界路径（见 <see cref="PathSafety.IsUnderDirectory"/> 为何要补分隔符）、
    /// 条目数、解压后总大小。
    /// </summary>
    private static void ValidateEntries(ZipArchive archive, string root)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"CRX 条目数过多（{archive.Entries.Count}），已中止解包。");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!PathSafety.IsUnderDirectory(destination, root)
                && !string.Equals(destination.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"CRX 内含越界路径，已中止解包：{entry.FullName}");
            }

            total += entry.Length;
            if (total > MaxExtractedBytes)
            {
                throw new InvalidDataException("CRX 解压后体积过大，已中止解包。");
            }
        }
    }

    /// <summary>定位内嵌 ZIP 的起始偏移（不校验签名，兼容旧调用）。</summary>
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
            2 => 16 + reader.ReadUInt32() + reader.ReadUInt32(),
            3 => 12 + reader.ReadUInt32(),
            _ => throw new InvalidDataException($"不支持的 CRX 版本：{version}。仅支持 v2 / v3。"),
        };
    }

    // ------------------------------------------------------------------ //
    // CRX3：protobuf 头部 + RSA/ECDSA 签名
    // ------------------------------------------------------------------ //

    private static CrxPackageInfo ReadV3(byte[] bytes)
    {
        var headerSize = (int)BitConverter.ToUInt32(bytes, 8);
        if (headerSize <= 0 || 12 + headerSize > bytes.Length)
        {
            throw new InvalidDataException("CRX3 头部长度非法。");
        }

        var header = bytes.AsSpan(12, headerSize);
        var proofs = ParseCrx3Header(header, out var signedHeaderData);

        var zipOffset = 12 + headerSize;
        var signedData = BuildCrx3SignedData(signedHeaderData, bytes.AsSpan(zipOffset));

        // 商店包通常同时带 RSA 与 ECDSA 两把公钥（两把是不同密钥，哈希也不同）。
        // 头部声明的 crx_id 只对应其中一把（历史上是 RSA），所以先按 crx_id 筛出那一份，
        // 再验签——否则拿 ECDSA 的公钥去比 crx_id 必然「不一致」。
        var declared = TryReadSignedCrxId(signedHeaderData);
        var candidates = declared is { Length: 16 }
            ? proofs.Where(p => IdBytes(p.PublicKey).AsSpan().SequenceEqual(declared))
            : proofs;

        foreach (var proof in candidates)
        {
            if (VerifyCrx3Proof(proof, signedData))
            {
                return new CrxPackageInfo(3, ComputeExtensionId(proof.PublicKey), proof.PublicKey, zipOffset);
            }
        }

        throw new InvalidDataException(
            declared is { Length: 16 }
                ? "CRX3 签名校验失败，或头部声明的扩展 ID 与签名公钥不一致，已拒绝安装。"
                : "CRX3 签名校验失败，已拒绝安装。");
    }

    private sealed record Proof(byte[] PublicKey, byte[] Signature, bool IsRsa);

    private static List<Proof> ParseCrx3Header(ReadOnlySpan<byte> header, out byte[] signedHeaderData)
    {
        var proofs = new List<Proof>();
        signedHeaderData = [];

        var offset = 0;
        while (offset < header.Length)
        {
            var tag = ReadVarint(header, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            if (wireType != 2)
            {
                SkipField(header, ref offset, wireType);
                continue;
            }

            var length = (int)ReadVarint(header, ref offset);
            var payload = header.Slice(offset, length);
            offset += length;

            switch (fieldNumber)
            {
                case 2:   // sha256_with_rsa
                case 3:   // sha256_with_ecdsa
                    if (TryParseProof(payload, out var proof))
                    {
                        proofs.Add(proof with { IsRsa = fieldNumber == 2 });
                    }

                    break;

                case 10000:   // signed_header_data
                    signedHeaderData = payload.ToArray();
                    break;
            }
        }

        return proofs;
    }

    private static bool TryParseProof(ReadOnlySpan<byte> payload, out Proof proof)
    {
        proof = new Proof([], [], true);

        byte[] publicKey = [];
        byte[] signature = [];
        var offset = 0;

        while (offset < payload.Length)
        {
            var tag = ReadVarint(payload, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            if (wireType != 2)
            {
                SkipField(payload, ref offset, wireType);
                continue;
            }

            var length = (int)ReadVarint(payload, ref offset);
            var value = payload.Slice(offset, length).ToArray();
            offset += length;

            if (fieldNumber == 1)
            {
                publicKey = value;
            }
            else if (fieldNumber == 2)
            {
                signature = value;
            }
        }

        if (publicKey.Length == 0 || signature.Length == 0)
        {
            return false;
        }

        proof = new Proof(publicKey, signature, true);
        return true;
    }

    private static byte[] BuildCrx3SignedData(ReadOnlySpan<byte> signedHeaderData, ReadOnlySpan<byte> zip)
    {
        var length = Crx3SignatureContext.Length + 4 + signedHeaderData.Length + zip.Length;
        var buffer = new byte[length];
        var offset = 0;

        Crx3SignatureContext.CopyTo(buffer.AsSpan(offset));
        offset += Crx3SignatureContext.Length;

        BitConverter.TryWriteBytes(buffer.AsSpan(offset), (uint)signedHeaderData.Length);
        offset += 4;

        signedHeaderData.CopyTo(buffer.AsSpan(offset));
        offset += signedHeaderData.Length;

        zip.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static bool VerifyCrx3Proof(Proof proof, byte[] signedData)
    {
        try
        {
            if (proof.IsRsa)
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(proof.PublicKey, out _);
                return rsa.VerifyData(signedData, proof.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(proof.PublicKey, out _);

            // 不同平台的签名编码不同：先按 DER 试，再按 P1363 试。
            return ecdsa.VerifyData(signedData, proof.Signature, HashAlgorithmName.SHA256)
                   || ecdsa.VerifyData(
                       signedData,
                       proof.Signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[]? TryReadSignedCrxId(byte[] signedHeaderData)
    {
        // SignedData { optional bytes crx_id = 1; }
        var span = signedHeaderData.AsSpan();
        var offset = 0;
        while (offset < span.Length)
        {
            var tag = ReadVarint(span, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            if (wireType != 2)
            {
                SkipField(span, ref offset, wireType);
                continue;
            }

            var length = (int)ReadVarint(span, ref offset);
            var value = span.Slice(offset, length).ToArray();
            offset += length;

            if (fieldNumber == 1)
            {
                return value;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ //
    // CRX2：公钥与签名直接在头部
    // ------------------------------------------------------------------ //

    private static CrxPackageInfo ReadV2(byte[] bytes)
    {
        var keyLength = (int)BitConverter.ToUInt32(bytes, 8);
        var signatureLength = (int)BitConverter.ToUInt32(bytes, 12);
        var keyOffset = 16;
        var signatureOffset = keyOffset + keyLength;
        var zipOffset = signatureOffset + signatureLength;

        if (keyLength <= 0 || signatureLength <= 0 || zipOffset > bytes.Length)
        {
            throw new InvalidDataException("CRX2 头部长度非法。");
        }

        var publicKey = bytes.AsSpan(keyOffset, keyLength).ToArray();
        var signature = bytes.AsSpan(signatureOffset, signatureLength).ToArray();
        var zip = bytes.AsSpan(zipOffset);

        if (!VerifyCrx2Signature(publicKey, signature, zip))
        {
            throw new InvalidDataException("CRX2 签名校验失败，已拒绝安装。");
        }

        return new CrxPackageInfo(2, ComputeExtensionId(publicKey), publicKey, zipOffset);
    }

    private static bool VerifyCrx2Signature(byte[] publicKey, byte[] signature, ReadOnlySpan<byte> zip)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            // 老包用 SHA1，新一点的用 SHA256，都试一下。
            return rsa.VerifyData(zip, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)
                   || rsa.VerifyData(zip, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ //
    // 扩展 ID
    // ------------------------------------------------------------------ //

    /// <summary>
    /// 由公钥算扩展 ID：公钥的 SHA-256 取前 16 字节，每个半字节映射成 <c>a</c>–<c>p</c>。
    /// 这是 Chrome 的算法；把公钥写进 manifest 的 <c>key</c> 后，WebView2 会算出同一个 ID。
    /// </summary>
    public static string ComputeExtensionId(byte[] publicKey)
    {
        return IdToString(SHA256.HashData(publicKey));
    }

    /// <summary>公钥的 SHA-256 前 16 字节（CRX3 头部 <c>crx_id</c> 用的就是它）。</summary>
    internal static byte[] IdBytes(byte[] publicKey) => SHA256.HashData(publicKey)[..16];

    private static string IdToString(ReadOnlySpan<byte> hash)
    {
        var chars = new char[32];
        for (var i = 0; i < 16; i++)
        {
            chars[i * 2] = (char)('a' + (hash[i] >> 4));
            chars[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }

        return new string(chars);
    }

    private static byte[] IdToBytes(string id)
    {
        var bytes = new byte[16];
        for (var i = 0; i < 16 && i * 2 + 1 < id.Length; i++)
        {
            bytes[i] = (byte)(((id[i * 2] - 'a') << 4) | (id[i * 2 + 1] - 'a'));
        }

        return bytes;
    }

    /// <summary>ID 是否形如 Chrome 扩展 ID（32 个 a–p 字母）。</summary>
    public static bool LooksLikeExtensionId(string? value) =>
        value is { Length: 32 } && value.All(c => c is >= 'a' and <= 'p');

    // ------------------------------------------------------------------ //
    // protobuf 小工具
    // ------------------------------------------------------------------ //

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;

        while (offset < data.Length && shift < 64)
        {
            var b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("CRX 头部格式非法（varint 越界）。");
    }

    private static void SkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(data, ref offset);
                break;
            case 1:
                offset += 8;
                break;
            case 2:
                var length = (int)ReadVarint(data, ref offset);
                offset += length;
                break;
            case 5:
                offset += 4;
                break;
            default:
                throw new InvalidDataException($"CRX 头部含不支持的 protobuf 字段类型：{wireType}");
        }
    }

    /// <summary>
    /// 由 CRX 文件名派生一个安全的扩展目录名（仅在无法得到扩展 ID 时使用）。
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
