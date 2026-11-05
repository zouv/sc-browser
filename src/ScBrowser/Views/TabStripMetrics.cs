namespace ScBrowser.Views;

/// <summary>
/// 标签条宽度换算（对齐 Chrome）：标签等宽；放不下时压缩宽度，而不是让标签条横向滚动。
/// <para>
/// 做成纯函数是为了能用单测机械验证「总宽不超过视口」这个不变量——
/// 它一旦被破坏，ScrollViewer 就会重新出现横向滚动，回到了本次修改前的行为。
/// </para>
/// </summary>
public static class TabStripMetrics
{
    /// <summary>标签最小宽度。低于这个值连图标都放不下，再挤就没有可读性了。</summary>
    public const double MinTabWidth = 28;

    /// <summary>标签最大宽度（与 <c>BrowserTabButtonStyle</c> 的 MaxWidth 保持一致）。</summary>
    public const double MaxTabWidth = 220;

    /// <summary>标签之间的水平间距（即按钮右边距，见 <c>BrowserTabButtonStyle</c> 的 Margin）。</summary>
    public const double TabSpacing = 2;

    /// <summary>
    /// 按标签条可用宽度与标签数量算出每个标签的宽度。
    /// <para>
    /// 先向下取整再减间距，保证 <c>(宽度 + 间距) × 数量 ≤ 可用宽度</c>，
    /// 不会因为亚像素除法把总宽撑出视口、重新触发横向滚动。
    /// </para>
    /// </summary>
    public static double ResolveTabWidth(double availableWidth, int tabCount)
    {
        if (tabCount <= 0 || availableWidth <= 0)
        {
            return MaxTabWidth;
        }

        var width = Math.Floor(availableWidth / tabCount) - TabSpacing;
        return Math.Clamp(width, MinTabWidth, MaxTabWidth);
    }
}
