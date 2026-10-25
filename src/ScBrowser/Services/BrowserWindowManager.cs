using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;
using ScBrowser.Storage;
using ScBrowser.ViewModels;
using ScBrowser.Views;

namespace ScBrowser.Services;

/// <summary>新建窗口的入参。</summary>
/// <param name="Kind">普通 / 无痕。</param>
/// <param name="Urls">初始标签页；为空则打开主页。</param>
/// <param name="ActiveIndex">初始激活的标签下标。</param>
/// <param name="IsPrimary">是否「主窗口」——只有主窗口负责会话恢复与会话/窗口几何的保存。</param>
public sealed record BrowserWindowOptions(
    BrowserSessionKind Kind = BrowserSessionKind.Normal,
    IReadOnlyList<string>? Urls = null,
    int ActiveIndex = 0,
    bool IsPrimary = false);

/// <summary>
/// 窗口总管：创建 / 跟踪所有浏览器窗口，并把「每个窗口一份」的东西按窗口组装起来。
/// <para>
/// <b>为什么需要它</b>：标签页集合（<see cref="TabManager"/>）与主视图模型
/// （<see cref="MainViewModel"/>）天然是「一个窗口一份」，而设置、主题、环境、仓储却是全应用共享。
/// 于是这里成为唯一的组装点：per-window 的对象手工 new，共享对象走构造函数注入。
/// </para>
/// <para>
/// <b>为什么不让 DI 直接解析窗口</b>：窗口之间只有「会话类型」这一个差异，
/// 而 DI 容器不适合表达「按参数构造」；手工组装反而更直白，也避免了
/// 单例服务误持某个具体窗口实例。
/// </para>
/// </summary>
public sealed class BrowserWindowManager
{
    private readonly BrowserEnvironmentProvider _environment;
    private readonly HistoryRepository _history;
    private readonly BookmarkRepository _bookmarks;
    private readonly SettingsService _settings;
    private readonly SessionStore _session;
    private readonly ThemeService _theme;
    private readonly StartupCommandRunner _startupCommands;
    private readonly DefaultBrowserService _defaultBrowser;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BrowserWindowManager> _logger;

    private readonly List<MainWindow> _windows = [];
    private readonly List<TabManager> _tabManagers = [];

    /// <summary>当前应用主题，用于给新窗口的标签页设置颜色方案与默认底色。</summary>
    private AppliedTheme _webTheme = new("System", IsDark: false);

