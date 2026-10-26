namespace ScBrowser.Storage;

/// <summary>下载记录仓储。同一 id 重复写入视为进度更新。</summary>
public sealed class DownloadRepository
{
    private readonly BrowserDatabase _database;

    public DownloadRepository(BrowserDatabase database) => _database = database;

    public void Upsert(DownloadEntry entry)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO downloads (id, url, file_path, file_name, total_bytes, received_bytes, state, started_utc)
            VALUES ($id, $url, $path, $name, $total, $received, $state, $started)
            ON CONFLICT(id) DO UPDATE SET
                file_path      = excluded.file_path,
                file_name      = excluded.file_name,
                total_bytes    = excluded.total_bytes,
                received_bytes = excluded.received_bytes,
                state          = excluded.state;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$url", entry.Url);
        command.Parameters.AddWithValue("$path", entry.FilePath);
        command.Parameters.AddWithValue("$name", entry.FileName);
        command.Parameters.AddWithValue("$total", entry.TotalBytes);
        command.Parameters.AddWithValue("$received", entry.ReceivedBytes);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$started", entry.Started.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DownloadEntry> Query(int limit = 200)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, url, file_path, file_name, total_bytes, received_bytes, state, started_utc
            FROM downloads
            ORDER BY started_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        return ReadEntries(command);
    }

    /// <summary>按 id 取一条下载记录；不存在返回 <c>null</c>。</summary>
    public DownloadEntry? Find(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, url, file_path, file_name, total_bytes, received_bytes, state, started_utc
            FROM downloads
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        return ReadEntries(command).FirstOrDefault();
    }

    private static List<DownloadEntry> ReadEntries(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var result = new List<DownloadEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DownloadEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                (DownloadState)reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return result;
    }

    public Task UpsertAsync(DownloadEntry entry) => Task.Run(() => Upsert(entry));

    public Task<IReadOnlyList<DownloadEntry>> QueryAsync(int limit = 200) => Task.Run(() => Query(limit));

    public Task<DownloadEntry?> FindAsync(string id) => Task.Run(() => Find(id));

    public Task RemoveAsync(string id) => Task.Run(() => Remove(id));

    public Task ClearAsync() => Task.Run(Clear);

    public void Clear()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 把仍标记为「下载中 / 已暂停」的记录改为失败。
    /// 应用启动时调用：进程退出时未完成的下载不可能自行续传，留着只会显示成永远转圈的幽灵项。
    /// </summary>
    public int MarkInProgressAsFailed()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE downloads SET state = $failed WHERE state IN ($inProgress, $paused);";
        command.Parameters.AddWithValue("$failed", (int)DownloadState.Failed);
        command.Parameters.AddWithValue("$inProgress", (int)DownloadState.InProgress);
        command.Parameters.AddWithValue("$paused", (int)DownloadState.Paused);
        return command.ExecuteNonQuery();
    }

    public void Remove(string id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
