using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// Ctrl+单击新标签的插入位置：插在发起标签右侧；连续打开时按顺序向右排
/// （每个新标签的 <see cref="BrowserTab.Opener"/> 都指向同一个发起标签）。
/// </summary>
public class TabInsertionTests
{
    [Fact]
    public void 发起标签不在集合里时返回空()
    {
        using var opener = new BrowserTab(1);
        using var other = new BrowserTab(2);

        Assert.Null(TabInsertion.RightOfOpener([other], opener));
    }

    [Fact]
    public void 首个新标签插到发起标签右侧()
    {
        using var opener = new BrowserTab(1);
        using var following = new BrowserTab(2);
        var tabs = new List<BrowserTab> { opener, following };

        Assert.Equal(1, TabInsertion.RightOfOpener(tabs, opener));
    }

    [Fact]
    public void 连续打开的新标签按打开顺序向右排()
    {
        using var opener = new BrowserTab(1);
        using var first = new BrowserTab(2) { Opener = opener };
        using var second = new BrowserTab(3) { Opener = opener };
        using var following = new BrowserTab(4);
        var tabs = new List<BrowserTab> { opener, first, second, following };

        Assert.Equal(3, TabInsertion.RightOfOpener(tabs, opener));
    }

    [Fact]
    public void 不跳过不是由发起标签打开的标签()
    {
        using var opener = new BrowserTab(1);
        using var other = new BrowserTab(2);
        using var childOfOpener = new BrowserTab(3) { Opener = opener };
        var tabs = new List<BrowserTab> { opener, other, childOfOpener };

        Assert.Equal(1, TabInsertion.RightOfOpener(tabs, opener));
    }

    [Fact]
    public void 只跳过来源相同的连续标签()
    {
        using var opener = new BrowserTab(1);
        using var otherOpener = new BrowserTab(2);
        using var first = new BrowserTab(3) { Opener = opener };
        using var second = new BrowserTab(4) { Opener = opener };
        using var otherChild = new BrowserTab(5) { Opener = otherOpener };
        var tabs = new List<BrowserTab> { opener, first, second, otherChild };

        Assert.Equal(3, TabInsertion.RightOfOpener(tabs, opener));
    }
}
