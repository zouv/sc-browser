using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ScBrowser.Infrastructure;

namespace ScBrowser.Browser;

/// <summary>
/// 一个标签页：持有一个 WebView2 控件 + 该页的全部可观察状态。
/// <para>
/// <b>进程模型</b>：每标签页一个 WebView2，而非多标签共用一个再反复导航。
/// 好处是切换标签零延迟、页面状态（JS 堆、滚动位置、播放中的视频）得以保留，
/// 代价是内存占用随标签数上升——这与 Chrome / Edge 的进程模型一致，是刻意的取舍。
/// </para>
/// <para>
/// <b>为何它同时是 ViewModel</b>：标签页状态与它的 WebView2 控件天然一一对应，
/// 拆成「模型 + VM」只会带来两份状态同步的开销。此处让 <see cref="BrowserTab"/>
/// 直接承载可观察状态，由视图绑定。
/// </para>
/// </summary>
public sealed partial class BrowserTab : ObservableObject, IDisposable
{
    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _securityReceiver;
    private string? _logicalUrl;
    private bool _disposed;

    /// <summary>标签内部标识（单调递增，用于状态存储与日志）。</summary>
    public int Id { get; }

    /// <summary>标签页的 WebView2 控件；由 <see cref="IBrowserTabHost"/> 挂入可视化树。</summary>
    public WebView2 View { get; }

    /// <summary>底层 CoreWebView2；在 <see cref="InitializeAsync"/> 完成前为 <c>null</c>。</summary>
    public CoreWebView2? Core => _core;

    /// <summary>是否已完成 WebView2 初始化。</summary>
    public bool IsInitialized => _core is not null;

    // ---- 可观察状态（绑定到标签条 / 工具栏 / 地址栏）----

    [ObservableProperty]
    private string _title = "新标签页";

    /// <summary>真实地址（内部页面时为 file:// 实际路径）。</summary>
    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>地址栏展示用的地址（内部页面显示为 <c>browser://xxx</c>）。</summary>
    [ObservableProperty]
    private string _displayUrl = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private ImageSource? _favicon;

    [ObservableProperty]
    private SecurityState _securityState = SecurityState.Unknown;

    /// <summary>是否为当前激活标签（由 <see cref="TabManager"/> 维护）。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>导航失败时的错误描述；成功时为 <c>null</c>。</summary>
    [ObservableProperty]
    private string? _lastError;

    // ---- 对外事件（由 TabManager 订阅）----

    /// <summary>页面请求打开新窗口（<c>window.open</c> / target=_blank / 中键点击）。</summary>
    public event EventHandler<NewWindowRequestedEventArgs>? NewWindowRequested;

    /// <summary>页面请求关闭自身（<c>window.close()</c>）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>收到来自内部页面（WebUI）的 JS 消息。</summary>
    public event EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived;

    /// <summary>页面发起下载。</summary>
    public event EventHandler<CoreWebView2DownloadStartingEventArgs>? DownloadStarting;

    /// <summary>一次导航成功完成（用于记录历史）。失败导航不触发。</summary>
    public event EventHandler? NavigationFinished;

    /// <summary>渲染进程异常退出（用于错误页与恢复）。</summary>
    public event EventHandler<CoreWebView2ProcessFailedEventArgs>? ProcessFailed;

    public BrowserTab(int id)
    {
        Id = id;
        View = new WebView2();
    }

