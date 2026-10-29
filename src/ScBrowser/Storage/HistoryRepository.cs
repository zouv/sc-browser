using Microsoft.Data.Sqlite;

namespace ScBrowser.Storage;

/// <summary>
/// 历史仓储。同一 URL 在 <c>history</c> 表里只保留一行（累加次数、刷新时间），
/// 同时往 <c>visits</c> 表写一条访问流水——后者支撑「按日期分组」。
/// <para>
/// 所有读写都有 <c>*Async</c> 包装：SQLite 调用是同步 IO，放在 UI 线程上会卡界面。
/// 界面 / 消息路由一律用 Async 版本。
/// </para>
/// </summary>
public sealed class HistoryRepository
{
    private readonly BrowserDatabase _database;

    public HistoryRepository(BrowserDatabase database) => _database = database;

    /// <summary>记录一次访问；已存在则累加次数并更新时间与标题，同时写一条访问流水。</summary>
    public void RecordVisit(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
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

        using (var visit = connection.CreateCommand())
        {
            visit.Transaction = transaction;
            visit.CommandText =
                "INSERT INTO visits (url, title, visited_utc) VALUES ($url, $title, $now);";
            visit.Parameters.AddWithValue("$url", url);
            visit.Parameters.AddWithValue("$title", title ?? string.Empty);
            visit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            visit.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task RecordVisitAsync(string url, string title) => Task.Run(() => RecordVisit(url, title));

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

    public Task<IReadOnlyList<HistoryEntry>> QueryAsync(string? search = null, int limit = 200, int offset = 0) =>
        Task.Run(() => Query(search, limit, offset));

    /// <summary>访问流水：按时间倒序，可按关键字与时间区间过滤（时间边界是 UTC）。</summary>
    public IReadOnlyList<VisitEntry> QueryVisits(
        string? search = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int limit = 500)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(url LIKE $q OR title LIKE $q)");
            command.Parameters.AddWithValue("$q", $"%{search!.Trim()}%");
        }

        if (fromUtc is { } from)
        {
            conditions.Add("visited_utc >= $from");
            command.Parameters.AddWithValue("$from", from.ToString("O"));
        }

        if (toUtc is { } to)
        {
            conditions.Add("visited_utc < $to");
            command.Parameters.AddWithValue("$to", to.ToString("O"));
        }

        command.CommandText =
            "SELECT id, url, title, visited_utc FROM visits"
            + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty)
            + " ORDER BY visited_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

        var result = new List<VisitEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new VisitEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return result;
    }

    public Task<IReadOnlyList<VisitEntry>> QueryVisitsAsync(
        string? search = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int limit = 500) =>
        Task.Run(() => QueryVisits(search, fromUtc, toUtc, limit));

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

    public Task<IReadOnlyList<HistoryEntry>> QueryTopSitesAsync(int limit = 8) =>
        Task.Run(() => QueryTopSites(limit));

    public void Remove(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public Task RemoveAsync(long id) => Task.Run(() => Remove(id));

    /// <summary>批量删除（历史页的多选删除；一条 SQL 干掉全部 id）。</summary>
    public void RemoveMany(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => "$id" + i));
        command.CommandText = $"DELETE FROM history WHERE id IN ({placeholders});";
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue("$id" + i, ids[i]);
        }

        command.ExecuteNonQuery();
    }

    public Task RemoveManyAsync(IReadOnlyList<long> ids) => Task.Run(() => RemoveMany(ids));

    /// <summary>按 URL 删除（用于「删除该站点全部记录」，连同访问流水）。</summary>
    public void RemoveByUrl(string url)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history WHERE url = $url;";
            command.Parameters.AddWithValue("$url", url);
            command.ExecuteNonQuery();
        }

        using (var visits = connection.CreateCommand())
        {
            visits.Transaction = transaction;
            visits.CommandText = "DELETE FROM visits WHERE url = $url;";
            visits.Parameters.AddWithValue("$url", url);
            visits.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task RemoveByUrlAsync(string url) => Task.Run(() => RemoveByUrl(url));

    public void Clear()
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var table in new[] { "history", "visits" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task ClearAsync() => Task.Run(Clear);

    /// <summary>
    /// 按时间范围清除（清除浏览数据对话框的「过去 1 小时/24 小时…」）：
    /// 删掉范围内的访问流水，再删掉「流水已被删光、且最近访问也在范围内」的聚合行——
    /// 迁移前没有流水的旧行（last_visit 更早）不会被误删。
    /// </summary>
    public void ClearSince(DateTimeOffset since)
    {
        var sinceUtc = since.ToUniversalTime().ToString("O");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM visits WHERE visited_utc >= $since;";
            command.Parameters.AddWithValue("$since", sinceUtc);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM history
                WHERE last_visit_utc >= $since
                  AND url NOT IN (SELECT DISTINCT url FROM visits);
                """;
            command.Parameters.AddWithValue("$since", sinceUtc);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task ClearSinceAsync(DateTimeOffset since) => Task.Run(() => ClearSince(since));

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
