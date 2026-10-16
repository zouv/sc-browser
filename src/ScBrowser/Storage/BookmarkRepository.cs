namespace ScBrowser.Storage;

/// <summary>书签仓储。</summary>
public sealed class BookmarkRepository
{
    private readonly BrowserDatabase _database;

    public BookmarkRepository(BrowserDatabase database) => _database = database;

    public IReadOnlyList<BookmarkEntry> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, url, title, created_utc FROM bookmarks ORDER BY created_utc DESC;";

        var result = new List<BookmarkEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BookmarkEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return result;
    }

    public BookmarkEntry? Find(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, url, title, created_utc FROM bookmarks WHERE url = $url LIMIT 1;";
        command.Parameters.AddWithValue("$url", url);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new BookmarkEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)))
            : null;
    }

    public bool Exists(string url) => Find(url) is not null;

    /// <summary>新增书签；同 URL 已存在时更新标题（幂等）。</summary>
    public void Add(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO bookmarks (url, title, created_utc)
            VALUES ($url, $title, $now)
            ON CONFLICT(url) DO UPDATE SET
                title = CASE WHEN excluded.title <> '' THEN excluded.title ELSE title END;
            """;
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>按 URL 删除；返回是否确实删掉了一行。</summary>
    public bool Remove(string url)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE url = $url;";
        command.Parameters.AddWithValue("$url", url);
        return command.ExecuteNonQuery() > 0;
    }

    public bool RemoveById(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>切换书签状态；返回切换后是否处于已收藏。</summary>
    public bool Toggle(string url, string title)
    {
        if (Remove(url))
        {
            return false;
        }

        Add(url, title);
        return true;
    }
}
