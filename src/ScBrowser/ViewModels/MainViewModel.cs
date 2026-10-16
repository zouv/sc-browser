using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScBrowser.Browser;
using ScBrowser.Services;
using ScBrowser.Storage;

namespace ScBrowser.ViewModels;

/// <summary>
/// 外壳主视图模型：标签条、工具栏、地址栏的粘合层。
/// 真正的浏览逻辑在 <see cref="TabManager"/> / <see cref="BrowserTab"/>；这里只做
/// 状态投影与命令转译，不直接操作 WebView2。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly TabManager _tabs;
    private readonly BookmarkRepository _bookmarks;
    private readonly SettingsService _settings;
    private readonly HistoryRepository _history;

    public MainViewModel(
        TabManager tabs,
        BookmarkRepository bookmarks,
        SettingsService settings,
        HistoryRepository history)
    {
        _tabs = tabs;
        _bookmarks = bookmarks;
        _settings = settings;
        _history = history;

        _tabs.ActiveTabChanged += (_, tab) => HandleActiveTabChanged(tab);
    }

    /// <summary>地址栏下拉的候选行。</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    private int _selectedSuggestion = -1;

    /// <summary>当前键盘高亮的候选；没有则为 <c>null</c>。</summary>
    public SuggestionItem? SelectedSuggestion =>
        _selectedSuggestion >= 0 && _selectedSuggestion < Suggestions.Count
            ? Suggestions[_selectedSuggestion]
            : null;

    /// <summary>标签条的数据源（与 <see cref="TabManager.Tabs"/> 同一集合实例）。</summary>
    public ObservableCollection<BrowserTab> Tabs => _tabs.Tabs;

    /// <summary>本窗口是否无痕（驱动标签条上的「无痕」徽标与窗口标题）。</summary>
    public bool IsIncognito => _tabs.IsIncognito;

    /// <summary>当前激活标签。</summary>
    [ObservableProperty]
    private BrowserTab? _activeTab;

    /// <summary>地址栏文本。</summary>
    [ObservableProperty]
    private string _addressBarText = string.Empty;

    /// <summary>地址栏是否处于编辑态；编辑时不要用页面地址覆盖用户输入。</summary>
    [ObservableProperty]
    private bool _isAddressBarEditing;

    /// <summary>当前页面是否已加入书签（驱动工具栏星标状态）。</summary>
    [ObservableProperty]
    private bool _isActiveTabBookmarked;

    /// <summary>窗口标题（跟随激活标签；无痕窗口带前缀以示区分）。</summary>
    public string WindowTitle
    {
        get
        {
            var title = ActiveTab is null ? "ScBrowser" : $"{ActiveTab.Title} — ScBrowser";
            return IsIncognito ? "无痕：" + title : title;
        }
    }

    /// <summary>主页地址（取自设置）。</summary>
    public string HomePageUrl => _settings.Current.HomePage;

    /// <summary>按输入内容重算地址栏下拉（本地历史 + 「使用 XX 搜索」行）。</summary>
    public void UpdateSuggestions(string? query)
    {
        var text = query ?? string.Empty;

        // 「↓ 选中候选后回填地址」同样会走到这里（输入框文本变化）。
        // 那种情况下新文本正好等于当前高亮项的地址，此时必须保持列表与高亮不动——
        // 否则会形成「回填 → 重建列表 → 清空高亮」的回路，表现为「↓ 只能按一次」。
        if (SelectedSuggestion is { } current
            && string.Equals(current.Url, text, StringComparison.Ordinal))
        {
            return;
        }

        // 历史查询交给 SQL 过滤（url/title LIKE），这里多取一些供排序挑选。
        var history = _history.Query(
            string.IsNullOrWhiteSpace(text) ? null : text,
            OmniboxSuggestions.MaxItems * 4);

        Suggestions.Clear();
        foreach (var item in OmniboxSuggestions.Build(text, history, _settings.Current.SearchTemplate))
        {
            Suggestions.Add(item);
        }

        SetSelectedSuggestion(-1);
    }

    /// <summary>上下键移动高亮；同时把地址栏文本换成该项地址（Chrome 的行为）。</summary>
    public void MoveSuggestionSelection(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var next = _selectedSuggestion + delta;

        // 从「未选中」按上键 → 落到最后一项；按下键 → 落到第一项。
        if (_selectedSuggestion < 0)
        {
            next = delta > 0 ? 0 : Suggestions.Count - 1;
        }

        if (next < 0 || next >= Suggestions.Count)
        {
            SetSelectedSuggestion(-1);
            AddressBarText = ActiveTab?.DisplayUrl ?? string.Empty;
            IsAddressBarEditing = false;
            return;
        }

        SetSelectedSuggestion(next);
        AddressBarText = Suggestions[next].Url;
        // 让「地址栏跟随页面地址」的逻辑别把候选文本冲掉。
        IsAddressBarEditing = true;
    }

    /// <summary>清空下拉（导航或失焦时）。</summary>
    public void ClearSuggestions()
    {
        Suggestions.Clear();
        SetSelectedSuggestion(-1);
    }

    /// <summary>
    /// 回车：有高亮候选就打开它，否则按地址栏内容照常导航（地址/搜索词判定在
    /// <see cref="BrowserTab.NavigateFromAddressBar"/> 里）。
    /// </summary>
    public void NavigateOrCommitSuggestion()
    {
        if (SelectedSuggestion is { } item)
        {
            NavigateToSuggestion(item);
            return;
        }

        GoCommand.Execute(null);
    }

    /// <summary>打开某一项候选（回车或鼠标点击）。</summary>
    public void NavigateToSuggestion(SuggestionItem item)
    {
        IsAddressBarEditing = false;
        AddressBarText = item.Url;
        ActiveTab?.NavigateFromAddressBar(item.Url);
        ClearSuggestions();
    }

    private void SetSelectedSuggestion(int index)
    {
        if (_selectedSuggestion >= 0 && _selectedSuggestion < Suggestions.Count)
        {
            Suggestions[_selectedSuggestion].IsSelected = false;
        }

        _selectedSuggestion = index;

        if (index >= 0 && index < Suggestions.Count)
        {
            Suggestions[index].IsSelected = true;
        }
    }

    // --------------------------------------------------------------------- //
    // 标签页命令
    // --------------------------------------------------------------------- //

    [RelayCommand]
    private Task NewTabAsync() => _tabs.CreateAsync(InternalPageRegistry.NewTabUrl, activate: true);

    [RelayCommand]
    private void ActivateTab(BrowserTab? tab)
    {
        if (tab is not null)
        {
            _tabs.Activate(tab);
        }
    }

    [RelayCommand]
    private void CloseTab(BrowserTab? tab) => _tabs.Close(tab ?? ActiveTab!);

    /// <summary>重开最近关闭的标签页（Ctrl+Shift+T）。</summary>
    [RelayCommand]
    private async Task ReopenClosedTabAsync()
    {
        if (await _tabs.ReopenLastClosedAsync().ConfigureAwait(true) is null)
        {
            // 没有可还原的记录时退回「开新标签」——与 Chrome 在空栈时的观感一致：按了总有反应。
            await NewTabAsync().ConfigureAwait(true);
        }
    }

    /// <summary>在该标签右侧新增标签页（标签右键菜单）。</summary>
    [RelayCommand]
    private Task NewTabToRightAsync(BrowserTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return NewTabAsync();
        }

        return _tabs.CreateAsync(InternalPageRegistry.NewTabUrl, activate: true, insertAt: Tabs.IndexOf(tab) + 1);
    }

    /// <summary>重新加载指定标签页（标签右键菜单）。</summary>
    [RelayCommand]
    private void ReloadTab(BrowserTab? tab) => tab?.Reload();

    /// <summary>
    /// 在右侧新建当前页面的拷贝页（标签右键菜单的「复制」）。
    /// 与 Chrome 的「复制标签页」同义：地址照搬，但页面状态是新的。
    /// </summary>
    [RelayCommand]
    private Task DuplicateTabAsync(BrowserTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return Task.CompletedTask;
        }

        var url = string.IsNullOrWhiteSpace(tab.DisplayUrl) ? InternalPageRegistry.NewTabUrl : tab.DisplayUrl;
        return _tabs.CreateAsync(url, activate: true, insertAt: Tabs.IndexOf(tab) + 1);
    }

    /// <summary>关闭除该标签外的其它标签（标签右键菜单）。</summary>
    [RelayCommand]
    private void CloseOtherTabs(BrowserTab? tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var other in Tabs.Where(t => !ReferenceEquals(t, tab)).ToList())
        {
            _tabs.Close(other);
        }

        _tabs.Activate(tab);
    }

    /// <summary>关闭该标签右侧的所有标签（标签右键菜单）。</summary>
    [RelayCommand]
    private void CloseTabsToRight(BrowserTab? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        foreach (var other in Tabs.Skip(index + 1).ToList())
        {
            _tabs.Close(other);
        }
    }

    [RelayCommand]
    private Task ReopenInNewTabAsync(string? url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? InternalPageRegistry.NewTabUrl : url;
        return _tabs.CreateAsync(target, activate: true);
    }

    // --------------------------------------------------------------------- //
    // 导航命令
    // --------------------------------------------------------------------- //

    [RelayCommand]
    private void Go()
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.NavigateFromAddressBar(AddressBarText);
        IsAddressBarEditing = false;
    }

    [RelayCommand]
    private void Back() => ActiveTab?.GoBack();

    [RelayCommand]
    private void Forward() => ActiveTab?.GoForward();

    /// <summary>刷新；若正在加载则等价于「停止」（与刷新按钮的双态一致）。</summary>
    [RelayCommand]
    private void Reload()
    {
        if (ActiveTab is null)
        {
            return;
        }

        if (ActiveTab.IsLoading)
        {
            ActiveTab.Stop();
        }
        else
        {
            ActiveTab.Reload();
        }
    }

    [RelayCommand]
    private void Stop() => ActiveTab?.Stop();

    [RelayCommand]
    private void Home() => ActiveTab?.Navigate(HomePageUrl);

    // --------------------------------------------------------------------- //
    // 书签
    // --------------------------------------------------------------------- //

    /// <summary>切换当前页面的书签状态（Ctrl+D）。</summary>
    [RelayCommand]
    private void ToggleBookmark()
    {
        var tab = ActiveTab;
        if (tab is null || string.IsNullOrWhiteSpace(tab.DisplayUrl) || InternalPageRegistry.IsInternalPage(tab.DisplayUrl))
        {
            return;
        }

        IsActiveTabBookmarked = _bookmarks.Toggle(tab.DisplayUrl, tab.Title);
    }

    // --------------------------------------------------------------------- //
    // 内部页面快捷入口
    //
    // 一律**开新标签页**而不是覆盖当前页——与 Chrome 的 Ctrl+J / Ctrl+H / 菜单项一致：
    // 用户按快捷键的意图是「顺手看一眼」，把正在读的页面顶掉是很讨厌的行为。
    // --------------------------------------------------------------------- //

    [RelayCommand]
    private Task OpenHistoryAsync() => OpenInternalPageAsync("browser://history");

    [RelayCommand]
    private Task OpenBookmarksAsync() => OpenInternalPageAsync("browser://bookmarks");

    [RelayCommand]
    private Task OpenSettingsAsync() => OpenInternalPageAsync("browser://settings");

    [RelayCommand]
    private Task OpenExtensionsAsync() => OpenInternalPageAsync("browser://extensions");

    [RelayCommand]
    private Task OpenDownloadsAsync() => OpenInternalPageAsync("browser://downloads");

    /// <summary>打开一个内部页面；已开着的同一个内部页面直接激活，不再堆叠重复标签。</summary>
    private Task OpenInternalPageAsync(string url)
    {
        var existing = _tabs.Tabs.FirstOrDefault(
            t => string.Equals(t.DisplayUrl, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _tabs.Activate(existing);
            return Task.CompletedTask;
        }

        return _tabs.CreateAsync(url, activate: true);
    }

    // --------------------------------------------------------------------- //

    /// <summary>
    /// 启动时建立初始标签页。传入 <paramref name="urls"/> 则用于会话恢复。
    /// </summary>
    public async Task StartAsync(IReadOnlyList<string>? urls = null, int activeIndex = 0)
    {
        if (urls is null || urls.Count == 0)
        {
            await _tabs.CreateAsync(HomePageUrl, activate: true);
            return;
        }

        BrowserTab? toActivate = null;
        for (var i = 0; i < urls.Count; i++)
        {
            var tab = await _tabs.CreateAsync(urls[i], activate: false);
            if (i == activeIndex)
            {
                toActivate = tab;
            }
        }

        _tabs.Activate(toActivate ?? _tabs.Tabs[0]);
    }

    /// <summary>
    /// 激活标签变化：转移 PropertyChanged 订阅，并刷新地址栏、标题与书签状态。
    /// 方法名刻意不叫 OnActiveTabChanged——那是 MVVM 源生成器为
    /// [ObservableProperty] _activeTab 生成的部分方法钩子，重名会冲突。
    /// </summary>
    private void HandleActiveTabChanged(BrowserTab? tab)
    {
        if (ActiveTab is not null)
        {
            ActiveTab.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        ActiveTab = tab;

        if (ActiveTab is not null)
        {
            ActiveTab.PropertyChanged += OnActiveTabPropertyChanged;
        }

        OnPropertyChanged(nameof(WindowTitle));
        SyncAddressBarFromActiveTab();
        RefreshBookmarkState();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserTab.DisplayUrl):
                SyncAddressBarFromActiveTab();
                RefreshBookmarkState();
                break;
            case nameof(BrowserTab.Title):
                OnPropertyChanged(nameof(WindowTitle));
                break;
            default:
                break;
        }
    }

    /// <summary>把激活标签的展示地址写回地址栏；用户正在编辑时不打扰。</summary>
    private void SyncAddressBarFromActiveTab()
    {
        if (IsAddressBarEditing)
        {
            return;
        }

        AddressBarText = ActiveTab?.DisplayUrl ?? string.Empty;
    }

    private void RefreshBookmarkState()
    {
        var url = ActiveTab?.DisplayUrl;
        IsActiveTabBookmarked = !string.IsNullOrWhiteSpace(url)
                                && !InternalPageRegistry.IsInternalPage(url)
                                && _bookmarks.Exists(url);
    }
}
