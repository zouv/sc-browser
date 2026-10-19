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

    /// <summary>一次导航失败（携带 WebView2 的错误状态名），供调用方写日志。</summary>
    public event EventHandler<string>? NavigationFailed;

    /// <summary>渲染进程异常退出（用于错误页与恢复）。</summary>
    public event EventHandler<CoreWebView2ProcessFailedEventArgs>? ProcessFailed;

    /// <summary>页内查找的匹配数或当前匹配序号发生变化。</summary>
    public event EventHandler? FindStatusChanged;

    /// <summary>当前查找会话的匹配总数；没有会话时为 0。</summary>
    public int FindMatchCount => _usingJsFind ? _jsFindCount : _find?.MatchCount ?? 0;

    /// <summary>当前匹配序号（从 1 开始）；没有活动匹配时为 -1。</summary>
    public int FindActiveMatchIndex => _usingJsFind ? _jsFindIndex : _find?.ActiveMatchIndex ?? -1;

    /// <summary>当前页面是否可执行页内查找（原生 API 或 JS 兜底任一可用）。</summary>
    public bool IsFindSupported => _core is not null;

    public BrowserTab(int id)
    {
        Id = id;
        View = new WebView2();
    }

    /// <summary>
    /// 绑定 WebView2 环境并接线全部事件。必须在控件已挂入可视化树之后调用。
    /// </summary>
    /// <param name="environment">全应用共用的那一个环境。</param>
    /// <param name="controllerOptions">
    /// 控制器选项；无痕窗口传 InPrivate 选项，普通窗口传 <c>null</c>（沿用默认 profile）。
    /// </param>
    public async Task InitializeAsync(
        CoreWebView2Environment environment,
        CoreWebView2ControllerOptions? controllerOptions = null)
    {
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
            // 内部页面要按逻辑地址重走一遍，否则会丢掉 browser:// 的显示地址。
            Navigate(_logicalUrl ?? failedSource);
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

        core.NavigationStarting += (_, _) =>
        {
            IsLoading = true;
            ClearNavigationError();
        };

        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            UpdateHistoryState();
            if (e.IsSuccess)
            {
                _hasNavigated = true;
                UpdateUrlFromCore();
                ClearNavigationError();
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
            NewWindowRequested?.Invoke(this, new NewWindowRequestedEventArgs(
                e.Uri,
                foreground: true));
        };

        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        core.WebMessageReceived += (_, e) => WebMessageReceived?.Invoke(this, e);
        core.DownloadStarting += (_, e) => DownloadStarting?.Invoke(this, e);
        core.ProcessFailed += (_, e) => ProcessFailed?.Invoke(this, e);

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
        // 内部页面没有 favicon——它们是 file:// 页面，Chromium 不会去取站标（实测
        // FaviconChanged 根本不触发），于是标签条上会一直挂着通用的地球字形。
        // 这里用应用图标兜底，让内部页面的标签也有品牌标识。
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
            View.Dispose();
        }
        catch (Exception)
        {
            // 关闭期的释放异常无需上抛。
        }

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
