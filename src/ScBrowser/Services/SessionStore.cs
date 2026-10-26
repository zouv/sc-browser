using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;

namespace ScBrowser.Services;

/// <summary>会话里一个标签所属的组（没有组时为 <c>null</c>）。</summary>
public sealed record SessionTabGroup(string Name, string Color);

/// <summary>一次会话的快照。<paramref name="Groups"/> 与 <paramref name="Urls"/> 等长，逐项对应。</summary>
public sealed record SessionSnapshot(
    IReadOnlyList<string> Urls,
    IReadOnlyList<SessionTabGroup?>? Groups,
    int ActiveIndex,
    DateTimeOffset SavedAt);

/// <summary>
/// 会话保存与恢复：退出时记下打开的标签页（含标签组），下次启动还原。
/// 落盘于 <c>%LOCALAPPDATA%\sc-browser\session.json</c>。
/// </summary>
public sealed class SessionStore
{
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ILogger<SessionStore> logger) => _logger = logger;

    /// <summary>
    /// 保存会话。<paramref name="urls"/> 与 <paramref name="groups"/> 逐项对应（长度可不同，缺的按无组处理）。
    /// </summary>
    public void Save(IReadOnlyList<string> urls, IReadOnlyList<SessionTabGroup?> groups, int activeIndex)
    {
        try
        {
            var snapshot = BuildSnapshot(urls, groups, activeIndex);
            if (snapshot is null)
            {
                Clear();
                return;
            }

            AtomicFile.WriteAllText(
                AppPaths.SessionFile,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存会话失败");
        }
    }

    /// <summary>
    /// 过滤空白标签、对齐组信息、钳制激活下标。返回 <c>null</c> 表示没有可保存的标签。
    /// （纯函数，单独拆出来便于单测。）
    /// </summary>
    internal static SessionSnapshot? BuildSnapshot(
        IReadOnlyList<string> urls,
        IReadOnlyList<SessionTabGroup?> groups,
        int activeIndex)
    {
        var meaningfulUrls = new List<string>();
        var meaningfulGroups = new List<SessionTabGroup?>();

        for (var i = 0; i < urls.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(urls[i]))
            {
                continue;
            }

            meaningfulUrls.Add(urls[i]);
            meaningfulGroups.Add(i < groups.Count ? groups[i] : null);
        }

        if (meaningfulUrls.Count == 0)
        {
            return null;
        }

        return new SessionSnapshot(
            meaningfulUrls,
            meaningfulGroups,
            Math.Clamp(activeIndex, 0, meaningfulUrls.Count - 1),
            DateTimeOffset.UtcNow);
    }

    public SessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionFile))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(AppPaths.SessionFile));
            if (snapshot is null || snapshot.Urls.Count == 0)
            {
                return null;
            }

            return Normalize(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取会话失败，将按默认方式启动");
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
            {
                File.Delete(AppPaths.SessionFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清除会话文件失败");
        }
    }

    /// <summary>从标签页集合生成待保存的 URL 列表（跳过空白页）。</summary>
    public static IReadOnlyList<string> SnapshotUrls(IEnumerable<BrowserTab> tabs) =>
        tabs.Where(t => !string.IsNullOrWhiteSpace(t.DisplayUrl))
            .Select(t => t.DisplayUrl)
            .ToList();

    /// <summary>与 <see cref="SnapshotUrls"/> 一一对应的标签组列表。</summary>
    public static IReadOnlyList<SessionTabGroup?> SnapshotGroups(IEnumerable<BrowserTab> tabs) =>
        tabs.Where(t => !string.IsNullOrWhiteSpace(t.DisplayUrl))
            .Select(t => t.GroupName is { Length: > 0 }
                ? new SessionTabGroup(t.GroupName, t.GroupColor ?? TabManager.GroupColors[0])
                : null)
            .ToList();

    /// <summary>老会话文件没有 Groups 时补成等长的 null 列表；激活下标钳到范围内。</summary>
    internal static SessionSnapshot Normalize(SessionSnapshot snapshot)
    {
        var index = Math.Clamp(snapshot.ActiveIndex, 0, snapshot.Urls.Count - 1);
        var groups = snapshot.Groups is null
            ? Enumerable.Repeat<SessionTabGroup?>(null, snapshot.Urls.Count).ToList()
            : PadGroups(snapshot.Groups, snapshot.Urls.Count);

        return snapshot with { ActiveIndex = index, Groups = groups };
    }

    private static List<SessionTabGroup?> PadGroups(IReadOnlyList<SessionTabGroup?> groups, int count)
    {
        var result = new List<SessionTabGroup?>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(i < groups.Count ? groups[i] : null);
        }

        return result;
    }
}
