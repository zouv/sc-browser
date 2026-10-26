using System.IO;
using System.Text.Json;
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
    private CoreWebView2Find? _find;
    private bool _usingJsFind;
    private int _jsFindCount;
    private int _jsFindIndex = -1;
    private string? _pendingInternalPage;
    private bool _disposed;

    /// <summary>标签内部标识（单调递增，用于状态存储与日志）。</summary>
    public int Id { get; }

    /// <summary>
    /// 标签页的 WebView2 控件；<b>尚未加载引擎时为 <c>null</c></b>。
    /// <para>
    /// 引擎是<b>按需</b>创建的（见 <see cref="EnsureInitializedAsync"/>）：会话恢复出来的后台标签
    /// 只持有地址与标题，激活时才真正创建 WebView2。这正是「标签状态」与「引擎」分开的意义——
    /// 标签集合不再等于 N 个 WebView2 进程。
    /// </para>
    /// </summary>
    public WebView2? View { get; private set; }

    /// <summary>底层 CoreWebView2；在引擎初始化完成前为 <c>null</c>。</summary>
    public CoreWebView2? Core => _core;

    /// <summary>引擎是否已就绪（WebView2 已创建并初始化）。</summary>
    public bool IsInitialized => _core is not null;

    /// <summary>
    /// 内核主进程退出后引擎已失效：标签仍在、状态还在，重新加载时会重建引擎。
    /// </summary>
    public bool EngineLost { get; private set; }

    /// <summary>所属的标签集合（窗口）。迁移到别的窗口后随之改变，事件处理器据此路由。</summary>
    public TabManager? Owner { get; internal set; }

    /// <summary>WebView2 控件已创建，宿主该把它挂进可视化树了。</summary>
    public event EventHandler? EngineAttached;

    /// <summary>引擎失效 / 控件即将销毁，宿主该把它从可视化树摘掉了。</summary>
    public event EventHandler? EngineDetached;

    /// <summary>页面进入 / 退出全屏（HTML5 <c>requestFullscreen</c>），参数为是否全屏。</summary>
    public event EventHandler<bool>? FullScreenChanged;

    /// <summary>遇到证书错误，需要用户决定是否继续；把 <c>Allow</c> 置真表示继续访问。</summary>
    public event EventHandler<CertificateErrorEventArgs>? CertificateErrorDetected;

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

    /// <summary>缩放比例（引擎未创建时也记着，创建后补上）。</summary>
    [ObservableProperty]
    private double _zoomFactor = 1.0;

    /// <summary>链接悬停时地址栏下方的状态提示（由页面透过 WebView2 的状态栏文本给出）。</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>页面是否正在播放音频（驱动标签条上的扬声器图标）。</summary>
    [ObservableProperty]
    private bool _isPlayingAudio;

    /// <summary>标签是否被静音。</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>标签条上是否显示音频图标（正在播放或已静音）。</summary>
    public bool ShowAudioIcon => IsPlayingAudio || IsMuted;

    partial void OnIsPlayingAudioChanged(bool value) => OnPropertyChanged(nameof(ShowAudioIcon));

    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(ShowAudioIcon));

    /// <summary>所属标签组的名称；不在组里时为 <c>null</c>。</summary>
    [ObservableProperty]
    private string? _groupName;

    /// <summary>标签组颜色（#RRGGBB）。</summary>
    [ObservableProperty]
    private string? _groupColor;

    /// <summary>是否属于某个标签组。</summary>
    public bool HasGroup => !string.IsNullOrEmpty(GroupName);

    partial void OnGroupNameChanged(string? value) => OnPropertyChanged(nameof(HasGroup));

    /// <summary>最近一次被激活的时间；后台标签据此判断是否可以休眠。</summary>
    public DateTime LastActivatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>引擎是否处于休眠（WebView2 <c>TrySuspendAsync</c>）。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>待加载的地址：引擎还没创建时先记着，初始化完成后再导航。</summary>
    private string? _pendingUrl;

    /// <summary>待应用的缩放：引擎还没创建时先记着。</summary>
    private double? _pendingZoom;

    /// <summary>引擎初始化任务（并发激活时复用同一次初始化）。</summary>
    private Task? _initialization;

    /// <summary>正在进行的顶层导航地址；证书错误只对它的请求询问用户。</summary>
    private string? _pendingMainFrameUri;

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

    /// <summary>一次导航失败（携带 WebView2 的错误状态名），供调用方写日志。</summary>
    public event EventHandler<string>? NavigationFailed;

    /// <summary>渲染进程异常退出（用于错误页与恢复）。</summary>
    public event EventHandler<CoreWebView2ProcessFailedEventArgs>? ProcessFailed;

    /// <summary>出于安全原因拦下了一次导航 / 开窗（携带说明），供调用方写日志。</summary>
    public event EventHandler<string>? NavigationBlocked;

    /// <summary>页内查找的匹配数或当前匹配序号发生变化。</summary>
    public event EventHandler? FindStatusChanged;

    /// <summary>当前查找会话的匹配总数；没有会话时为 0。</summary>
    public int FindMatchCount => _usingJsFind ? _jsFindCount : _find?.MatchCount ?? 0;

    /// <summary>当前匹配序号（从 1 开始）；没有活动匹配时为 -1。</summary>
    public int FindActiveMatchIndex => _usingJsFind ? _jsFindIndex : _find?.ActiveMatchIndex ?? -1;

    /// <summary>当前页面是否可执行页内查找（原生 API 或 JS 兜底任一可用）。</summary>
    public bool IsFindSupported => _core is not null;

    public BrowserTab(int id) => Id = id;

    /// <summary>
    /// 在引擎创建前先写好「这个标签要显示什么」：地址、显示地址与标题。
    /// 惰性恢复的标签没有任何引擎，标签条与会话保存都得靠这份状态。
    /// </summary>
    public void SetInitialLocation(string url)
    {
        Url = url;
        DisplayUrl = InternalPageRegistry.IsInternalPage(url)
            ? InternalPageRegistry.NormalizeDisplayUrl(url)
            : url;
        Title = InternalPageRegistry.GetPageTitle(url)
                ?? (string.IsNullOrWhiteSpace(url) ? "新标签页" : DisplayUrl);
    }

    /// <summary>
    /// 按需创建引擎并接线事件。重复调用复用同一次初始化；失败后允许重试（下次激活/导航时再来一次）。
    /// </summary>
    public async Task EnsureInitializedAsync(
        CoreWebView2Environment environment,
        CoreWebView2ControllerOptions? controllerOptions = null)
    {
        var task = _initialization ??= InitializeCoreAsync(environment, controllerOptions);
        try
        {
            await task.ConfigureAwait(true);
        }
        catch
        {
            // 失败的初始化不该被永久缓存：运行时更新完、资源恢复后应该能重来。
            if (ReferenceEquals(_initialization, task))
            {
                _initialization = null;
            }

            throw;
        }
    }

    private async Task InitializeCoreAsync(
        CoreWebView2Environment environment,
        CoreWebView2ControllerOptions? controllerOptions)
    {
        if (View is null)
        {
            View = new WebView2 { Visibility = System.Windows.Visibility.Collapsed };

            // 宿主必须在 EnsureCoreWebView2Async 之前把控件挂进可视化树。
            EngineAttached?.Invoke(this, EventArgs.Empty);
        }

        // 复用同一个环境 → 与其它标签共享 Cookie / 缓存 / 扩展安装态；
        // 无痕则靠 controllerOptions 换一套内存 profile。
        if (controllerOptions is null)
        {
            await View.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        else
        {
            await View.EnsureCoreWebView2Async(environment, controllerOptions).ConfigureAwait(true);
        }

        _core = View.CoreWebView2;
        ConfigureSettings(_core.Settings);
        ServeInternalPages(_core);
        WireEvents(_core);
        await EnableSecurityEventsAsync(_core).ConfigureAwait(true);

        EngineLost = false;
        IsSuspended = false;

        // 新引擎先跟当前标签状态对齐（静音、音频指示）。
        try
        {
            _core.IsMuted = IsMuted;
            IsPlayingAudio = _core.IsDocumentPlayingAudio;
        }
        catch (Exception)
        {
            // 个别运行时没有这两个属性：只是少了指示，不影响浏览。
        }

        // 引擎就绪前收到的导航 / 缩放请求，现在补上。
        if (_pendingZoom is { } zoom)
        {
            _pendingZoom = null;
            SetZoom(zoom);
        }

        if (_pendingUrl is { } pending)
        {
            _pendingUrl = null;
            Navigate(pending);
        }
    }

    /// <summary>
    /// 由宿主直接供给 <c>browser://</c> 的页面与资源（scheme 在环境创建时注册，见
    /// <see cref="BrowserEnvironmentProvider"/>）。过滤器必须在首次导航前加上。
    /// </summary>
    private static void ServeInternalPages(CoreWebView2 core)
    {
        const string filter = InternalPageRegistry.Scheme + "*";
        try
        {
            core.AddWebResourceRequestedFilter(
                filter,
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document);
        }
        catch (Exception)
        {
            // 旧运行时没有带来源类型的重载（ICoreWebView2_22）；退回旧版重载，行为对顶层文档一致。
#pragma warning disable CS0618
            core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
#pragma warning restore CS0618
        }

        core.WebResourceRequested += (_, e) =>
        {
            if (!InternalPageRegistry.IsInternalPage(e.Request.Uri))
            {
                return;
            }

            var path = InternalPageRegistry.ResolveResourcePath(e.Request.Uri);
            if (path is null)
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                return;
            }

            // 整个文件读进内存：WebUI 文件都很小，这样不必操心 WebView2 何时释放流、文件句柄何时关。
            var content = new MemoryStream(File.ReadAllBytes(path), writable: false);
            e.Response = core.Environment.CreateWebResourceResponse(
                content,
                200,
                "OK",
                $"Content-Type: {InternalPageRegistry.ContentTypeOf(path)}\r\n"
                + "Cache-Control: no-cache\r\n"
                // 内部页面不允许被任何页面嵌进 iframe（防点击劫持），也不加载插件/改基址。
                + "Content-Security-Policy: frame-ancestors 'none'; object-src 'none'; base-uri 'none'");
        };
    }

    /// <summary>
    /// 网页不得自行跳到内部页面（链接、<c>location=</c>、服务器重定向都算）：内部页面握有宿主能力，
    /// 只该由用户（地址栏/菜单/快捷键）或内部页面自己打开。
    /// 放行：宿主发起的导航、从内部页面出发的导航、前进/后退/刷新、空白页起步。
    /// </summary>
    private bool ShouldBlockInternalNavigation(CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!InternalPageRegistry.TryGetPageName(e.Uri, out var target))
        {
            return false;
        }

        if (_pendingInternalPage is not null && string.Equals(_pendingInternalPage, target, StringComparison.Ordinal))
        {
            _pendingInternalPage = null;
            return false;
        }

        var current = core.Source;
        if (string.IsNullOrEmpty(current)
            || current.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || InternalPageRegistry.IsInternalPage(current))
        {
            return false;
        }

        try
        {
            if (e.NavigationKind is CoreWebView2NavigationKind.BackOrForward or CoreWebView2NavigationKind.Reload)
            {
                return false;
            }
        }
        catch (Exception)
        {
            // 旧运行时拿不到导航类型：放行。消息桥按来源鉴权才是主防线，这里只是纵深防御。
            return false;
        }

        return true;
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

    /// <summary>失败提示延迟多久才落地（毫秒）。</summary>
    private const int NavigationErrorDelayMs = 700;

    /// <summary>首次导航失败后隔多久自动重试（毫秒）。</summary>
    private const int FirstNavigationRetryDelayMs = 400;

    /// <summary>用于作废「已排队但尚未落地」的错误提示。</summary>
    private int _errorVersion;

    /// <summary>本标签是否成功导航过（用于日志标注首次导航失败）。</summary>
    private bool _hasNavigated;

    /// <summary>首次导航是否已经自动重试过（只重试一次，避免掩盖真实故障）。</summary>
    private bool _retriedFirstNavigation;

    /// <summary>导航恢复正常（开始新导航或成功完成）时清掉错误并作废未落地的提示。</summary>
    private void ClearNavigationError()
    {
        _errorVersion++;
        LastError = null;
    }

    /// <summary>
    /// 标签的首次导航失败时自动重试一次。
    /// <para>
    /// 首次导航最容易撞上 WebView2 冷启动时的偶发失败（新开窗口、新建标签时尤其明显）——
    /// 直接挂错误条的表现就是用户看到的「闪一下」。重试成功则用户完全无感；
    /// 重试仍失败才走正常的错误提示，所以真实故障（站点挂了、域名解析不了）不会被掩盖。
    /// </para>
    /// </summary>
    private bool TryRetryFirstNavigation(CoreWebView2 core)
    {
        if (_hasNavigated || _retriedFirstNavigation || Core?.Source is not { Length: > 0 } source)
        {
            return false;
        }

        _retriedFirstNavigation = true;
        _ = RetryFirstNavigationAsync(source);
        return true;
    }

    private async Task RetryFirstNavigationAsync(string failedSource)
    {
        await Task.Delay(FirstNavigationRetryDelayMs).ConfigureAwait(true);

        // 期间已经成功、或用户又发起了别的导航 → 放弃这次重试。
        if (_disposed || _hasNavigated || Core?.Source != failedSource)
        {
            return;
        }

        try
        {
            Navigate(failedSource);
        }
        catch (Exception)
        {
            // 标签可能已被关闭；重试失败也不该抛到 UI 线程。
        }
    }

    /// <summary>
    /// 记一次导航失败，但<b>延迟</b>再显示。
    /// <para>
    /// 为什么不当场显示：WebView2 冷启动、被后续导航取代的请求，都可能在「失败」后几毫秒内
    /// 就成功，立刻挂红条的表现就是用户看到的「闪一下」（新开窗口时最明显）。
    /// 延迟一小会儿后如果这次失败仍是最新的一次（版本号没被作废），才认定是真失败；
    /// 真失败（主机解析不了等）只是晚几百毫秒出现，不影响可用性。
    /// </para>
    /// </summary>
    private async void ReportNavigationError(CoreWebView2WebErrorStatus status)
    {
        var version = ++_errorVersion;
        var wasFirst = !_hasNavigated;
        await Task.Delay(NavigationErrorDelayMs).ConfigureAwait(true);

        if (version != _errorVersion || _disposed)
        {
            return;
        }

        LastError = $"导航失败：{status}";
        // 日志带上地址与「是不是该标签的首次导航」——排障时最想知道的正是这两点。
        NavigationFailed?.Invoke(this, $"{status} → {DisplayUrl}{(wasFirst ? "（首个导航）" : string.Empty)}");
    }

    private void WireEvents(CoreWebView2 core)
    {
        core.SourceChanged += (_, _) => UpdateUrlFromCore();
        core.HistoryChanged += (_, _) => UpdateHistoryState();

        core.NavigationStarting += (_, e) =>
        {
            if (ShouldBlockInternalNavigation(core, e))
            {
                e.Cancel = true;
                NavigationBlocked?.Invoke(this, $"网页 {core.Source} 试图跳转到内部页面 {e.Uri}");
                return;
            }

            _pendingMainFrameUri = e.Uri;
            IsLoading = true;
            ClearNavigationError();
        };

        core.NavigationCompleted += (_, e) =>
        {
            _pendingMainFrameUri = null;
            IsLoading = false;
            UpdateHistoryState();
            if (e.IsSuccess)
            {
                _hasNavigated = true;
                UpdateUrlFromCore();
                ClearNavigationError();
                if (InternalPageRegistry.IsInternalPage(DisplayUrl))
                {
                    // 内部页面不保证触发 FaviconChanged，导航完成时直接挂上品牌图标。
                    Favicon = BrandIcon.Value;
                }

                NavigationFinished?.Invoke(this, EventArgs.Empty);
            }
            else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                // 被新导航取代、或用户按了停止：这不是「错误」，不该给用户挂红条。
            }
            else if (TryRetryFirstNavigation(core))
            {
                // 首次导航的偶发失败已经排了重试，先不打扰用户。
            }
            else
            {
                ReportNavigationError(e.WebErrorStatus);
            }
        };

        core.DocumentTitleChanged += (_, _) => Title = ResolveTitle(core);
        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync(core).ConfigureAwait(true);

        core.NewWindowRequested += (_, e) =>
        {
            // 我们自行决定在新标签打开，而不是让 WebView2 弹一个无壳的裸窗口。
            e.Handled = true;

            // 网页开窗到内部页面同样拦下（新标签由宿主导航，绕得过 NavigationStarting 的拦截）。
            if (InternalPageRegistry.IsInternalPage(e.Uri) && !InternalPageRegistry.IsInternalPage(core.Source))
            {
                NavigationBlocked?.Invoke(this, $"网页 {core.Source} 试图在新窗口打开内部页面 {e.Uri}");
                return;
            }

            NewWindowRequested?.Invoke(this, new NewWindowRequestedEventArgs(
                e.Uri,
                foreground: true));
        };

        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        core.WebMessageReceived += (_, e) => WebMessageReceived?.Invoke(this, e);
        core.DownloadStarting += (_, e) => DownloadStarting?.Invoke(this, e);
        core.ProcessFailed += (_, e) =>
        {
            OnProcessFailed(e);
            ProcessFailed?.Invoke(this, e);
        };

        // 链接悬停地址：WebView2 自己的状态栏是画在网页里的，关掉它、由外壳在底部绘制。
        core.StatusBarTextChanged += (_, _) =>
            StatusText = string.IsNullOrEmpty(core.StatusBarText) ? null : core.StatusBarText;

        // 标签条上的扬声器图标 / 静音状态。
        core.IsDocumentPlayingAudioChanged += (_, _) => IsPlayingAudio = core.IsDocumentPlayingAudio;
        core.IsMutedChanged += (_, _) => IsMuted = core.IsMuted;

        // 网页视频全屏（requestFullscreen）：让外壳收起标签条与工具栏。
        core.ContainsFullScreenElementChanged += (_, _) =>
            FullScreenChanged?.Invoke(this, core.ContainsFullScreenElement);

        // 证书错误：只对「正在进行的顶层导航」询问用户。
        // 子资源（图片/脚本）也走这个事件，对每一个都弹窗是不可接受的；
        // 顶层导航一旦被取消，WebView2 会显示自己的证书错误页。
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (string.IsNullOrEmpty(_pendingMainFrameUri)
                || !string.Equals(e.RequestUri, _pendingMainFrameUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var args = new CertificateErrorEventArgs(e.RequestUri, e.ServerCertificate, e.ErrorStatus);
            CertificateErrorDetected?.Invoke(this, args);
            e.Action = args.Allow
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        };

        // 页内查找是较新的平台能力（需要 ICoreWebView2_28）。旧运行时上访问 Find
        // 会直接抛 NotImplementedException；此时退回到 ExecuteScript + CSS Highlight
        // 的 JS 实现，保证功能仍然可用。
        try
        {
            _find = core.Find;
            _find.MatchCountChanged += (_, _) => FindStatusChanged?.Invoke(this, EventArgs.Empty);
            _find.ActiveMatchIndexChanged += (_, _) => FindStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            _find = null;
        }
    }

    /// <summary>
    /// 进程故障的用户可见处理。此前该事件只是转发、无人订阅——渲染进程崩了标签就静静地白屏。
    /// <para>
    /// 渲染进程退出/无响应：挂错误条，用户点「重新加载」即可恢复（WebView2 会重建渲染进程）。
    /// 浏览器主进程退出：这个 WebView2 已不可再用，只能关掉标签重开——自动重建引擎属于
    /// 「标签状态与引擎拆分」那一步（见 ROADMAP）。其余进程（GPU / 工具进程、子帧）由 Chromium 自行拉起。
    /// </para>
    /// </summary>
    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // 整个内核进程没了：这个 WebView2 已经不可用。状态（地址/标题）留着，
            // 用户点「重新加载」或再次激活标签时会重建引擎（见 TabManager.TryEnsureEngineAsync）。
            LoseEngine();
            _errorVersion++;
            IsLoading = false;
            LastError = "浏览器内核进程已退出，点击「重新加载」重建页面。";
            return;
        }

        var message = e.ProcessFailedKind switch
        {
            CoreWebView2ProcessFailedKind.RenderProcessExited => "页面已崩溃，点击「重新加载」恢复。",
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => "页面无响应，可以等待或点击「重新加载」。",
            _ => null,
        };

        if (message is null)
        {
            return;
        }

        _errorVersion++;            // 作废还在排队的导航错误提示，免得把崩溃提示盖掉
        IsLoading = false;
        LastError = message;
    }

    /// <summary>内核进程退出后丢弃当前引擎，保留标签状态。</summary>
    private void LoseEngine()
    {
        EngineLost = true;
        IsSuspended = false;

        if (_securityReceiver is not null)
        {
            _securityReceiver.DevToolsProtocolEventReceived -= OnSecurityStateChanged;
            _securityReceiver = null;
        }

        var view = View;
        View = null;
        _core = null;
        _find = null;
        _initialization = null;

        // 先让宿主把控件从可视化树摘掉，再释放它。
        EngineDetached?.Invoke(this, EventArgs.Empty);
        try
        {
            view?.Dispose();
        }
        catch (Exception)
        {
            // 内核已经退出，释放失败不影响后续重建。
        }
    }

    /// <summary>由宿主挂一条错误提示（例如引擎初始化失败）。</summary>
    public void ShowEngineError(string message)
    {
        _errorVersion++;
        IsLoading = false;
        LastError = message;
    }

    /// <summary>激活时间更新（供后台休眠判定）。</summary>
    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            LastActivatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 休眠：长时间没被看的标签让 WebView2 暂停脚本与渲染，省内存/CPU。
    /// 正在播放音频的标签不碰（用户还在听）。
    /// </summary>
    public async Task SuspendAsync()
    {
        if (_core is null || IsSuspended || IsActive)
        {
            return;
        }

        try
        {
            if (_core.IsDocumentPlayingAudio)
            {
                return;
            }

            await _core.TrySuspendAsync().ConfigureAwait(true);
            IsSuspended = true;
        }
        catch (Exception)
        {
            // 旧运行时没有 TrySuspendAsync；休眠只是优化，失败即放弃。
        }
    }

    /// <summary>从休眠中唤醒（切回该标签时调用）。</summary>
    public void Resume()
    {
        if (!IsSuspended)
        {
            return;
        }

        try
        {
            _core?.Resume();
        }
        catch (Exception)
        {
            // 引擎已退出等边界情况：重建由后续导航/激活负责。
        }

        IsSuspended = false;
        LastActivatedAt = DateTime.UtcNow;
    }

    /// <summary>切换静音。</summary>
    public void ToggleMute()
    {
        if (_core is null)
        {
            // 引擎未创建：记在状态里，初始化时应用。
            IsMuted = !IsMuted;
            return;
        }

        try
        {
            _core.IsMuted = !_core.IsMuted;
            IsMuted = _core.IsMuted;
        }
        catch (Exception)
        {
            // 个别运行时没有 IsMuted。
        }
    }

    /// <summary>打印当前页面（Ctrl+P）；引擎不可用时返回 <c>false</c>。</summary>
    public bool Print()
    {
        try
        {
            _core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            return _core is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>另存为（Ctrl+S）；当前 WebView2 运行时太旧时返回 <c>false</c>。</summary>
    public async Task<bool> SavePageAsync()
    {
        if (_core is null)
        {
            return false;
        }

        try
        {
            await _core.ShowSaveAsUIAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
    /// 导航到指定地址。内部页面（<c>browser://</c>）未登记时退回新标签页。
    /// 这是宿主发起的导航，所以即使当前在普通网页上，也放行到内部页面（见 <see cref="ShouldBlockInternalNavigation"/>）。
    /// </summary>
    public void Navigate(string url)
    {
        if (_core is null)
        {
            // 引擎还没创建（惰性恢复的后台标签）：先记下来，初始化完成后再导航。
            _pendingUrl = url;
            SetInitialLocation(url);
            return;
        }

        // 休眠中的标签要接收导航，先唤醒。
        Resume();

        if (InternalPageRegistry.IsInternalPage(url))
        {
            // 未登记的内部页面：退回新标签页，避免落到 404。
            var target = InternalPageRegistry.ToNavigableUri(url)
                         ?? InternalPageRegistry.ToNavigableUri(InternalPageRegistry.NewTabUrl);
            if (target is null || !InternalPageRegistry.TryGetPageName(target, out var page))
            {
                return;
            }

            _pendingInternalPage = page;
            _core.Navigate(target);
        }
        else
        {
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
        var value = Math.Clamp(factor, 0.25, 5.0);
        ZoomFactor = value;

        if (View?.CoreWebView2 is null)
        {
            // 引擎未创建：记下来，初始化完成后补上（菜单里显示的比例也已经是新值）。
            _pendingZoom = value;
            return;
        }

        View.ZoomFactor = value;
    }

    /// <summary>把输入焦点交给页面（切回网页内容时用）；引擎未创建时无操作。</summary>
    public void FocusContent() => View?.Focus();

    // --------------------------------------------------------------------- //
    // 页内查找
    //
    // 首选 WebView2 的 Find API（隐藏它自带的查找条，由外壳绘制与主题一致的 UI）。
    // 但该 API 需要较新的 WebView2 运行时；旧运行时（如 131.x）访问 CoreWebView2.Find
    // 会抛 NotImplementedException，此时退回到 JS 实现：
    //   · 用 TreeWalker 收集文本节点里的匹配区间；
    //   · 用 CSS Custom Highlight API 高亮（不修改页面 DOM，站点脚本也看不到）；
    //   · 状态挂在 window.__scFind 上，跨多次 ExecuteScriptAsync 保持。
    // JS 路径只覆盖主文档、且匹配不跨文本节点——对旧运行时是可接受的降级。
    // --------------------------------------------------------------------- //

    /// <summary>开始一次查找（从页首搜索）。返回是否成功发起。</summary>
    public async Task<bool> StartFindAsync(string term, bool caseSensitive = false)
    {
        if (_core is null || string.IsNullOrEmpty(term))
        {
            return false;
        }

        if (_find is not null && await StartNativeFindAsync(term, caseSensitive).ConfigureAwait(true))
        {
            _usingJsFind = false;
            return true;
        }

        // 运行时只实现了部分 Find 接口、或本次会话启动失败：改走 JS 实现。
        _usingJsFind = true;
        return await ExecuteJsFindAsync(BuildFindStartScript(term)).ConfigureAwait(true);
    }

    private async Task<bool> StartNativeFindAsync(string term, bool caseSensitive)
    {
        try
        {
            _find!.Stop();

            var options = _core!.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = caseSensitive;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;

            await _find.StartAsync(options).ConfigureAwait(true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>跳到下一个匹配。</summary>
    public void FindNext()
    {
        if (_find is not null && !_usingJsFind)
        {
            try
            {
                _find.FindNext();
            }
            catch (Exception)
            {
                // 没有活动会话时静默失败即可。
            }

            return;
        }

        _ = ExecuteJsFindAsync(BuildFindStepScript(+1));
    }

    /// <summary>跳到上一个匹配。</summary>
    public void FindPrevious()
    {
        if (_find is not null && !_usingJsFind)
        {
            try
            {
                _find.FindPrevious();
            }
            catch (Exception)
            {
                // 没有活动会话时静默失败即可。
            }

            return;
        }

        _ = ExecuteJsFindAsync(BuildFindStepScript(-1));
    }

    /// <summary>结束查找会话并清除页面上的高亮。</summary>
    public void StopFind()
    {
        if (_find is not null && !_usingJsFind)
        {
            try
            {
                _find.Stop();
            }
            catch (Exception)
            {
                // 标签可能正在关闭，无需上抛。
            }

            return;
        }

        _ = ExecuteJsFindAsync(FindStopScript);
    }

    /// <summary>
    /// 执行一段 JS 查找脚本，并把返回的 <c>{count, index, supported?}</c> 写回状态。
    /// </summary>
    private async Task<bool> ExecuteJsFindAsync(string script)
    {
        if (_core is null)
        {
            return false;
        }

        try
        {
            var json = await _core.ExecuteScriptAsync(script).ConfigureAwait(true);
            if (!TryParseFindResult(json, out var count, out var index, out var supported))
            {
                return false;
            }

            _jsFindCount = count;
            _jsFindIndex = index > 0 ? index : -1;

            if (!supported)
            {
                return false;
            }

            FindStatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception)
        {
            // 页面已被导航走 / 无法执行脚本：调用方据此显示「查找不可用」。
            return false;
        }
    }

    private static bool TryParseFindResult(string? json, out int count, out int index, out bool supported)
    {
        count = 0;
        index = 0;
        supported = true;

        if (string.IsNullOrEmpty(json) || json == "null")
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("count", out var countProp) || !countProp.TryGetInt32(out count)
                || !root.TryGetProperty("index", out var indexProp) || !indexProp.TryGetInt32(out index))
            {
                return false;
            }

            // 没有该字段 = 支持；显式 false 才是「浏览器没有 CSS Highlight API」。
            supported = !root.TryGetProperty("supported", out var supportedProp)
                        || supportedProp.ValueKind != JsonValueKind.False;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildFindStartScript(string term)
    {
        // 用 JSON 序列化得到合法的 JS 字符串字面量（引号/反斜杠/换行都安全）。
        var termLiteral = JsonSerializer.Serialize(term);

        return $$"""
            (() => {
              if (window.__scFind) { window.__scFind.ranges = []; }
              window.__scFind = { term: {{termLiteral}}, ranges: [], index: 0 };

              const styleId = '__sc_find_style';
              if (!document.getElementById(styleId)) {
                const style = document.createElement('style');
                style.id = styleId;
                style.textContent =
                  '::highlight(sc-find-all){background:#ffe066;color:#111;}' +
                  '::highlight(sc-find-active){background:#ff8c00;color:#111;}';
                (document.head || document.documentElement).appendChild(style);
              }

              window.__scFindApply = function (index) {
                const state = window.__scFind;
                if (!state || state.ranges.length === 0) {
                  if (window.CSS && CSS.highlights) {
                    CSS.highlights.delete('sc-find-all');
                    CSS.highlights.delete('sc-find-active');
                  }
                  return { count: 0, index: 0 };
                }

                const total = state.ranges.length;
                state.index = ((index % total) + total) % total;
                const active = state.ranges[state.index];

                try {
                  CSS.highlights.set('sc-find-all', new Highlight(...state.ranges));
                  CSS.highlights.set('sc-find-active', new Highlight(active));
                } catch (e) {
                  // 匹配过多时可能超出参数上限：至少保证当前匹配可见。
                  try { CSS.highlights.set('sc-find-active', new Highlight(active)); } catch (e2) {}
                }

                const el = active.startContainer.nodeType === 3
                  ? active.startContainer.parentElement
                  : active.startContainer;
                if (el && el.scrollIntoView) {
                  el.scrollIntoView({ block: 'center', inline: 'nearest' });
                }

                return { count: total, index: state.index + 1 };
              };

              if (!window.CSS || !CSS.highlights) {
                return { count: 0, index: 0, supported: false };
              }

              const root = document.body || document.documentElement;
              if (!root) { return { count: 0, index: 0 }; }

              const needle = {{termLiteral}};
              const hayNeedle = needle.toLowerCase();
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
                acceptNode(node) {
                  const parent = node.parentElement;
                  if (!parent) { return NodeFilter.FILTER_REJECT; }
                  const tag = parent.tagName;
                  if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' ||
                      tag === 'TEXTAREA' || tag === 'INPUT' || tag === 'SELECT') {
                    return NodeFilter.FILTER_REJECT;
                  }
                  return node.nodeValue ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
                }
              });

              let node;
              while ((node = walker.nextNode()) && window.__scFind.ranges.length < 10000) {
                const text = node.nodeValue;
                const hay = text.toLowerCase();
                let at = 0;
                while ((at = hay.indexOf(hayNeedle, at)) !== -1) {
                  const range = document.createRange();
                  range.setStart(node, at);
                  range.setEnd(node, at + needle.length);
                  window.__scFind.ranges.push(range);
                  at += needle.length;
                }
              }

              return window.__scFindApply(0);
            })()
            """;
    }

    private static string BuildFindStepScript(int delta) => $$"""
        (() => {
          const state = window.__scFind;
          if (!state || typeof window.__scFindApply !== 'function') {
            return { count: 0, index: 0 };
          }
          return window.__scFindApply(state.index + ({{delta}}));
        })()
        """;

    private const string FindStopScript = """
        (() => {
          window.__scFind = null;
          window.__scFindApply = null;
          if (window.CSS && CSS.highlights) {
            CSS.highlights.delete('sc-find-all');
            CSS.highlights.delete('sc-find-active');
          }
          return { count: 0, index: 0 };
        })()
        """;

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
    /// 从 WebView2 的实际地址推导展示地址：内部页面去掉尾部的 <c>/</c>（<c>browser://settings</c>），其余原样。
    /// </summary>
    private void UpdateUrlFromCore()
    {
        if (_core is null)
        {
            return;
        }

        var source = _core.Source ?? string.Empty;
        Url = source;
        DisplayUrl = InternalPageRegistry.IsInternalPage(source)
            ? InternalPageRegistry.NormalizeDisplayUrl(source)
            : source;
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
        // 内部页面没有自己的 favicon（宿主对 /favicon.ico 返回 404），
        // 标签条上会一直挂着通用的地球字形——用应用图标兜底，让内部页面的标签也有品牌标识。
        if (InternalPageRegistry.IsInternalPage(DisplayUrl))
        {
            Favicon = BrandIcon.Value;
            return;
        }

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

    /// <summary>内部页面用的品牌图标（懒加载 + 冻结，供所有标签共享）。</summary>
    private static readonly Lazy<ImageSource?> BrandIcon = new(() =>
    {
        try
        {
            var icon = new BitmapImage(new Uri("pack://application:,,,/Assets/sc-browser.ico"));
            icon.Freeze();
            return icon;
        }
        catch (Exception)
        {
            // 取不到图标只是少个装饰，不该影响标签创建。
            return null;
        }
    });

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
            // 惰性恢复的标签可能从未创建过引擎。
            View?.Dispose();
        }
        catch (Exception)
        {
            // 关闭期的释放异常无需上抛。
        }

        View = null;
        _core = null;
        _find = null;
        _usingJsFind = false;
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

/// <summary>证书错误：用户选择是否继续访问。</summary>
public sealed class CertificateErrorEventArgs : EventArgs
{
    public CertificateErrorEventArgs(
        string requestUri,
        CoreWebView2Certificate certificate,
        CoreWebView2WebErrorStatus errorStatus)
    {
        RequestUri = requestUri;
        Certificate = certificate;
        ErrorStatus = errorStatus;
    }

    public string RequestUri { get; }

    public CoreWebView2Certificate Certificate { get; }

    public CoreWebView2WebErrorStatus ErrorStatus { get; }

    /// <summary>是否继续访问；默认不继续（与浏览器的安全默认一致）。</summary>
    public bool Allow { get; set; }
}
