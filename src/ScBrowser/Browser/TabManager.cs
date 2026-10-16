using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
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

    public TabManager(
        BrowserEnvironmentProvider environment,
        HistoryRepository history,
        ILogger<TabManager> logger)
    {
        _environment = environment;
        _history = history;
        _logger = logger;
    }

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

    /// <summary>收到来自任意标签内部页面的 JS 消息（由 <see cref="WebMessageRouter"/> 订阅）。</summary>
    public event EventHandler<TabWebMessageEventArgs>? WebMessageReceived;

    /// <summary>
    /// 当前浏览器 Profile。扩展管理、清缓存等按 profile 维度的能力都需要它，
    /// 而 profile 只能从已初始化的 CoreWebView2 实例上取。
    /// </summary>
    public CoreWebView2Profile? Profile =>
        ActiveTab?.Core?.Profile
        ?? Tabs.FirstOrDefault(t => t.Core is not null)?.Core?.Profile;

    /// <summary>
    /// 新建标签页并导航到 <paramref name="url"/>。
    /// </summary>
    public async Task<BrowserTab> CreateAsync(string url, bool activate = true)
    {
        var tab = new BrowserTab(_nextId++);

        Tabs.Add(tab);
        Host?.AttachTab(tab);

        try
        {
            var environment = await _environment.GetAsync().ConfigureAwait(true);
            await tab.InitializeAsync(environment).ConfigureAwait(true);
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

        WireTabEvents(tab);

        _logger.LogDebug("已创建标签页 {TabId} → {Url}", tab.Id, url);

        TabCreated?.Invoke(this, tab);

        if (activate)
        {
            Activate(tab);
        }
        else if (ActiveTab is null)
        {
            // 首个标签即使不要求激活也应显示，否则窗口内容区是空的。
            Activate(tab);
        }

        tab.Navigate(url);
        return tab;
    }

    /// <summary>激活指定标签页。</summary>
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
        ActiveTabChanged?.Invoke(this, tab);
    }

    /// <summary>关闭指定标签页。若关闭的是当前页，自动切到相邻标签（优先右侧）。</summary>
    public void Close(BrowserTab tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

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

    private void WireTabEvents(BrowserTab tab)
    {
        tab.NewWindowRequested += async (_, e) =>
        {
            // 页面要求开新窗 → 在本应用开新标签，而不是交给 WebView2 弹裸窗。
            try
            {
                await CreateAsync(e.Url, activate: e.Foreground).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "响应 NewWindowRequested 失败：{Url}", e.Url);
            }
        };

        tab.CloseRequested += (_, _) => Close(tab);

        tab.WebMessageReceived += (_, e) => WebMessageReceived?.Invoke(this, new TabWebMessageEventArgs(tab, e));

        tab.NavigationFinished += (_, _) => RecordHistory(tab);
    }

    /// <summary>
    /// 记录一次历史访问。
    /// 内部页面（browser://）、about:、本地 file:// 不入历史——与 Chrome 一致，
    /// 这些不是「用户访问的网站」。
    /// </summary>
    private void RecordHistory(BrowserTab tab)
    {
        var url = tab.DisplayUrl;
        if (string.IsNullOrWhiteSpace(url)
            || InternalPageRegistry.IsInternalPage(url)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _history.RecordVisit(url, tab.Title);
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
