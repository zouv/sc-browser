using System.Reflection;
using ScBrowser;

namespace ScBrowser.Tests;

/// <summary>
/// 关于页 / 启动日志的版本号展示：取 <c>InformationalVersion</c> 并去掉构建元数据（+提交哈希），
/// 不能显示 <c>AssemblyVersion</c> 自动补齐的第四段 0（例如 0.7.0.0）。
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void 展示版本号去掉构建元数据且不显示四段补齐的零()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.NotNull(informational);
        var expected = informational.Split('+')[0];

        Assert.Equal(expected, App.DisplayVersion);
        Assert.DoesNotContain("+", App.DisplayVersion);
        Assert.NotEqual(assembly.GetName().Version!.ToString(), App.DisplayVersion);
    }
}
