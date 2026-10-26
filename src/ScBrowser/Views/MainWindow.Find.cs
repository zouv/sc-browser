using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：页内查找条：输入即搜、匹配计数、上/下跳转。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
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

        var wasOpen = FindBar.IsOpen;
        FindBar.IsOpen = true;

        // 首次打开由 Opened 事件聚焦（那时 Popup 的 HWND 才就绪）；重复打开直接聚焦。
        if (wasOpen)
        {
            FocusFindBox();
        }

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
        if (!FindBar.IsOpen && _findTab is null)
        {
            return;
        }

        _findDebounce.Stop();
        _findRequestVersion++;
        FindBar.IsOpen = false;
        FindStatus.Text = string.Empty;
        DetachFindTab(stop: true);

        // focusContent=false 专指「切换标签触发的收起」：清掉关键词，
        // 避免上一个页面的搜索词带进新页面——查找条只跟随当前页面。
        if (!focusContent)
        {
            FindBox.Text = string.Empty;
        }

        if (focusContent)
        {
            _viewModel.ActiveTab?.FocusContent();
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
            || !FindBar.IsOpen
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
        if (!FindBar.IsOpen)
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

    /// <summary>Enter = 下一个，Shift+Enter = 上一个，F3 跳转，Esc = 关闭（Chrome 语义）。</summary>
    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                StepFind(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.F3:
                // 焦点在查找输入框里时 F3 不会回到主窗口，这里补一份。
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

    /// <summary>聚焦查找输入框。由 <c>FindBar.Opened</c> 或「重复打开」调用——
    /// 那时 Popup 的 HWND 已就绪，直接 Focus 即可；过早调用会静默失败导致打不进字。</summary>
    private void FocusFindBox()
    {
        // Popup 的输入焦点要走 Keyboard.Focus，让键盘输入路由到 Popup 自己的 HWND；
        // 仅用 FindBox.Focus() 有时只挪逻辑焦点，打字仍落到主窗口。
        Keyboard.Focus(FindBox);
        FindBox.SelectAll();
    }

    /// <summary>查找条靠右停靠在内容区右上角（留 10px 边距）。</summary>
    private static CustomPopupPlacement[] PlaceFindBar(Size popupSize, Size targetSize, Point offset) =>
    [
        new CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width - 10, 10),
            PopupPrimaryAxis.Horizontal),
    ];

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
}
