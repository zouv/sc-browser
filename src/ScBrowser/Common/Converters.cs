using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ScBrowser.Browser;

namespace ScBrowser.Common;

/// <summary>
/// 把 <see cref="SecurityState"/> 映射为地址栏左侧的安全图标字形。
/// 字形取自 Segoe Fluent Icons / Segoe MDL2 Assets（两套字体在此范围的码位一致）。
/// </summary>
public sealed class SecurityStateToGlyphConverter : IValueConverter
{
    // Segoe MDL2 Assets 码位。用 \u 转义书写，避免源码中出现不可见的私用区字符。
    private const string GlyphInfo = "";
    private const string GlyphLock = "";
    private const string GlyphUnlock = "";
    private const string GlyphWarning = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SecurityState state
            ? state switch
            {
                SecurityState.Secure => GlyphLock,
                SecurityState.Insecure => GlyphUnlock,
                SecurityState.Danger => GlyphWarning,
                _ => GlyphInfo,
            }
            : GlyphInfo;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>安全状态 → 图标颜色：安全为强调色，危险为红，其余为次要文字色。</summary>
public sealed class SecurityStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is SecurityState state
            ? state switch
            {
                SecurityState.Secure => "AccentBrush",
                SecurityState.Danger => "DangerBrush",
                _ => "IconBrush",
            }
            : "IconBrush";

        return Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
               ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反（用于「有标签才启用」之类的场景）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>非空字符串 → Visible（用于错误提示条的显隐）。</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 文本非空 <b>且</b> 外壳 chrome 可见 → Visible。
/// <para>
/// 错误条与状态栏平时靠绑定控制显隐；全屏时又必须收起来。不能直接给 Visibility 赋
/// <c>Collapsed</c> 再 <c>ClearValue</c>——那会把 XAML 里的绑定一起清掉，退出全屏后
/// 绑定失效、退回类型默认值 <c>Visible</c>，于是出现一条没有文字的红条。
/// 因此把「chrome 是否可见」做成窗口的依赖属性，与文本一起参与多值绑定。
/// </para>
/// </summary>
public sealed class TextAndChromeVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Length > 0 ? values[0] as string : null;
        var chromeVisible = values.Length > 1 && values[1] is true;
        return chromeVisible && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 安全状态 + 当前地址 → 安全图标的悬停提示。
/// <para>
/// 之所以还要地址：<see cref="SecurityState.Unknown"/> 有两种截然不同的成因——
/// 内部页面（<c>browser://</c>）与本地文件本来就不经过网络，而普通网页显示未知则意味着
/// 尚未收到安全事件。以前直接把枚举名（<c>Unknown</c>）当提示显示，用户只会看到一句莫名其妙的英文。
/// </para>
/// </summary>
public sealed class SecurityStateToTipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values.Length > 0 && values[0] is SecurityState s ? s : SecurityState.Unknown;
        var url = values.Length > 1 ? values[1] as string : null;
        return Describe(state, url);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>纯函数部分，可直接单测。</summary>
    public static string Describe(SecurityState state, string? url)
    {
        if (InternalPageRegistry.IsInternalPage(url))
        {
            return "内部页面：由 ScBrowser 自身提供，不经过网络";
        }

        if (url?.StartsWith("file://", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "本地文件：不经过网络";
        }

        return state switch
        {
            SecurityState.Secure => "连接是安全的（HTTPS）",
            SecurityState.Neutral => "此连接未加密（HTTP）",
            SecurityState.Insecure => "此连接有部分内容不安全",
            SecurityState.Danger => "此连接不安全，请勿在此输入敏感信息",
            _ => "连接安全性未知（尚未收到该页面的安全信息）",
        };
    }
}
