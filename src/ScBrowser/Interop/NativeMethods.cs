using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScBrowser.Interop;

/// <summary>
/// 少量必要的 Win32 互操作。集中在此，避免 P/Invoke 声明散落各处。
/// </summary>
internal static class NativeMethods
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Windows 10 1809+ / Windows 11）。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（Windows 11）。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND——圆角窗口。</summary>
    private const int DwmcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 让系统按深色模式绘制窗口边框/标题栏（避免深色主题下出现刺眼的白边）。
    /// 在 Windows 10 1809 以下无效，此时静默跳过。
    /// </summary>
    public static void ApplyDarkMode(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var flag = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref flag, sizeof(int));
    }

    /// <summary>应用 Windows 11 的圆角窗口外观；旧系统上静默跳过。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
