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

    /// <summary>DWMWCP_DONOTROUND——直角（最大化贴边时用）。</summary>
    private const int DwmcpDoNotRound = 1;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- 最大化尺寸钳制（见 ClampMaximizedToWorkArea）----

    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    /// <summary>
    /// 把「最大化」的尺寸钉到显示器工作区。
    /// <para>
    /// 为什么需要：自绘外壳（<c>WindowStyle=None</c> + <c>WindowChrome</c>）时，
    /// 系统仍按「有边框的窗口」计算最大化——把隐形缩放边框也算进去，窗口于是向外溢出屏幕
    /// 若干像素（本机实测 7px），靠近边缘的内容被裁掉：标签条两端、右侧的关闭按钮首当其冲。
    /// 在 <c>WM_GETMINMAXINFO</c> 里直接改用工作区尺寸即可，且天然适配多显示器与 DPI。
    /// </para>
    /// <para>
    /// 不把消息标记为已处理：WPF 自身也用这条消息落实 MinWidth/MinHeight，
    /// 而本钩子是在窗口内部钩子之后挂上的，改完结构体再放行不会互相覆盖。
    /// </para>
    /// </summary>
    public static void ClampMaximizedToWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return;
        }

        source.AddHook(MaximizeClampHook);
    }

    private static IntPtr MaximizeClampHook(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // 位置相对显示器左上角：多显示器拼接时工作区原点不是 (0,0)。
        bounds.MaxPosition = new Point
        {
            X = info.Work.Left - info.Monitor.Left,
            Y = info.Work.Top - info.Monitor.Top,
        };
        bounds.MaxSize = new Point
        {
            X = info.Work.Right - info.Work.Left,
            Y = info.Work.Bottom - info.Work.Top,
        };
        Marshal.StructureToPtr(bounds, lParam, fDeleteOld: true);

        return IntPtr.Zero;
    }

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

    /// <summary>
    /// 应用 Windows 11 的圆角窗口外观；旧系统上静默跳过。
    /// <paramref name="rounded"/> 为 <c>false</c> 时改回直角——最大化贴边时保留圆角
    /// 会在屏幕角落露出背景缝隙，系统自身也是直角。
    /// </summary>
    public static void ApplyRoundedCorners(Window window, bool rounded)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = rounded ? DwmcpRound : DwmcpDoNotRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
