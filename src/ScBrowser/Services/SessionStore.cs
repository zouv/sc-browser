using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;

namespace ScBrowser.Services;

/// <summary>一次会话的快照。</summary>
public sealed record SessionSnapshot(IReadOnlyList<string> Urls, int ActiveIndex, DateTimeOffset SavedAt);

/// <summary>
/// 会话保存与恢复：退出时记下打开的标签页，下次启动还原。
/// 落盘于 <c>%LOCALAPPDATA%\sc-browser\session.json</c>。
/// </summary>
public sealed class SessionStore
{
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ILogger<SessionStore> logger) => _logger = logger;

    public void Save(IReadOnlyList<string> urls, int activeIndex)
    {
        try
        {
            // 只留真正有内容的标签；全空则视为「没什么可恢复」。
            var meaningful = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (meaningful.Count == 0)
            {
                Clear();
                return;
            }

            var snapshot = new SessionSnapshot(meaningful, Math.Clamp(activeIndex, 0, meaningful.Count - 1), DateTimeOffset.UtcNow);
            AtomicFile.WriteAllText(
                AppPaths.SessionFile,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存会话失败");
        }
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

            // 过滤掉已经不可解析的地址类型由调用方兜底；这里只保证不越界。
            var index = Math.Clamp(snapshot.ActiveIndex, 0, snapshot.Urls.Count - 1);
            return snapshot with { ActiveIndex = index };
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
        tabs.Select(t => t.DisplayUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
}
