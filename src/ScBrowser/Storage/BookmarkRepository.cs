using Microsoft.Data.Sqlite;

namespace ScBrowser.Storage;

/// <summary>
/// 书签仓储（支持文件夹树）。
/// <para>
/// 节点用 <c>parent_id</c> 组织：<c>0</c> 是根，文件夹的 <c>kind = 1</c>、<c>url</c> 为空。
/// 同一父节点内用 <c>sort_order</c> 排序。删除文件夹会连同子孙一起删（递归 CTE）。
/// </para>
/// </summary>
public sealed class BookmarkRepository
{
    private readonly BrowserDatabase _database;

    public BookmarkRepository(BrowserDatabase database) => _database = database;

    /// <summary>书签数据发生变化（增删改/导入导出后刷新书签栏与星标用）。</summary>
    public event EventHandler? Changed;

    // 写操作都跑在后台线程（Async 包装），但订阅者（书签栏/星标）要碰 UI 集合——
    // 捕获创建时的同步上下文（DI 装配在 UI 线程上），事件回投到 UI 线程再触发。
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    private void NotifyChanged()
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>所有节点（根与子孙都在这一个列表里，由界面按 parent_id 组装成树）。</summary>
    public IReadOnlyList<BookmarkEntry> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, url, title, parent_id, sort_order, kind, created_utc
            FROM bookmarks
            ORDER BY parent_id, sort_order, title COLLATE NOCASE;
            """;

        var result = new List<BookmarkEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public Task<IReadOnlyList<BookmarkEntry>> GetAllAsync() => Task.Run(GetAll);

    public BookmarkEntry? Find(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, url, title, parent_id, sort_order, kind, created_utc FROM bookmarks WHERE url = $url AND kind = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$url", url);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public Task<BookmarkEntry?> FindAsync(string url) => Task.Run(() => Find(url));

    public bool Exists(string url) => Find(url) is not null;

    public Task<bool> ExistsAsync(string url) => Task.Run(() => Exists(url));

    /// <summary>新增书签；同 URL 已存在时更新标题（幂等）。返回节点 id。</summary>
    public long Add(string url, string title, long parentId = 0)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return 0;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM bookmarks WHERE url = $url AND kind = 0 LIMIT 1;";
            existing.Parameters.AddWithValue("$url", url);
            if (existing.ExecuteScalar() is { } found)
            {
                var id = Convert.ToInt64(found);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE bookmarks SET title = CASE WHEN $title <> '' THEN $title ELSE title END, parent_id = $parent WHERE id = $id;";
                update.Parameters.AddWithValue("$title", title ?? string.Empty);
                update.Parameters.AddWithValue("$parent", parentId);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
                transaction.Commit();
                NotifyChanged();
                return id;
            }
        }

        var newId = Insert(connection, transaction, url, title, parentId, kind: 0);
        transaction.Commit();
        NotifyChanged();
        return newId;
    }

    public Task<long> AddAsync(string url, string title, long parentId = 0) =>
        Task.Run(() => Add(url, title, parentId));

    /// <summary>新建文件夹。</summary>
    public long AddFolder(string title, long parentId = 0)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var id = Insert(connection, transaction, string.Empty, title, parentId, kind: 1);
        transaction.Commit();
        NotifyChanged();
        return id;
    }

    public Task<long> AddFolderAsync(string title, long parentId = 0) =>
        Task.Run(() => AddFolder(title, parentId));

    /// <summary>重命名书签或文件夹。</summary>
    public bool Rename(long id, string title)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE bookmarks SET title = $title WHERE id = $id;";
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public Task<bool> RenameAsync(long id, string title) => Task.Run(() => Rename(id, title));

    /// <summary>把节点移到另一个文件夹（不能移进自己或自己的子孙）。</summary>
    public bool Move(long id, long newParentId, int? sortOrder = null)
    {
        if (id == newParentId)
        {
            return false;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (newParentId != 0 && IsSelfOrDescendant(connection, transaction, id, newParentId))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE bookmarks SET parent_id = $parent, sort_order = $order WHERE id = $id;";
        command.Parameters.AddWithValue("$parent", newParentId);
        command.Parameters.AddWithValue("$order", sortOrder ?? NextSortOrder(connection, transaction, newParentId));
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public Task<bool> MoveAsync(long id, long newParentId, int? sortOrder = null) =>
        Task.Run(() => Move(id, newParentId, sortOrder));

    /// <summary>删除节点（文件夹连同子孙）。</summary>
    public bool RemoveById(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE sub(id) AS (
                SELECT id FROM bookmarks WHERE id = $id
                UNION ALL
                SELECT b.id FROM bookmarks b JOIN sub s ON b.parent_id = s.id
            )
            DELETE FROM bookmarks WHERE id IN (SELECT id FROM sub);
            """;
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public Task<bool> RemoveByIdAsync(long id) => Task.Run(() => RemoveById(id));

