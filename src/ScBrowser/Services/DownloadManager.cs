using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;
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
    private readonly BrowserWindowManager _windows;
    private readonly DownloadRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadManager> _logger;

    /// <summary>本次会话中活跃的下载（含最近一次落盘的进度百分比，用于节流）。</summary>
    private readonly ConcurrentDictionary<string, DownloadTrack> _active = new();

    /// <summary>用户点了暂停 / 取消的下载。WebView2 对这两种操作都报 UserCanceled，
    /// 只能自己记着意图，才能把状态区分成「已暂停」与「已取消」。</summary>
    private readonly ConcurrentDictionary<string, byte> _pausedByUser = new();
    private readonly ConcurrentDictionary<string, byte> _cancelledByUser = new();

    public DownloadManager(
        BrowserWindowManager windows,
        DownloadRepository repository,
        SettingsService settings,
        ILogger<DownloadManager> logger)
    {
        _windows = windows;
        _repository = repository;
        _settings = settings;
        _logger = logger;

        // 启动时把上次异常退出遗留的「下载中」标记为失败，避免列表里出现永远转圈的幽灵项。
        _repository.MarkInProgressAsFailed();

        // 逐窗口订阅：闭包里记住这个窗口的 TabManager，才能判断下载来自普通还是无痕窗口。
        windows.TabManagerCreated += (_, manager) =>
            manager.TabCreated += (_, tab) =>
                tab.DownloadStarting += (_, e) => OnDownloadStarting(manager, e);
    }

    /// <summary>下载列表发生变化（进度/状态），供界面实时刷新。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<DownloadEntry> List() => _repository.Query();

    /// <summary>按 id 取一条下载记录（内部页面只传 id，路径以库里的为准）。</summary>
    public DownloadEntry? Find(string id) => _repository.Find(id);

    /// <summary>当前正在进行的下载数（工具栏指示器用）。</summary>
    public int ActiveCount => _active.Count;

    /// <summary>暂停一个下载中的条目。</summary>
    public void Pause(string id)
    {
        if (!_active.TryGetValue(id, out var track))
        {
            return;
        }

        try
        {
            _pausedByUser[id] = 0;
            track.Operation.Pause();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "暂停下载失败：{Id}", id);
        }
    }

    /// <summary>继续一个被暂停的下载。</summary>
    public void Resume(string id)
    {
        if (!_active.TryGetValue(id, out var track))
        {
            return;
        }

        try
        {
            _pausedByUser.TryRemove(id, out _);
            track.Operation.Resume();
            _repository.Upsert(track.Entry with { State = DownloadState.InProgress });
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "继续下载失败：{Id}", id);
        }
    }

    /// <summary>取消一个下载。</summary>
    public void Cancel(string id)
    {
        if (!_active.TryGetValue(id, out var track))
        {
            return;
        }

        try
        {
            _cancelledByUser[id] = 0;
            track.Operation.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消下载失败：{Id}", id);
        }
    }

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

    private void OnDownloadStarting(TabManager manager, CoreWebView2DownloadStartingEventArgs e)
    {
        if (manager.IsIncognito)
        {
            // 无痕窗口的下载**不入库**：下载记录会留在本地 SQLite 里，那正是无痕要避免的。
            // 这里也不接管，交给 WebView2 自带的下载提示条，用户仍能看到进度。
            _logger.LogInformation("无痕窗口开始下载 {Url}（不记录）", e.DownloadOperation.Uri);
            return;
        }

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

            var target = MakeUniquePath(folder, fileName);

            // 「下载前询问保存位置」：取消对话框 = 取消这次下载。
            if (_settings.Current.AskWhereToSaveDownloads && !PromptForSavePath(manager, fileName, ref target))
            {
                e.Cancel = true;
                _logger.LogInformation("用户取消了下载 {Url}", operation.Uri);
                return;
            }

            e.ResultFilePath = target;
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

        _active[id] = new DownloadTrack(entry, LastPersistedPercent: 0, operation);
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
            CoreWebView2DownloadState.Interrupted =>
                _cancelledByUser.ContainsKey(id) ? DownloadState.Cancelled
                : _pausedByUser.ContainsKey(id) ? DownloadState.Paused
                : DownloadState.Failed,
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
        var finished = state is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed;

        // 「已暂停」仍然留在这个会话里（可以继续）；完成 / 取消 / 失败才移出活跃表。
        // 必须先移出再发通知：界面收到 Changed 时读的是 ActiveCount，顺序反了指示器就永远不消失。
        if (finished)
        {
            _active.TryRemove(id, out _);
            _pausedByUser.TryRemove(id, out _);
            _cancelledByUser.TryRemove(id, out _);
            _logger.LogInformation("下载{Result}：{File}", state switch
            {
                DownloadState.Completed => "完成",
                DownloadState.Cancelled => "已取消",
                _ => "中断",
            }, updated.FileName);
        }
        else
        {
            _active[id] = track with { Entry = updated, LastPersistedPercent = percent };
        }

        // 节流：只在状态变化或进度前进 1% 时落盘 / 通知界面。
        if (stateChanged || progressed || finished)
        {
            _repository.Upsert(updated);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string ResolveDownloadFolder()
    {
        var configured = _settings.Current.DownloadsFolder;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        // 未配置则用系统「下载」文件夹（用户可能把它挪到了别的盘，不能自己拼路径）。
        return KnownFolders.Downloads;
    }

    /// <summary>弹出「另存为」；取消返回 <c>false</c>。</summary>
    private bool PromptForSavePath(TabManager manager, string fileName, ref string target)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = fileName,
            InitialDirectory = Path.GetDirectoryName(target) ?? KnownFolders.Downloads,
            OverwritePrompt = true,
        };

        var owner = _windows.WindowOf(manager) ?? System.Windows.Application.Current?.MainWindow;
        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }

        target = dialog.FileName;
        return true;
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

    private sealed record DownloadTrack(
        DownloadEntry Entry,
        int LastPersistedPercent,
        CoreWebView2DownloadOperation Operation);
}
