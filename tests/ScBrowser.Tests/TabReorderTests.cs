using ScBrowser.Views;

namespace ScBrowser.Tests;

/// <summary>
/// 标签拖拽的落点换算：判据是「指针落在哪个槽位」，断言最终顺序而不是下标数字。
/// </summary>
public class TabReorderTests
{
    private static List<TabReorder.Slot> Slots(int count, double width = 100, double gap = 2) =>
        Enumerable.Range(0, count)
            .Select(i => new TabReorder.Slot(i * (width + gap), i * (width + gap) + width))
            .ToList();

    /// <summary>模拟一次拖拽：按当前布局算落点并换位。</summary>
    private static List<string> DragTo(List<string> tabs, int from, double pointerX)
    {
        var to = TabReorder.ResolveTargetIndex(Slots(tabs.Count), pointerX);
        var moved = tabs[from];
        tabs.RemoveAt(from);
        tabs.Insert(to, moved);
        return tabs;
    }

    [Fact]
    public void 指针进入第二个标签范围时它应让到第一位()
    {
        // A 被拖走，指针进入 B 的槽位（x=100..202 之间）→ B 移到 A 原来的位置
        Assert.Equal(["B", "A", "C", "D"], DragTo(["A", "B", "C", "D"], from: 0, pointerX: 150));
    }

    [Fact]
    public void 指针进入第三个标签范围时应继续右移()
    {
        Assert.Equal(["B", "C", "A", "D"], DragTo(["A", "B", "C", "D"], from: 0, pointerX: 260));
    }

    [Fact]
    public void 指针还在自己槽位内时不动()
    {
        Assert.Equal(["A", "B", "C", "D"], DragTo(["A", "B", "C", "D"], from: 0, pointerX: 40));
    }

    [Fact]
    public void 指针落进槽位间隙时归到右边那个标签()
    {
        // 间隙 100..102 → 归 A 之后、B 之前的位置（即 index 1）
        Assert.Equal(1, TabReorder.ResolveTargetIndex(Slots(3), 101));
    }

    [Fact]
    public void 指针拖到最右端之外应落到末位()
    {
        Assert.Equal(3, TabReorder.ResolveTargetIndex(Slots(4), 9999));
        Assert.Equal(["B", "C", "D", "A"], DragTo(["A", "B", "C", "D"], from: 0, pointerX: 9999));
    }

    [Fact]
    public void 指针拖到最左端之外应落到首位()
    {
        Assert.Equal(0, TabReorder.ResolveTargetIndex(Slots(4), -50));
    }

    [Fact]
    public void 从右往左拖时判据同样成立()
    {
        // D 拖到指针位于 B 的槽位（150）→ 落在 B 的位置
        Assert.Equal(["A", "D", "B", "C"], DragTo(["A", "B", "C", "D"], from: 3, pointerX: 150));
    }

    [Fact]
    public void 没有标签时返回零()
    {
        Assert.Equal(0, TabReorder.ResolveTargetIndex([], 100));
    }

    [Fact]
    public void 拖拽全程应贴着鼠标且换位瞬间不跳()
    {
        // 这就是用户报告过的 bug：走到换位判定点时标签会「跳一下」。
        // 判据：任一指针位置下，「落点槽位左边缘 + 渲染偏移」都必须等于指针位置减抓取偏移，
        // 也就是被拖标签的视觉左边缘始终贴着鼠标。
        var slots = Slots(4);
        const double grabOffset = 30;
        var from = 0;

        for (var pointer = grabOffset; pointer <= 520; pointer += 2)
        {
            var step = TabReorder.Step(slots, from, grabOffset, pointer);
            var visualLeft = pointer - grabOffset;

            Assert.Equal(visualLeft, slots[step.TargetIndex].Left + step.RenderOffset, 6);
            from = step.TargetIndex;
        }

        // 一路拖到底应落在末位
        Assert.Equal(3, from);
    }

    [Fact]
    public void 往左拖也应贴着鼠标()
    {
        var slots = Slots(4);
        const double grabOffset = 20;
        var from = 3;

        for (var pointer = 360.0; pointer >= 0; pointer -= 2)
        {
            var step = TabReorder.Step(slots, from, grabOffset, pointer);
            var visualLeft = pointer - grabOffset;

            Assert.Equal(visualLeft, slots[step.TargetIndex].Left + step.RenderOffset, 6);
            from = step.TargetIndex;
        }

        Assert.Equal(0, from);
    }

    [Fact]
    public void 标签条内的点应算在标签条里()
    {
        Assert.True(TabReorder.IsInStrip(50, 20, width: 500, height: 38));
        Assert.True(TabReorder.IsInStrip(0, 0, width: 500, height: 38));
        Assert.True(TabReorder.IsInStrip(500, 38, width: 500, height: 38));
    }

    [Fact]
    public void 标签条边缘的容差内仍算在标签条里()
    {
        Assert.True(TabReorder.IsInStrip(-8, -8, width: 500, height: 38));
        Assert.True(TabReorder.IsInStrip(508, 46, width: 500, height: 38));
    }

    [Fact]
    public void 拖出标签条上下边界应算离开()
    {
        Assert.False(TabReorder.IsInStrip(50, 47, width: 500, height: 38));
        Assert.False(TabReorder.IsInStrip(50, -9, width: 500, height: 38));
    }

    [Fact]
    public void 水平拖出窗口也应算离开标签条()
    {
        // 回归：以前只看 Y。把标签横着拖出窗口、高度仍与标签条齐平时，
        // 会被误判成「还在标签条里」，松手后既不分窗也不合并。
        Assert.False(TabReorder.IsInStrip(509, 20, width: 500, height: 38));
        Assert.False(TabReorder.IsInStrip(-9, 20, width: 500, height: 38));
    }
}