    /// <summary>按 URL 删除；返回是否确实删掉了一行。</summary>
    public Task<bool> RemoveAsync(string url) => Task.Run(() => Remove(url));

    public bool Remove(string url)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE url = $url AND kind = 0;";
        command.Parameters.AddWithValue("$url", url);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
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

    public Task<bool> ToggleAsync(string url, string title) => Task.Run(() => Toggle(url, title));

    /// <summary>编辑书签的名称与地址（URL 只对书签生效，文件夹只改名）。</summary>
    public bool Edit(long id, string title, string url)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE bookmarks
            SET title = $title,
                url = CASE WHEN kind = 0 THEN $url ELSE url END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$url", url ?? string.Empty);
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public Task<bool> EditAsync(long id, string title, string url) => Task.Run(() => Edit(id, title, url));

    /// <summary>
    /// 把导入树写进库（递归建文件夹与书签，挂到 <paramref name="parentId"/> 下）。
    /// 返回实际写入的书签数（不含文件夹）。
    /// </summary>
    public int Import(IReadOnlyList<BookmarkHtml.Node> nodes, long parentId = 0)
    {
        var count = 0;
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        void InsertNode(BookmarkHtml.Node node, long parent)
        {
            if (node.IsFolder)
            {
                var folderId = Insert(connection, transaction, string.Empty, node.Title, parent, kind: 1);
                foreach (var child in node.Children)
                {
                    InsertNode(child, folderId);
                }
            }
            else if (node.Url.Length > 0)
            {
                Insert(connection, transaction, node.Url, node.Title, parent, kind: 0);
                count++;
            }
        }

        foreach (var node in nodes)
        {
            InsertNode(node, parentId);
        }

        transaction.Commit();
        if (count > 0)
        {
            NotifyChanged();
        }

        return count;
    }

    public Task<int> ImportAsync(IReadOnlyList<BookmarkHtml.Node> nodes, long parentId = 0) =>
        Task.Run(() => Import(nodes, parentId));

    /// <summary>取一个文件夹（或其子孙文件夹）里的全部书签 URL，按树序展开——「打开文件夹内全部书签」用。</summary>
    public IReadOnlyList<string> GetFolderUrls(long folderId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // 根目录没有对应的行（约定 parent_id=0 表示根），要特判成全树。
        if (folderId == 0)
        {
            command.CommandText =
                """
                SELECT url FROM bookmarks
                WHERE kind = 0 AND url <> ''
                ORDER BY parent_id, sort_order;
                """;
        }
        else
        {
            command.CommandText =
                """
                WITH RECURSIVE sub(id) AS (
                    SELECT id FROM bookmarks WHERE id = $id
                    UNION ALL
                    SELECT b.id FROM bookmarks b JOIN sub s ON b.parent_id = s.id
                )
                SELECT b.url
                FROM bookmarks b
                JOIN sub s ON s.id = b.id
                WHERE b.kind = 0 AND b.url <> ''
                ORDER BY b.parent_id, b.sort_order;
                """;
            command.Parameters.AddWithValue("$id", folderId);
        }

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public Task<IReadOnlyList<string>> GetFolderUrlsAsync(long folderId) => Task.Run(() => GetFolderUrls(folderId));

    private static long Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string url,
        string title,
        long parentId,
        int kind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO bookmarks (url, title, parent_id, sort_order, kind, created_utc)
            VALUES ($url, $title, $parent, $order, $kind, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$url", url ?? string.Empty);
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$parent", parentId);
        command.Parameters.AddWithValue("$order", NextSortOrder(connection, transaction, parentId));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static int NextSortOrder(SqliteConnection connection, SqliteTransaction transaction, long parentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM bookmarks WHERE parent_id = $parent;";
        command.Parameters.AddWithValue("$parent", parentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool IsSelfOrDescendant(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long nodeId,
        long candidateParentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH RECURSIVE sub(id) AS (
                SELECT id FROM bookmarks WHERE id = $id
                UNION ALL
                SELECT b.id FROM bookmarks b JOIN sub s ON b.parent_id = s.id
            )
            SELECT COUNT(*) FROM sub WHERE id = $parent;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        command.Parameters.AddWithValue("$parent", candidateParentId);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static BookmarkEntry Read(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt32(5) == 1,
            DateTimeOffset.Parse(reader.GetString(6)));
}
