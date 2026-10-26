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
/// <para>
/// <b>安全边界（务必知悉）</b>：WebView2 给<b>所有</b>顶层页面都注入了 <c>chrome.webview.postMessage</c>，
/// 普通网页也能往这里发消息。因此每条消息先按发送方文档地址（<c>args.Source</c>，网页无法伪造）鉴权：
/// 不是内部页面的直接丢弃，内部页面也只能调 <see cref="InternalPageRegistry"/> 给它登记的那几类消息。
/// 宿主推送同理，只发给内部页面。
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

    /// <summary>已记过「拒绝非内部页面消息」日志的标签（消息都在 UI 线程上处理，无需加锁）。</summary>
    private readonly HashSet<int> _rejectedTabs = [];
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

    /// <summary>向所有停留在内部页面上的标签推送一个事件（普通网页收不到）。</summary>
    public void Broadcast(string type, object? payload = null)
    {
        var json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);

        foreach (var tab in _windows.TabManagers.SelectMany(m => m.Tabs))
        {
            try
            {
                if (tab.Core is { } core && InternalPageRegistry.IsInternalPage(core.Source))
                {
                    core.PostWebMessageAsJson(json);
                }
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

            var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;
            if (string.IsNullOrEmpty(type))
            {
                return;
            }

            var source = args.Source;
            if (!InternalPageRegistry.IsInternalPage(source))
            {
                // 普通网页发来的：不回执（不让网页探测到消息桥存在）。每个标签只记一次日志，
                // 免得恶意页面循环发消息把日志写爆。
                if (_rejectedTabs.Add(tab.Id))
                {
                    _logger.LogWarning("已拒绝来自非内部页面的宿主消息 {Type}，来源 {Source}（此标签后续同类拒绝不再记录）", type, source);
                }

                return;
            }

            if (root.TryGetProperty("requestId", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                requestId = idProp.GetString();
            }

            if (!InternalPageRegistry.IsMessageAllowed(source, type))
            {
                throw new UnauthorizedAccessException($"页面 {InternalPageRegistry.NormalizeDisplayUrl(source)} 无权调用 {type}");
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
                return await _history.QueryAsync(
                    GetString(payload, "search"),
                    GetInt(payload, "limit", 500),
                    GetInt(payload, "offset", 0)).ConfigureAwait(true);

            case "history.visits":
                return await _history.QueryVisitsAsync(
                    GetString(payload, "search"),
                    GetDate(payload, "from"),
                    GetDate(payload, "to"),
                    GetInt(payload, "limit", 1000)).ConfigureAwait(true);

            case "history.remove":
                await _history.RemoveAsync(GetLong(payload, "id", -1)).ConfigureAwait(true);
                return null;

            case "history.removeByUrl":
                await _history.RemoveByUrlAsync(GetString(payload, "url") ?? string.Empty).ConfigureAwait(true);
                return null;

            case "history.clear":
                await _history.ClearAsync().ConfigureAwait(true);
                return null;

            case "history.topSites":
                return await _history.QueryTopSitesAsync(GetInt(payload, "limit", 8)).ConfigureAwait(true);

            // ---------------- 书签 ----------------
            case "bookmarks.query":
                return await _bookmarks.GetAllAsync().ConfigureAwait(true);

            case "bookmarks.add":
                return await _bookmarks.AddAsync(
                    GetString(payload, "url") ?? string.Empty,
                    GetString(payload, "title") ?? string.Empty,
                    GetLong(payload, "parentId", 0)).ConfigureAwait(true);

            case "bookmarks.addFolder":
                return await _bookmarks.AddFolderAsync(
                    GetString(payload, "title") ?? "新建文件夹",
                    GetLong(payload, "parentId", 0)).ConfigureAwait(true);

            case "bookmarks.rename":
                return await _bookmarks.RenameAsync(
                    GetLong(payload, "id", -1),
                    GetString(payload, "title") ?? string.Empty).ConfigureAwait(true);

            case "bookmarks.move":
                return await _bookmarks.MoveAsync(
                    GetLong(payload, "id", -1),
                    GetLong(payload, "parentId", 0)).ConfigureAwait(true);

            case "bookmarks.remove":
                return await _bookmarks.RemoveAsync(GetString(payload, "url") ?? string.Empty).ConfigureAwait(true);

            case "bookmarks.removeById":
                return await _bookmarks.RemoveByIdAsync(GetLong(payload, "id", -1)).ConfigureAwait(true);

            case "bookmarks.toggle":
                return await _bookmarks.ToggleAsync(
                    GetString(payload, "url") ?? string.Empty,
                    GetString(payload, "title") ?? string.Empty).ConfigureAwait(true);

            case "bookmarks.exists":
                return await _bookmarks.ExistsAsync(GetString(payload, "url") ?? string.Empty).ConfigureAwait(true);

            // ---------------- 下载 ----------------
            case "downloads.query":
                return await _downloads.ListAsync().ConfigureAwait(true);

            case "downloads.clear":
                _downloads.Clear();
                return null;

            case "downloads.remove":
                _downloads.Remove(GetString(payload, "id") ?? string.Empty);
                return null;

            case "downloads.pause":
                _downloads.Pause(GetString(payload, "id") ?? string.Empty);
                return null;

            case "downloads.resume":
                _downloads.Resume(GetString(payload, "id") ?? string.Empty);
                return null;

            case "downloads.cancel":
                _downloads.Cancel(GetString(payload, "id") ?? string.Empty);
                return null;

            // 只收下载记录的 id，路径由宿主自己查库——页面传来的路径一律不信，
            // 否则「打开文件」就成了「执行任意本地程序」。
            case "downloads.openFile":
                OpenDownloadedFile(_downloads.Find(GetString(payload, "id") ?? string.Empty));
                return null;

            case "downloads.revealInFolder":
                RevealInExplorer(_downloads.Find(GetString(payload, "id") ?? string.Empty)?.FilePath);
                return null;

            // ---------------- 扩展 ----------------
            case "extensions.list":
                return await _extensions.ListAsync(ProfileOf(tab)).ConfigureAwait(true);

            case "extensions.installFromFolder":
                {
                    var folder = PickFolder(OwnerOf(manager), "选择扩展目录（须包含 manifest.json）");
                    if (folder is null)
                    {
                        return null;
                    }

                    var info = await _extensions
                        .InstallFromFolderAsync(ProfileOf(tab), folder, m => ConfirmExtensionInstall(manager, m))
                        .ConfigureAwait(true);
                    if (info is not null)
                    {
                        Broadcast("extensions.changed");
                    }

                    return info;
                }

            case "extensions.installFromCrx":
                {
                    var crx = PickFile(OwnerOf(manager), "选择扩展包", "Chrome 扩展包 (*.crx)|*.crx|所有文件 (*.*)|*.*");
                    if (crx is null)
                    {
                        return null;
                    }

                    var info = await _extensions
                        .InstallFromCrxAsync(ProfileOf(tab), crx, m => ConfirmExtensionInstall(manager, m))
                        .ConfigureAwait(true);
                    if (info is not null)
                    {
                        Broadcast("extensions.changed");
                    }

                    return info;
                }

            case "extensions.installFromStore":
                {
                    // 支持直接粘扩展 ID，或商店详情页链接（Chrome / Edge）。
                    var input = GetString(payload, "input") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        return null;
                    }

                    var info = await _extensions
                        .InstallFromStoreAsync(ProfileOf(tab), input, m => ConfirmExtensionInstall(manager, m))
                        .ConfigureAwait(true);
                    if (info is not null)
                    {
                        Broadcast("extensions.changed");
                    }

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
                OpenFolder(AppPaths.ManagedExtensionsFolder);
                return null;

            // ---------------- 设置 ----------------
            case "settings.get":
                return _settings.Current;

            case "settings.update":
                {
                    // 只合并「用户偏好」字段：窗口几何等宿主自管的字段不归设置页管，
                    // 整体替换会把它们清空（设置页根本不发这些字段）。
                    var updated = payload.Deserialize<AppSettings>(JsonOptions);
                    if (updated is not null)
                    {
                        _settings.Update(s => s.CopyPreferencesFrom(updated));
                        _theme.Apply(_settings.Current.Theme);
                        Broadcast("settings.changed", _settings.Current);
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

        if (GetBool(payload, "passwords", false))
        {
            kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;
        }

        if (GetBool(payload, "autofill", false))
        {
            kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
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

    /// <summary>
    /// 发起操作的标签所在的窗口——系统对话框要挂在它上面。
    /// 以前用 <c>Application.MainWindow</c>，多窗口时对话框会跑到第一个窗口后面。
    /// </summary>
    private Window? OwnerOf(TabManager manager) =>
        _windows.Windows.FirstOrDefault(w => ReferenceEquals(w.Tabs, manager))
        ?? Application.Current?.MainWindow;

    private static string? PickFolder(Window? owner, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    private static string? PickFile(Window? owner, string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
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

    /// <summary>
    /// 安装前把扩展申请的权限摊给用户看。扩展代码等于本机程序，装之前至少要知道它能做什么。
    /// </summary>
    private bool ConfirmExtensionInstall(TabManager manager, ExtensionMetadata metadata)
    {
        var permissions = metadata.Permissions.Count > 0
            ? string.Join(Environment.NewLine, metadata.Permissions.Select(p => "  - " + p))
            : "  （未声明权限）";

        var manifestNote = metadata.ManifestVersion < 3
            ? Environment.NewLine + Environment.NewLine + "注意：这是 Manifest V2 扩展，新版 Chromium 已逐步停止支持。"
            : string.Empty;

        var owner = _windows.WindowOf(manager) ?? Application.Current?.MainWindow;

        var nl = Environment.NewLine;
        var message = $"扩展：{metadata.Name}{nl}"
                      + $"版本：{metadata.Version}（Manifest V{metadata.ManifestVersion}）{nl}{nl}"
                      + "它将申请以下权限：" + nl + permissions + manifestNote
                      + nl + nl + "扩展代码会以这些权限运行，确认安装？";

        var answer = MessageBox.Show(
            owner!,
            message,
            "安装扩展",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>在资源管理器中打开一个目录（<see cref="RevealInExplorer"/> 只认文件，目录要走这里）。</summary>
    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开目录失败：{Path}", path);
        }
    }

    /// <summary>用系统默认程序打开一条<b>已完成</b>的下载。</summary>
    private void OpenDownloadedFile(DownloadEntry? entry)
    {
        if (entry is not { State: DownloadState.Completed } || !File.Exists(entry.FilePath))
        {
            return;
        }

        var path = entry.FilePath;
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

    /// <summary>ISO 8601 时间串 → <see cref="DateTimeOffset"/>；缺失或格式不对返回 <c>null</c>。</summary>
    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static long GetLong(JsonElement element, string name, long fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : fallback;
}
