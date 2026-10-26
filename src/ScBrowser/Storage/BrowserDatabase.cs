using Microsoft.Data.Sqlite;
using ScBrowser.Infrastructure;

namespace ScBrowser.Storage;

/// <summary>
/// SQLite 数据库的建表、迁移与连接工厂（历史 / 访问明细 / 书签 / 下载）。
/// <para>
/// 连接策略：<b>每次操作开一个连接</b>（依赖 Microsoft.Data.Sqlite 的内置连接池）。
/// 桌面单进程场景下这比维护长连接更省心，也天然规避了跨线程共享连接的问题。
/// </para>
/// <para>
/// <b>schema 迁移用 <c>PRAGMA user_version</c></b>：每个版本一段幂等的升级脚本，启动时从当前版本
/// 依次升到最新。不要再把新表 / 新列直接塞进 <c>CREATE TABLE IF NOT EXISTS</c>——那对已存在的库无效。
/// </para>
/// </summary>
public sealed class BrowserDatabase
{
    /// <summary>当前 schema 版本。加表 / 加列时 +1 并在 <see cref="Migrate"/> 里补一段。</summary>
    public const int SchemaVersion = 2;

    private readonly string _connectionString;

    public BrowserDatabase() : this(AppPaths.DatabaseFile)
    {
    }

    /// <summary>指定数据库文件（单测用临时路径）。</summary>
    public BrowserDatabase(string databaseFile)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 不要用 Cache=Shared：多连接共享缓存要靠 SQLite 的 shared-cache 模式，容易出锁竞争，
            // 官方也不推荐在长期运行的进程里这么配。WAL 已经足够应对「读写并发」。
        }.ToString();
    }

    /// <summary>打开一个新连接（调用方负责释放）。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // WAL：读写并发更好，且崩溃后不易损坏库文件。
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>建表 / 迁移（幂等）。应用启动时调用一次。</summary>
    public void Initialize()
    {
        using var connection = OpenConnection();
        var version = GetUserVersion(connection);

        if (version > SchemaVersion)
        {
            // 用户装过更新版本又降级：不认识的库不去动它，避免损坏数据。
            throw new InvalidOperationException(
                $"数据库 schema 版本 {version} 高于当前程序支持的 {SchemaVersion}，请升级到更新版本。");
        }

        Migrate(connection, version);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Migrate(SqliteConnection connection, int fromVersion)
    {
        using var transaction = connection.BeginTransaction();

        if (fromVersion < 1)
        {
            Execute(connection, transaction, """
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
                """);
        }

        if (fromVersion < 2)
        {
            // 访问明细：历史表按 URL 聚合（每次访问只累加计数），做不了「按日期分组」。
            // 这里另存一条一行的访问流水。
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS visits (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    url         TEXT NOT NULL,
                    title       TEXT NOT NULL DEFAULT '',
                    visited_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_visits_time ON visits(visited_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_visits_url ON visits(url);
                """);

            // 书签树：parent_id = 0 表示根；kind: 0 = 书签，1 = 文件夹。
            AddColumnIfMissing(connection, transaction, "bookmarks", "parent_id", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, transaction, "bookmarks", "sort_order", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, transaction, "bookmarks", "kind", "INTEGER NOT NULL DEFAULT 0");

            // 文件夹的 url 为空，原来的「url 唯一」索引会挡住第二个文件夹——改成只对书签生效的部分索引。
            Execute(connection, transaction, """
                DROP INDEX IF EXISTS ix_bookmarks_url;
                CREATE UNIQUE INDEX IF NOT EXISTS ix_bookmarks_url
                    ON bookmarks(url) WHERE kind = 0 AND url <> '';
                CREATE INDEX IF NOT EXISTS ix_bookmarks_parent ON bookmarks(parent_id, sort_order);
                """);
        }

        if (fromVersion != SchemaVersion)
        {
            Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }
}
