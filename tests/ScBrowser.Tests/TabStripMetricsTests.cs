using ScBrowser.Views;

namespace ScBrowser.Tests;

/// <summary>
/// 标签条宽度换算：标签等宽、放不下时压缩而不是横向滚动。
/// 关键不变量是「总宽（含间距）不超过视口」，否则 ScrollViewer 会重新出现横向滚动。
/// </summary>
public class TabStripMetricsTests
{
    [Fact]
    public void 标签少时用最大宽度()
    {
        Assert.Equal(TabStripMetrics.MaxTabWidth, TabStripMetrics.ResolveTabWidth(1200, 1));
        Assert.Equal(TabStripMetrics.MaxTabWidth, TabStripMetrics.ResolveTabWidth(1200, 2));
    }

    [Fact]
    public void 标签多时等分可用宽度且总宽不超过视口()
    {
        const double available = 1200;
        const int count = 12;

        var width = TabStripMetrics.ResolveTabWidth(available, count);

        Assert.Equal(98, width);   // floor(1200 / 12) − 2
        Assert.True(
            count * (width + TabStripMetrics.TabSpacing) <= available,
            $"总宽 {count * (width + TabStripMetrics.TabSpacing)} 超过视口 {available}");
    }

    [Fact]
    public void 视口极小时缩到最小宽度()
    {
        Assert.Equal(TabStripMetrics.MinTabWidth, TabStripMetrics.ResolveTabWidth(300, 30));
    }

    [Fact]
    public void 无标签或无可用宽度时回退到最大宽度()
    {
        Assert.Equal(TabStripMetrics.MaxTabWidth, TabStripMetrics.ResolveTabWidth(800, 0));
        Assert.Equal(TabStripMetrics.MaxTabWidth, TabStripMetrics.ResolveTabWidth(0, 3));
    }
}