    public BrowserWindowManager(
        BrowserEnvironmentProvider environment,
        HistoryRepository history,
        BookmarkRepository bookmarks,
        SettingsService settings,
        SessionStore session,
        ThemeService theme,
        StartupCommandRunner startupCommands,
        DefaultBrowserService defaultBrowser,
        ILoggerFactory loggerFactory,
        ILogger<BrowserWindowManager> logger)
    {
        _environment = environment;
        _history = history;
        _bookmarks = bookmarks;
        _settings = settings;
        _session = session;
        _theme = theme;
        _startupCommands = startupCommands;
        _defaultBrowser = defaultBrowser;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>所有窗口（含无痕窗口）。</summary>
    public IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>所有窗口的标签页集合。跨窗口的服务（消息路由、下载）靠它覆盖全部标签。</summary>
    public IReadOnlyList<TabManager> TabManagers => _tabManagers;

    /// <summary>新窗口的标签页集合就绪——供 DownloadManager / WebMessageRouter 逐窗口订阅。</summary>
    public event EventHandler<TabManager>? TabManagerCreated;

    /// <summary>窗口关闭。</summary>
    public event EventHandler<MainWindow>? WindowClosed;

    /// <summary>是否存在普通（非无痕）窗口——会话保存只看这些窗口。</summary>
    public bool HasNormalWindow => _tabManagers.Any(t => !t.IsIncognito);

    /// <summary>
    /// 新建一个窗口。调用方负责 <c>Show()</c>（主窗口要先设好主题再显示，避免闪一下白）。
    /// </summary>
    public MainWindow CreateWindow(BrowserWindowOptions options)
    {
        var tabs = new TabManager(
            _environment,
            _history,
            _loggerFactory.CreateLogger<TabManager>(),
            options.Kind);

        _tabManagers.Add(tabs);
        tabs.ApplyWebTheme(_webTheme);
        TabManagerCreated?.Invoke(this, tabs);

        var viewModel = new MainViewModel(tabs, _bookmarks, _settings, _history, Downloads);
        var window = new MainWindow(
            viewModel,
            tabs,
            _session,
            _settings,
            _theme,
            this,
            options,
            _startupCommands,
            _defaultBrowser);

        window.Closed += (_, _) => Forget(window);

        _windows.Add(window);
        EnsureAutoSave();
        _logger.LogInformation(
            "已创建{Kind}窗口（共 {Count} 个窗口）",
            options.Kind == BrowserSessionKind.Incognito ? "无痕" : "普通",
            _windows.Count);

        return window;
    }

    /// <summary>
    /// 下载管理器。它在构造时就订阅窗口事件，反过来又需要按窗口弹「另存为」对话框——
    /// 两边互相依赖，所以由组合根（<c>App</c>）在创建窗口前把实例挂进来，而不是走构造函数注入。
    /// </summary>
    public DownloadManager? Downloads { get; set; }

    /// <summary>某个标签页集合所属的窗口；找不到返回 <c>null</c>（系统对话框要挂在它上面）。</summary>
    public MainWindow? WindowOf(TabManager tabs) =>
        _windows.FirstOrDefault(w => ReferenceEquals(w.Tabs, tabs));

    /// <summary>
    /// 把标签从当前窗口移到新窗口（标签条拖出）。引擎与页面状态都保留：
    /// 只是控件从旧内容区摘下、挂到新窗口。无痕窗口里的标签只会拖进无痕窗口，
    /// 否则等同于把隐私会话暴露到普通窗口。
    /// </summary>
    /// <param name="tab">要迁移的标签。</param>
    /// <param name="location">新窗口左上角（屏幕 DIP）；<c>null</c> 表示居中。</param>
    public MainWindow? MoveTabToNewWindow(BrowserTab tab, System.Windows.Point? location = null)
    {
        var source = _tabManagers.FirstOrDefault(m => m.Tabs.Contains(tab));
        if (source is null)
        {
            return null;
        }

        var window = CreateWindow(new BrowserWindowOptions(
            Kind: source.IsIncognito ? BrowserSessionKind.Incognito : BrowserSessionKind.Normal,
            Urls: [],                 // 空列表 = 不建初始标签，等迁移过来的标签
            IsPrimary: false));

        // 窗口的 Host 在 Loaded 里才注入，所以迁移动作要等它加载完。
        window.Loaded += (_, _) =>
        {
            if (location is { } point)
            {
                // 让新窗口大致出现在鼠标位置（标题栏在指针下方一点）。
                window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                window.Left = Math.Max(0, point.X - 120);
                window.Top = Math.Max(0, point.Y - 20);
            }

            source.ReleaseTab(tab);
            window.Tabs.Adopt(tab);

            // 源窗口若因此关闭，退出流程可能已经把会话写成「空」——立刻按新窗口的状态补存一次。
            SaveSession(window.Tabs);
            window.BringToFront();
        };

        window.Show();
        return window;
    }

    /// <summary>
    /// 处理二次启动转发来的命令行（见 <see cref="SingleInstance"/>）：带地址就在最近的普通窗口里开标签，
    /// 什么都不带就新开一个窗口（Chrome 的行为）；扩展安装请求照常执行。
    /// </summary>
    public async Task HandleForwardedArgsAsync(IReadOnlyList<string> args)
    {
        var urls = StartupCommandRunner.ExtractUrls(args);
        var target = _windows.FirstOrDefault(w => w.IsActive && !w.Tabs.IsIncognito)
                     ?? _windows.LastOrDefault(w => !w.Tabs.IsIncognito);

        if (target is null || (urls.Count == 0 && !StartupCommandRunner.HasCommands(args)))
        {
            target = CreateWindow(new BrowserWindowOptions(Urls: urls.Count > 0 ? urls : null));
            target.Show();
        }
        else
        {
            foreach (var url in urls)
            {
                try
                {
                    await target.Tabs.CreateAsync(url, activate: true).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "打开转发的地址失败：{Url}", url);
                }
            }
        }

        target.BringToFront();
        await _startupCommands.RunAsync(target.Tabs.Profile, args).ConfigureAwait(true);
    }

