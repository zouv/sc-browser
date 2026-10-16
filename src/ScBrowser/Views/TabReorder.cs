namespace ScBrowser.Views;

/// <summary>
/// 标签条拖拽重排的落点换算。
/// <para>
/// 判据是「鼠标指针落在哪个标签的槽位里」——不是「被拖标签的中心越过谁的中线」。
/// 前者与 Chrome 一致：鼠标一进入 B 的范围，B 就移到 A 原来的位置，
/// 而被拖的 A 继续跟着鼠标走，直到松手才落到指针所在的位置。
/// </para>
/// </summary>
public static class TabReorder
{
    /// <summary>一个标签在标签条上的水平占位。</summary>
    public readonly record struct Slot(double Left, double Right);

    /// <summary>拖拽一步的结果：应换到哪个槽位、以及被拖标签的渲染偏移。</summary>
    public readonly record struct DragStep(int TargetIndex, double RenderOffset);

    /// <summary>
    /// 推进拖拽一步。
    /// <para>
    /// <paramref name="RenderOffset"/> 是给被拖标签设的 <c>TranslateTransform.X</c>：
    /// 「视觉左边缘 − 落点槽位的左边缘」。换位时槽位几何不变、只是标签互换位置，
    /// 因此这个偏移能保证<b>换位那一瞬间视觉位置不变</b>——标签全程贴着鼠标，不会跳。
    /// （这正是之前的 bug：偏移里减的是元素自身相对父级的偏移，恒为 0，于是每次换位都跳一下。）
    /// </para>
    /// </summary>
    public static DragStep Step(
        IReadOnlyList<Slot> slots,
        int from,
        double grabOffset,
        double pointerX)
    {
        if (slots.Count == 0)
        {
            return new DragStep(0, pointerX - grabOffset);
        }

        var visualLeft = pointerX - grabOffset;
        var target = ResolveTargetIndex(slots, pointerX);
        return new DragStep(target, visualLeft - slots[target].Left);
    }

    /// <summary>
    /// 指针落在第几个槽位。指针在槽位之间的间隙里时归到它右边那个（符合「拖过去一点就换位」的直觉）；
    /// 越过最右端则归到最后一个。
    /// </summary>
    public static int ResolveTargetIndex(IReadOnlyList<Slot> slots, double pointerX)
    {
        if (slots.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            if (pointerX < slots[i].Right)
            {
                return i;
            }
        }

        return slots.Count - 1;
    }
}
