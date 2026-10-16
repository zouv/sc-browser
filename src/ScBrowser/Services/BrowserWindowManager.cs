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

        var viewModel = new MainViewModel(tabs, _bookmarks, _settings, _history);
        var window = new MainWindow(
            viewModel,
            tabs,
            _session,
            _settings,
            _theme,
            this,
            options,
            _startupCommands);

        window.Closed += (_, _) => Forget(window);

        _windows.Add(window);
        _logger.LogInformation(
            "已创建{Kind}窗口（共 {Count} 个窗口）",
            options.Kind == BrowserSessionKind.Incognito ? "无痕" : "普通",
            _windows.Count);

        return window;
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
    /// 把「普通窗口」的标签页存成会话。由主窗口在退出时调用——
    /// 无痕窗口不参与（这正是无痕的含义）。
    /// </summary>
    public void SaveSession()
    {
        var tabs = _tabManagers.FirstOrDefault(t => !t.IsIncognito);
        if (tabs is null)
        {
            return;
        }

        var snapshot = SessionStore.SnapshotUrls(tabs.Tabs);
        if (snapshot.Count == 0)
        {
            return;
        }

        _session.Save(snapshot, tabs.ActiveTab is null ? 0 : tabs.Tabs.IndexOf(tabs.ActiveTab));
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
