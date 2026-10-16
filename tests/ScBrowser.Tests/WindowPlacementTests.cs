using ScBrowser.Services;

namespace ScBrowser.Tests;

/// <summary>
/// 窗口几何的读回逻辑。纯函数，不依赖真实窗口。
/// </summary>
public class WindowPlacementTests
{
    // 典型 2560x1440 主屏 + 一块在左侧的副屏（虚桌面起点为负）。
    private const double ScreenLeft = -1920;
    private const double ScreenTop = 0;
    private const double ScreenWidth = 4480;
    private const double ScreenHeight = 1440;

    [Fact]
    public void 正常保存的几何应原样读回()
    {
        var r = WindowPlacement.Resolve(100, 50, 1000, 700, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(1000, r.Width);
        Assert.Equal(700, r.Height);
        Assert.Equal(100, r.Left);
        Assert.Equal(50, r.Top);
    }

    [Fact]
    public void 位置丢在桌面之外应放弃位置但保留尺寸()
    {
        // 副屏被拔掉：x=-1500 现在落在虚桌面之外。
        var r = WindowPlacement.Resolve(-3000, 200, 1000, 700, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(1000, r.Width);
        Assert.Equal(700, r.Height);
        Assert.Null(r.Left);
        Assert.Null(r.Top);
    }

    [Fact]
    public void 只剩一条边露在桌面内也应放弃位置()
    {
        // 只露出不到 48px：窗口拖不回来，宁可居中。
        var r = WindowPlacement.Resolve(4440, 200, 1000, 700, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Null(r.Left);
        Assert.Null(r.Top);
    }

    [Fact]
    public void 尺寸小于最小限制应回退默认尺寸()
    {
        var r = WindowPlacement.Resolve(null, null, 100, 100, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(WindowPlacement.DefaultWidth, r.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, r.Height);
    }

    [Fact]
    public void 尺寸大于虚拟桌面应被钳制()
    {
        var r = WindowPlacement.Resolve(0, 0, 9999, 9999, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(ScreenWidth, r.Width);
        Assert.Equal(ScreenHeight, r.Height);
    }

    [Fact]
    public void 未保存过位置时应居中且使用默认尺寸()
    {
        var r = WindowPlacement.Resolve(null, null, null, null, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(WindowPlacement.DefaultWidth, r.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, r.Height);
        Assert.Null(r.Left);
        Assert.Null(r.Top);
    }

    [Fact]
    public void 只有尺寸没有位置时保留尺寸()
    {
        var r = WindowPlacement.Resolve(null, null, 900, 600, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(900, r.Width);
        Assert.Equal(600, r.Height);
        Assert.Null(r.Left);
        Assert.Null(r.Top);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法数值应被当作未保存(double bad)
    {
        var r = WindowPlacement.Resolve(bad, bad, bad, bad, ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight);

        Assert.Equal(WindowPlacement.DefaultWidth, r.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, r.Height);
        Assert.Null(r.Left);
        Assert.Null(r.Top);
    }
}
