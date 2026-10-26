using System.Windows;
using System.Windows.Input;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：键盘快捷键（窗口级 PreviewKeyDown，网页聚焦时同样生效）。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
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
                    case Key.P when !isEditingText:
                        PrintActivePage();
                        return true;
                    case Key.S when !isEditingText:
                        _ = SaveActivePageAsync();
                        return true;
                    case Key.U when !isEditingText:
                        ViewSourceOfActivePage();
                        return true;
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
                    case Key.F3 when FindBar.IsOpen:
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
                    case Key.F3 when FindBar.IsOpen:
                        StepFind(forward: true);
                        return true;
                    case Key.F5:
                        _viewModel.ActiveTab?.Reload();
                        return true;
                    case Key.F12:
                        // WebView2 已开启 DevTools，这里不拦截，交给默认处理。
                        return false;
                    case Key.F11:
                        ToggleFullScreen();
                        return true;
                    case Key.Escape when FindBar.IsOpen:
                        CloseFindBar();
                        return true;
                    case Key.Escape when _userFullScreen:
                        SetUserFullScreen(false);
                        return true;
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

    private void PrintActivePage()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (!tab.Print())
        {
            MessageBox.Show(this, "当前 WebView2 运行时不支持打印。", "ScBrowser",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task SaveActivePageAsync()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (!await tab.SavePageAsync().ConfigureAwait(true))
        {
            MessageBox.Show(
                this,
                "当前 WebView2 运行时太旧，不支持「另存为」。请更新 WebView2 运行时后重试。",
                "ScBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ViewSourceOfActivePage()
    {
        var url = _viewModel.ActiveTab?.DisplayUrl;
        if (string.IsNullOrWhiteSpace(url)
            || InternalPageRegistry.IsInternalPage(url)
            || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _ = _tabs.CreateAsync("view-source:" + url, activate: true);
    }

    private void AdjustZoom(double delta)
    {
        // 惰性标签也有 ZoomFactor（记在标签状态里），引擎创建后会补上，所以这里不要求引擎已就绪。
        var tab = _viewModel.ActiveTab;
        tab?.SetZoom(tab.ZoomFactor + delta);
    }
}
