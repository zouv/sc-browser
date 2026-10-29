namespace ScBrowser.Views;

/// <summary>
/// 标签条拖拽的纯函数换算：换位落点、是否还在标签条里、让位动画的补偿量。
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

    /// <summary>
    /// 松手点是否仍算在标签条上。<paramref name="x"/>/<paramref name="y"/> 是相对标签条左上角的 DIP。
    /// <para>
    /// 垂直容差对齐 Chrome 的 <c>kVerticalDetachMagnetism = 15</c>px：往下（或往上）拖动是
    /// 「要分窗」的意图，需要明显越出标签条才算离开，避免手抖一两个像素就把窗口分出去。
    /// 水平方向超出标签条则立即算离开：只判断纵向的话，把标签横着拖到窗口外侧、
    /// 但高度仍与标签条齐平时，会被误判成「放回原处」而不分窗。
    /// </para>
    /// </summary>
    public static bool IsInStrip(
        double x,
        double y,
        double width,
        double height,
        double horizontalTolerance = 8,
        double verticalTolerance = 15) =>
        x >= -horizontalTolerance
        && x <= width + horizontalTolerance
        && y >= -verticalTolerance
        && y <= height + verticalTolerance;

    /// <summary>
    /// Chrome 式让位动画的补偿量（纯函数）：布局槽位是即时变化的（StackPanel 决定），
    /// 每个容器挂一个补偿 <c>TranslateTransform</c>，把视觉位置从旧槽位平滑滑到新槽位。
    /// <para>
    /// 连续性要求：旧视觉位置 = <c>oldPos + currentComp</c>，新视觉位置 = <c>newPos + 新补偿</c>，
    /// 两者必须相等——所以新补偿 = <c>currentComp + oldPos − newPos</c>。
    /// 换位中途再换位时把残余补偿叠进去即可，动画自然重定向，无需打断。
    /// </para>
    /// </summary>
    public static double SlideCompensation(double oldPos, double newPos, double currentComp) =>
        currentComp + oldPos - newPos;
}
