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

    public MainViewModel(TabManager tabs, BookmarkRepository bookmarks, SettingsService settings)
    {
        _tabs = tabs;
        _bookmarks = bookmarks;
        _settings = settings;

        _tabs.ActiveTabChanged += (_, tab) => HandleActiveTabChanged(tab);
    }

    /// <summary>标签条的数据源（与 <see cref="TabManager.Tabs"/> 同一集合实例）。</summary>
    public ObservableCollection<BrowserTab> Tabs => _tabs.Tabs;

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

    /// <summary>窗口标题（跟随激活标签）。</summary>
    public string WindowTitle => ActiveTab is null
        ? "sc-browser"
        : $"{ActiveTab.Title} — sc-browser";

    /// <summary>主页地址（取自设置）。</summary>
    public string HomePageUrl => _settings.Current.HomePage;

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
    // --------------------------------------------------------------------- //

    [RelayCommand]
    private void OpenHistory() => ActiveTab?.Navigate("browser://history");

    [RelayCommand]
    private void OpenBookmarks() => ActiveTab?.Navigate("browser://bookmarks");

    [RelayCommand]
    private void OpenSettings() => ActiveTab?.Navigate("browser://settings");

    [RelayCommand]
    private void OpenExtensions() => ActiveTab?.Navigate("browser://extensions");

    [RelayCommand]
    private void OpenDownloads() => ActiveTab?.Navigate("browser://downloads");

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
