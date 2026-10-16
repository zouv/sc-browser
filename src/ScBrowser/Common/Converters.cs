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
