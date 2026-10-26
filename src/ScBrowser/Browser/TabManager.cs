using System.Collections.ObjectModel;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Services;
using ScBrowser.Storage;

namespace ScBrowser.Browser;

/// <summary>
/// 标签页集合与当前页的中枢。只管业务生命周期（创建 / 关闭 / 切换 / 事件转发），
/// 不直接触碰任何 WPF 控件——可视化树的挂载通过 <see cref="IBrowserTabHost"/> 交给视图层。
/// </summary>
public sealed class TabManager
{
    private readonly BrowserEnvironmentProvider _environment;
    private readonly HistoryRepository _history;
    private readonly ILogger<TabManager> _logger;
    private int _nextId = 1;

    /// <summary>当前应用主题；新标签初始化后据此设置颜色方案与默认底色。</summary>
    private AppliedTheme _webTheme = new("System", IsDark: false);

    /// <summary>无痕会话的控制器选项；普通会话为 <c>null</c>。首次创建标签时生成并复用。</summary>
    private CoreWebView2ControllerOptions? _controllerOptions;
    private bool _controllerOptionsReady;

    public TabManager(
        BrowserEnvironmentProvider environment,
        HistoryRepository history,
        ILogger<TabManager> logger,
        BrowserSessionKind sessionKind = BrowserSessionKind.Normal)
    {
        _environment = environment;
        _history = history;
        _logger = logger;
        SessionKind = sessionKind;

        // 后台休眠：每分钟看一次，把长时间没被激活的标签挂起（省内存/CPU）。
        _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _suspendTimer.Tick += (_, _) => SuspendIdleTabs();
        _suspendTimer.Start();
    }

    /// <summary>本标签页集合所属窗口的会话类型（普通 / 无痕）。</summary>
    public BrowserSessionKind SessionKind { get; }

    /// <summary>是否无痕窗口。无痕下不写历史、不参与会话保存。</summary>
    public bool IsIncognito => SessionKind == BrowserSessionKind.Incognito;

    /// <summary>当前所有标签页，按标签条顺序排列。</summary>
    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    /// <summary>当前激活的标签页；无标签时为 <c>null</c>。</summary>
    public BrowserTab? ActiveTab { get; private set; }

    /// <summary>内容区宿主，由 <c>MainWindow</c> 在加载时注入。</summary>
    public IBrowserTabHost? Host { get; set; }

    /// <summary>标签被创建并完成初始化后触发。</summary>
    public event EventHandler<BrowserTab>? TabCreated;

    /// <summary>标签被关闭后触发。</summary>
    public event EventHandler<BrowserTab>? TabClosed;

    /// <summary>激活标签发生变化时触发（参数为新激活页，可能为 <c>null</c>）。</summary>
    public event EventHandler<BrowserTab?>? ActiveTabChanged;

    /// <summary>最后一个标签被关闭——由外壳决定是否随之退出。</summary>
    public event EventHandler? LastTabClosed;

    /// <summary>最近关闭的标签（Ctrl+Shift+T 用）。</summary>
    public ClosedTabHistory ClosedTabs { get; } = new();

    /// <summary>收到来自任意标签内部页面的 JS 消息（由 <see cref="WebMessageRouter"/> 订阅）。</summary>
    public event EventHandler<TabWebMessageEventArgs>? WebMessageReceived;

    /// <summary>某个标签的网页进入 / 退出全屏（外壳据此收起或恢复 chrome）。</summary>
    public event EventHandler<TabFullScreenEventArgs>? TabFullScreenChanged;

    /// <summary>某个标签遇到证书错误，需要窗口询问用户是否继续。</summary>
    public event EventHandler<CertificateErrorEventArgs>? CertificateErrorDetected;

    /// <summary>后台标签空闲多久后休眠。</summary>
    private static readonly TimeSpan SuspendIdleAfter = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _suspendTimer;

    /// <summary>
    /// 当前浏览器 Profile。扩展管理、清缓存等按 profile 维度的能力都需要它，
    /// 而 profile 只能从已初始化的 CoreWebView2 实例上取。
    /// </summary>
    public CoreWebView2Profile? Profile =>
        ActiveTab?.Core?.Profile
        ?? Tabs.FirstOrDefault(t => t.Core is not null)?.Core?.Profile;

