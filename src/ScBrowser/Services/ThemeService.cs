using System.Windows;
using Microsoft.Win32;
using ScBrowser.Interop;

namespace ScBrowser.Services;

/// <summary>一次已生效的主题：原始取值（System/Light/Dark）与解析后的深浅。</summary>
public readonly record struct AppliedTheme(string Name, bool IsDark);

/// <summary>
/// 主题切换：把 <c>App.Resources</c> 里的颜色字典整体换成浅色/深色版本。
/// <para>
/// 之所以能「热换肤」：控件样式一律用 <c>DynamicResource</c> 引用颜色令牌，
/// 替换字典后 WPF 会重新解析，无需重建任何控件。
/// </para>
/// </summary>
public sealed class ThemeService
{
    private const string LightDictionary = "Themes/Theme.Light.xaml";
    private const string DarkDictionary = "Themes/Theme.Dark.xaml";

    /// <summary>当前是否为深色（供内部页面推送主题用）。</summary>
    public bool IsDark { get; private set; }

    /// <summary>
    /// 主题已应用（携带原始取值与解析后的深浅）。
    /// <para>
    /// 外壳之外的地方（例如网页内容的颜色方案）靠它跟着一起变——订阅放在组合根
    /// <c>App</c> 里，这样「主题生效」只有 <see cref="Apply"/> 一个入口，
    /// 不会因为新增调用点而漏同步。
    /// </para>
    /// </summary>
    public event EventHandler<AppliedTheme>? Applied;

    /// <summary>应用主题；<paramref name="theme"/> 取 <c>System</c> / <c>Light</c> / <c>Dark</c>。</summary>
    public void Apply(string theme)
    {
        var dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDark(),
        };

        IsDark = dark;
        SwapDictionary(dark ? DarkDictionary : LightDictionary);

        // 让系统绘制的窗口边框/标题栏也跟着变，避免深色下出现白边。
        foreach (Window window in Application.Current.Windows)
        {
            NativeMethods.ApplyDarkMode(window, dark);
        }

        Applied?.Invoke(this, new AppliedTheme(theme, dark));
    }

    private static void SwapDictionary(string source)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative),
        };

        // 颜色字典固定排在第一位（见 App.xaml），直接原位替换。
        if (merged.Count == 0)
        {
            merged.Add(replacement);
        }
        else
        {
            merged[0] = replacement;
        }
    }

    /// <summary>读取系统「应用模式」偏好：AppsUseLightTheme == 0 表示深色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            // 读注册表失败就当作浅色，不影响可用性。
            return false;
        }
    }
}