    /// <summary>
    /// 绑定 WebView2 环境并接线全部事件。必须在控件已挂入可视化树之后调用。
    /// </summary>
    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        // 复用同一个环境 → 与其它标签共享 Cookie / 缓存 / 扩展安装态。
        await View.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        _core = View.CoreWebView2;
        ConfigureSettings(_core.Settings);
        WireEvents(_core);
        await EnableSecurityEventsAsync(_core).ConfigureAwait(true);
    }

    /// <summary>
    /// WebView2 的默认设置调整。
    /// 关键一条：关掉内置的浏览器快捷键，让 <c>Ctrl+T / Ctrl+W / F5</c> 等由本应用统一处理，
    /// 否则会与外壳的快捷键体系打架。
    /// </summary>
    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDevToolsEnabled = true;          // 保留 F12 调试（发布版可按需关闭）
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsZoomControlEnabled = true;
        settings.IsScriptEnabled = true;
    }

    private void WireEvents(CoreWebView2 core)
    {
        core.SourceChanged += (_, _) => UpdateUrlFromCore();
        core.HistoryChanged += (_, _) => UpdateHistoryState();

        core.NavigationStarting += (_, _) =>
        {
            IsLoading = true;
            LastError = null;
        };

        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            UpdateHistoryState();
            if (e.IsSuccess)
            {
                UpdateUrlFromCore();
                NavigationFinished?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                LastError = $"导航失败：{e.WebErrorStatus}";
            }
        };

        core.DocumentTitleChanged += (_, _) => Title = ResolveTitle(core);
        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync(core).ConfigureAwait(true);

        core.NewWindowRequested += (_, e) =>
        {
            // 我们自行决定在新标签打开，而不是让 WebView2 弹一个无壳的裸窗口。
            e.Handled = true;
            NewWindowRequested?.Invoke(this, new NewWindowRequestedEventArgs(
                e.Uri,
                foreground: true));
        };

        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        core.WebMessageReceived += (_, e) => WebMessageReceived?.Invoke(this, e);
        core.DownloadStarting += (_, e) => DownloadStarting?.Invoke(this, e);
        core.ProcessFailed += (_, e) => ProcessFailed?.Invoke(this, e);
    }

    /// <summary>
    /// 打开 CDP 的 Security 域并订阅状态变化，用于驱动地址栏的安全图标。
    /// 这与原 C++ 示例的做法一致（<c>Security.enable</c> + <c>Security.securityStateChanged</c>）。
    /// </summary>
    private async Task EnableSecurityEventsAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Security.enable", "{}").ConfigureAwait(true);
            _securityReceiver = core.GetDevToolsProtocolEventReceiver("Security.securityStateChanged");
            _securityReceiver.DevToolsProtocolEventReceived += OnSecurityStateChanged;
        }
        catch (Exception)
        {
            // 安全事件是增强信息，拿不到不应影响浏览；降级为 Unknown 图标。
            SecurityState = SecurityState.Unknown;
        }
    }

    private void OnSecurityStateChanged(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
            if (!doc.RootElement.TryGetProperty("securityState", out var stateProp))
            {
                return;
            }

            SecurityState = stateProp.GetString()?.ToLowerInvariant() switch
            {
                "secure" => SecurityState.Secure,
                "neutral" => SecurityState.Neutral,
                "insecure" => SecurityState.Insecure,
                "warning" or "danger" => SecurityState.Danger,
                _ => SecurityState.Unknown,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            // 事件负载异常，保持上一次状态即可。
        }
    }

    /// <summary>
    /// 导航到指定地址。内部页面（<c>browser://</c>）会被解析成本地 WebUI 文件。
    /// </summary>
    public void Navigate(string url)
    {
        if (_core is null)
        {
            return;
        }

        if (InternalPageRegistry.IsInternalPage(url))
        {
            var fileUri = InternalPageRegistry.ResolveToFileUri(url);
            if (fileUri is null)
            {
                // 未登记的内部页面：退回新标签页，避免落到空白。
                fileUri = InternalPageRegistry.ResolveToFileUri(InternalPageRegistry.NewTabUrl);
                if (fileUri is null)
                {
                    return;
                }

                url = InternalPageRegistry.NewTabUrl;
            }

            _logicalUrl = url;
            _core.Navigate(fileUri);
        }
        else
        {
            _logicalUrl = null;
            _core.Navigate(url);
        }
    }

    /// <summary>执行地址栏输入：先解析再导航。</summary>
    public void NavigateFromAddressBar(string input) => Navigate(AddressBarResolver.Resolve(input));

    public void GoBack()
    {
        if (_core?.CanGoBack == true)
        {
            _core.GoBack();
        }
    }

    public void GoForward()
    {
        if (_core?.CanGoForward == true)
        {
            _core.GoForward();
        }
    }

    public void Reload()
    {
        _core?.Reload();
    }

    public void Stop() => _core?.Stop();

    /// <summary>重新加载并绕过缓存（Ctrl+Shift+R）。</summary>
    public void ReloadIgnoringCache()
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            _ = _core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache": true}""");
        }
        catch (Exception)
        {
            _core.Reload();
        }
    }

    /// <summary>请求关闭当前页（走 <c>window.close()</c> 同路径，供内部页面调用）。</summary>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void SetZoom(double factor)
    {
        if (View.CoreWebView2 is not null)
        {
            View.ZoomFactor = Math.Clamp(factor, 0.25, 5.0);
        }
    }

    private void UpdateHistoryState()
    {
        if (_core is null)
        {
            return;
        }

        CanGoBack = _core.CanGoBack;
        CanGoForward = _core.CanGoForward;
    }

    /// <summary>
    /// 从 WebView2 的实际地址推导展示地址。
    /// 内部页面在 WebView2 眼里是 <c>file://</c> 路径，但用户应看到 <c>browser://xxx</c>。
    /// </summary>
    private void UpdateUrlFromCore()
    {
        if (_core is null)
        {
            return;
        }

        var source = _core.Source ?? string.Empty;

        // 仍在内部页面上（地址是 WebUI 目录下的文件）→ 继续展示逻辑地址。
        if (_logicalUrl is not null && IsUnderWebUiFolder(source))
        {
            Url = source;
            DisplayUrl = InternalPageRegistry.NormalizeDisplayUrl(_logicalUrl);
            return;
        }

        _logicalUrl = null;
        Url = source;
        DisplayUrl = source;
    }

    private static bool IsUnderWebUiFolder(string source)
    {
        if (!source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var localPath = new Uri(source).LocalPath;
            var root = Path.GetFullPath(AppPaths.WebUiFolder);
            var full = Path.GetFullPath(localPath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string ResolveTitle(CoreWebView2 core)
    {
        var title = core.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var source = core.Source ?? string.Empty;
        if (InternalPageRegistry.IsInternalPage(source) || source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return "新标签页";
        }

        return string.IsNullOrEmpty(source) ? "新标签页" : source;
    }

    private async Task UpdateFaviconAsync(CoreWebView2 core)
    {
        try
        {
            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png)
                .ConfigureAwait(true);
            if (stream is null)
            {
                Favicon = null;
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 拷进内存，脱离流生命周期
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            Favicon = bitmap;
        }
        catch (Exception)
        {
            // 多数页面本就没有 favicon；失败即视为无图标。
            Favicon = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_securityReceiver is not null)
        {
            _securityReceiver.DevToolsProtocolEventReceived -= OnSecurityStateChanged;
            _securityReceiver = null;
        }

        try
        {
            View.Dispose();
        }
        catch (Exception)
        {
            // 关闭期的释放异常无需上抛。
        }

        _core = null;
    }
}

/// <summary>新窗口/新标签请求的参数。</summary>
public sealed class NewWindowRequestedEventArgs : EventArgs
{
    public NewWindowRequestedEventArgs(string url, bool foreground)
    {
        Url = url;
        Foreground = foreground;
    }

    /// <summary>目标地址。</summary>
    public string Url { get; }

    /// <summary>是否应抢占焦点（即切换到新标签）。</summary>
    public bool Foreground { get; }
}