    /// <summary>
    /// 把应用主题同步给网页内容（<c>prefers-color-scheme</c> 随应用主题走）。
    /// <para>
    /// 颜色方案是 profile 级设置，而全应用只有一个 profile，所以设一次即对全部标签生效；
    /// 仍然逐个标签设置，是为了不依赖「之前一定设过」这个隐含前提。
    /// </para>
    /// </summary>
    public void ApplyWebTheme(AppliedTheme theme)
    {
        _webTheme = theme;

        foreach (var tab in Tabs)
        {
            ApplyWebTheme(tab);
        }
    }

    /// <summary>
    /// 新建标签页并导航到 <paramref name="url"/>。
    /// </summary>
    /// <param name="url">起始地址。</param>
    /// <param name="activate">是否切换过去。</param>
    /// <param name="insertAt">插入位置（标签条上的下标）；<c>null</c> 表示追加到末尾。</param>
    /// <param name="deferLoad">
    /// 先不创建引擎，等被激活时再加载（会话惰性恢复）。标签地址与标题照常可用。
    /// </param>
    public async Task<BrowserTab> CreateAsync(
        string url,
        bool activate = true,
        int? insertAt = null,
        bool deferLoad = false)
    {
        var tab = new BrowserTab(_nextId++);
        tab.SetInitialLocation(url);
        tab.Owner = this;

        // 引擎是后创建的，宿主得在那时把它挂进可视化树；
        // 处理器里用 tab.Owner（而不是捕获 this），标签迁移到别的窗口后也能挂对地方。
        tab.EngineAttached += (_, _) => tab.Owner?.AttachEngine(tab);
        tab.EngineDetached += (_, _) => tab.Owner?.DetachEngine(tab);

        Tabs.Add(tab);
        if (insertAt is { } index && index >= 0 && index < Tabs.Count - 1)
        {
            // 「在右侧新增标签页」：先追加再挪位，避免为插入单独写一套初始化流程。
            Tabs.Move(Tabs.Count - 1, index);
        }

        WireTabEvents(tab);

        _logger.LogDebug("已创建标签页 {TabId} → {Url}{Deferred}", tab.Id, url, deferLoad ? "（按需加载）" : string.Empty);

        TabCreated?.Invoke(this, tab);

        if (!deferLoad)
        {
            try
            {
                await EnsureEngineAsync(tab).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 环境创建/初始化失败（常见于 WebView2 运行时缺失）。回收这个半成品标签，
                // 让上层能给出明确错误，而不是留一个永远白屏的标签。
                _logger.LogError(ex, "标签页 {TabId} 初始化失败", tab.Id);
                Tabs.Remove(tab);
                Host?.DetachTab(tab);
                tab.Dispose();
                throw;
            }
        }

        if (activate)
        {
            Activate(tab);
        }
        else if (ActiveTab is null && !deferLoad)
        {
            // 首个立即加载的标签即使不要求激活也应显示，否则窗口内容区是空的。
            Activate(tab);
        }

        // 引擎未就绪时这里只会记下待加载地址，等激活初始化后补上。
        tab.Navigate(url);
        return tab;
    }

    /// <summary>
    /// 确保标签的引擎已创建。失败时把原因挂到标签的错误条上，而不是抛到 UI 线程——
    /// 这条路径由「激活一个惰性标签」触发，用户此刻正看着这个标签。
    /// </summary>
    /// <returns>初始化是否成功。</returns>
    private async Task<bool> TryEnsureEngineAsync(BrowserTab tab)
    {
        // 内核进程退出后引擎被丢弃：重建成功后把页面重新打开到原地址。
        var wasLost = tab.EngineLost;

        try
        {
            await EnsureEngineAsync(tab).ConfigureAwait(true);
            if (wasLost)
            {
                tab.Navigate(tab.DisplayUrl);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "标签页 {TabId} 初始化失败", tab.Id);
            tab.ShowEngineError("页面加载失败：无法初始化浏览器内核（请检查 WebView2 运行时）。");
            return false;
        }
    }