    /// <summary>把应用主题同步给所有窗口的网页内容（含之后新建的窗口）。</summary>
    public void ApplyWebTheme(AppliedTheme theme)
    {
        _webTheme = theme;

        foreach (var tabs in _tabManagers)
        {
            tabs.ApplyWebTheme(theme);
        }
    }

    /// <summary>
    /// 把「普通窗口」的标签页存成会话。由窗口在关闭时调用——无痕窗口不参与（这正是无痕的含义）。
    /// </summary>
    /// <param name="closing">正在关闭的窗口的标签集合。</param>
    public void SaveSession(TabManager closing)
    {
        if (closing.IsIncognito)
        {
            return;
        }

        // 关的是「最后一个有标签的普通窗口」且它已经空了（Ctrl+W 关掉了最后一个标签）：
        // 用户明确把标签都关了，下次就不该再恢复出来——清掉会话，而不是留着上一次的旧文件。
        SaveSessionCore(clearWhenEmpty: true);
    }

    /// <summary>定期保存的间隔：崩溃 / 断电 / 被结束进程时，最多丢这么久的标签变化。</summary>
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(20);

    private DispatcherTimer? _autoSave;
    private string? _lastSavedSessionKey;

    /// <summary>
    /// 开启会话定期保存。此前会话只在关窗时写——进程一崩溃，这次开的标签就全丢了。
    /// 内容没变就不落盘（键 = 地址列表 + 激活下标）。
    /// </summary>
    private void EnsureAutoSave()
    {
        if (_autoSave is not null)
        {
            return;
        }

        _autoSave = new DispatcherTimer { Interval = AutoSaveInterval };
        _autoSave.Tick += (_, _) =>
        {
            if (_settings.Current.RememberSessionOnExit)
            {
                SaveSessionCore(clearWhenEmpty: false);
            }
        };
        _autoSave.Start();
    }

    private void SaveSessionCore(bool clearWhenEmpty)
    {
        var tabs = _tabManagers.FirstOrDefault(t => !t.IsIncognito && t.Tabs.Count > 0);
        var snapshot = tabs is null ? [] : SessionStore.SnapshotUrls(tabs.Tabs);
        if (snapshot.Count == 0)
        {
            if (clearWhenEmpty)
            {
                _session.Clear();
                _lastSavedSessionKey = null;
            }

            return;
        }

        var activeIndex = tabs!.ActiveTab is null ? 0 : tabs.Tabs.IndexOf(tabs.ActiveTab);
        var key = string.Join('\n', snapshot) + "\n#" + activeIndex;
        if (key == _lastSavedSessionKey)
        {
            return;
        }

        _session.Save(snapshot, activeIndex);
        _lastSavedSessionKey = key;
    }

    /// <summary>关闭所有窗口（应用退出时兜底，避免 WebView2 进程残留）。</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
        {
            window.Close();
        }
    }

    /// <summary>
    /// 窗口关闭后把它连同它的标签页集合一起摘掉——
    /// 否则跨窗口服务（消息路由 / 下载）会一直持有已经死掉的标签。
    /// </summary>
    private void Forget(MainWindow window)
    {
        _windows.Remove(window);
        _tabManagers.Remove(window.Tabs);
        WindowClosed?.Invoke(this, window);
    }
}
