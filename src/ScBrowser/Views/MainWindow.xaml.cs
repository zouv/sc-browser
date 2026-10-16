using System.Windows;
using System.Windows.Input;
using ScBrowser.Browser;
using ScBrowser.Interop;
using ScBrowser.Services;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳。只做三件事：装配内容区宿主、应用窗口外观、处理键盘快捷键。
/// 业务逻辑一律落在 <see cref="MainViewModel"/> / <see cref="TabManager"/>。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TabManager _tabs;
    private readonly SessionStore _session;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly StartupCommandRunner _startupCommands;

    public MainWindow(
        MainViewModel viewModel,
        TabManager tabs,
        SessionStore session,
        SettingsService settings,
        ThemeService theme,
        StartupCommandRunner startupCommands)
    {
        _viewModel = viewModel;
        _tabs = tabs;
        _session = session;
        _settings = settings;
        _theme = theme;
        _startupCommands = startupCommands;

        InitializeComponent();
        DataContext = viewModel;

        // 最后一个标签关闭即退出（Chrome 语义）。外壳自己决定怎么响应，TabManager 不越权。
        _tabs.LastTabClosed += (_, _) => Close();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 内容区宿主在这里注入：TabManager 只认接口，不认具体控件。
        _tabs.Host = ContentHost;

        NativeMethods.ApplyRoundedCorners(this);
        NativeMethods.ApplyDarkMode(this, _theme.IsDark);

        var snapshot = _settings.Current.RestoreSessionOnStartup ? _session.Load() : null;
        if (snapshot is not null)
        {
            await _viewModel.StartAsync(snapshot.Urls, snapshot.ActiveIndex);
        }
        else
        {
            await _viewModel.StartAsync();
        }

        // 命令行请求（如 --install-extension）要在首个标签就绪后再执行：扩展按 profile 管理。
        await _startupCommands.RunAsync();

        AddressBar.Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 先存会话再关标签——CloseAll 之后标签集合就空了。
        if (_settings.Current.RememberSessionOnExit)
        {
            _session.Save(
                SessionStore.SnapshotUrls(_tabs.Tabs),
                _tabs.ActiveTab is null ? 0 : _tabs.Tabs.IndexOf(_tabs.ActiveTab));
        }

        _tabs.CloseAll();
    }

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
                        // Ctrl+F 暂未实现页内查找，先不拦截，交回页面。
                        return false;
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
                    case Key.T:
                        // 「重开已关闭的标签」——会话级历史尚未实现，先开新标签兜底。
                        _viewModel.NewTabCommand.Execute(null);
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
    // 地址栏
    // --------------------------------------------------------------------- //

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
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

    private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 与主流浏览器一致：聚焦即全选，方便直接覆盖输入。
        AddressBar.SelectAll();
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
}
