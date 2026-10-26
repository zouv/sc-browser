using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>扩展的展示信息（WebView2 的 id / 名称 / 启停 + 我们自己解析的 manifest 元数据）。</summary>
public sealed record ExtensionInfo(
    string Id,
    string Name,
    bool IsEnabled,
    string? Version = null,
    string? Description = null,
    int? ManifestVersion = null,
    IReadOnlyList<string>? Permissions = null,
    string? IconDataUrl = null,
    string? PopupPath = null,
    string? OptionsPath = null,
    bool HasUpdateUrl = false);

/// <summary>
/// 扩展管理：列出 / 安装（目录、CRX、商店）/ 启用停用 / 卸载。
/// <para>
/// <b>平台限制</b>：WebView2 只支持加载解包扩展，扩展按 <c>Profile</c> 持久化；它只返回
/// id / name / 是否启用，图标、描述、权限要自己解析 manifest。
/// </para>
/// <para>
/// <b>受管目录</b>：所有扩展都复制到 <c>%LOCALAPPDATA%\sc-browser\Extensions\&lt;id&gt;\&lt;版本&gt;\</c>。
/// 不直接加载用户选中的目录，是因为 WebView2 不复制扩展内容——原目录一改，扩展就会被卸载；
/// 而且按文件名命名容易撞名、也无法区分版本。
/// </para>
/// </summary>
public sealed class ExtensionService
{
    private readonly ILogger<ExtensionService> _logger;

    public ExtensionService(ILogger<ExtensionService> logger) => _logger = logger;

