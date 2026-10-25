using System.Windows;
using ScBrowser.Common;

namespace ScBrowser.Tests;

/// <summary>外壳可见性绑定的转换器：全屏时错误条 / 状态栏必须收起，退出后还要能回来。</summary>
public class VisibilityConverterTests
{
    private static readonly TextAndChromeVisibilityConverter Converter = new();

    [Theory]
    [InlineData("https://a.com/x", true, Visibility.Visible)]
    [InlineData("https://a.com/x", false, Visibility.Collapsed)]
    [InlineData(null, true, Visibility.Collapsed)]
    [InlineData("", true, Visibility.Collapsed)]
    [InlineData("   ", true, Visibility.Collapsed)]
    public void 有文本且非全屏才显示(string? text, bool chromeVisible, Visibility expected)
    {
        var values = new object[] { text!, chromeVisible };
        var result = Converter.Convert(values, typeof(Visibility), null!, null!);
        Assert.Equal(expected, (Visibility)result);
    }

    [Fact]
    public void 缺少参数时应收起而不是显示()
    {
        // 绑定路径取不到值时会给空数组：必须退回 Collapsed，
        // 否则 Visibility 的默认值 Visible 会让空提示条闪出来（ARCHITECTURE §7）。
        Assert.Equal(Visibility.Collapsed, (Visibility)Converter.Convert([], typeof(Visibility), null!, null!));
        Assert.Equal(
            Visibility.Collapsed,
            (Visibility)Converter.Convert(new object[] { null! }, typeof(Visibility), null!, null!));
    }
}
