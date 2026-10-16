using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Infrastructure;
using ScBrowser.Services;
using ScBrowser.Storage;

namespace ScBrowser.Browser;

/// <summary>
/// 内部页面（WebUI）与宿主之间的消息路由。
/// <para>
/// <b>协议</b>（与 <c>Assets/ui/common/browser-api.js</c> 一一对应）：
/// <list type="bullet">
/// <item>页面 → 宿主：<c>{ type, requestId?, payload }</c></item>
/// <item>宿主 → 页面：<c>{ type: "response", requestId, ok, result | error }</c></item>
/// <item>宿主 → 页面：<c>{ type: "&lt;事件名&gt;", payload }</c>（单向推送）</item>
/// </list>
/// 带 <c>requestId</c> 的视为请求/响应，没有的视为单向通知。
/// </para>
/// <para>
/// 本类不依赖任何视图，只依赖服务层——因此扩展新内部页面时，
/// 「加一个 case」即可，不需要动外壳代码。
/// </para>
/// </summary>
public sealed class WebMessageRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BrowserWindowManager _windows;
    private readonly HistoryRepository _history;
    private readonly BookmarkRepository _bookmarks;
    private readonly ExtensionService _extensions;
    private readonly SettingsService _settings;
    private readonly DownloadManager _downloads;
    private readonly ThemeService _theme;
    private readonly ILogger<WebMessageRouter> _logger;

    public WebMessageRouter(
        BrowserWindowManager windows,
        HistoryRepository history,
        BookmarkRepository bookmarks,
        ExtensionService extensions,
        SettingsService settings,
        DownloadManager downloads,
        ThemeService theme,
        ILogger<WebMessageRouter> logger)
    {
        _windows = windows;
        _history = history;
        _bookmarks = bookmarks;
        _extensions = extensions;
        _settings = settings;
        _downloads = downloads;
        _theme = theme;
        _logger = logger;

        // 逐窗口订阅：闭包里记住这个窗口的 TabManager，供「在哪个窗口开新标签」这类
        // 与窗口相关的操作使用（无痕窗口的标签必须开在无痕窗口里）。
        _windows.TabManagerCreated += (_, manager) =>
            manager.WebMessageReceived += (_, e) => _ = HandleAsync(manager, e.Tab, e.Args);

        // 下载进度变化 → 推给所有内部页面（订阅了 downloads.changed 的页面自会刷新）。
        _downloads.Changed += (_, _) => Broadcast("downloads.changed");
    }

    /// <summary>向所有标签页推送一个事件。</summary>
    public void Broadcast(string type, object? payload = null)
    {
        var json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);

        foreach (var tab in _windows.TabManagers.SelectMany(m => m.Tabs))
        {
            try
            {
                tab.Core?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                // 单个标签推送失败不应影响其它标签。
                _logger.LogDebug(ex, "向标签 {TabId} 推送 {Type} 失败", tab.Id, type);
            }
        }
    }

    private async Task HandleAsync(
        TabManager manager,
        BrowserTab tab,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (string.IsNullOrEmpty(type))
            {
                return;
            }

            if (root.TryGetProperty("requestId", out var idProp))
            {
                requestId = idProp.GetString();
            }

            var payload = root.TryGetProperty("payload", out var payloadProp)
                ? payloadProp
                : default;

            var result = await DispatchAsync(manager, tab, type, payload).ConfigureAwait(true);

            if (requestId is not null)
            {
                Reply(tab, requestId, ok: true, result: result, error: null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理内部页面消息失败");
            if (requestId is not null)
            {
                Reply(tab, requestId, ok: false, result: null, error: ex.Message);
            }
        }
    }

    private async Task<object?> DispatchAsync(
        TabManager manager,
        BrowserTab tab,
        string type,
        JsonElement payload)
    {
        switch (type)
        {
            // ---------------- 导航 ----------------
            case "navigation.navigate":
                {
                    var target = GetString(payload, "input") ?? GetString(payload, "url");
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        tab.NavigateFromAddressBar(target);
                    }

                    return null;
                }

            case "navigation.openInNewTab":
                {
                    var url = GetString(payload, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await manager.CreateAsync(url, activate: GetBool(payload, "active", true)).ConfigureAwait(true);
                    }

                    return null;
                }

            // ---------------- 历史 ----------------
            case "history.query":
                return _history.Query(
                    GetString(payload, "search"),
                    GetInt(payload, "limit", 500),
                    GetInt(payload, "offset", 0));

            case "history.remove":
                _history.Remove(GetLong(payload, "id", -1));
                return null;

            case "history.removeByUrl":
                _history.RemoveByUrl(GetString(payload, "url") ?? string.Empty);
                return null;

            case "history.clear":
                _history.Clear();
                return null;

            case "history.topSites":
                return _history.QueryTopSites(GetInt(payload, "limit", 8));

            // ---------------- 书签 ----------------
            case "bookmarks.query":
                return _bookmarks.GetAll();

            case "bookmarks.add":
                _bookmarks.Add(GetString(payload, "url") ?? string.Empty, GetString(payload, "title") ?? string.Empty);
                return null;

            case "bookmarks.remove":
                return _bookmarks.Remove(GetString(payload, "url") ?? string.Empty);

            case "bookmarks.toggle":
                return _bookmarks.Toggle(
                    GetString(payload, "url") ?? string.Empty,
                    GetString(payload, "title") ?? string.Empty);

            case "bookmarks.exists":
                return _bookmarks.Exists(GetString(payload, "url") ?? string.Empty);

            // ---------------- 下载 ----------------
            case "downloads.query":
                return _downloads.List();

            case "downloads.clear":
                _downloads.Clear();
                return null;

            case "downloads.remove":
                _downloads.Remove(GetString(payload, "id") ?? string.Empty);
                return null;

            case "downloads.openFile":
                OpenDownloadedFile(GetString(payload, "path"));
                return null;

            case "downloads.revealInFolder":
                RevealInExplorer(GetString(payload, "path"));
                return null;

            // ---------------- 扩展 ----------------
            case "extensions.list":
                return await _extensions.ListAsync(ProfileOf(tab)).ConfigureAwait(true);

            case "extensions.installFromFolder":
                {
                    var folder = PickFolder("选择扩展目录（须包含 manifest.json）");
                    if (folder is null)
                    {
                        return null;
                    }

                    var info = await _extensions.InstallFromFolderAsync(ProfileOf(tab), folder).ConfigureAwait(true);
                    Broadcast("extensions.changed");
                    return info;
                }

            case "extensions.installFromCrx":
                {
                    var crx = PickFile("选择扩展包", "Chrome 扩展包 (*.crx)|*.crx|所有文件 (*.*)|*.*");
                    if (crx is null)
                    {
                        return null;
                    }

                    var info = await _extensions.InstallFromCrxAsync(ProfileOf(tab), crx).ConfigureAwait(true);
                    Broadcast("extensions.changed");
                    return info;
                }

            case "extensions.remove":
                await _extensions.RemoveAsync(ProfileOf(tab), GetString(payload, "id") ?? string.Empty).ConfigureAwait(true);
                Broadcast("extensions.changed");
                return null;

            case "extensions.setEnabled":
                await _extensions
                    .SetEnabledAsync(
                        ProfileOf(tab),
                        GetString(payload, "id") ?? string.Empty,
                        GetBool(payload, "enabled", true))
                    .ConfigureAwait(true);
                Broadcast("extensions.changed");
                return null;

            case "extensions.openManagedFolder":
                RevealInExplorer(AppPaths.ManagedExtensionsFolder);
                return null;

            // ---------------- 设置 ----------------
            case "settings.get":
                return _settings.Current;

            case "settings.update":
                {
                    var updated = payload.Deserialize<AppSettings>(JsonOptions);
                    if (updated is not null)
                    {
                        _settings.Replace(updated);
                        _theme.Apply(updated.Theme);
                        Broadcast("settings.changed", updated);
                    }

                    return null;
                }

            case "settings.setTheme":
                {
                    var theme = GetString(payload, "theme") ?? "System";
                    _settings.Update(s => s.Theme = theme);
                    _theme.Apply(theme);
                    Broadcast("settings.changed", _settings.Current);
                    return null;
                }

            // ---------------- 应用 ----------------
            case "app.info":
                return new
                {
                    version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                    userDataFolder = AppPaths.UserDataFolder,
                    managedExtensionsFolder = AppPaths.ManagedExtensionsFolder,
                    databaseFile = AppPaths.DatabaseFile,
                    logFolder = AppPaths.LogFolder,
                    theme = _theme.IsDark ? "Dark" : "Light",
                };

            case "app.clearBrowsingData":
                await ClearBrowsingDataAsync(tab, payload).ConfigureAwait(true);
                return null;

            default:
                throw new NotSupportedException($"未知的消息类型：{type}");
        }
    }

    /// <summary>
    /// 取发起消息的那个标签页所属的 profile。
    /// 扩展与浏览数据都是「按 profile」的，而普通/无痕窗口的 profile 不同——
    /// 从标签上取才保证操作落在用户当前所在的会话里。
    /// </summary>
    private static CoreWebView2Profile? ProfileOf(BrowserTab tab) => tab.Core?.Profile;

    private async Task ClearBrowsingDataAsync(BrowserTab tab, JsonElement payload)
    {
        var profile = ProfileOf(tab)
            ?? throw new InvalidOperationException("浏览器尚未初始化。");

        // CoreWebView2BrowsingDataKinds 没有 None 成员，用 0 值作为空集起点（[Flags] 语义）。
        var kinds = (CoreWebView2BrowsingDataKinds)0;

        if (GetBool(payload, "cache", false))
        {
            kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        }

        if (GetBool(payload, "cookies", false))
        {
            kinds |= CoreWebView2BrowsingDataKinds.Cookies;
        }

        if (GetBool(payload, "storage", false))
        {
            kinds |= CoreWebView2BrowsingDataKinds.LocalStorage | CoreWebView2BrowsingDataKinds.IndexedDb;
        }

        if (kinds == 0)
        {
            return;
        }

        await profile.ClearBrowsingDataAsync(kinds).ConfigureAwait(true);
        _logger.LogInformation("已清除浏览数据：{Kinds}", kinds);
    }

    private void Reply(BrowserTab tab, string requestId, bool ok, object? result, string? error)
    {
        if (tab.Core is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new { type = "response", requestId, ok, result, error }, JsonOptions);
        tab.Core.PostWebMessageAsJson(json);
    }

    // --------------------------------------------------------------------- //
    // 系统对话框 / 资源管理器
    // --------------------------------------------------------------------- //

    private static Window? Owner => Application.Current?.MainWindow;

    private static string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    private static string? PickFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "在资源管理器中定位文件失败：{Path}", path);
        }
    }

    private void OpenDownloadedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            // UseShellExecute 才能交给系统默认程序打开。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开文件失败：{Path}", path);
        }
    }

    // --------------------------------------------------------------------- //
    // JsonElement 取值助手（容忍字段缺失/类型不符）
    // --------------------------------------------------------------------- //

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int GetInt(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static long GetLong(JsonElement element, string name, long fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : fallback;
}
