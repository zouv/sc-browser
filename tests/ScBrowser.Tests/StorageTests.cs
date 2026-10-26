using System.IO;
using Microsoft.Data.Sqlite;
using ScBrowser.Storage;

namespace ScBrowser.Tests;

/// <summary>
/// 数据层：schema 迁移、访问流水、书签树。
/// 用临时数据库文件（<see cref="BrowserDatabase(string)"/>），不碰用户真实数据。
/// </summary>
public class StorageTests : IDisposable
{
    private readonly string _dir;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scbrowser-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite 连接池可能还握着文件句柄：清理失败不影响测试结论。
        }

        GC.SuppressFinalize(this);
    }

    private string DbPath => Path.Combine(_dir, "browser.db");

    private BrowserDatabase CreateDatabase()
    {
        var database = new BrowserDatabase(DbPath);
        database.Initialize();
        return database;
    }

    [Fact]
    public void 初始化后_user_version_应为最新版本()
    {
        CreateDatabase();

        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(BrowserDatabase.SchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void 重复初始化应幂等()
    {
        var database = new BrowserDatabase(DbPath);
        database.Initialize();
        database.Initialize();
    }

    [Fact]
    public void 老库_只到_v1_时应自动补列且保留数据()
    {
        // 模拟 0.3.0 之前创建的库：没有 visits 表，bookmarks 没有树结构列。
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '', visit_count INTEGER NOT NULL DEFAULT 1,
                    last_visit_utc TEXT NOT NULL);
                CREATE UNIQUE INDEX ix_history_url ON history(url);
                CREATE TABLE bookmarks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL);
                CREATE UNIQUE INDEX ix_bookmarks_url ON bookmarks(url);
                CREATE TABLE downloads (
                    id TEXT PRIMARY KEY, url TEXT NOT NULL, file_path TEXT NOT NULL,
                    file_name TEXT NOT NULL, total_bytes INTEGER NOT NULL DEFAULT 0,
                    received_bytes INTEGER NOT NULL DEFAULT 0, state INTEGER NOT NULL DEFAULT 0,
                    started_utc TEXT NOT NULL);
                INSERT INTO bookmarks (url, title, created_utc)
                    VALUES ('https://old.example/', '旧书签', '2026-01-01T00:00:00+00:00');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database).GetAll();

        var old = Assert.Single(bookmarks);
        Assert.Equal("https://old.example/", old.Url);
        Assert.Equal(0, old.ParentId);
        Assert.False(old.IsFolder);

        // 新表也要能用
        new HistoryRepository(database).RecordVisit("https://new.example/", "新页面");
    }

    [Fact]
    public void 访问流水应逐次记录_历史表仍按_URL_聚合()
    {
        var database = CreateDatabase();
        var history = new HistoryRepository(database);

        history.RecordVisit("https://a.example/", "A");
        history.RecordVisit("https://a.example/", "A2");

        var aggregated = Assert.Single(history.Query());
        Assert.Equal(2, aggregated.VisitCount);
        Assert.Equal("A2", aggregated.Title);

        var visits = history.QueryVisits();
        Assert.Equal(2, visits.Count);
        Assert.All(visits, v => Assert.Equal("https://a.example/", v.Url));
    }

    [Fact]
    public void 访问流水支持时间区间与关键字过滤()
    {
        var database = CreateDatabase();
        var history = new HistoryRepository(database);
        history.RecordVisit("https://old.example/", "旧");

        Thread.Sleep(20);
        var cutoff = DateTimeOffset.UtcNow;
        Thread.Sleep(20);
        history.RecordVisit("https://new.example/", "新");

        var recent = history.QueryVisits(fromUtc: cutoff);
        Assert.Single(recent);
        Assert.Equal("https://new.example/", recent[0].Url);

        Assert.Single(history.QueryVisits(toUtc: cutoff));
        Assert.Single(history.QueryVisits(search: "old"));
        Assert.Empty(history.QueryVisits(search: "不存在"));
    }

    [Fact]
    public void 删除站点记录应同时清掉访问流水()
    {
        var database = CreateDatabase();
        var history = new HistoryRepository(database);
        history.RecordVisit("https://a.example/", "A");
        history.RecordVisit("https://b.example/", "B");

        history.RemoveByUrl("https://a.example/");

        Assert.Single(history.Query());
        Assert.Single(history.QueryVisits());
        Assert.Equal("https://b.example/", history.QueryVisits()[0].Url);
    }

    [Fact]
    public void 书签树_文件夹与子项()
    {
        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database);

        var folder = bookmarks.AddFolder("工作");
        var inner = bookmarks.Add("https://work.example/", "工作台", folder);

        var all = bookmarks.GetAll();
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(b => b.Id == folder).IsFolder);
        Assert.Equal(folder, all.Single(b => b.Id == inner).ParentId);
    }

    [Fact]
    public void 删除文件夹应连同子孙一起删()
    {
        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database);

        var folder = bookmarks.AddFolder("目录");
        var sub = bookmarks.AddFolder("子目录", folder);
        bookmarks.Add("https://x.example/", "X", sub);

        Assert.True(bookmarks.RemoveById(folder));
        Assert.Empty(bookmarks.GetAll());
    }

    [Fact]
    public void 不允许把文件夹移进自己的子孙()
    {
        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database);

        var parent = bookmarks.AddFolder("父");
        var child = bookmarks.AddFolder("子", parent);

        Assert.False(bookmarks.Move(parent, child));
        Assert.True(bookmarks.Move(child, 0));
        Assert.Equal(0, bookmarks.GetAll().Single(b => b.Id == child).ParentId);
    }

    [Fact]
    public void 重命名与同_URL_幂等新增()
    {
        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database);

        var id = bookmarks.Add("https://a.example/", "旧标题");
        var again = bookmarks.Add("https://a.example/", "新标题");

        Assert.Equal(id, again);
        var entry = Assert.Single(bookmarks.GetAll());
        Assert.Equal("新标题", entry.Title);

        Assert.True(bookmarks.Rename(id, "改过"));
        Assert.Equal("改过", bookmarks.Find("https://a.example/")!.Title);
    }

    [Fact]
    public void 多个文件夹可以同名且不冲突()
    {
        // 文件夹的 url 为空：旧的全表唯一索引会把第二个文件夹挡掉，新库必须允许。
        var database = CreateDatabase();
        var bookmarks = new BookmarkRepository(database);

        bookmarks.AddFolder("重复");
        bookmarks.AddFolder("重复");

        Assert.Equal(2, bookmarks.GetAll().Count);
    }
}
