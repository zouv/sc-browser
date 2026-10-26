using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：标签条交互：中键关闭 / 拖拽重排（含跨窗口合并）/ 右键菜单 / 标签组。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
    // --------------------------------------------------------------------- //
    // 标签条交互：中键关闭 / 左键拖拽重排 / 右键菜单
    //
    // 拖拽没有用 WPF 的 DragDrop（它接管鼠标、显示系统拖拽影像、只在落点才换位），
    // 而是自己捕获鼠标：被拖标签跟着鼠标**横向**移动，其他标签实时让位——即 Chrome 的手感。
    // 位置补偿是关键：把渲染偏移设成「视觉左边缘 − 布局左边缘」，这样挪位后标签不会跳。
    // --------------------------------------------------------------------- //

    /// <summary>超过这个位移（逻辑像素）才算拖拽，否则「点击」会被误判成拖拽。</summary>
    private const double DragThreshold = 4;

    /// <summary>拖拽中的标签抬到最上层，避免被相邻标签盖住。</summary>
    private const int DraggingZIndex = 100;

    /// <summary>标签条容器（ItemsControl 的 ItemsPanel 实例）；延迟解析，因为模板要等首帧才应用。</summary>
    private Panel? _tabStripPanel;

    private BrowserTab? _dragTab;
    private FrameworkElement? _dragElement;
    private TranslateTransform? _dragTransform;
    private Point _dragStartPoint;
    private double _dragGrabOffset;
    private bool _isDragging;

    /// <summary>鼠标中键点击标签 → 关闭（与 Chrome 一致：中键永远是「关掉它」）。</summary>
    private void TabButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab)
        {
            e.Handled = true;
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is not { } tab || sender is not FrameworkElement element || ResolveTabStripPanel() is not { } panel)
        {
            return;
        }

        // 只有一个标签：拖它等于拖窗口（Chrome 行为）。标签条本身是标题栏命中区，
        // 但按钮标了 IsHitTestVisibleInChrome，系统不会自动把它当标题栏，所以这里手动 DragMove。
        if (_tabs.Tabs.Count == 1)
        {
            e.Handled = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经松开等边界情况：忽略。
            }

            return;
        }

        // 先只记录，不捕获鼠标——否则普通点击也会走拖拽路径、标签激活不了。
        _dragTab = tab;
        _dragElement = element;
        _dragStartPoint = e.GetPosition(panel);

        // 抓取偏移 = 指针相对「标签当前槽位左边缘」的距离。必须用槽位几何：
        // 元素本身在 DataTemplate 里，对它调 VisualTreeHelper.GetOffset 得到的是
        // 「相对 ContentPresenter」的偏移（恒为 0），不是标签条坐标——用它算补偿会跳位。
        var index = _tabs.Tabs.IndexOf(tab);
        var slots = EnumerateTabSlots();
        _dragGrabOffset = _dragStartPoint.X - (index >= 0 && index < slots.Count ? slots[index].Left : 0);

        _isDragging = false;
    }

    private void TabButton_PreviewMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    private void OnWindowMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    /// <summary>拖拽候选 → 正式拖拽 → 跟随鼠标。</summary>
    private void ContinueDrag(MouseEventArgs e)
    {
        if (_dragTab is null || _dragElement is null || ResolveTabStripPanel() is not { } panel
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(panel);

        if (!_isDragging)
        {
            // 横竖都要看：只判断横向的话，往下拖（拖出去合并/分窗）永远起不了拖拽。
            if (Math.Abs(now.X - _dragStartPoint.X) < DragThreshold
                && Math.Abs(now.Y - _dragStartPoint.Y) < DragThreshold)
            {
                return;
            }

            BeginDrag();
        }

        UpdateDragPosition(now);
    }

    /// <summary>
    /// 结束拖拽。拖拽过程中必须吞掉这次鼠标抬起，否则按钮会把它当成一次点击、
    /// 顺手把标签激活——那样「拖完就跳走」会很突兀。
    /// </summary>
    private void TabButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishDrag(e);

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            FinishDrag(e);
        }
    }

    private void FinishDrag(MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            _dragTab = null;
            _dragElement = null;
            return;
        }

        var dragged = _dragTab;
        var stillInStrip = IsInTabStrip(e.GetPosition(TabItems));

        // 屏幕坐标用「窗口 Left/Top + 窗口内偏移」算：拖到窗口外时偏移可以是负数，
        // 仍然得到正确的屏幕 DIP，且不必处理 PointToScreen 的物理像素/DPI 换算。
        var local = e.GetPosition(this);
        var screenPoint = new Point(Left + local.X, Top + local.Y);

        EndDrag();
        e.Handled = true;

        if (dragged is null)
        {
            return;
        }

        // 1) 松手时落在别的窗口的标签条上 → 合并进去（插到指针所在的槽位）。
        var target = _windows.FindWindowAtTabStrip(screenPoint, this);
        Serilog.Log.Debug(
            "拖拽结束：标签 {TabId}，落点 ({X},{Y})，本窗口标签条内={InStrip}，目标窗口={Target}",
            dragged.Id, screenPoint.X, screenPoint.Y, stillInStrip, target?.Title ?? "（无）");

        if (target is not null && target.IsIncognito == IsIncognito)
        {
            _windows.MoveTabToWindow(dragged, target, target.TabIndexAtWindowX(screenPoint.X - target.Left));
            return;
        }

        // 2) 否则只要离开了本窗口的标签条，就在指针位置开新窗口把标签迁过去。
        //    （无痕与普通窗口之间不合并，所以上面不匹配时也走这条。）
        if (!stillInStrip)
        {
            _windows.MoveTabToNewWindow(dragged, screenPoint);
        }
    }

    /// <summary>本窗口是否无痕（拖拽合并时用来拒绝跨隐私会话迁移）。</summary>
    public bool IsIncognito => _viewModel.IsIncognito;

    /// <summary>
    /// 屏幕点（DIP）是否落在本窗口的标签条上——跨窗口合并的判定区。
    /// 用窗口几何而不是 HitTest：拖动时鼠标被原窗口捕获，命中测试拿不到别的窗口。
    /// </summary>
    public bool ContainsTabStripPoint(Point screenPoint)
    {
        const double tolerance = 8;
        return IsVisible
               && screenPoint.X >= Left - tolerance
               && screenPoint.X <= Left + Width + tolerance
               && screenPoint.Y >= Top - tolerance
               && screenPoint.Y <= Top + TabStrip.ActualHeight + tolerance;
    }

    /// <summary>指针（相对本窗口的 X）落在标签条的哪个插入位置。</summary>
    public int TabIndexAtWindowX(double windowX)
    {
        var panel = ResolveTabStripPanel();
        if (panel is null)
        {
            return _tabs.Tabs.Count;
        }

        var origin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
        var x = windowX - origin.X;
        var slots = EnumerateTabSlots();

        for (var i = 0; i < slots.Count; i++)
        {
            if (x < (slots[i].Left + slots[i].Right) / 2)
            {
                return i;
            }
        }

        return slots.Count;
    }

    /// <summary>指针是否仍算「在标签条里」（上下留 8px 容差，避免手抖就拖出窗口）。</summary>
    private static bool IsInTabStrip(Point point, double height) =>
        point.Y >= -8 && point.Y <= height + 8;

    private bool IsInTabStrip(Point point) =>
        IsInTabStrip(point, TabItems.ActualHeight);
}
