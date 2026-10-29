using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScBrowser.Browser;
using ScBrowser.Interop;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：页内查找——浮动窗口 + **每个标签各自的状态**（是否打开、关键词）。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
    // --------------------------------------------------------------------- //
    // 页内查找（Ctrl+F）
    //
    // 行为对齐 Chrome：
    //   · Ctrl+F 打开浮动面板并抢焦点；点击页面则面板保留、焦点回到网页。
    //   · 查找条是**按标签**的：切到没有查找会话的标签时面板隐藏，
    //     切回原来的标签再显示，并恢复该标签上次的关键词与高亮。
    //   · 浏览器切到后台时面板随之隐藏（不会浮在别的程序上面）。
    //
    // 面板是独立顶层窗口而不是 Popup：Popup 的 HWND 拿不到键盘焦点
    // （光标会闪但打字落到网页），见 FindBarWindow 的注释。
    // --------------------------------------------------------------------- //

    /// <summary>一个标签的查找状态。</summary>
    private sealed class FindTabState
    {
        /// <summary>该标签的查找面板是否处于「打开」状态。</summary>
        public bool IsOpen { get; set; }

        /// <summary>该标签上次的关键词（切走时保存，切回时恢复）。</summary>
        public string Term { get; set; } = string.Empty;
    }

    /// <summary>输入防抖：打字停下才发起搜索，避免每个字符都排一次异步查询。</summary>
    private readonly DispatcherTimer _findDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>当前持有查找会话的标签；面板关闭或切走时解除。</summary>
    private BrowserTab? _findTab;

    /// <summary>浮动查找窗口（懒创建，Owner 为本窗口）。</summary>
    private FindBarWindow? _findWindow;

    /// <summary>tab.Id → 查找状态。</summary>
    private readonly Dictionary<int, FindTabState> _findStates = [];

    /// <summary>最近一次成功发起搜索的关键词；用于区分「重新搜索」与「下一个」。</summary>
    private string? _startedFindTerm;

    /// <summary>作废旧的在途搜索回调（防抖 + 异步完成顺序）。</summary>
    private int _findRequestVersion;

    /// <summary>程序性改写输入框文本时，抑制 TextChanged 触发的搜索。</summary>
    private bool _suppressFindSearch;

    /// <summary>查找面板当前是否可见（F3 / Esc 判断用）。</summary>
    public bool IsFindBarOpen => _findWindow is { IsVisible: true };

    /// <summary>
    /// 查找面板可见性（依赖属性）：工具栏的放大镜按钮据此显隐。
    /// 做成 DP 是为了让 XAML 绑定能随开关实时更新。
    /// </summary>
    public static readonly DependencyProperty IsFindBarVisibleProperty =
        DependencyProperty.Register(
            nameof(IsFindBarVisible),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

    public bool IsFindBarVisible
    {
        get => (bool)GetValue(IsFindBarVisibleProperty);
        set => SetValue(IsFindBarVisibleProperty, value);
    }

    /// <summary>
    /// 打开（或聚焦）当前标签的查找面板。
    /// <para>
    /// 对齐 Chrome：会话已打开时只聚焦并全选旧词；**首次打开**时把页面选中的文本
    /// 预填进搜索框（上限 <see cref="SelectionFillLimit"/> 字符）并立即搜索。
    /// 选区是异步取的，面板先显示、取回后再填；期间用户已输入或已切走则不填。
    /// </para>
    /// </summary>
    private async Task<bool> ShowFindBarAsync()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        var state = StateOf(tab);
        var isFreshOpen = !state.IsOpen;
        state.IsOpen = true;

        if (!ReferenceEquals(_findTab, tab))
        {
            DetachFindTab(stop: true);
            AttachFindTab(tab);
        }

        // 恢复该标签上次的关键词；恢复时不要触发「防抖搜索」，随后统一跑一次。
        SetFindText(state.Term);

        ShowFindWindow(focusBox: true);
        IsFindBarVisible = true;

        if (state.Term.Length > 0)
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        }
        else if (isFreshOpen)
        {
            _findWindow!.Status = string.Empty;
            var term = TruncateSelection(await tab.GetSelectedTextAsync().ConfigureAwait(true));

            // 取选区期间用户可能已经输入或切走了标签：都不该再覆盖。
            if (term.Length > 0
                && ReferenceEquals(_viewModel.ActiveTab, tab)
                && StateOf(tab).IsOpen
                && _findWindow is { } window
                && window.Text.Length == 0)
            {
                state.Term = term;
                SetFindText(term);
                _findDebounce.Stop();
                _ = RunFindAsync();
            }
        }
        else
        {
            _findWindow!.Status = string.Empty;
        }

        return true;
    }

    /// <summary>选区超过 150 字符就不预填（Chrome 同款上限；再长通常是误选了大段内容）。</summary>
    private const int SelectionFillLimit = 150;

    private static string TruncateSelection(string selection) =>
        selection.Length > SelectionFillLimit ? selection[..SelectionFillLimit] : selection;

    /// <summary>关闭查找面板（Esc / 关闭按钮）：结束该标签的查找会话并清空关键词。</summary>
    private void CloseFindBar()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is not null)
        {
            var state = StateOf(tab);
            state.IsOpen = false;
            state.Term = string.Empty;
        }

        _findDebounce.Stop();
        _findRequestVersion++;
        DetachFindTab(stop: true);
        SetFindText(string.Empty);
        _findWindow?.Hide();
        IsFindBarVisible = false;
        _viewModel.ActiveTab?.FocusContent();
    }

    /// <summary>
    /// 切换标签：保存旧标签的关键词；新标签有打开的查找会话就显示并恢复它的词，
    /// 否则隐藏面板（Chrome 的按标签行为）。切换标签不抢焦点，焦点留在网页里。
    /// </summary>
    private void OnActiveTabChanged()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null)
        {
            return;
        }

        // 先保存旧标签的词（_findTab 是切换前那个）。
        if (_findTab is not null && !ReferenceEquals(_findTab, tab))
        {
            var old = StateOf(_findTab);
            old.Term = _findWindow?.Text ?? old.Term;
        }

        DetachFindTab(stop: true);

        var state = StateOf(tab);
        if (!state.IsOpen)
        {
            _findWindow?.Hide();
            IsFindBarVisible = false;
            SetFindText(string.Empty);
            return;
        }

        AttachFindTab(tab);
        SetFindText(state.Term);
        ShowFindWindow(focusBox: false);
        IsFindBarVisible = true;

        if (state.Term.Length > 0)
        {
            _findDebounce.Stop();
            _ = RunFindAsync();
        }
        else
        {
            _findWindow!.Status = string.Empty;
        }
    }

    /// <summary>主窗口/查找窗口失活时：只要前台不是本进程，就隐藏面板（状态保留）。</summary>
    private void HideFindBarForBackground()
    {
        if (!IsFindBarOpen)
        {
            return;
        }

        if (NativeMethods.GetForegroundProcessId() == (uint)Environment.ProcessId)
        {
            return;   // 焦点还在本应用（主窗口或查找窗口），属于正常使用。
        }

        _findWindow!.Hide();
        IsFindBarVisible = false;
    }

    /// <summary>回到前台：当前标签的查找会话仍是打开的，就把面板带回来。</summary>
    private void RestoreFindBarForForeground()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null || !StateOf(tab).IsOpen || IsFindBarOpen)
        {
            return;
        }

        if (!ReferenceEquals(_findTab, tab))
        {
            AttachFindTab(tab);
        }

        ShowFindWindow(focusBox: false);
        IsFindBarVisible = true;
    }

    /// <summary>主窗口最小化时收起查找面板；还原时按当前标签状态恢复。</summary>
    private void SyncFindBarWithWindowState()
    {
        if (WindowState == WindowState.Minimized)
        {
            _findWindow?.Hide();
            IsFindBarVisible = false;
        }
        else
        {
            RestoreFindBarForForeground();
        }
    }

    private FindTabState StateOf(BrowserTab tab)
    {
        if (!_findStates.TryGetValue(tab.Id, out var state))
        {
            state = new FindTabState();
            _findStates[tab.Id] = state;
        }

        return state;
    }

    /// <summary>程序性设置输入框文本（不触发搜索）。</summary>
    private void SetFindText(string text)
    {
        if (_findWindow is null || _findWindow.Text == text)
        {
            return;
        }

        _suppressFindSearch = true;
        try
        {
            _findWindow.Text = text;
        }
        finally
        {
            _suppressFindSearch = false;
        }
    }

    private void ShowFindWindow(bool focusBox)
    {
        EnsureFindWindow();
        PositionFindWindow();

        // 同一进程内把浮动窗口切到前台，键盘输入才会真正落到输入框。
        _findWindow!.FocusBoxOnActivate = focusBox;
        NativeMethods.BringToForeground(new WindowInteropHelper(_findWindow).Handle);
        _findWindow.Show();
        _findWindow.Activate();

        if (focusBox)
        {
            // Activate() 的激活是异步完成的：立刻 Focus 会被随后的激活流程覆盖，
            // 表现为「窗口在前台但输入框没有键盘焦点」。等输入优先级再聚焦一次。
            _findWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => _findWindow.FocusAndSelect()));
        }
    }

    private void PositionFindWindow()
    {
        if (_findWindow is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(ContentFrame);
        var target = ContentFrame.PointToScreen(new Point(0, 0));
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
        _findWindow.Deactivated += (_, _) => HideFindBarForBackground();
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
        if (_suppressFindSearch || _findWindow is null)
        {
            return;
        }

        // 记录到当前标签，切换标签时才能恢复。
        if (_findTab is not null)
        {
            StateOf(_findTab).Term = _findWindow.Text;
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

    private void Menu_Find(object sender, RoutedEventArgs e) => _ = ShowFindBarAsync();

    /// <summary>工具栏放大镜：查找面板已打开时聚焦它，否则打开。</summary>
    private void FindButton_Click(object sender, RoutedEventArgs e) => _ = ShowFindBarAsync();

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
