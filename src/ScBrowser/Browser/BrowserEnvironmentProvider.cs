using System.IO;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Infrastructure;
using ScBrowser.Services;

namespace ScBrowser.Browser;

/// <summary>
/// 创建并缓存 WebView2 环境（<see cref="CoreWebView2Environment"/>）。
/// <para>
/// 整个应用<b>只用一个环境</b>：所有标签页共享它，因而共享同一份用户数据（Cookie、
/// 缓存、已安装扩展）。这不同于原 C++ 示例——该示例为「外壳 UI」单独开了第二个环境，
/// 而本项目的壳是 WPF 原生控件，不需要那层隔离。
/// </para>
/// </summary>
public sealed class BrowserEnvironmentProvider
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private Task<CoreWebView2Environment>? _creation;

    public BrowserEnvironmentProvider(SettingsService settings) => _settings = settings;

    /// <summary>
    /// 获取（首次调用时创建）环境。并发调用会复用同一次创建任务。
    /// </summary>
    public Task<CoreWebView2Environment> GetAsync()
    {
        lock (_gate)
        {
            return _creation ??= CreateAsync();
        }
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(AppPaths.UserDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            // 扩展支持必须先在这里开总开关，否则 Profile.AddBrowserExtensionAsync 直接
            // 返回 ERROR_NOT_SUPPORTED。该开关属于「环境创建期参数」，改动需重启应用生效。
            AreBrowserExtensionsEnabled = _settings.Current.EnableExtensions,
        };

        // browserExecutableFolder 传 null → 使用系统安装的 Evergreen WebView2 运行时。
        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.UserDataFolder,
            options: options).ConfigureAwait(true);
    }
}
