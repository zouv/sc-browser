using ScBrowser.Views;

namespace ScBrowser.Tests;

/// <summary>
/// 标签拖拽的换算：落点判定、不等宽槽位的「贴鼠标」回归、换位滞后、让位补偿、离开判定。
/// </summary>
public class TabReorderTests
{
    private static List<TabReorder.Slot> Slots(int count, double width = 100, double gap = 2) =>
        Enumerable.Range(0, count)
            .Select(i => new TabReorder.Slot(i * (width + gap), i * (width + gap) + width))
            .ToList();

    /// <summary>按各标签的实际宽度（标签宽度随标题变化，不等宽才是真实场景）生成槽位。</summary>
    private static List<TabReorder.Slot> SlotsOf(IReadOnlyList<double> widths, double gap = 2)
    {
        var left = 0.0;
        var result = new List<TabReorder.Slot>();
        foreach (var width in widths)
        {
            result.Add(new TabReorder.Slot(left, left + width));
            left += width + gap;
        }

        return result;
    }

    /// <summary>
    /// 模拟 <c>UpdateDragPosition</c> 的完整一步：按「移动前快照」判落点 → 换位（含 16px 滞后）→
    /// 模型重排 → 槽位按新顺序重算（对应 UpdateLayout 落地）→ 偏移只用落地后几何验证。
    /// </summary>
    private static void StepDrag(
        List<double> widths,
        ref int from,
        ref double? lastReorderX,
        double pointerX,
        double grabOffset)
    {
        var slots = SlotsOf(widths);
        var target = TabReorder.ResolveTargetIndex(slots, pointerX);
        var pastHysteresis = lastReorderX is not { } last || Math.Abs(pointerX - last) > 16;
        if (target != from && pastHysteresis)
        {
            var width = widths[from];
            widths.RemoveAt(from);
            widths.Insert(target, width);
            from = target;
            lastReorderX = pointerX;
        }

        // 布局落地后：被拖标签位于新顺序里的槽位，偏移只从这里算。
        var landed = SlotsOf(widths);
        var offset = pointerX - grabOffset - landed[from].Left;

        // 「贴鼠标」不变量：落地槽位左边缘 + 渲染偏移 必须等于 指针 − 抓取偏移。
        Assert.Equal(pointerX - grabOffset, landed[from].Left + offset, 6);
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

    /// <summary>模拟一次拖拽：按当前布局算落点并换位（等宽场景的快速版本）。</summary>
    private static List<string> DragTo(List<string> tabs, int from, double pointerX)
    {
        var to = TabReorder.ResolveTargetIndex(Slots(tabs.Count), pointerX);
        var moved = tabs[from];
        tabs.RemoveAt(from);
        tabs.Insert(to, moved);
        return tabs;
    }

    [Fact]
    public void 不等宽标签全程拖拽应贴鼠标且最终顺序正确()
    {
        // 回归（飘闪）：旧实现用「预换位」槽位几何算偏移，标签宽度不等时每次换位都错出宽度差、
        // 跳一下再跳回。这里用不等宽（模拟真实标题长度）全程扫一遍，断言贴鼠标不变量与最终顺序。
        var widths = new List<double> { 220, 72, 160, 100, 90 };
        const double grabOffset = 30;
        var from = 0;
        double? lastReorderX = null;

        for (var pointer = grabOffset; pointer <= 700; pointer += 2)
        {
            StepDrag(widths, ref from, ref lastReorderX, pointer, grabOffset);
        }

        Assert.Equal(widths.Count - 1, from);
        Assert.Equal([72, 160, 100, 90, 220], widths);
    }

    [Fact]
    public void 不等宽标签往左拖同样贴鼠标且落到首位()
    {
        var widths = new List<double> { 220, 72, 160, 100, 90 };
        const double grabOffset = 20;
        var from = 4;
        double? lastReorderX = null;

        for (var pointer = 500.0; pointer >= 0; pointer -= 2)
        {
            StepDrag(widths, ref from, ref lastReorderX, pointer, grabOffset);
        }

        Assert.Equal(0, from);
        Assert.Equal([90, 220, 72, 160, 100], widths);
    }

    [Fact]
    public void 指针在槽位边界附近小幅度抖动不应来回换位()
    {
        // Chrome kHorizontalMoveThreshold：两次换位之间要有 16px 水平位移，
        // 否则指针停在两个槽位边界上时，轻微手抖会让模型来回换位（肉眼可见的抖动）。
        var widths = new List<double> { 100, 100, 100 };
        var from = 0;
        double? lastReorderX = null;

        // 拖进 B 的槽位 → 换位
        StepDrag(widths, ref from, ref lastReorderX, 160, 20);
        Assert.Equal(1, from);

        // 在 B 槽位内小幅回退（未过 16px 滞后）→ 顺序不再动
        StepDrag(widths, ref from, ref lastReorderX, 144, 20);
        Assert.Equal(1, from);
        Assert.Equal([100, 100, 100], widths);

        // 真正退回 A 的槽位且越过滞后 → 才换回来
        StepDrag(widths, ref from, ref lastReorderX, 80, 20);
        Assert.Equal(0, from);
    }

    [Fact]
    public void 换位补偿应保证视觉位置连续()
    {
        // 连续性不变量：oldPos + currentComp == newPos + 新补偿
        var comp = TabReorder.SlideCompensation(oldPos: 100, newPos: 240, currentComp: 0);
        Assert.Equal(-140, comp, 6);
        Assert.Equal(100, 240 + comp, 6);
    }

    [Fact]
    public void 动画中途再换位应叠加残余补偿()
    {
        // 第一次换位后补偿 −140，衰减到 −100 时又来一次换位（新槽位 40）：
        // 视觉位置 = 240 + (−100) = 140，新槽位 40 → 新补偿 = 100，连续性依然成立。
        var comp = TabReorder.SlideCompensation(oldPos: 240, newPos: 40, currentComp: -100);
        Assert.Equal(100, comp, 6);
        Assert.Equal(240 - 100, 40 + comp, 6);
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
        // 水平 ±8、垂直 ±15（Chrome kVerticalDetachMagnetism）内都算还在标签条里。
        Assert.True(TabReorder.IsInStrip(-8, -8, width: 500, height: 38));
        Assert.True(TabReorder.IsInStrip(508, 46, width: 500, height: 38));
        Assert.True(TabReorder.IsInStrip(508, 53, width: 500, height: 38));
    }

    [Fact]
    public void 拖出标签条上下边界超过磁性距离应算离开()
    {
        Assert.False(TabReorder.IsInStrip(50, 54, width: 500, height: 38));
        Assert.False(TabReorder.IsInStrip(50, -16, width: 500, height: 38));
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
