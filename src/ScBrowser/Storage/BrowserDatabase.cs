using Microsoft.Data.Sqlite;
using ScBrowser.Infrastructure;

namespace ScBrowser.Storage;

/// <summary>
/// SQLite 数据库的建表与连接工厂（历史 / 书签 / 下载）。
/// <para>
/// 连接策略：<b>每次操作开一个连接</b>（依赖 Microsoft.Data.Sqlite 的内置连接池）。
/// 桌面单进程场景下这比维护长连接更省心，也天然规避了跨线程共享连接的问题。
/// </para>
/// </summary>
public sealed class BrowserDatabase
{
    private readonly string _connectionString;

    public BrowserDatabase()
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>打开一个新连接（调用方负责释放）。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // WAL：读写并发更好，且崩溃后不易损坏库文件。
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>建表（幂等）。应用启动时调用一次。</summary>
    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                url           TEXT    NOT NULL,
                title         TEXT    NOT NULL DEFAULT '',
                visit_count   INTEGER NOT NULL DEFAULT 1,
                last_visit_utc TEXT   NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_history_url ON history(url);
            CREATE INDEX IF NOT EXISTS ix_history_visit ON history(last_visit_utc DESC);

            CREATE TABLE IF NOT EXISTS bookmarks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                url         TEXT    NOT NULL,
                title       TEXT    NOT NULL DEFAULT '',
                created_utc TEXT    NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_bookmarks_url ON bookmarks(url);

            CREATE TABLE IF NOT EXISTS downloads (
                id             TEXT    PRIMARY KEY,
                url            TEXT    NOT NULL,
                file_path      TEXT    NOT NULL,
                file_name      TEXT    NOT NULL,
                total_bytes    INTEGER NOT NULL DEFAULT 0,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                state          INTEGER NOT NULL DEFAULT 0,
                started_utc    TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_downloads_started ON downloads(started_utc DESC);
            """;
        command.ExecuteNonQuery();
    }
}
