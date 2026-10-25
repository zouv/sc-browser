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
    /// 创建<b>失败</b>的任务不会被缓存——否则一次偶发失败（运行时正在更新、缺失后又装上）
    /// 之后，本进程里所有新标签都会永远拿到同一个异常。
    /// </summary>
    public Task<CoreWebView2Environment> GetAsync()
    {
        lock (_gate)
        {
            if (_creation is { IsFaulted: true } or { IsCanceled: true })
            {
                _creation = null;
            }

            return _creation ??= CreateAsync();
        }
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(AppPaths.UserDataFolder);

        // 内部页面的 browser:// scheme。带 authority（browser://settings/）才有「每页一个来源」，
        // 消息桥据此判定权限；TreatAsSecure 让页面处于安全上下文（与 https 同等的 Web API 可用性）。
        // AllowedOrigins 留空：任何别的来源都不能向它发跨源请求。
        // 注意 CustomSchemeRegistrations 是只读属性且默认为 null，只能经构造函数传入。
        var schemes = new List<CoreWebView2CustomSchemeRegistration>
        {
            new(InternalPageRegistry.SchemeName)
            {
                TreatAsSecure = true,
                HasAuthorityComponent = true,
            },
        };

        var options = new CoreWebView2EnvironmentOptions(customSchemeRegistrations: schemes)
        {
            // 扩展支持必须先在这里开总开关，否则 Profile.AddBrowserExtensionAsync 直接
            // 返回 ERROR_NOT_SUPPORTED。该开关属于「环境创建期参数」，改动需重启应用生效。
            AreBrowserExtensionsEnabled = _settings.Current.EnableExtensions,
        };

        // browserExecutableFolder 传 null → 使用系统安装的 Evergreen WebView2 运行时。
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.UserDataFolder,
            options: options).ConfigureAwait(true);

        // 浏览器主进程一旦退出（崩溃 / 被结束），这个环境就废了：作废缓存，
        // 之后新建的标签会重新创建环境、拉起新的浏览器进程，而不是一直拿着死环境报错。
        environment.BrowserProcessExited += (_, _) =>
        {
            lock (_gate)
            {
                _creation = null;
            }
        };

        return environment;
    }

    /// <summary>
    /// 本机可用的 WebView2 运行时版本；未安装时返回 <c>null</c>。
    /// 启动时据此给出明确提示，而不是让每个标签都以「初始化失败」告终。
    /// </summary>
    public static string? GetRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }
}
