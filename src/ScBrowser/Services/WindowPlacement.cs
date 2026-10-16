namespace ScBrowser.Services;

/// <summary>从设置里读回的窗口几何（<see cref="Left"/>/<see cref="Top"/> 为 <c>null</c> 表示交给系统居中）。</summary>
public readonly record struct RestoredWindow(double Width, double Height, double? Left, double? Top);

/// <summary>
/// 窗口几何的合法性判断与钳制。
/// <para>
/// 单独抽成纯函数是因为它极易静默出错：显示器换过之后，旧坐标可能把窗口丢到屏幕外——
/// 用户看到的现象是「应用启动了但找不到窗口」，很难联想到是配置里的陈旧坐标。
/// </para>
/// </summary>
public static class WindowPlacement
{
    /// <summary>与 <c>MainWindow</c> 的 <c>MinWidth</c> / <c>MinHeight</c> 保持一致。</summary>
    public const double MinWidth = 640;
    public const double MinHeight = 420;

    /// <summary>默认尺寸，与 <c>MainWindow</c> 的初始值一致。</summary>
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 820;

    /// <summary>至少要有这么多像素露在桌面内，窗口才抓得住（拖标题条 / 看得见）。</summary>
    private const double MinVisibleWidth = 48;
    private const double MinVisibleHeight = 24;

    /// <summary>
    /// 解析保存的窗口几何。
    /// <list type="bullet">
    /// <item>尺寸缺失/越界 → 用默认尺寸；尺寸大于虚拟桌面（换小屏）→ 钳到桌面大小。</item>
    /// <item>位置缺失或把窗口丢到桌面外 → 返回 <c>null</c> 位置，由 WPF 居中显示。</item>
    /// </list>
    /// </summary>
    public static RestoredWindow Resolve(
        double? left,
        double? top,
        double? width,
        double? height,
        double screenLeft,
        double screenTop,
        double screenWidth,
        double screenHeight)
    {
        var w = width is { } wv && IsFinite(wv) && wv >= MinWidth ? wv : DefaultWidth;
        var h = height is { } hv && IsFinite(hv) && hv >= MinHeight ? hv : DefaultHeight;

        if (screenWidth > 0)
        {
            w = Math.Min(w, screenWidth);
        }

        if (screenHeight > 0)
        {
            h = Math.Min(h, screenHeight);
        }

        if (left is not { } l || top is not { } t || !IsFinite(l) || !IsFinite(t))
        {
            return new RestoredWindow(w, h, null, null);
        }

        // 位置判据：窗口与虚拟桌面的交集必须够大，否则宁可居中。
        var visibleWidth = Math.Min(l + w, screenLeft + screenWidth) - Math.Max(l, screenLeft);
        var visibleHeight = Math.Min(t + h, screenTop + screenHeight) - Math.Max(t, screenTop);
        if (visibleWidth < MinVisibleWidth || visibleHeight < MinVisibleHeight)
        {
            return new RestoredWindow(w, h, null, null);
        }

        return new RestoredWindow(w, h, l, t);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
