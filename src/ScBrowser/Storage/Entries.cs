namespace ScBrowser.Storage;

/// <summary>一条历史记录（按 URL 聚合）。标题与地址都可能是任意文本，展示层负责转义。</summary>
public sealed record HistoryEntry(
    long Id,
    string Url,
    string Title,
    int VisitCount,
    DateTimeOffset LastVisit);

/// <summary>一条访问流水（每次访问一行，用于按日期分组）。</summary>
public sealed record VisitEntry(
    long Id,
    string Url,
    string Title,
    DateTimeOffset Visited);

/// <summary>书签节点：书签或文件夹（<see cref="IsFolder"/>）。</summary>
/// <param name="ParentId">父文件夹 id；<c>0</c> 表示根目录。</param>
/// <param name="SortOrder">同一父节点内的排序值（小的在前）。</param>
public sealed record BookmarkEntry(
    long Id,
    string Url,
    string Title,
    long ParentId,
    int SortOrder,
    bool IsFolder,
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
