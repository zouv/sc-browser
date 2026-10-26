using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScBrowser.Browser;
using ScBrowser.Interop;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：页内查找——浮动窗口 + 每个标签各自记住关键词。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
    // --------------------------------------------------------------------- //
    // 页内查找（Ctrl+F）
    //
    // WebView2 自带的查找条被隐藏（见 BrowserTab.StartFindAsync），这里用外壳自绘。
    // 查找条是一个独立顶层窗口（见 FindBarWindow 的说明），搜索会话挂在对应的
    // BrowserTab 上；每个标签各自记住自己的关键词，切换标签时互不串。
    // --------------------------------------------------------------------- //

    /// <summary>输入防抖：打字停下才发起搜索，避免每个字符都排一次异步查询。</summary>
    private readonly DispatcherTimer _findDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>当前持有查找会话的标签；关闭查找条时解除。</summary>
    private BrowserTab? _findTab;

    /// <summary>浮动查找窗口（懒创建，Owner 为本窗口）。</summary>
    private FindBarWindow? _findWindow;

    /// <summary>每个标签上次的关键词（tab.Id → 词），切换标签时恢复。</summary>
    private readonly Dictionary<int, string> _findTerms = [];

    /// <summary>查找条是否「应显示」——与「窗口因切后台而临时 Hide」区分开。</summary>
    private bool _findBarVisible;

    /// <summary>最近一次成功发起搜索的关键词；用于区分「重新搜索」与「下一个」。</summary>
    private string? _startedFindTerm;

    /// <summary>作废旧的在途搜索回调（防抖 + 异步完成顺序）。</summary>
    private int _findRequestVersion;

    /// <summary>查找条当前是否可见（快捷键 F3 / Esc 判断用）。</summary>
    public bool IsFindBarOpen => _findBarVisible && _findWindow is { IsVisible: true };

    /// <summary>打开查找条；已打开时只把焦点与选区交给输入框。</summary>
    private bool ShowFindBar()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        // 换标签或重新打开：停旧会话、接新会话（同一标签重复打开则不动）。
        if (!ReferenceEquals(_findTab, tab))
        {
            if (_findTab is not null)
            {
                _findTerms[_findTab.Id] = _findWindow?.Text ?? string.Empty;
            }

            DetachFindTab(stop: true);
            AttachFindTab(tab);
        }

        EnsureFindWindow();
        PositionFindWindow();

        _findBarVisible = true;
        _findWindow!.Text = _findTerms.GetValueOrDefault(tab.Id, string.Empty);
        _findWindow.Show();
        _findWindow.Activate();
        _findWindow.FocusAndSelect();

        // 复用上一次的关键词时，打开即重新搜索（Chrome 在同一标签内也是这个观感）。
        if (_findWindow.Text is { Length: > 0 })
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        }

        return true;
    }

    /// <summary>收起查找条并结束查找会话（清除页面高亮），关键词留给该标签。</summary>
    private void CloseFindBar()
    {
        if (_findWindow is null && _findTab is null)
        {
            return;
        }

        _findDebounce.Stop();
        _findRequestVersion++;

        if (_findTab is not null)
        {
            _findTerms[_findTab.Id] = _findWindow?.Text ?? string.Empty;
        }

        DetachFindTab(stop: true);
        _findWindow?.Hide();
        _findBarVisible = false;
        _viewModel.ActiveTab?.FocusContent();
    }

    /// <summary>切换标签：不关查找条，只把关键词换成新标签的那份。</summary>
    private void OnActiveTabChanged()
    {
        if (!_findBarVisible || _findWindow is null)
        {
            return;
        }

        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (_findTab is not null && !ReferenceEquals(_findTab, tab))
        {
            _findTerms[_findTab.Id] = _findWindow.Text;
        }

        DetachFindTab(stop: true);
        AttachFindTab(tab);

        _findWindow.Text = _findTerms.GetValueOrDefault(tab.Id, string.Empty);
        if (_findWindow.Text is { Length: > 0 })
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        }
        else
        {
            _findWindow.Status = string.Empty;
        }
    }

    /// <summary>
    /// 查找条失活时决定是否收起。<b>必须挂在查找窗口的 Deactivated 上，而不是主窗口的</b>：
    /// 打开查找条本身就会让主窗口失活，挂在主窗口上会把刚打开的条立刻藏掉。
    /// <para>
    /// 失活后看前台窗口是谁：回到主窗口 = 用户在网页里继续操作，保留查找条（Chrome 行为）；
    /// 否则说明切到了别的程序，收起。
    /// </para>
    /// </summary>
    private void OnFindWindowDeactivated()
    {
        if (!_findBarVisible || _findWindow is not { IsVisible: true })
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindowHandle();
        var mainHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (foreground == mainHandle)
        {
            return;
        }

        _findWindow.Hide();
    }

    /// <summary>主窗口最小化时一并收起查找条；还原时按「应显示」状态恢复。</summary>
    private void SyncFindBarWithWindowState()
    {
        if (!_findBarVisible || _findWindow is null)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            _findWindow.Hide();
        }
        else if (!_findWindow.IsVisible)
        {
            PositionFindWindow();
            _findWindow.Show();
        }
    }

    private void ShowFindBarWhenActive()
    {
        if (_findBarVisible && _findWindow is { IsVisible: false })
        {
            PositionFindWindow();
            _findWindow.Show();
        }
    }

    /// <summary>把查找窗口靠右停在本窗口内容区右上角。</summary>
    private void PositionFindWindow()
    {
        if (_findWindow is null)
        {
            return;
        }

        var target = ContentFrame.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(ContentFrame);
        _findWindow.Left = (target.X + ContentFrame.ActualWidth - _findWindow.Width - 10) / dpi.DpiScaleX;
        _findWindow.Top = (target.Y + 10) / dpi.DpiScaleY;
    }

    private void EnsureFindWindow()
    {
        if (_findWindow is not null)
        {
            return;
        }

        _findWindow = new FindBarWindow { Owner = this };
        _findWindow.SearchTextChanged += (_, _) => OnFindSearchTextChanged();
        _findWindow.StepForward += (_, _) => StepFind(forward: true);
        _findWindow.StepBackward += (_, _) => StepFind(forward: false);
        _findWindow.CloseRequested += (_, _) => CloseFindBar();
        _findWindow.Deactivated += (_, _) => OnFindWindowDeactivated();
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
            || !IsFindBarOpen
            || string.IsNullOrEmpty(_findWindow?.Text))
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

    private void OnFindSearchTextChanged()
    {
        if (_findWindow is null)
        {
            return;
        }

        _findDebounce.Stop();

        if (string.IsNullOrEmpty(_findWindow.Text))
        {
            _findRequestVersion++;
            _findTab?.StopFind();
            _startedFindTerm = null;
            _findWindow.Status = string.Empty;
            return;
        }

        _findDebounce.Start();
    }

    private void Menu_Find(object sender, RoutedEventArgs e) => ShowFindBar();

    private async Task RunFindAsync()
    {
        var tab = _findTab;
        var term = _findWindow?.Text;
        if (tab is null || string.IsNullOrEmpty(term))
        {
            return;
        }

        // 防抖期间用户可能又敲了字符、或切走了标签：用版本号作废旧回调。
        var version = ++_findRequestVersion;
        if (_findWindow is not null)
        {
            _findWindow.Status = string.Empty;
        }

        var started = await tab.StartFindAsync(term);
        if (version != _findRequestVersion || !ReferenceEquals(tab, _findTab))
        {
            return;
        }

        if (!started)
        {
            _startedFindTerm = null;
            if (_findWindow is not null)
            {
                _findWindow.Status = tab.IsInitialized ? "查找不可用" : string.Empty;
            }

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
        var term = _findWindow?.Text;
        if (tab is null || string.IsNullOrEmpty(term))
        {
            return;
        }

        _findDebounce.Stop();

        if (!string.Equals(term, _startedFindTerm, StringComparison.Ordinal))
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
        if (_findWindow is null || tab is null || string.IsNullOrEmpty(_findWindow.Text))
        {
            if (_findWindow is not null)
            {
                _findWindow.Status = string.Empty;
            }

            return;
        }

        var count = tab.FindMatchCount;
        _findWindow.Status = count <= 0
            ? "无结果"
            : $"{tab.FindActiveMatchIndex}/{count}";
    }
}
