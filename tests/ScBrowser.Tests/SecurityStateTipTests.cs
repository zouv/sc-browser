using ScBrowser.Browser;
using ScBrowser.Common;

namespace ScBrowser.Tests;

/// <summary>
/// 地址栏安全图标的悬停文案。以前直接显示枚举名（<c>Unknown</c>），用户看到的是英文乱码般的提示。
/// </summary>
public class SecurityStateTipTests
{
    [Theory]
    [InlineData(SecurityState.Secure, "连接是安全的（HTTPS）")]
    [InlineData(SecurityState.Neutral, "此连接未加密（HTTP）")]
    [InlineData(SecurityState.Insecure, "此连接有部分内容不安全")]
    [InlineData(SecurityState.Danger, "此连接不安全，请勿在此输入敏感信息")]
    public void 普通网页按状态给出中文提示(SecurityState state, string expected)
    {
        Assert.Equal(expected, SecurityStateToTipConverter.Describe(state, "https://example.com/"));
    }

    [Fact]
    public void 内部页面应说明不经过网络()
    {
        var tip = SecurityStateToTipConverter.Describe(SecurityState.Unknown, "browser://settings");

        Assert.Contains("内部页面", tip);
        Assert.DoesNotContain("Unknown", tip);
    }

    [Fact]
    public void 本地文件应说明不经过网络()
    {
        var tip = SecurityStateToTipConverter.Describe(SecurityState.Unknown, "file:///C:/tmp/x.html");

        Assert.Contains("本地文件", tip);
    }

    [Fact]
    public void 未知状态的网页提示不应出现英文枚举名()
    {
        var tip = SecurityStateToTipConverter.Describe(SecurityState.Unknown, "https://example.com/");

        Assert.Contains("未知", tip);
        Assert.DoesNotContain("Unknown", tip);
    }
}
