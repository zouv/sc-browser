using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScBrowser.Browser;
using ScBrowser.Interop;
using ScBrowser.Services;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳。只做三件事：装配内容区宿主、应用窗口外观、处理键盘快捷键。
/// 业务逻辑一律落在 <see cref="MainViewModel"/> / <see cref="TabManager"/>。
/// <para>
/// 每个窗口一份（含无痕窗口）：标签页集合与视图模型由
/// <see cref="BrowserWindowManager"/> 按窗口组装后注入。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TabManager _tabs;
    private readonly SessionStore _session;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly BrowserWindowManager _windows;
    private readonly BrowserWindowOptions _options;
    private readonly StartupCommandRunner? _startupCommands;
    private readonly DefaultBrowserService? _defaultBrowser;

    public MainWindow(
        MainViewModel viewModel,
        TabManager tabs,
        SessionStore session,
        SettingsService settings,
        ThemeService theme,
        BrowserWindowManager windows,
        BrowserWindowOptions options,
        StartupCommandRunner? startupCommands = null,
        DefaultBrowserService? defaultBrowser = null)
    {
        _viewModel = viewModel;
        _tabs = tabs;
        _session = session;
        _settings = settings;
        _theme = theme;
        _windows = windows;
        _options = options;
        _defaultBrowser = defaultBrowser;
        // 命令行参数只在第一个窗口执行一次（例如 --install-extension），第二个窗口不该重跑。
        _startupCommands = options.IsPrimary ? startupCommands : null;

        InitializeComponent();
        DataContext = viewModel;
        RestoreWindowPlacement();

        // 最后一个标签关闭即退出（Chrome 语义）。外壳自己决定怎么响应，TabManager 不越权。
        _tabs.LastTabClosed += (_, _) => Close();

        // 网页视频全屏 / 证书错误：外壳负责收起 chrome 与询问用户。
        _tabs.TabFullScreenChanged += (_, e) => OnTabFullScreenChanged(e);
        _tabs.CertificateErrorDetected += (_, e) => AskAboutCertificateError(e);

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        LocationChanged += OnWindowGeometryChanged;
        SizeChanged += OnWindowGeometryChanged;
        Deactivated += (_, _) => HideSuggestionPopup();
        Activated += (_, _) => HideSuggestionPopup();

        // 标签拖拽的候选状态从「按下标签」开始，但鼠标一旦移出按钮，按钮就收不到 MouseMove 了——
        // 于是「按下后往下拖出标签条」永远起不了拖拽。窗口级再挂一份（handledEventsToo），
        // 保证离开按钮后的移动、以及窗口外的松开都能收到。
        AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(OnWindowMouseMove), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnWindowMouseUp), handledEventsToo: true);

        // 输入即搜：打字期间只重启防抖计时器，停下来才真正发起一次查找。
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        };

        // 查找条是窗口级的：切换标签时收起，避免把上一个页面的匹配状态带过去。
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab))
            {
                CloseFindBar(focusContent: false);
            }
        };
    }

    /// <summary>本窗口的标签页集合（窗口关闭时由 <see cref="BrowserWindowManager"/> 摘除）。</summary>
    public TabManager Tabs => _tabs;

    /// <summary>
    /// 外壳 chrome（标签条 / 工具栏 / 状态栏 / 错误条）是否可见。全屏时为 <c>false</c>。
    /// 做成依赖属性是为了让错误条与状态栏的可见性绑定能参与多值判断——
    /// 直接给它们的 Visibility 赋本地值再 ClearValue 会把 XAML 绑定一起清掉。
    /// </summary>
    public static readonly DependencyProperty ChromeVisibleProperty =
        DependencyProperty.Register(
            nameof(ChromeVisible),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(true));

    public bool ChromeVisible
    {
        get => (bool)GetValue(ChromeVisibleProperty);
        set => SetValue(ChromeVisibleProperty, value);
    }

    /// <summary>把窗口切到前台（二次启动转发地址时用）；最小化的先还原。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>句柄就绪后挂上「最大化钉到工作区」的钩子，避免自绘外壳被裁边。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ClampMaximizedToWorkArea(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 内容区宿主在这里注入：TabManager 只认接口，不认具体控件。
        _tabs.Host = ContentHost;


        NativeMethods.ApplyRoundedCorners(this, rounded: true);
        NativeMethods.ApplyDarkMode(this, _theme.IsDark);
        ApplyWindowedFrame(WindowState != WindowState.Maximized);

        // 主窗口按会话恢复；其它窗口用创建时给的地址（无痕窗口永远不读写会话）。
        var urls = _options.Urls;
        var activeIndex = _options.ActiveIndex;
        SessionSnapshot? snapshot = null;
        if (_options.IsPrimary && _settings.Current.RestoreSessionOnStartup)
        {
            snapshot = _session.Load();
        }

        if (snapshot is not null)
        {
            urls = snapshot.Urls;
            activeIndex = snapshot.ActiveIndex;
        }

        // 命令行带了地址（从别的程序点开链接）：接在恢复的会话后面打开，并切到最后一个。
        var commandLineUrls = _options.IsPrimary ? StartupCommandRunner.ExtractUrls(StartupCommandRunner.OwnArgs) : [];
        if (commandLineUrls.Count > 0)
        {
            urls = [.. urls ?? [], .. commandLineUrls];
            activeIndex = urls.Count - 1;
        }

        // 空列表 = 拖出成新窗口：不建初始标签，等被拖来的标签挂进来。
        if (_options.Urls is not { Count: 0 })
        {
            await _viewModel.StartAsync(urls, activeIndex, snapshot?.Groups);
        }

        // 首个标签就绪后才有 profile，才能列扩展。
        _ = _viewModel.RefreshExtensionsAsync();

        // 命令行请求（如 --install-extension）要在首个标签就绪后再执行：扩展按 profile 管理。
        if (_startupCommands is not null)
        {
            await _startupCommands.RunAsync(_tabs.Profile);
        }

        // 最大化要在句柄就绪（WM_GETMINMAXINFO 钩子已挂上）之后再恢复，否则会按系统默认
        // 尺寸最大化、溢出屏幕被裁边。
        if (_options.IsPrimary && _settings.Current.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        // 启动时是程序性聚焦：先抑制下拉，等用户真的点进来/开始打字再展开。
        _suppressSuggestions = true;
        AddressBar.Focus();
    }

    /// <summary>
    /// 恢复上次关闭时的窗口尺寸与位置。只对主窗口生效——其它窗口（尤其无痕）
    /// 不该跟着主窗口的几何走，居中显示即可。
    /// 虚桌面之外的旧坐标会被丢弃并退回居中——显示器换过之后，
    /// 陈旧坐标的表现是「应用启动了但窗口看不见」，很难排查。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (!_options.IsPrimary)
        {
            return;
        }

        var settings = _settings.Current;
        var restored = WindowPlacement.Resolve(
            settings.WindowLeft,
            settings.WindowTop,
            settings.WindowWidth,
            settings.WindowHeight,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        Width = restored.Width;
        Height = restored.Height;

        if (restored is { Left: { } left, Top: { } top })
        {
            // 有保存的位置就用它，不要让 WPF 再居中一次。
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>把窗口几何写回设置（含最大化标记）。</summary>
    private void SaveWindowPlacement()
    {
        // 最大化时 Left/Top/Width/Height 是「最大化后」的值，必须存 RestoreBounds，
        // 否则下次启动会拿一个占满屏幕的尺寸去开普通窗口。
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        var maximized = WindowState == WindowState.Maximized;

        _settings.Update(s =>
        {
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowMaximized = maximized;
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 窗口几何只记主窗口的；会话则在任意窗口关闭时刷新一次，
        // 这样「关掉最后一个窗口」那一刻的状态一定被存下来（应用随之退出）。
        if (_options.IsPrimary)
        {
            SaveWindowPlacement();
        }

        if (_settings.Current.RememberSessionOnExit)
        {
            // 先存会话再关标签——CloseAll 之后标签集合就空了。
            _windows.SaveSession(_tabs);
        }

        _tabs.CloseAll();
    }

    // --------------------------------------------------------------------- //
    // 窗口按钮
    //
    // 这三个按钮挂在外壳里（标签条右端），取代被去掉的系统标题栏。
    // 拖拽移动、双击最大化、右键系统菜单、边缘缩放松手交给 WindowChrome 的
    // 标题栏命中测试（见 MainWindow.xaml 的 CaptionHeight），这里只负责点击。
    // --------------------------------------------------------------------- //

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>窗口状态变化：圆角与「边框带」都要跟着切。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var windowed = WindowState != WindowState.Maximized;

        // 最大化态改回直角：贴边时留着圆角会在屏幕角落露出背景缝。
        NativeMethods.ApplyRoundedCorners(this, windowed);
        ApplyWindowedFrame(windowed);
    }

    /// <summary>
    /// 窗口模式留一圈「边框带」（2px 底色 + 1px 描边），最大化时收起。
    /// <para>
    /// 这圈不是装饰：它必须由 WPF 自绘，缩放命中带才生效（WebView2 是独立子 HWND，
    /// 会吃掉落在自己身上的鼠标事件，见 ARCHITECTURE §7）。代价是页面视口少了几像素——
    /// 最大化时窗口本来就不能拖拽缩放，白让出视口只会让「宽度卡得紧」的页面冒出滚动条，
    /// 所以最大化时归零，让视口与普通浏览器对齐。
    /// </para>
    /// </summary>
    private void ApplyWindowedFrame(bool windowed)
    {
        RootLayout.Margin = windowed ? new Thickness(2, 0, 2, 2) : default;
        ContentFrame.BorderThickness = windowed ? new Thickness(1) : default;
    }

    // --------------------------------------------------------------------- //
    // 窗口
    // --------------------------------------------------------------------- //

    /// <summary>新开一个普通窗口（Ctrl+N）。</summary>
    private void OpenNewWindow() =>
        _windows.CreateWindow(new BrowserWindowOptions(Kind: BrowserSessionKind.Normal)).Show();

    /// <summary>新开一个无痕窗口（Ctrl+Shift+N）。</summary>
    private void OpenNewIncognitoWindow() =>
        _windows.CreateWindow(new BrowserWindowOptions(Kind: BrowserSessionKind.Incognito)).Show();

    // --------------------------------------------------------------------- //
    // 全屏（F11 + 网页视频全屏）与站点信息 / 证书
    // --------------------------------------------------------------------- //

    private bool _userFullScreen;
    private bool _pageFullScreen;
    private bool _fullScreenApplied;
    private Rect _preFullScreenBounds;
    private WindowState _preFullScreenState = WindowState.Normal;

    private void ToggleFullScreen() => SetUserFullScreen(!_userFullScreen);

    private void SetUserFullScreen(bool fullScreen)
    {
        _userFullScreen = fullScreen;
        UpdateFullScreenState();
    }

    /// <summary>只有当前标签的网页全屏才影响本窗口。</summary>
    private void OnTabFullScreenChanged(TabFullScreenEventArgs e)
    {
        if (!ReferenceEquals(e.Tab, _viewModel.ActiveTab))
        {
            return;
        }

        _pageFullScreen = e.IsFullScreen;
        UpdateFullScreenState();
    }

    private void UpdateFullScreenState()
    {
        var wanted = _userFullScreen || _pageFullScreen;
        if (wanted == _fullScreenApplied)
        {
            return;
        }

        _fullScreenApplied = wanted;
        if (wanted)
        {
            EnterFullScreen();
        }
        else
        {
            ExitFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        _preFullScreenState = WindowState;
        _preFullScreenBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        SetChromeVisible(false);

        // 先回普通态再交给 Win32 铺满整块显示器（含任务栏区域）。
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        NativeMethods.FillMonitor(this);
    }

    private void ExitFullScreen()
    {
        SetChromeVisible(true);

        // Win32 直接挪过窗口，WPF 的 Left/Top 已与窗口实际位置不一致，退出时按保存值重新落位。
        WindowState = WindowState.Normal;
        if (_preFullScreenBounds.Width > 0 && _preFullScreenBounds.Height > 0)
        {
            Left = _preFullScreenBounds.Left;
            Top = _preFullScreenBounds.Top;
            Width = _preFullScreenBounds.Width;
            Height = _preFullScreenBounds.Height;
        }

        if (_preFullScreenState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        ApplyWindowedFrame(WindowState != WindowState.Maximized);
    }

    /// <summary>全屏时收起标签条 / 工具栏 / 查找条 / 状态栏，并把标题栏命中区归零。</summary>
    private void SetChromeVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TabStrip.Visibility = visibility;
        Toolbar.Visibility = visibility;

        // 错误条与状态栏的可见性由绑定决定，全屏只是一个额外条件（见 ChromeVisible）。
        ChromeVisible = visible;

        if (!visible)
        {
            FindBar.Visibility = Visibility.Collapsed;
        }

        // 全屏时标签条不可见，若标题栏命中区还在，点网页顶部会变成拖窗口。
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = visible ? 38 : 0;
            chrome.ResizeBorderThickness = visible ? new Thickness(6) : default;
        }

        if (visible)
        {
            ApplyWindowedFrame(WindowState != WindowState.Maximized);
        }
        else
        {
            RootLayout.Margin = default;
            ContentFrame.BorderThickness = default;
        }
    }

    /// <summary>点工具栏上的扩展按钮：有弹窗就开弹窗，否则打开选项页。</summary>
    private void ExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MainViewModel.ExtensionButton item)
        {
            return;
        }

        var tab = _viewModel.ActiveTab;
        if (tab?.Core is null)
        {
            return;
        }

        if (item.PopupUrl is { } popupUrl)
        {
            OpenExtensionPopup(item, popupUrl, (FrameworkElement)sender);
            return;
        }

        if (item.OptionsUrl is { } optionsUrl)
        {
            _ = _tabs.CreateAsync(optionsUrl, activate: true);
        }
    }

    private void OpenExtensionPopup(MainViewModel.ExtensionButton item, string url, FrameworkElement anchor)
    {
        var core = _viewModel.ActiveTab?.Core;
        if (core is null)
        {
            return;
        }

        // 弹窗必须与标签共用同一个环境/profile，扩展的 chrome.* 能力才在弹窗里可用。
        var options = _tabs.IsIncognito
            ? BrowserSession.CreateControllerOptions(core.Environment, BrowserSessionKind.Incognito)
            : null;

        var screen = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(anchor);
        var anchorDip = new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);

        var popup = new ExtensionPopupWindow { Owner = this, Title = item.Name };
        popup.Show();
        _ = popup.OpenAsync(core.Environment, options, url, anchorDip, _tabs);
    }

    /// <summary>点锁形图标：显示站点信息（来源 / 安全状态 / 权限）。</summary>
    private void SecurityButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return;
        }

        var popup = new SiteInfoPopup(tab) { Owner = this };
        var anchor = SecurityButton.PointToScreen(new Point(0, SecurityButton.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(SecurityButton);
        popup.Left = anchor.X / dpi.DpiScaleX;
        popup.Top = anchor.Y / dpi.DpiScaleY;
        Serilog.Log.Debug("打开站点信息浮层：{Url}", tab.DisplayUrl);
        popup.ShowDialog();
        Serilog.Log.Debug("站点信息浮层已关闭");
    }

    /// <summary>证书错误：把关键信息给用户，由用户决定是否继续。</summary>
    private void AskAboutCertificateError(CertificateErrorEventArgs e)
    {
        var host = Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) ? uri.Host : e.RequestUri;
        var answer = MessageBox.Show(
            this,
            $"无法确认与「{host}」的连接是安全的。\n\n"
            + $"证书错误：{e.ErrorStatus}\n"
            + $"证书颁发给：{e.Certificate?.Subject ?? "（未知）"}\n\n"
            + "继续访问可能导致信息被窃听或篡改。是否继续？",
            "此连接不是私密连接",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        e.Allow = answer == MessageBoxResult.Yes;
    }

    private void BeginDrag()
    {
        _isDragging = true;
        _dragTransform = new TranslateTransform();
        _dragElement!.RenderTransform = _dragTransform;
        Panel.SetZIndex(_dragElement, DraggingZIndex);
        _dragElement.Opacity = 0.9;
        _dragElement.CaptureMouse();
    }

    private void EndDrag()
    {
        _dragElement?.ReleaseMouseCapture();
        if (_dragElement is not null)
        {
            _dragElement.RenderTransform = null;
            _dragElement.Opacity = 1;
            Panel.SetZIndex(_dragElement, 0);
        }

        _dragTab = null;
        _dragElement = null;
        _dragTransform = null;
        _isDragging = false;
    }

    /// <summary>让被拖标签贴着鼠标横移，并在越过他人中线时实时换位。</summary>
    private void UpdateDragPosition(Point mouseInStrip)
    {
        if (_dragElement is null || _dragTransform is null || _dragTab is null)
        {
            return;
        }

        var from = _tabs.Tabs.IndexOf(_dragTab);
        var slots = EnumerateTabSlots();
        if (from < 0 || slots.Count <= from)
        {
            return;
        }

        // 一步算出「该换到哪个槽位」与「渲染偏移」（见 TabReorder.Step 的注释：
        // 偏移按落点槽位的几何算，换位瞬间视觉位置才不会跳）。
        var step = TabReorder.Step(slots, from, _dragGrabOffset, mouseInStrip.X);
        if (step.TargetIndex != from)
        {
            _tabs.Move(_dragTab, step.TargetIndex);
        }

        _dragTransform.X = step.RenderOffset;
    }

    /// <summary>取标签条里每个标签的布局槽位（含被拖那个；<c>GetOffset</c> 不含渲染变换，正是所需）。</summary>
    private List<TabReorder.Slot> EnumerateTabSlots()
    {
        var result = new List<TabReorder.Slot>();
        if (_tabStripPanel is null)
        {
            return result;
        }

        foreach (var child in _tabStripPanel.Children.OfType<FrameworkElement>())
        {
            var left = VisualTreeHelper.GetOffset(child).X;
            result.Add(new TabReorder.Slot(left, left + child.ActualWidth));
        }

        return result;
    }

    /// <summary>
    /// 解析标签条面板。用可视树遍历而不是 <c>ItemsPanel.LoadContent()</c>——
    /// 后者返回的是模板**副本**，不是屏幕上那个真实容器，拿它算坐标永远是错的。
    /// </summary>
    private Panel? ResolveTabStripPanel()
    {
        _tabStripPanel ??= FindDescendant<Panel>(TabItems);
        return _tabStripPanel;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static BrowserTab? TabOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as BrowserTab;

    private void TabMenu_NewTabRight(object sender, RoutedEventArgs e) =>
        _viewModel.NewTabToRightCommand.Execute(TabOf(sender));

    private void TabMenu_Reload(object sender, RoutedEventArgs e) =>
        _viewModel.ReloadTabCommand.Execute(TabOf(sender));

    private void TabMenu_ToggleMute(object sender, RoutedEventArgs e) =>
        _viewModel.ToggleMuteCommand.Execute(TabOf(sender));

    private void TabMenu_Duplicate(object sender, RoutedEventArgs e) =>
        _viewModel.DuplicateTabCommand.Execute(TabOf(sender));

    private void TabMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.ContextMenu is not { } menu || TabOf(sender) is not { } tab)
        {
            return;
        }

        var addTo = menu.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Tag, "add-to-group"));
        if (addTo is null)
        {
            return;
        }

        addTo.Items.Clear();
        foreach (var (name, color) in _tabs.Groups)
        {
            var item = new MenuItem { Header = name, Tag = color };
            item.Click += (_, _) => _tabs.AssignGroup([tab], name, color);
            addTo.Items.Add(item);
        }

        addTo.Visibility = addTo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把标签放进一个新组（名字按已有组数量递增，颜色循环）。</summary>
    private void TabMenu_AddToNewGroup(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab)
        {
            return;
        }

        var index = _tabs.Groups.Count;
        var name = $"标签组 {index + 1}";
        var color = TabManager.GroupColors[index % TabManager.GroupColors.Count];
        _tabs.AssignGroup([tab], name, color);
    }

    private void TabMenu_RemoveFromGroup(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab)
        {
            _tabs.RemoveFromGroup(tab);
        }
    }

    private void TabMenu_Close(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab)
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabMenu_CloseOthers(object sender, RoutedEventArgs e) =>
        _viewModel.CloseOtherTabsCommand.Execute(TabOf(sender));

    private void TabMenu_CloseRight(object sender, RoutedEventArgs e) =>
        _viewModel.CloseTabsToRightCommand.Execute(TabOf(sender));

    // --------------------------------------------------------------------- //
    // 地址栏
    // --------------------------------------------------------------------- //

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // 导航后收起下拉，并抑制「地址栏回填新地址」引发的再次弹出。
                _suppressSuggestions = true;
                HideSuggestionPopup();
                _viewModel.GoCommand.Execute(null);
                AddressBar.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                RestoreAddressBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 「编辑态」只在用户真正敲键时才置位。
    /// <para>
    /// 不能仅凭「获得焦点」就置位：启动时会自动聚焦地址栏，若那时就算编辑态，
    /// 首个页面加载完成后写回地址栏会被挡掉，结果地址栏一直是空的。
    /// </para>
    /// </summary>
    private void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextProducingKey(e.Key))
        {
            _viewModel.IsAddressBarEditing = true;
        }
    }

    // --------------------------------------------------------------------- //
    // 地址栏下拉（omnibox 建议）
    //
    // 弹出层是独立顶层窗口（WebView2 的 airspace 限制，见 AddressBarPopup.xaml 注释），
    // 它自己不激活也不接收键盘——所有键盘交互都在这里做，焦点始终留在地址栏。
    // --------------------------------------------------------------------- //

    private AddressBarPopup? _suggestionPopup;

    /// <summary>
    /// 程序性聚焦（启动、导航后回填）不该弹下拉，否则一启动就糊一脸建议列表。
    /// 只有「用户点击/输入」引起的聚焦才弹。
    /// </summary>
    private bool _suppressSuggestions;

    private async void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只对「用户在地址栏里敲出来的改动」反应：IsAddressBarEditing 由 PreviewKeyDown 置位，
        // 而「页面导航完成后回填地址」不会经过键盘事件——否则每加载一个页面就弹一次下拉。
        if (!_viewModel.IsAddressBarEditing || !IsAddressBarFocused())
        {
            return;
        }

        await _viewModel.UpdateSuggestionsAsync(AddressBar.Text);
        SyncSuggestionPopup();
    }

    /// <summary>聚焦即全选并展开最近访问（与 Chrome 一致）；启动时的程序性聚焦除外。</summary>
    private async void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBar.SelectAll();

        if (_suppressSuggestions)
        {
            return;
        }

        // 聚焦时输入框里是当前页地址，直接拿它过滤只会命中当前页，
        // 因此按「空输入」处理 → 列出最近访问。
        await _viewModel.UpdateSuggestionsAsync(null);
        SyncSuggestionPopup();
    }

    /// <summary>键盘在上面、鼠标点进来的情况在这里解除抑制并立刻展开。</summary>
    private async void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressSuggestions = false;
        await _viewModel.UpdateSuggestionsAsync(null);
        SyncSuggestionPopup();
    }

    private void SyncSuggestionPopup()
    {
        if (_viewModel.Suggestions.Count == 0 || !IsAddressBarFocused())
        {
            HideSuggestionPopup();
            return;
        }

        _suggestionPopup ??= CreateSuggestionPopup();

        // 贴着地址栏正下方，宽度与地址栏一致（Chrome 的观感）。
        var corner = AddressBar.PointToScreen(new Point(0, AddressBar.ActualHeight));
        _suggestionPopup.SetBounds(corner, AddressBar.ActualWidth);

        if (!_suggestionPopup.IsVisible)
        {
            _suggestionPopup.Show();
        }
    }

    private AddressBarPopup CreateSuggestionPopup()
    {
        var popup = new AddressBarPopup(_viewModel);
        popup.SuggestionChosen += (_, item) =>
        {
            HideSuggestionPopup();
            _viewModel.NavigateToSuggestion(item);
            AddressBar.Focus();
        };
        return popup;
    }

    private void HideSuggestionPopup()
    {
        if (_suggestionPopup is { IsVisible: true } popup)
        {
            popup.Hide();
        }
    }

    /// <summary>下拉打开时，↑/↓/Enter/Esc 交给它先用（返回 true 表示已处理）。</summary>
    private bool HandleSuggestionKeys(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSuggestionSelection(+1);
                return true;
            case Key.Up:
                MoveSuggestionSelection(-1);
                return true;
            case Key.Enter:
                HideSuggestionPopup();
                _viewModel.NavigateOrCommitSuggestion();
                AddressBar.SelectAll();
                return true;
            case Key.Escape:
                // 与 Chrome 一致：Esc 收起下拉并放弃编辑，回到当前页地址。
                HideSuggestionPopup();
                _viewModel.ClearSuggestions();
                RestoreAddressBar();
                return true;
            default:
                return false;
        }
    }

    private void MoveSuggestionSelection(int delta) => _viewModel.MoveSuggestionSelection(delta);

    /// <summary>窗口移动/缩放后下拉会跟地址栏错位；直接收起来最省事也最不易出错。</summary>
    private void OnWindowGeometryChanged(object? sender, EventArgs e) => HideSuggestionPopup();

    /// <summary>判断一个按键是否会改变输入框内容（用于区分「聚焦」与「真的在编辑」）。</summary>
    private static bool IsTextProducingKey(Key key) => key switch
    {
        Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.PageUp or Key.PageDown
            or Key.Tab or Key.Enter or Key.Escape or Key.Insert
            or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.CapsLock or Key.NumLock or Key.Scroll
            or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
            or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12 => false,
        _ => true,
    };

    private void AddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.IsAddressBarEditing = false;
        HideSuggestionPopup();
        _viewModel.ClearSuggestions();

        // 未按回车就失焦 → 放弃编辑，恢复显示当前页面地址（Chrome 行为）。
        RestoreAddressBar();
    }

    // --------------------------------------------------------------------- //
    // 主菜单
    // --------------------------------------------------------------------- //

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // --------------------------------------------------------------------- //
    // 主菜单项（见 MainWindow.xaml 里 MenuButton.ContextMenu 的分组结构）
    // --------------------------------------------------------------------- //

    private void Menu_NewTab(object sender, RoutedEventArgs e) =>
        _viewModel.NewTabCommand.Execute(null);

    private void Menu_NewWindow(object sender, RoutedEventArgs e) => OpenNewWindow();

    private void Menu_NewIncognitoWindow(object sender, RoutedEventArgs e) => OpenNewIncognitoWindow();

    private void Menu_History(object sender, RoutedEventArgs e) =>
        _viewModel.OpenHistoryCommand.Execute(null);

    private void Menu_Downloads(object sender, RoutedEventArgs e) =>
        _viewModel.OpenDownloadsCommand.Execute(null);

    private void Menu_Bookmarks(object sender, RoutedEventArgs e) =>
        _viewModel.OpenBookmarksCommand.Execute(null);

    private void Menu_Extensions(object sender, RoutedEventArgs e) =>
        _viewModel.OpenExtensionsCommand.Execute(null);

    /// <summary>清除浏览数据：入口在设置页（那里有分项勾选），这里只负责跳过去。</summary>
    private void Menu_ClearBrowsingData(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSettingsCommand.Execute(null);

    /// <summary>「关于」：显示图标、版本与用户数据目录（排障时最先要问的信息）。</summary>
    private void Menu_About(object sender, RoutedEventArgs e) => new AboutDialog { Owner = this }.ShowDialog();

    private void Menu_Settings(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSettingsCommand.Execute(null);

    private void Menu_Exit(object sender, RoutedEventArgs e) => Close();

    private void Menu_Print(object sender, RoutedEventArgs e) => PrintActivePage();

    private void Menu_SavePage(object sender, RoutedEventArgs e) => _ = SaveActivePageAsync();

    private void Menu_ViewSource(object sender, RoutedEventArgs e) => ViewSourceOfActivePage();

    private void Menu_FullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Menu_SetDefaultBrowser(object sender, RoutedEventArgs e)
    {
        var ok = _defaultBrowser?.Register() ?? false;
        _defaultBrowser?.OpenWindowsSettings();

        MessageBox.Show(
            this,
            ok
                ? "已登记为可选的默认浏览器。\n\n请在打开的「默认应用」设置里，把「Web 浏览器」选为 ScBrowser。"
                : "无法完成注册（可能被系统策略阻止）。请在「设置 → 应用 → 默认应用」中手动选择 ScBrowser。",
            "设为默认浏览器",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void MenuZoomIn(object sender, RoutedEventArgs e) => AdjustZoom(+0.1);

    private void MenuZoomOut(object sender, RoutedEventArgs e) => AdjustZoom(-0.1);

    private void MenuZoomReset(object sender, RoutedEventArgs e) =>
        _viewModel.ActiveTab?.SetZoom(1.0);
}
