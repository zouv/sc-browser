using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>
/// 扩展管理：列出 / 安装 / 启用停用 / 卸载。
/// <para>
/// <b>平台限制（务必知悉）</b>：WebView2 只支持加载<b>解包扩展</b>（含 manifest.json 的目录），
/// 且扩展按 <c>Profile</c> 维度持久化。它没有 Chrome 应用商店、不校验签名，
/// 也拿不到扩展的图标/权限/来源等元数据。
/// </para>
/// <para>
/// <b>另一个易踩的坑</b>：扩展内容不会被复制到用户数据目录，安装后若<b>改动扩展目录里的文件</b>，
/// WebView2 会把这个扩展从 profile 中移除。因此「改代码 → 重装」是开发扩展时的正常循环。
/// </para>
/// </summary>
public sealed class ExtensionService
{
    private readonly TabManager _tabs;
    private readonly ILogger<ExtensionService> _logger;

    public ExtensionService(TabManager tabs, ILogger<ExtensionService> logger)
    {
        _tabs = tabs;
        _logger = logger;
    }

    /// <summary>列出当前 profile 已安装的扩展。</summary>
    public async Task<IReadOnlyList<ExtensionInfo>> ListAsync()
    {
        var profile = GetProfile();
        if (profile is null)
        {
            return [];
        }

        var extensions = await profile.GetBrowserExtensionsAsync().ConfigureAwait(true);
        return extensions
            .Select(e => new ExtensionInfo(e.Id, e.Name, e.IsEnabled))
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>从解包目录安装扩展。</summary>
    public async Task<ExtensionInfo> InstallFromFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"扩展目录不存在：{folderPath}");
        }

        if (!File.Exists(Path.Combine(folderPath, "manifest.json")))
        {
            throw new FileNotFoundException(
                "所选目录不是有效的扩展：缺少 manifest.json。请选择扩展的顶层目录（即直接含 manifest.json 的那一层）。",
                Path.Combine(folderPath, "manifest.json"));
        }

        var profile = GetProfile()
            ?? throw new InvalidOperationException("浏览器尚未初始化，暂时无法安装扩展。");

        var extension = await profile.AddBrowserExtensionAsync(folderPath).ConfigureAwait(true);
        _logger.LogInformation("已安装扩展 {Name}（{Id}）来源 {Path}", extension.Name, extension.Id, folderPath);

        return new ExtensionInfo(extension.Id, extension.Name, extension.IsEnabled);
    }

    /// <summary>
    /// 从 <c>.crx</c> 文件安装：先解包到受管目录，再按解包目录安装。
    /// 扩展目录归应用统一管理（<c>%LOCALAPPDATA%\sc-browser\Extensions</c>）。
    /// </summary>
    public async Task<ExtensionInfo> InstallFromCrxAsync(string crxPath)
    {
        if (!File.Exists(crxPath))
        {
            throw new FileNotFoundException($"CRX 文件不存在：{crxPath}", crxPath);
        }

        if (!CrxPackage.LooksLikeCrx(crxPath))
        {
            throw new InvalidDataException("所选文件不是有效的 CRX 扩展包（缺少 Cr24 标识）。");
        }

        var targetDirectory = Path.Combine(AppPaths.ManagedExtensionsFolder, CrxPackage.DeriveFolderName(crxPath));

        // 解包是同步 IO，放到后台线程避免卡住 UI。
        await Task.Run(() => CrxPackage.ExtractTo(crxPath, targetDirectory)).ConfigureAwait(true);

        _logger.LogInformation("CRX 已解包到 {Directory}", targetDirectory);
        return await InstallFromFolderAsync(targetDirectory).ConfigureAwait(true);
    }

    /// <summary>卸载扩展。</summary>
    public async Task RemoveAsync(string extensionId)
    {
        var extension = await FindAsync(extensionId).ConfigureAwait(true);
        if (extension is null)
        {
            return;
        }

        await extension.RemoveAsync().ConfigureAwait(true);
        _logger.LogInformation("已卸载扩展 {Id}", extensionId);
    }

    /// <summary>启用 / 停用扩展。立即对当前所有已打开页面生效。</summary>
    public async Task SetEnabledAsync(string extensionId, bool enabled)
    {
        var extension = await FindAsync(extensionId).ConfigureAwait(true);
        if (extension is null)
        {
            return;
        }

        await extension.EnableAsync(enabled).ConfigureAwait(true);
        _logger.LogInformation("扩展 {Id} 已{Action}", extensionId, enabled ? "启用" : "停用");
    }

    private async Task<CoreWebView2BrowserExtension?> FindAsync(string extensionId)
    {
        var profile = GetProfile();
        if (profile is null)
        {
            return null;
        }

        var extensions = await profile.GetBrowserExtensionsAsync().ConfigureAwait(true);
        return extensions.FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 取当前 profile。扩展是按 profile 管理的，而 profile 要从一个已存在的
    /// CoreWebView2 实例上取——所以要求至少有一个标签页已初始化。
    /// </summary>
    private CoreWebView2Profile? GetProfile() => _tabs.Profile;
}
