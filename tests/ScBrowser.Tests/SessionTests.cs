using System.Text.Json;
using ScBrowser.Services;

namespace ScBrowser.Tests;

/// <summary>会话保存里的标签组对齐、老文件兼容。</summary>
public class SessionTests
{
    [Fact]
    public void 空白标签应被过滤且组信息保持对齐()
    {
        var snapshot = SessionStore.BuildSnapshot(
            ["https://a/", "   ", "https://b/"],
            [new SessionTabGroup("工作", "#1a73e8"), new SessionTabGroup("不该保留", "#d93025"), null],
            activeIndex: 2);

        Assert.NotNull(snapshot);
        Assert.Equal(["https://a/", "https://b/"], snapshot!.Urls);
        Assert.Equal("工作", snapshot.Groups![0]!.Name);
        Assert.Null(snapshot.Groups[1]);
        Assert.Equal(1, snapshot.ActiveIndex);
    }

    [Fact]
    public void 没有可保存的标签时应返回空()
    {
        Assert.Null(SessionStore.BuildSnapshot(["", "  "], [], 0));
        Assert.Null(SessionStore.BuildSnapshot([], [], 0));
    }

    [Fact]
    public void 老会话文件没有_Groups_字段时应补成等长空列表()
    {
        const string json = """
            {"Urls":["https://a/","https://b/"],"ActiveIndex":9,"SavedAt":"2026-10-01T00:00:00+00:00"}
            """;

        var loaded = JsonSerializer.Deserialize<SessionSnapshot>(json);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.Groups);

        var normalized = SessionStore.Normalize(loaded);

        Assert.Equal(2, normalized.Groups!.Count);
        Assert.All(normalized.Groups, Assert.Null);
        Assert.Equal(1, normalized.ActiveIndex);   // 越界的激活下标被钳制
    }

    [Fact]
    public void 组列表比地址短时应补齐()
    {
        var snapshot = new SessionSnapshot(
            ["https://a/", "https://b/"],
            [new SessionTabGroup("组", "#188038")],
            0,
            DateTimeOffset.UtcNow);

        var normalized = SessionStore.Normalize(snapshot);

        Assert.Equal(2, normalized.Groups!.Count);
        Assert.Null(normalized.Groups[1]);
    }
}
