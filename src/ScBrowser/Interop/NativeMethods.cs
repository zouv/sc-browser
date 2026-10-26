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
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// 把窗口铺满它所在的整块显示器（含任务栏区域）——全屏用。
    /// <para>
    /// 用 Win32 而不是 WPF 的 <c>WindowState</c>：最大化只会铺满工作区（扣掉任务栏），
    /// 而 Win32 坐标是物理像素，多显示器 / 不同缩放下也比自己换算 DIP 更可靠。
    /// 退出全屏时由调用方把 Left/Top/Width/Height 重新设回去。
    /// </para>
    /// </summary>
    public static void FillMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }


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

    // ---- 单实例转发：把前台权交给已在运行的实例 ----

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>当前前台窗口的句柄；用于区分「切到别的程序」与「焦点进了自己的浮动窗口」。</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>当前前台窗口所属进程；无法取得时返回 0。用于判断焦点是否还在本应用内。</summary>
    public static uint GetForegroundProcessId()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    /// <summary>把指定窗口切到前台（同一进程内调用；用于让浮动查找条真正拿到键盘焦点）。</summary>
    public static void BringToForeground(IntPtr handle) => _ = SetForegroundWindow(handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>
    /// 允许任意进程（实际就是已在运行的那个实例）把自己的窗口切到前台。
    /// Windows 只允许「当前前台进程」抢前台；二次启动的进程此刻正是前台进程，
    /// 转发参数之前先让出这个权利，否则已有实例只会在任务栏闪烁。
    /// </summary>
    public static void AllowAnyProcessToSetForeground() => _ = AllowSetForegroundWindow(AsfwAny);
}
