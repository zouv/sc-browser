using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口分册：链接悬停地址气泡。
/// <para>
/// 旧实现把地址画在窗口底部的布局行里：一出现就把 WebView2 的高度压掉一截，页面跟着重排。
/// 现在改成独立的顶层窗口，浮动在内容区左下角（Chrome 同款），不影响页面视口。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>气泡距内容区左边 / 下边的距离（DIP）。</summary>
    private const double LinkStatusMargin = 8;

    /// <summary>懒创建的气泡窗口；每个主窗口各一个（多窗口互不干扰）。</summary>
    private LinkStatusWindow? _linkStatusWindow;

    /// <summary>当前订阅了 <see cref="BrowserTab.StatusText"/> 的标签。</summary>
    private BrowserTab? _statusTab;

    /// <summary>切换激活标签：把状态监听挪到新标签，旧标签残留的悬停地址立即作废。</summary>
    private void OnActiveStatusTabChanged()
    {
        if (_statusTab is { } old)
        {
            old.PropertyChanged -= OnStatusTabPropertyChanged;
        }

        _statusTab = _viewModel.ActiveTab;
        if (_statusTab is not null)
        {
            _statusTab.PropertyChanged += OnStatusTabPropertyChanged;
        }

        UpdateLinkStatus();
    }

    private void OnStatusTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.StatusText))
        {
            UpdateLinkStatus();
        }
    }

    /// <summary>按当前标签的悬停地址显示 / 隐藏 / 移动气泡。</summary>
    private void UpdateLinkStatus()
    {
        var tab = _viewModel.ActiveTab;
        var text = tab?.StatusText;

        // 页面失去链接悬停、切标签、窗口失焦或最小化时，气泡都不该留在屏幕上。
        if (tab is null
            || !ReferenceEquals(tab, _statusTab)
            || string.IsNullOrWhiteSpace(text)
            || !IsActive
            || !IsVisible
            || WindowState == WindowState.Minimized)
        {
            HideLinkStatus();
            return;
        }

        var window = EnsureLinkStatusWindow();
        window.Text = text;
        FitLinkStatusWindow(window);
        if (!window.IsVisible)
        {
            // ShowActivated=False + WS_EX_NOACTIVATE：显示气泡不抢焦点。
            window.Show();
        }

        // SizeToContent 要布局完才有正确的 ActualHeight（定位依赖底边）。
        window.UpdateLayout();
        PositionLinkStatusWindow(window);
    }

    private LinkStatusWindow EnsureLinkStatusWindow()
    {
        if (_linkStatusWindow is not null)
        {
            return _linkStatusWindow;
        }

        _linkStatusWindow = new LinkStatusWindow { Owner = this };

        // 文本长度变化会改变 SizeToContent 的尺寸，底边定位要跟着重算。
        _linkStatusWindow.SizeChanged += (_, _) =>
        {
            if (_linkStatusWindow is { IsVisible: true } window)
            {
                PositionLinkStatusWindow(window);
            }
        };

        return _linkStatusWindow;
    }

    /// <summary>长地址不能把气泡撑出内容区：按内容区宽度限制文本宽度（上限 720，与 XAML 默认一致）。</summary>
    private void FitLinkStatusWindow(LinkStatusWindow window) =>
        window.MaxTextWidth = Math.Clamp(ContentFrame.ActualWidth - (2 * LinkStatusMargin) - 24, 160, 720);

    /// <summary>把气泡贴到内容区左下角（窗口移动 / 缩放 / 全屏切换后重算）。</summary>
    private void PositionLinkStatusWindow(LinkStatusWindow window)
    {
        var dpi = VisualTreeHelper.GetDpi(ContentFrame);
        var origin = ContentFrame.PointToScreen(new Point(0, 0));

        var contentLeft = origin.X / dpi.DpiScaleX;
        var contentBottom = origin.Y / dpi.DpiScaleY + ContentFrame.ActualHeight;

        window.Left = contentLeft + LinkStatusMargin;
        window.Top = contentBottom - window.ActualHeight - LinkStatusMargin;
    }

    /// <summary>窗口移动 / 缩放：气泡跟着内容区走（它不参与主窗口布局，不会自动移动）。</summary>
    private void RepositionLinkStatus()
    {
        if (_linkStatusWindow is { IsVisible: true } window)
        {
            FitLinkStatusWindow(window);
            window.UpdateLayout();
            PositionLinkStatusWindow(window);
        }
    }

    /// <summary>隐藏气泡。窗口保留复用，避免每次悬停都创建 / 销毁顶层窗口。</summary>
    private void HideLinkStatus() => _linkStatusWindow?.Hide();
}
