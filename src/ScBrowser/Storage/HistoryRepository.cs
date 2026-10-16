using Microsoft.Data.Sqlite;

namespace ScBrowser.Storage;

/// <summary>
/// 历史记录仓储。同一 URL 只保留一行，重复访问累加访问次数并刷新时间——
/// 这与主流浏览器的历史去重语义一致（按 URL 而非按访问流水）。
/// </summary>
public sealed class HistoryRepository
{
    private readonly BrowserDatabase _database;

    public HistoryRepository(BrowserDatabase database) => _database = database;

    /// <summary>记录一次访问；已存在则累加次数并更新时间与标题。</summary>
    public void RecordVisit(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO history (url, title, visit_count, last_visit_utc)
            VALUES ($url, $title, 1, $now)
            ON CONFLICT(url) DO UPDATE SET
                visit_count    = visit_count + 1,
                last_visit_utc = excluded.last_visit_utc,
                title          = CASE WHEN excluded.title <> '' THEN excluded.title ELSE title END;
            """;
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>查询历史，按最近访问倒序。<paramref name="search"/> 同时匹配地址与标题。</summary>
    public IReadOnlyList<HistoryEntry> Query(string? search = null, int limit = 200, int offset = 0)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        var hasSearch = !string.IsNullOrWhiteSpace(search);
        command.CommandText = hasSearch
            ? """
              SELECT id, url, title, visit_count, last_visit_utc
              FROM history
              WHERE url LIKE $q OR title LIKE $q
              ORDER BY last_visit_utc DESC
              LIMIT $limit OFFSET $offset;
              """
            : """
              SELECT id, url, title, visit_count, last_visit_utc
              FROM history
              ORDER BY last_visit_utc DESC
              LIMIT $limit OFFSET $offset;
              """;

        if (hasSearch)
        {
            command.Parameters.AddWithValue("$q", $"%{search!.Trim()}%");
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        return ReadEntries(command);
    }

    /// <summary>按访问次数取热门站点（新标签页常用）。</summary>
    public IReadOnlyList<HistoryEntry> QueryTopSites(int limit = 8)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, url, title, visit_count, last_visit_utc
            FROM history
            ORDER BY visit_count DESC, last_visit_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        return ReadEntries(command);
    }

    public void Remove(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>按 URL 删除（用于「删除该站点全部记录」）。</summary>
    public void RemoveByUrl(string url)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE url = $url;";
        command.Parameters.AddWithValue("$url", url);
        command.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        command.ExecuteNonQuery();
    }

    public int Count()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static List<HistoryEntry> ReadEntries(SqliteCommand command)
    {
        var result = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return result;
    }
}
