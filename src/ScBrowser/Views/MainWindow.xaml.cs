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

    public MainWindow(
        MainViewModel viewModel,
        TabManager tabs,
        SessionStore session,
        SettingsService settings,
        ThemeService theme,
        BrowserWindowManager windows,
        BrowserWindowOptions options,
        StartupCommandRunner? startupCommands = null)
    {
        _viewModel = viewModel;
        _tabs = tabs;
        _session = session;
        _settings = settings;
        _theme = theme;
        _windows = windows;
        _options = options;
        // 命令行参数只在第一个窗口执行一次（例如 --install-extension），第二个窗口不该重跑。
        _startupCommands = options.IsPrimary ? startupCommands : null;

        InitializeComponent();
        DataContext = viewModel;
        RestoreWindowPlacement();

        // 最后一个标签关闭即退出（Chrome 语义）。外壳自己决定怎么响应，TabManager 不越权。
        _tabs.LastTabClosed += (_, _) => Close();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        LocationChanged += OnWindowGeometryChanged;
        SizeChanged += OnWindowGeometryChanged;
        Deactivated += (_, _) => HideSuggestionPopup();
        Activated += (_, _) => HideSuggestionPopup();

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
        if (_options.IsPrimary && _settings.Current.RestoreSessionOnStartup && _session.Load() is { } snapshot)
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

        await _viewModel.StartAsync(urls, activeIndex);

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
    // 标签条交互：中键关闭 / 左键拖拽重排 / 右键菜单
    //
    // 拖拽没有用 WPF 的 DragDrop（它接管鼠标、显示系统拖拽影像、只在落点才换位），
    // 而是自己捕获鼠标：被拖标签跟着鼠标**横向**移动，其他标签实时让位——即 Chrome 的手感。
    // 位置补偿是关键：把渲染偏移设成「视觉左边缘 − 布局左边缘」，这样挪位后标签不会跳。
    // --------------------------------------------------------------------- //

    /// <summary>超过这个位移（逻辑像素）才算拖拽，否则「点击」会被误判成拖拽。</summary>
    private const double DragThreshold = 4;

    /// <summary>拖拽中的标签抬到最上层，避免被相邻标签盖住。</summary>
    private const int DraggingZIndex = 100;

    /// <summary>标签条容器（ItemsControl 的 ItemsPanel 实例）；延迟解析，因为模板要等首帧才应用。</summary>
    private Panel? _tabStripPanel;

    private BrowserTab? _dragTab;
    private FrameworkElement? _dragElement;
    private TranslateTransform? _dragTransform;
    private Point _dragStartPoint;
    private double _dragGrabOffset;
    private bool _isDragging;

    /// <summary>鼠标中键点击标签 → 关闭（与 Chrome 一致：中键永远是「关掉它」）。</summary>
    private void TabButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab)
        {
            e.Handled = true;
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is not { } tab || sender is not FrameworkElement element || ResolveTabStripPanel() is not { } panel)
        {
            return;
        }

        // 先只记录，不捕获鼠标——否则普通点击也会走拖拽路径、标签激活不了。
        _dragTab = tab;
        _dragElement = element;
        _dragStartPoint = e.GetPosition(panel);

        // 抓取偏移 = 指针相对「标签当前槽位左边缘」的距离。必须用槽位几何：
        // 元素本身在 DataTemplate 里，对它调 VisualTreeHelper.GetOffset 得到的是
        // 「相对 ContentPresenter」的偏移（恒为 0），不是标签条坐标——用它算补偿会跳位。
        var index = _tabs.Tabs.IndexOf(tab);
        var slots = EnumerateTabSlots();
        _dragGrabOffset = _dragStartPoint.X - (index >= 0 && index < slots.Count ? slots[index].Left : 0);

        _isDragging = false;
    }

    private void TabButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || _dragElement is null || ResolveTabStripPanel() is not { } panel
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(panel);

        if (!_isDragging)
        {
            if (Math.Abs(now.X - _dragStartPoint.X) < DragThreshold)
            {
                return;
            }

            BeginDrag();
        }

        UpdateDragPosition(now);
    }

    /// <summary>
    /// 结束拖拽。拖拽过程中必须吞掉这次鼠标抬起，否则按钮会把它当成一次点击、
    /// 顺手把标签激活——那样「拖完就跳走」会很突兀。
    /// </summary>
    private void TabButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            _dragTab = null;
            _dragElement = null;
            return;
        }

        EndDrag();
        e.Handled = true;
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

    private void TabMenu_Duplicate(object sender, RoutedEventArgs e) =>
        _viewModel.DuplicateTabCommand.Execute(TabOf(sender));

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
    // 键盘快捷键
    //
    // 为什么放在窗口级 PreviewKeyDown 就够：WPF 版 WebView2 内部会把
    // CoreWebView2Controller.AcceleratorKeyPressed 转发成 WPF 的
    // PreviewKeyDown / KeyDown 重新抛出，因此即便焦点在网页里，这里也能收到。
    // 前提是 BrowserTab 里已把 AreBrowserAcceleratorKeysEnabled 关掉，
    // 否则 WebView2 会先把它当浏览器快捷键吃掉。
    // --------------------------------------------------------------------- //

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TryHandleShortcut(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private bool TryHandleShortcut(KeyEventArgs e)
    {
        // 地址栏下拉打开时，↑/↓/Enter/Esc 归它先用（Chrome 的行为）。
        if (_suggestionPopup is { IsVisible: true } && IsAddressBarFocused() && HandleSuggestionKeys(e))
        {
            return true;
        }

        // Alt 组合键在 WPF 里表现为 Key.System，真实键值在 SystemKey 上。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // 用「焦点是否在地址栏」判断是否该让路——比「编辑态」标记更准，
        // 因为标记要靠用户敲键才置位，而快捷键可能在第一下敲击时就到达。
        var isEditingText = IsAddressBarFocused();

        switch (mods)
        {
            case ModifierKeys.Control:
                switch (key)
                {
                    case Key.N:
                        OpenNewWindow();
                        return true;
                    case Key.T:
                        _viewModel.NewTabCommand.Execute(null);
                        return true;
                    case Key.W:
                        _viewModel.CloseTabCommand.Execute(_viewModel.ActiveTab);
                        return true;
                    case Key.L when !isEditingText:
                        FocusAddressBar();
                        return true;
                    case Key.R:
                        _viewModel.ActiveTab?.Reload();
                        return true;
                    case Key.D:
                        _viewModel.ToggleBookmarkCommand.Execute(null);
                        return true;
                    case Key.F:
                        return ShowFindBar();
                    case Key.H when !isEditingText:
                        _viewModel.OpenHistoryCommand.Execute(null);
                        return true;
                    case Key.J when !isEditingText:
                        _viewModel.OpenDownloadsCommand.Execute(null);
                        return true;
                    case Key.Tab:
                        _tabs.ActivateNext();
                        return true;
                    case >= Key.D1 and <= Key.D8 when !isEditingText:
                        _tabs.ActivateByIndex(key - Key.D1);
                        return true;
                    case Key.D9 when !isEditingText:
                        _tabs.ActivateLast();
                        return true;
                    case Key.OemPlus or Key.Add:
                        AdjustZoom(+0.1);
                        return true;
                    case Key.OemMinus or Key.Subtract:
                        AdjustZoom(-0.1);
                        return true;
                    case Key.D0 or Key.NumPad0:
                        _viewModel.ActiveTab?.SetZoom(1.0);
                        return true;
                    default:
                        return false;
                }

            case ModifierKeys.Control | ModifierKeys.Shift:
                switch (key)
                {
                    case Key.N:
                        OpenNewIncognitoWindow();
                        return true;
                    case Key.T:
                        // 重开最近关闭的标签页（Chrome 语义）。
                        _viewModel.ReopenClosedTabCommand.Execute(null);
                        return true;
                    case Key.R:
                        _viewModel.ActiveTab?.ReloadIgnoringCache();
                        return true;
                    case Key.Tab:
                        _tabs.ActivatePrevious();
                        return true;
                    default:
                        return false;
                }

            case ModifierKeys.Shift:
                switch (key)
                {
                    case Key.F3 when FindBar.Visibility == Visibility.Visible:
                        // Shift+F3 = 上一个匹配（查找条打开时才有意义）。
                        StepFind(forward: false);
                        return true;
                    default:
                        return false;
                }

            case ModifierKeys.Alt:
                switch (key)
                {
                    case Key.Left:
                        _viewModel.BackCommand.Execute(null);
                        return true;
                    case Key.Right:
                        _viewModel.ForwardCommand.Execute(null);
                        return true;
                    case Key.D when !isEditingText:
                        FocusAddressBar();
                        return true;
                    default:
                        return false;
                }

            case ModifierKeys.None:
                switch (key)
                {
                    case Key.F3 when FindBar.Visibility == Visibility.Visible:
                        StepFind(forward: true);
                        return true;
                    case Key.F5:
                        _viewModel.ActiveTab?.Reload();
                        return true;
                    case Key.F12:
                        // WebView2 已开启 DevTools，这里不拦截，交给默认处理。
                        return false;
                    case Key.Escape when IsAddressBarFocused():
                        RestoreAddressBar();
                        return true;
                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    private bool IsAddressBarFocused() => AddressBar.IsKeyboardFocusWithin;

    private void FocusAddressBar()
    {
        // Ctrl+L / Alt+D 是「用户想改地址」的明确信号：解除启动期抑制，让它照样弹出下拉。
        _suppressSuggestions = false;
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    /// <summary>放弃编辑，把地址栏恢复成当前页面的地址。</summary>
    private void RestoreAddressBar()
    {
        _viewModel.IsAddressBarEditing = false;
        _viewModel.AddressBarText = _viewModel.ActiveTab?.DisplayUrl ?? string.Empty;
    }

    private void AdjustZoom(double delta)
    {
        var tab = _viewModel.ActiveTab;
        if (tab?.Core is null)
        {
            return;
        }

        tab.SetZoom(tab.View.ZoomFactor + delta);
    }

    // --------------------------------------------------------------------- //
    // 页内查找（Ctrl+F）
    //
    // WebView2 自带的查找条被隐藏（见 BrowserTab.StartFindAsync），这里用外壳自绘：
    // 颜色令牌随主题走，排版也与其它工具栏控件一致。
    // 查找条是窗口级状态，切换标签时关闭；查找会话本身挂在对应的 BrowserTab 上。
    // --------------------------------------------------------------------- //

    /// <summary>输入防抖：打字停下才发起搜索，避免每个字符都排一次异步查询。</summary>
    private readonly DispatcherTimer _findDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>当前持有查找会话的标签；切换标签或关闭查找条时解除。</summary>
    private BrowserTab? _findTab;

    /// <summary>最近一次成功发起搜索的关键词；用于区分「重新搜索」与「下一个」。</summary>
    private string? _startedFindTerm;

    /// <summary>作废旧的在途搜索回调（防抖 + 异步完成顺序）。</summary>
    private int _findRequestVersion;

    /// <summary>打开查找条；已打开时只把焦点与选区交给输入框。</summary>
    private bool ShowFindBar()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        if (!ReferenceEquals(_findTab, tab))
        {
            DetachFindTab(stop: true);
            AttachFindTab(tab);
        }

        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();

        // 复用上一次的关键词时，打开即重新搜索（Chrome 在同一标签内也是这个观感）。
        if (!string.IsNullOrEmpty(FindBox.Text))
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        }

        return true;
    }

    /// <summary>收起查找条并结束查找会话（清除页面高亮）。</summary>
    private void CloseFindBar(bool focusContent = true)
    {
        if (FindBar.Visibility != Visibility.Visible && _findTab is null)
        {
            return;
        }

        _findDebounce.Stop();
        _findRequestVersion++;
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = string.Empty;
        DetachFindTab(stop: true);

        if (focusContent)
        {
            _viewModel.ActiveTab?.View.Focus();
        }
    }

    private void AttachFindTab(BrowserTab tab)
    {
        _findTab = tab;
        _startedFindTerm = null;
        tab.FindStatusChanged += OnFindStatusChanged;
        tab.NavigationFinished += OnFindTabNavigationFinished;
    }

    private void DetachFindTab(bool stop)
    {
        if (_findTab is not null)
        {
            _findTab.FindStatusChanged -= OnFindStatusChanged;
            _findTab.NavigationFinished -= OnFindTabNavigationFinished;

            if (stop)
            {
                _findTab.StopFind();
            }
        }

        _findTab = null;
        _startedFindTerm = null;
    }

    /// <summary>同一标签内导航到新文档后，旧的匹配区间已失效，按当前关键词重新搜索。</summary>
    private void OnFindTabNavigationFinished(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _findTab)
            || FindBar.Visibility != Visibility.Visible
            || string.IsNullOrEmpty(FindBox.Text))
        {
            return;
        }

        _startedFindTerm = null;
        _ = RunFindAsync();
    }

    private void OnFindStatusChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _findTab))
        {
            UpdateFindStatus();
        }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 程序性切换（查找条未显示时）不该触发搜索。
        if (FindBar.Visibility != Visibility.Visible)
        {
            return;
        }

        _findDebounce.Stop();

        if (string.IsNullOrEmpty(FindBox.Text))
        {
            _findRequestVersion++;
            _findTab?.StopFind();
            _startedFindTerm = null;
            FindStatus.Text = string.Empty;
            return;
        }

        _findDebounce.Start();
    }

    /// <summary>Enter = 下一个，Shift+Enter = 上一个，Esc = 关闭（Chrome 语义）。</summary>
    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                StepFind(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFindBar();
                e.Handled = true;
                break;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => StepFind(forward: true);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => StepFind(forward: false);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private void Menu_Find(object sender, RoutedEventArgs e) => ShowFindBar();

    private async Task RunFindAsync()
    {
        var tab = _findTab;
        var term = FindBox.Text;
        if (tab is null || string.IsNullOrEmpty(term))
        {
            return;
        }

        // 防抖期间用户可能又敲了字符、或切走了标签：用版本号作废旧回调。
        var version = ++_findRequestVersion;
        FindStatus.Text = string.Empty;

        var started = await tab.StartFindAsync(term);
        if (version != _findRequestVersion || !ReferenceEquals(tab, _findTab))
        {
            return;
        }

        if (!started)
        {
            _startedFindTerm = null;
            FindStatus.Text = tab.IsInitialized ? "查找不可用" : string.Empty;
            return;
        }

        _startedFindTerm = term;
        UpdateFindStatus();
    }

    /// <summary>
    /// 下一个 / 上一个。若当前输入尚未真正搜索过（输入后立刻按了回车、防抖还没触发），
    /// 先发起一次搜索，保证「回车总有反应」。
    /// </summary>
    private void StepFind(bool forward)
    {
        var tab = _findTab;
        if (tab is null || string.IsNullOrEmpty(FindBox.Text))
        {
            return;
        }

        _findDebounce.Stop();

        if (!string.Equals(FindBox.Text, _startedFindTerm, StringComparison.Ordinal))
        {
            _ = RunFindAsync();
            return;
        }

        if (forward)
        {
            tab.FindNext();
        }
        else
        {
            tab.FindPrevious();
        }
    }

    private void UpdateFindStatus()
    {
        var tab = _findTab;
        if (tab is null || string.IsNullOrEmpty(FindBox.Text))
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var count = tab.FindMatchCount;
        FindStatus.Text = count <= 0
            ? "无结果"
            : $"{tab.FindActiveMatchIndex}/{count}";
    }

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

    private void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只对「用户在地址栏里敲出来的改动」反应：IsAddressBarEditing 由 PreviewKeyDown 置位，
        // 而「页面导航完成后回填地址」不会经过键盘事件——否则每加载一个页面就弹一次下拉。
        if (!_viewModel.IsAddressBarEditing || !IsAddressBarFocused())
        {
            return;
        }

        _viewModel.UpdateSuggestions(AddressBar.Text);
        SyncSuggestionPopup();
    }

    /// <summary>聚焦即全选并展开最近访问（与 Chrome 一致）；启动时的程序性聚焦除外。</summary>
    private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBar.SelectAll();

        if (_suppressSuggestions)
        {
            return;
        }

        // 聚焦时输入框里是当前页地址，直接拿它过滤只会命中当前页，
        // 因此按「空输入」处理 → 列出最近访问。
        _viewModel.UpdateSuggestions(null);
        SyncSuggestionPopup();
    }

    /// <summary>键盘在上面、鼠标点进来的情况在这里解除抑制并立刻展开。</summary>
    private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressSuggestions = false;
        _viewModel.UpdateSuggestions(null);
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

    private void MenuZoomIn(object sender, RoutedEventArgs e) => AdjustZoom(+0.1);

    private void MenuZoomOut(object sender, RoutedEventArgs e) => AdjustZoom(-0.1);

    private void MenuZoomReset(object sender, RoutedEventArgs e) =>
        _viewModel.ActiveTab?.SetZoom(1.0);
}
