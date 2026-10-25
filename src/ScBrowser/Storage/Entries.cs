namespace ScBrowser.Storage;

/// <summary>一条历史记录。标题与地址都可能是任意文本，展示层负责转义。</summary>
public sealed record HistoryEntry(
    long Id,
    string Url,
    string Title,
    int VisitCount,
    DateTimeOffset LastVisit);

/// <summary>一条书签。</summary>
public sealed record BookmarkEntry(
    long Id,
    string Url,
    string Title,
    DateTimeOffset Created);

/// <summary>下载状态。数值与数据库存储一一对应，勿随意改序（新值只能追加）。</summary>
public enum DownloadState
{
    InProgress = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,

    /// <summary>用户主动暂停。进程退出后无法续传，启动时会统一改为失败。</summary>
    Paused = 4,
}

/// <summary>一次下载的记录。</summary>
public sealed record DownloadEntry(
    string Id,
    string Url,
    string FilePath,
    string FileName,
    long TotalBytes,
    long ReceivedBytes,
    DownloadState State,
    DateTimeOffset Started);
