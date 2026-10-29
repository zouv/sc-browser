using System.Windows;
using Microsoft.Web.WebView2.Core;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 清除浏览数据对话框（Ctrl+Shift+Delete 直达，Chrome 同款）：
/// 时间范围下拉 + 五类数据勾选。WebView 数据经当前标签的 profile 清除
/// （按时间范围走 <c>ClearBrowsingDataAsyncInTimeRange</c>），历史走仓储。
/// </summary>
public partial class ClearBrowsingDataDialog : Window
{
    private readonly MainViewModel _viewModel;

    public ClearBrowsingDataDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>时间范围 → 起始时间；「所有时间」为 <c>null</c>。</summary>
    private DateTimeOffset? RangeStart() => RangeBox.SelectedIndex switch
    {
        0 => DateTimeOffset.UtcNow.AddHours(-1),
        1 => DateTimeOffset.UtcNow.AddDays(-1),
        2 => DateTimeOffset.UtcNow.AddDays(-7),
        3 => DateTimeOffset.UtcNow.AddDays(-28),
        _ => null,
    };

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var since = RangeStart();

        // CoreWebView2BrowsingDataKinds 没有 None 成员，用 0 值作为空集起点（[Flags] 语义）。
        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (CookiesCheck.IsChecked == true)
        {
            kinds |= CoreWebView2BrowsingDataKinds.Cookies
                     | CoreWebView2BrowsingDataKinds.LocalStorage
                     | CoreWebView2BrowsingDataKinds.IndexedDb;
        }

        if (CacheCheck.IsChecked == true)
        {
            kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        }

        if (PasswordsCheck.IsChecked == true)
        {
            kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;
        }

        if (AutofillCheck.IsChecked == true)
        {
            kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
        }

        try
        {
            if (HistoryCheck.IsChecked == true)
            {
                await _viewModel.ClearHistoryAsync(since);
            }

            // 内核 API 只有「全部清除」一种（本 SDK 没有按时间范围的清除），
            // 所以 Cookie/缓存等 WebView 数据不做范围区分——对话框里已如实提示。
            var profile = _viewModel.ActiveProfile;
            if (kinds != 0 && profile is not null)
            {
                await profile.ClearBrowsingDataAsync(kinds);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "清除失败：" + ex.Message, "清除浏览数据",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
