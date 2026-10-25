using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 「站点信息」浮层：点地址栏的锁形图标打开。显示来源与安全状态、当前站点被改过的权限，
/// 并提供两个最常用的动作——清掉这个站点的数据、把权限恢复成默认。
/// </summary>
public partial class SiteInfoPopup : Window
{
    private readonly BrowserTab _tab;

    public SiteInfoPopup(BrowserTab tab)
    {
        InitializeComponent();
        _tab = tab;

        var origin = OriginOf(tab.DisplayUrl);
        OriginText.Text = origin ?? tab.DisplayUrl;
        SecurityText.Text = DescribeSecurity(tab.SecurityState);

        Loaded += async (_, _) => await LoadPermissionsAsync(origin).ConfigureAwait(true);
    }

    /// <summary>取 scheme://host[:port]；内部页面与本地文件没有可管理的来源，返回 <c>null</c>。</summary>
    private static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || InternalPageRegistry.IsInternalPage(url)
            || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static string DescribeSecurity(SecurityState state) => state switch
    {
        SecurityState.Secure => "连接是安全的（证书有效）",
        SecurityState.Neutral => "连接未加密，或信息不足",
        SecurityState.Insecure => "连接不安全（内容可能被窃听或篡改）",
        SecurityState.Danger => "此连接存在安全风险",
        _ => "此页面的安全状态未知",
    };

    private async Task LoadPermissionsAsync(string? origin)
    {
        PermissionList.Items.Clear();

        var profile = _tab.Core?.Profile;
        if (profile is null || origin is null)
        {
            PermissionHint.Text = origin is null
                ? "内部页面与本地文件没有站点权限。"
                : "浏览器尚未初始化，暂时无法读取权限。";
            return;
        }

        try
        {
            var settings = await profile.GetNonDefaultPermissionSettingsAsync().ConfigureAwait(true);
            var mine = settings.Where(s => SameOrigin(s.PermissionOrigin, origin)).ToList();

            if (mine.Count == 0)
            {
                PermissionHint.Text = "该站点使用默认权限（没有单独设置过）。";
                return;
            }

            PermissionHint.Text = "以下权限被单独设置过：";
            foreach (var setting in mine)
            {
                PermissionList.Items.Add($"{DescribeKind(setting.PermissionKind)}：{DescribeState(setting.PermissionState)}");
            }
        }
        catch (Exception)
        {
            PermissionHint.Text = "读取站点权限失败（当前运行时可能不支持）。";
        }
    }

    private static bool SameOrigin(string? a, string b) =>
        string.Equals(a?.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string DescribeKind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "麦克风",
        CoreWebView2PermissionKind.Camera => "摄像头",
        CoreWebView2PermissionKind.Geolocation => "位置",
        CoreWebView2PermissionKind.Notifications => "通知",
        CoreWebView2PermissionKind.ClipboardRead => "读取剪贴板",
        CoreWebView2PermissionKind.OtherSensors => "传感器",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "自动多次下载",
        _ => kind.ToString(),
    };

    private static string DescribeState(CoreWebView2PermissionState state) => state switch
    {
        CoreWebView2PermissionState.Allow => "允许",
        CoreWebView2PermissionState.Deny => "阻止",
        _ => "询问",
    };

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        var profile = _tab.Core?.Profile;
        var origin = OriginOf(_tab.DisplayUrl);
        if (profile is null || origin is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"清除 {origin} 的 Cookie、缓存与站点数据？\n\n该站点的登录状态会被退出。",
            "清除站点数据",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // CDP 的 Storage.clearDataForOrigin 能按来源清 Cookie / localStorage / IndexedDB；
            // Profile.ClearBrowsingDataAsync 没有「按来源」的重载。
            var payload = JsonSerializer.Serialize(new { origin, storageTypes = "all" });
            await _tab.Core!.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", payload)
                .ConfigureAwait(true);

            MessageBox.Show(this, "已清除该站点的数据。", "ScBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
            SafeClose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"清除失败：{ex.Message}", "ScBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResetPermissions_Click(object sender, RoutedEventArgs e)
    {
        var profile = _tab.Core?.Profile;
        var origin = OriginOf(_tab.DisplayUrl);
        if (profile is null || origin is null)
        {
            return;
        }

        try
        {
            var settings = await profile.GetNonDefaultPermissionSettingsAsync().ConfigureAwait(true);
            var mine = settings.Where(s => SameOrigin(s.PermissionOrigin, origin)).ToList();
            foreach (var setting in mine)
            {
                await profile.SetPermissionStateAsync(
                    setting.PermissionKind,
                    origin,
                    CoreWebView2PermissionState.Default).ConfigureAwait(true);
            }

            MessageBox.Show(
                this,
                mine.Count == 0 ? "该站点本来就没有单独设置的权限。" : $"已把 {mine.Count} 项权限恢复为默认。",
                "ScBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SafeClose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"重置权限失败：{ex.Message}", "ScBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SafeClose();

    /// <summary>关闭是幂等的：失活事件与按钮/对话框可能接连触发 Close()，重复调用会在关闭期间抛异常。</summary>
    private void SafeClose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SafeClose();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 点别处就收起（与地址栏下拉一致）。
    /// 只在<b>确实激活过</b>之后才认「失活」——从后台窗口弹出时可能一次都没激活，
    /// 那时立刻关掉会让浮层一闪而过。
    /// </summary>
    private bool _everActivated;
    private bool _closed;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _everActivated = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_everActivated)
        {
            SafeClose();
        }
    }
}