    /// <summary>扩展集合发生变化（安装 / 移除 / 启停）。外壳的工具栏按钮据此刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>列出该 profile 已安装的扩展（附带自建元数据）。</summary>
    public async Task<IReadOnlyList<ExtensionInfo>> ListAsync(CoreWebView2Profile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var extensions = await profile.GetBrowserExtensionsAsync().ConfigureAwait(true);
        var index = LoadIndex();

        return extensions
            .Select(e => Describe(e, index))
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 从解包目录安装扩展：先复制进受管目录，再加载。
    /// </summary>
    /// <param name="profile">目标 profile。</param>
    /// <param name="folderPath">用户选择的目录（须直接包含 manifest.json）。</param>
    /// <param name="confirm">安装前确认（拿到解析出的元数据，返回 false 取消安装）。</param>
    public async Task<ExtensionInfo?> InstallFromFolderAsync(
        CoreWebView2Profile? profile,
        string folderPath,
        Func<ExtensionMetadata, bool>? confirm = null)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"扩展目录不存在：{folderPath}");
        }

        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "所选目录不是有效的扩展：缺少 manifest.json。请选择扩展的顶层目录（即直接含 manifest.json 的那一层）。",
                manifestPath);
        }

        if (profile is null)
        {
            throw new InvalidOperationException("浏览器尚未初始化，暂时无法安装扩展。");
        }

        var source = ExtensionManifest.Read(folderPath);
        if (source is null)
        {
            throw new InvalidDataException("manifest.json 无法解析，已中止安装。");
        }

        var folderKey = source.Id ?? SanitizeFolderName(source.Name);
        var target = BuildVersionedFolder(folderKey, source.Version);

        CopyDirectory(folderPath, target);
        var metadata = ExtensionManifest.Read(target) ?? source;

        if (confirm is not null && !confirm(metadata))
        {
            TryDeleteDirectory(target);
            return null;
        }

        return await InstallManagedFolderAsync(profile, target, metadata, sourceUrl: null).ConfigureAwait(true);
    }

    /// <summary>
    /// 从 <c>.crx</c> 安装：<b>先验签</b>，再解包到受管目录，并把公钥写回 manifest 的 <c>key</c>，
    /// 让 WebView2 算出的扩展 ID 与 Chrome 一致。
    /// </summary>
    public async Task<ExtensionInfo?> InstallFromCrxAsync(
        CoreWebView2Profile? profile,
        string crxPath,
        Func<ExtensionMetadata, bool>? confirm = null)
    {
        if (!File.Exists(crxPath))
        {
            throw new FileNotFoundException($"CRX 文件不存在：{crxPath}", crxPath);
        }

        if (profile is null)
        {
            throw new InvalidOperationException("浏览器尚未初始化，暂时无法安装扩展。");
        }

        // 验签 + 算 ID；签名不对直接抛异常，不会落盘。
        var package = CrxPackage.Read(crxPath);
        _logger.LogInformation("CRX 校验通过：ID {Id}（格式 v{Format}）", package.ExtensionId, package.FormatVersion);

        // 先解到 _incoming，再从 manifest 里读版本号决定最终目录名。
        // （不要用「定位 ZIP 偏移后直接交给 ZipArchive」的写法：ZipArchive 要求流的 0 位置就是归档起点，
        //   从非零偏移读会报 End Of Central Directory 对不上——见 ARCHITECTURE §7。）
        var targetRoot = Path.Combine(
            AppPaths.ManagedExtensionsFolder,
            SanitizeFolderName(package.ExtensionId));
        var incoming = Path.Combine(targetRoot, "_incoming");

        var target = await Task.Run(() =>
        {
            CrxPackage.ExtractTo(crxPath, incoming, package);

            var version = ExtensionManifest.Read(incoming)?.Version
                          ?? throw new InvalidDataException("扩展包内缺少可解析的 manifest.json。");

            var finalFolder = Path.Combine(targetRoot, SanitizeFolderName(version));
            TryDeleteDirectory(finalFolder);
            Directory.CreateDirectory(targetRoot);
            Directory.Move(incoming, finalFolder);
            return finalFolder;
        }).ConfigureAwait(true);

        ExtensionManifest.EnsureKey(target, Convert.ToBase64String(package.PublicKey));

        var metadata = ExtensionManifest.Read(target)
                       ?? throw new InvalidDataException("扩展包内缺少可解析的 manifest.json。");

        if (confirm is not null && !confirm(metadata))
        {
            TryDeleteDirectory(target);
            return null;
        }

        return await InstallManagedFolderAsync(profile, target, metadata, sourceUrl: crxPath).ConfigureAwait(true);
    }

    /// <summary>
    /// 从商店链接或扩展 ID 安装：下载 CRX → 走 CRX 安装流程。返回 <c>null</c> 表示用户取消。
    /// </summary>
    public async Task<ExtensionInfo?> InstallFromStoreAsync(
        CoreWebView2Profile? profile,
        string input,
        Func<ExtensionMetadata, bool>? confirm = null)
    {
        var tempPath = await ExtensionStoreClient.DownloadAsync(input).ConfigureAwait(true);
        try
        {
            return await InstallFromCrxAsync(profile, tempPath, confirm).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响安装结果。
            }
        }
    }

    /// <summary>卸载扩展（同时清掉受管目录）。</summary>
    public async Task RemoveAsync(CoreWebView2Profile? profile, string extensionId)
    {
        var extension = await FindAsync(profile, extensionId).ConfigureAwait(true);
        if (extension is not null)
        {
            await extension.RemoveAsync().ConfigureAwait(true);
            _logger.LogInformation("已卸载扩展 {Id}", extensionId);
        }

        var index = LoadIndex();
        if (index.TryGetValue(extensionId, out var record))
        {
            TryDeleteDirectory(Path.GetDirectoryName(record.Folder) ?? record.Folder);
            index.Remove(extensionId);
            SaveIndex(index);
        }
    }

    /// <summary>启用 / 停用扩展。立即对当前所有已打开页面生效。</summary>
    public async Task SetEnabledAsync(CoreWebView2Profile? profile, string extensionId, bool enabled)
    {
        var extension = await FindAsync(profile, extensionId).ConfigureAwait(true);
        if (extension is null)
        {
            return;
        }

        await extension.EnableAsync(enabled).ConfigureAwait(true);
        _logger.LogInformation("扩展 {Id} 已{Action}", extensionId, enabled ? "启用" : "停用");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ //
    // 内部
    // ------------------------------------------------------------------ //

    private async Task<ExtensionInfo> InstallManagedFolderAsync(
        CoreWebView2Profile profile,
        string folder,
        ExtensionMetadata metadata,
        string? sourceUrl)
    {
        // 同 ID 的旧版本先卸掉：WebView2 以 ID 认扩展，直接 Add 新路径可能不生效。
        if (metadata.Id is { } id && await FindAsync(profile, id).ConfigureAwait(true) is { } existing)
        {
            await existing.RemoveAsync().ConfigureAwait(true);
        }

        var extension = await profile.AddBrowserExtensionAsync(folder).ConfigureAwait(true);
        _logger.LogInformation(
            "已安装扩展 {Name}（{Id}，版本 {Version}，MV{Manifest}）来源 {Path}",
            extension.Name, extension.Id, metadata.Version, metadata.ManifestVersion, folder);

        // 记下 WebView2 的 ID 与我们目录的对应关系：目录安装时 WebView2 的 ID 与 manifest 无必然关系。
        var index = LoadIndex();
        index[extension.Id] = new IndexEntry(folder, sourceUrl, DateTimeOffset.UtcNow);
        SaveIndex(index);
        Changed?.Invoke(this, EventArgs.Empty);

        return new ExtensionInfo(
            extension.Id,
            metadata.Name.Length > 0 ? metadata.Name : extension.Name,
            extension.IsEnabled,
            metadata.Version,
            metadata.Description,
            metadata.ManifestVersion,
            metadata.Permissions,
            TryReadIconDataUrl(metadata.IconPath),
            metadata.PopupPath,
            metadata.OptionsPath,
            !string.IsNullOrWhiteSpace(metadata.UpdateUrl));
    }

    private ExtensionInfo Describe(CoreWebView2BrowserExtension extension, Dictionary<string, IndexEntry> index)
    {
        if (!index.TryGetValue(extension.Id, out var record))
        {
            return new ExtensionInfo(extension.Id, extension.Name, extension.IsEnabled);
        }

        var metadata = ExtensionManifest.Read(record.Folder);
        if (metadata is null)
        {
            return new ExtensionInfo(extension.Id, extension.Name, extension.IsEnabled);
        }

        return new ExtensionInfo(
            extension.Id,
            metadata.Name,
            extension.IsEnabled,
            metadata.Version,
            metadata.Description,
            metadata.ManifestVersion,
            metadata.Permissions,
            TryReadIconDataUrl(metadata.IconPath),
            metadata.PopupPath,
            metadata.OptionsPath,
            !string.IsNullOrWhiteSpace(metadata.UpdateUrl));
    }

    /// <summary>图标转 data URL：内部页面是 <c>browser://</c> 来源，加载不了 <c>file://</c>。</summary>
    private static string? TryReadIconDataUrl(string? iconPath)
    {
        const int maxBytes = 256 * 1024;
        if (iconPath is null || !File.Exists(iconPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(iconPath);
            if (info.Length > maxBytes)
            {
                return null;
            }

            var mime = Path.GetExtension(iconPath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".gif" => "image/gif",
                _ => "application/octet-stream",
            };

            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(iconPath))}";
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string BuildVersionedFolder(string idOrName, string version) =>
        Path.Combine(AppPaths.ManagedExtensionsFolder, SanitizeFolderName(idOrName), SanitizeFolderName(version));

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray())
            .Trim('-', '.');

        if (cleaned.Length == 0)
        {
            cleaned = "extension";
        }

        return cleaned.StartsWith('_') ? "ext" + cleaned : cleaned;
    }

    private static void CopyDirectory(string source, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 文件被占用时留给用户手动清理，不应让安装流程失败。
        }
    }

    private static async Task<CoreWebView2BrowserExtension?> FindAsync(
        CoreWebView2Profile? profile,
        string extensionId)
    {
        if (profile is null)
        {
            return null;
        }

        var extensions = await profile.GetBrowserExtensionsAsync().ConfigureAwait(true);
        return extensions.FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ //
    // 安装索引：WebView2 的扩展 ID → 受管目录
    // ------------------------------------------------------------------ //

    private sealed record IndexEntry(string Folder, string? Source, DateTimeOffset InstalledAt);

    private static string IndexFile => Path.Combine(AppPaths.ManagedExtensionsFolder, "index.json");

    private static Dictionary<string, IndexEntry> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexFile))
            {
                return [];
            }

            return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(IndexFile)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static void SaveIndex(Dictionary<string, IndexEntry> index)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ManagedExtensionsFolder);
            AtomicFile.WriteAllText(
                IndexFile,
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // 索引只是「展示用元数据」的快捷方式，写失败不影响扩展本身。
        }
    }
}