    /// <summary>重新加载 / 停止（工具栏、F5、Ctrl+R 共用）。引擎丢失时重建。</summary>
    public void ReloadTab(BrowserTab? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (tab.EngineLost || !tab.IsInitialized)
        {
            _ = TryEnsureEngineAsync(tab);
            return;
        }

        if (tab.IsLoading)
        {
            tab.Stop();
        }
        else
        {
            tab.Reload();
        }
    }

    /// <summary>创建引擎并把当前应用主题写进它的 profile。</summary>
    private async Task EnsureEngineAsync(BrowserTab tab)
    {
        var environment = await _environment.GetAsync().ConfigureAwait(true);

        // 控制器选项按会话类型生成一次后复用（同窗口所有标签共用一套 profile）。
        if (!_controllerOptionsReady)
        {
            _controllerOptions = BrowserSession.CreateControllerOptions(environment, SessionKind);
            _controllerOptionsReady = true;
        }

        await tab.EnsureInitializedAsync(environment, _controllerOptions).ConfigureAwait(true);

        // CoreWebView2 就绪后才能拿到 profile 设置颜色方案，所以放在这里。
        ApplyWebTheme(tab);
    }

    /// <summary>引擎创建后把控件挂进本窗口的内容区（由 <see cref="BrowserTab.EngineAttached"/> 触发）。</summary>
    private void AttachEngine(BrowserTab tab) => Host?.AttachTab(tab);

    /// <summary>引擎失效时把控件从内容区摘掉（由 <see cref="BrowserTab.EngineDetached"/> 触发）。</summary>
    private void DetachEngine(BrowserTab tab) => Host?.DetachTab(tab);

    /// <summary>激活指定标签页；若它的引擎尚未创建（惰性恢复），现在加载。</summary>
    public void Activate(BrowserTab tab)
    {
        if (!Tabs.Contains(tab) || ReferenceEquals(tab, ActiveTab))
        {
            return;
        }

        if (ActiveTab is not null)
        {
            ActiveTab.IsActive = false;
        }

        ActiveTab = tab;
        tab.IsActive = true;
        Host?.SetActiveTab(tab);

        // 休眠中的标签唤醒；惰性标签此时才创建引擎；引擎丢失的标签重建。
        tab.Resume();
        if (!tab.IsInitialized)
        {
            _ = TryEnsureEngineAsync(tab);
        }

        ActiveTabChanged?.Invoke(this, tab);
    }

    /// <summary>关闭指定标签页。若关闭的是当前页，自动切到相邻标签（优先右侧）。</summary>
    public void Close(BrowserTab tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        // 记进「最近关闭」，供 Ctrl+Shift+T 还原；必须在 Dispose 之前取状态。
        ClosedTabs.Push(tab.DisplayUrl, tab.Title);

        var index = Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, ActiveTab);
        var wasLast = Tabs.Count == 1;

        Tabs.Remove(tab);
        Host?.DetachTab(tab);
        tab.Dispose();

        _logger.LogDebug("已关闭标签页 {TabId}", tab.Id);
        TabClosed?.Invoke(this, tab);

        if (wasLast)
        {
            ActiveTab = null;
            ActiveTabChanged?.Invoke(this, null);
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (wasActive)
        {
            // 优先取右邻；已是最后一个则取左邻（Chrome 的行为）。
            var next = Tabs[Math.Min(index, Tabs.Count - 1)];
            ActiveTab = null;                       // 强制 Activate 走完整切换流程
            Activate(next);
        }
    }

    /// <summary>重开最近关闭的标签页（Ctrl+Shift+T）；没有可还原的记录时返回 <c>null</c>。</summary>
    public async Task<BrowserTab?> ReopenLastClosedAsync()
    {
        if (!ClosedTabs.TryPop(out var entry))
        {
            return null;
        }

        _logger.LogDebug("重开已关闭的标签页 → {Url}", entry.Url);
        return await CreateAsync(entry.Url, activate: true).ConfigureAwait(true);
    }

    /// <summary>关闭当前标签页。</summary>
    public void CloseActive()
    {
        if (ActiveTab is not null)
        {
            Close(ActiveTab);
        }
    }

