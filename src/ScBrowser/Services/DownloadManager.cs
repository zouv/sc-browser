using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Browser;
using ScBrowser.Storage;

namespace ScBrowser.Services;

/// <summary>
/// 下载管理：接管 WebView2 的下载事件，统一落到 <c>browser://downloads</c> 页展示。
/// <para>
/// 下载记录持久化到 SQLite，重启后仍在——这是「下载内容」页存在的意义。
/// 为避免频繁写库，进度只在<b>状态变化</b>或<b>进度前进 ≥1%</b> 时才落盘。
/// </para>
/// </summary>
public sealed class DownloadManager
{
    private readonly DownloadRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadManager> _logger;

    /// <summary>本次会话中活跃的下载（含最近一次落盘的进度百分比，用于节流）。</summary>
    private readonly ConcurrentDictionary<string, DownloadTrack> _active = new();

    public DownloadManager(
        TabManager tabs,
        DownloadRepository repository,
        SettingsService settings,
        ILogger<DownloadManager> logger)
    {
        _repository = repository;
        _settings = settings;
        _logger = logger;

        // 启动时把上次异常退出遗留的「下载中」标记为失败，避免列表里出现永远转圈的幽灵项。
        _repository.MarkInProgressAsFailed();

        tabs.TabCreated += (_, tab) => tab.DownloadStarting += OnDownloadStarting;
    }

    /// <summary>下载列表发生变化（进度/状态），供界面实时刷新。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<DownloadEntry> List() => _repository.Query();

    public void Clear()
    {
        _repository.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        _repository.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // 关掉 WebView2 内建的下载提示条，统一由内部页面承载（与 Chrome 一致）。
        e.Handled = true;

        var operation = e.DownloadOperation;
        var id = Guid.NewGuid().ToString("N");

        try
        {
            var folder = ResolveDownloadFolder();
            Directory.CreateDirectory(folder);

            var fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "download";
            }

            e.ResultFilePath = MakeUniquePath(folder, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "确定下载保存路径失败，将使用 WebView2 默认路径");
        }

        var entry = new DownloadEntry(
            id,
            operation.Uri,
            e.ResultFilePath,
            Path.GetFileName(e.ResultFilePath),
            0,
            0,
            DownloadState.InProgress,
            DateTimeOffset.UtcNow);

        _active[id] = new DownloadTrack(entry, LastPersistedPercent: 0);
        _repository.Upsert(entry);

        _logger.LogInformation("开始下载 {File} ← {Url}", entry.FileName, entry.Url);

        operation.BytesReceivedChanged += (_, _) => OnProgress(id, operation);
        operation.StateChanged += (_, _) => OnProgress(id, operation);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProgress(string id, CoreWebView2DownloadOperation operation)
    {
        if (!_active.TryGetValue(id, out var track))
        {
            return;
        }

        var state = operation.State switch
        {
            CoreWebView2DownloadState.Completed => DownloadState.Completed,
            CoreWebView2DownloadState.Interrupted => DownloadState.Failed,
            _ => DownloadState.InProgress,
        };

        // WebView2 的 TotalBytesToReceive 是 ulong?（无 Content-Length 时为 null），
        // BytesReceived 是 long；统一收敛成 long 再算百分比。
        var total = (long)(operation.TotalBytesToReceive ?? 0UL);
        var received = operation.BytesReceived;

        var percent = total > 0
            ? (int)(received * 100 / total)
            : 0;

        var updated = track.Entry with
        {
            FilePath = operation.ResultFilePath ?? track.Entry.FilePath,
            FileName = Path.GetFileName(operation.ResultFilePath ?? track.Entry.FilePath),
            TotalBytes = total,
            ReceivedBytes = received,
            State = state,
        };

        var stateChanged = state != track.Entry.State;
        var progressed = percent >= track.LastPersistedPercent + 1;

        _active[id] = track with { Entry = updated, LastPersistedPercent = percent };

        // 节流：只在状态变化或进度前进 1% 时落盘。
        if (stateChanged || progressed || state != DownloadState.InProgress)
        {
            _repository.Upsert(updated);
        }

        if (state != DownloadState.InProgress)
        {
            _active.TryRemove(id, out _);
            _logger.LogInformation("下载{Result}：{File}", state == DownloadState.Completed ? "完成" : "中断", updated.FileName);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string ResolveDownloadFolder()
    {
        var configured = _settings.Current.DownloadsFolder;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        // 未配置则用系统「下载」文件夹。
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        return downloads;
    }

    /// <summary>同名文件不覆盖，追加 (1)(2) 后缀。</summary>
    private static string MakeUniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private sealed record DownloadTrack(DownloadEntry Entry, int LastPersistedPercent);
}