    /// <summary>按标签条位置选择第 <paramref name="index"/> 个标签（用于 Ctrl+1..8）。</summary>
    public void ActivateByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            Activate(Tabs[index]);
        }
    }

    /// <summary>激活最后一个标签（Ctrl+9，Chrome 语义）。</summary>
    public void ActivateLast()
    {
        if (Tabs.Count > 0)
        {
            Activate(Tabs[^1]);
        }
    }

    /// <summary>在当前标签右侧循环切换（Ctrl+Tab）。</summary>
    public void ActivateNext()
    {
        if (Tabs.Count < 2 || ActiveTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index + 1) % Tabs.Count]);
    }

    /// <summary>在当前标签左侧循环切换（Ctrl+Shift+Tab）。</summary>
    public void ActivatePrevious()
    {
        if (Tabs.Count < 2 || ActiveTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index - 1 + Tabs.Count) % Tabs.Count]);
    }

    /// <summary>把标签移动到新位置（拖拽重排）。</summary>
    public void Move(BrowserTab tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Tabs.Count || oldIndex == newIndex)
        {
            return;
        }

        Tabs.Move(oldIndex, newIndex);
    }

    /// <summary>把当前应用主题写进某个标签的 profile；失败只记日志，不影响浏览。</summary>
    private void ApplyWebTheme(BrowserTab tab)
    {
        if (tab.Core?.Profile is not { } profile)
        {
            return;
        }

        try
        {
            profile.PreferredColorScheme = WebTheme.Resolve(_webTheme.Name);

            // 页面自己上色前的底色也跟着主题，否则深色下新建标签会闪一下白。
            // 注意：这个属性挂在 WPF 的 WebView2 控件上（对应的 CoreWebView2Controller.DefaultBackgroundColor
            // 在 WPF 封装里就是它），不是 CoreWebView2 上的属性。
            if (tab.View is { } view)
            {
                view.DefaultBackgroundColor = WebTheme.DefaultBackground(_webTheme.IsDark);
            }
        }
        catch (Exception ex)
        {
            // profile 已被释放等边界情况：主题同步失败不值得让标签创建失败。
            _logger.LogError(ex, "同步网页颜色方案失败（主题 {Theme}）", _webTheme.Name);
        }
    }

    /// <summary>把长时间没被激活的后台标签挂起（正在播放音频的由 <see cref="BrowserTab.SuspendAsync"/> 自行跳过）。</summary>
    private void SuspendIdleTabs()
    {
        var now = DateTime.UtcNow;
        foreach (var tab in Tabs)
        {
            if (tab.IsActive || !tab.IsInitialized || tab.IsSuspended)
            {
                continue;
            }

            if (now - tab.LastActivatedAt < SuspendIdleAfter)
            {
                continue;
            }

            _ = tab.SuspendAsync();
        }
    }

    /// <summary>
    /// 把标签交给另一个窗口（拖出成新窗口）：不写「最近关闭」、不释放引擎，
    /// 控件从本窗口内容区摘下，由新窗口重新挂上。
    /// </summary>
    public void ReleaseTab(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(tab, ActiveTab);
        Tabs.Remove(tab);
        Host?.DetachTab(tab);
        tab.IsActive = false;

        if (wasActive)
        {
            ActiveTab = null;
        }

        TabClosed?.Invoke(this, tab);

        if (Tabs.Count == 0)
        {
            // 最后一个标签被拖走了：本窗口没有存在的意义（与 Chrome 一致）。
            ActiveTabChanged?.Invoke(this, null);
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (wasActive)
        {
            Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
        }
    }

    /// <summary>接收从别的窗口迁移来的标签（引擎与页面状态都保留）。</summary>
    /// <param name="tab">要接收的标签。</param>
    /// <param name="insertAt">插入位置；<c>null</c> 表示追加到末尾。</param>
    public void Adopt(BrowserTab tab, int? insertAt = null)
    {
        tab.Owner = this;
        Tabs.Add(tab);

        if (insertAt is { } index && index >= 0 && index < Tabs.Count - 1)
        {
            Tabs.Move(Tabs.Count - 1, index);
        }

        if (tab.View is { } view)
        {
            Host?.AttachTab(tab);
            view.Visibility = System.Windows.Visibility.Collapsed;
        }

        Activate(tab);
        _logger.LogDebug("标签页 {TabId} 已迁移到当前窗口", tab.Id);
    }

    private void WireTabEvents(BrowserTab tab)
    {
        // 处理器一律经 tab.Owner 转发：标签被拖到别的窗口后，事件要落到新宿主上，
        // 不能因为闭包捕获了当时的 TabManager 而继续路由回旧窗口。
        tab.NewWindowRequested += async (_, e) =>
        {
            // 页面要求开新窗 → 在本应用开新标签，而不是交给 WebView2 弹裸窗。
            try
            {
                if (tab.Owner is { } owner)
                {
                    await owner.CreateAsync(e.Url, activate: e.Foreground).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "响应 NewWindowRequested 失败：{Url}", e.Url);
            }
        };

        tab.CloseRequested += (_, _) => tab.Owner?.Close(tab);

        tab.WebMessageReceived += (_, e) =>
            tab.Owner?.WebMessageReceived?.Invoke(tab.Owner, new TabWebMessageEventArgs(tab, e));

        tab.NavigationFinished += (_, _) => tab.Owner?.RecordHistory(tab);

        // 导航失败只在界面上挂一条提示，日志里此前什么都没有——排障时看不到，这里补上。
        tab.NavigationFailed += (_, status) =>
            _logger.LogWarning("标签页 {TabId} 导航失败：{Status}", tab.Id, status);

        tab.NavigationBlocked += (_, reason) =>
            _logger.LogWarning("标签页 {TabId} 已拦截：{Reason}", tab.Id, reason);

        tab.ProcessFailed += (_, e) =>
            _logger.LogError("标签页 {TabId} 进程故障：{Kind}（{Reason}，退出码 {ExitCode}）",
                tab.Id, e.ProcessFailedKind, e.Reason, e.ExitCode);

        tab.FullScreenChanged += (_, fullScreen) =>
            tab.Owner?.TabFullScreenChanged?.Invoke(tab.Owner, new TabFullScreenEventArgs(tab, fullScreen));

        tab.CertificateErrorDetected += (_, e) =>
            tab.Owner?.CertificateErrorDetected?.Invoke(tab.Owner, e);
    }

    /// <summary>
    /// 记录一次历史访问。
    /// 内部页面（browser://）、about:、本地 file:// 不入历史——与 Chrome 一致，
    /// 这些不是「用户访问的网站」。无痕窗口一律不记录。
    /// </summary>
    private void RecordHistory(BrowserTab tab)
    {
        if (IsIncognito)
        {
            return;
        }

        var url = tab.DisplayUrl;
        if (string.IsNullOrWhiteSpace(url)
            || InternalPageRegistry.IsInternalPage(url)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 写库放到后台线程：SQLite 是同步 IO，放在 UI 线程上会卡住界面切换。
        _ = RecordHistorySafeAsync(url, tab.Title);
    }

    private async Task RecordHistorySafeAsync(string url, string title)
    {
        try
        {
            await _history.RecordVisitAsync(url, title).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 历史记录失败不应影响浏览本身。
            _logger.LogError(ex, "记录历史失败：{Url}", url);
        }
    }

    /// <summary>关闭全部标签（应用退出时调用）。</summary>
    public void CloseAll()
    {
        _suspendTimer.Stop();

        foreach (var tab in Tabs.ToList())
        {
            Host?.DetachTab(tab);
            tab.Dispose();
        }

        Tabs.Clear();
        ActiveTab = null;
    }
}

/// <summary>某个标签页收到了来自它内部承载页面的 JS 消息。</summary>
public sealed class TabWebMessageEventArgs : EventArgs
{
    public TabWebMessageEventArgs(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs args)
    {
        Tab = tab;
        Args = args;
    }

    /// <summary>消息来自哪个标签页。</summary>
    public BrowserTab Tab { get; }

    /// <summary>WebView2 原始事件参数（含 JSON / 字符串两种取法）。</summary>
    public CoreWebView2WebMessageReceivedEventArgs Args { get; }
}

/// <summary>某个标签的网页进入了 / 退出了全屏。</summary>
public sealed class TabFullScreenEventArgs : EventArgs
{
    public TabFullScreenEventArgs(BrowserTab tab, bool isFullScreen)
    {
        Tab = tab;
        IsFullScreen = isFullScreen;
    }

    public BrowserTab Tab { get; }

    public bool IsFullScreen { get; }
}
