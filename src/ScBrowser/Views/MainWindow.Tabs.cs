using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScBrowser.Browser;
using ScBrowser.Interop;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：标签条交互：中键关闭 / 拖拽重排 / 拖出分窗 / 跨窗口合并 / 右键菜单 / 标签组。
/// <para>纯 partial 拆分，字段与构造函数仍在 <c>MainWindow.xaml.cs</c>。</para>
/// </summary>
public partial class MainWindow
{
    // --------------------------------------------------------------------- //
    // 标签条交互：中键关闭 / 左键拖拽 / 右键菜单
    //
    // 拖拽没有用 WPF 的 DragDrop（它接管鼠标、显示系统拖拽影像、只在落点才换位），
    // 而是自己捕获鼠标。拖拽是**实时状态机**，鼠标不松手也能连续分窗 / 合并：
    //
    //   InStrip  —— 指针在标签条内：被拖标签跟着鼠标横移，其他标签实时让位（Chrome 手感）。
    //   Detached —— 指针离开标签条：立刻开一个新窗口，标签落进去，窗口跟着鼠标走；
    //               再进入别的窗口标签条就立即合并，合并后回到 InStrip，可以继续拖出。
    //
    // 接管（hand-off）是这套逻辑的关键：标签迁到另一个窗口后，控件会被重新生成，
    // 必须等新窗口布局完成、标签按钮出现，才能在新元素上重新 CaptureMouse，
    // 否则后续 MouseMove / MouseUp 会丢。见 TakeOverStripDrag / TakeOverDetachedDrag。
    //
    // 渲染位置（对齐 Chromium 的 TabDragController）：
    // - 被拖标签的偏移**只从「UpdateLayout 落地后的真实左边缘 + 指针位置」算**，绝不读
    //   「预换位」的槽位几何——标签宽度不等时读预换位几何，每次换位都会跳一下（飘闪根因）。
    // - 其他标签的让位由 TabStripSlideAnimator 补偿变换做平滑滑动；换位带 16px 水平滞后
    //   （Chrome kHorizontalMoveThreshold），防指针停在槽位边界上时来回抖动。
    // - 标签溢出时，被拖标签视觉边缘越过滚动视口即自动滚动（Chrome TabDragScrollHandler 同款判据）。
    // --------------------------------------------------------------------- //

    /// <summary>超过这个位移（逻辑像素）才算拖拽，否则「点击」会被误判成拖拽。</summary>
    private const double DragThreshold = 4;

    /// <summary>两次换位之间的最小水平位移（对齐 Chrome <c>kHorizontalMoveThreshold = 16</c> DIP）。</summary>
    private const double ReorderHysteresis = 16;

    /// <summary>拖拽中的标签抬到最上层，避免被相邻标签盖住。</summary>
    private const int DraggingZIndex = 100;

    /// <summary>分窗时让指针落在新窗口标签条顶部的距离（标签条高 38）。</summary>
    private const double DetachedStripAnchorY = 20;

    /// <summary>拖拽处于哪个阶段。</summary>
    private enum DragPhase
    {
        /// <summary>标签还在某个窗口的标签条里（可能正在重排）。</summary>
        InStrip,

        /// <summary>标签已经分到独立窗口；窗口跟着鼠标走，等它再进入别的标签条。</summary>
        Detached,
    }

    /// <summary>标签条容器（ItemsControl 的 ItemsPanel 实例）；延迟解析，因为模板要等首帧才应用。</summary>
    private Panel? _tabStripPanel;

    /// <summary>让位动画（其他标签滑向新槽位 / 松手落回）的视图层实现。</summary>
    private readonly TabStripSlideAnimator _slide = new();

    /// <summary>拖拽边缘自动滚动的定时器：被拖标签视觉边缘越过视口时持续滚动（16ms 步进）。</summary>
    private readonly DispatcherTimer _dragScrollTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };

    private BrowserTab? _dragTab;
    private FrameworkElement? _dragElement;
    private TranslateTransform? _dragTransform;
    private Point _dragStartPoint;
    private double _dragGrabOffset;
    private bool _isDragging;
    private DragPhase _dragPhase = DragPhase.InStrip;

    /// <summary>上次换位时的指针 X（panel 坐标）；滞后判定用，见 <see cref="ReorderHysteresis"/>。</summary>
    private double? _lastReorderX;

    /// <summary>
    /// 已经发起「交接」（分窗 / 合并）。交接后标签已不属于本窗口，必须拦住后续重复的
    /// MouseMove（预览 + 冒泡各一次）再次分窗——否则一次拖出会建出两个窗口。
    /// </summary>
    private bool _handOffPending;

    /// <summary>Detached 阶段：指针在窗口内的锚点；窗口按它跟随鼠标，指针始终停在标签条上。</summary>
    private Point _detachedAnchor;

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

        // 单标签窗口也走同一套拖拽：按住唯一的标签拖出去要能分出 / 合并到新窗口。
        // 移动窗口仍可按住标签条空白处（WindowChrome 的标题栏区域）——那里不是标签按钮。

        // 先只记录，不捕获鼠标——否则普通点击也会走拖拽路径、标签激活不了。
        _dragTab = tab;
        _dragElement = element;
        _dragStartPoint = e.GetPosition(panel);
        _dragPhase = DragPhase.InStrip;
        _handOffPending = false;

        // 抓取偏移 = 指针相对「标签当前槽位左边缘」的距离。必须用槽位几何（panel 子元素
        // 的偏移）：元素本身是 DataTemplate 里的 Button，对它调 VisualTreeHelper.GetOffset
        // 得到的是相对 ContentPresenter 的偏移（恒约 0），不是标签条坐标——用它算会飞。
        var index = _tabs.Tabs.IndexOf(tab);
        var slots = EnumerateTabSlots();
        _dragGrabOffset = _dragStartPoint.X - (index >= 0 && index < slots.Count ? slots[index].Left : 0);

        _isDragging = false;
    }

    private void TabButton_PreviewMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    private void OnWindowMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    /// <summary>拖拽候选 → 正式拖拽 → 实时分窗 / 合并 / 跟随鼠标。</summary>
    private void ContinueDrag(MouseEventArgs e)
    {
        if (_handOffPending || _dragTab is null || _dragElement is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // Detached：标签已在独立窗口里，窗口跟着鼠标走；指针进入别的标签条就立刻合并。
        if (_dragPhase == DragPhase.Detached)
        {
            FollowDetachedWindow(e);
            MergeFromDetachedIfNeeded(e);
            return;
        }

        if (ResolveTabStripPanel() is not { } panel)
        {
            return;
        }

        if (!_isDragging)
        {
            var now = e.GetPosition(panel);

            // 横竖都要看：只判断横向的话，往下拖（拖出去合并/分窗）永远起不了拖拽。
            if (Math.Abs(now.X - _dragStartPoint.X) < DragThreshold
                && Math.Abs(now.Y - _dragStartPoint.Y) < DragThreshold)
            {
                return;
            }

            BeginDrag();
        }

        ProcessStripDrag(e, panel);
    }

    /// <summary>
    /// 还在标签条里的一个移动步：没离开就重排；离开了就看落点是不是别的窗口的标签条
    /// （是 → 立即合并，否 → 立即分窗）。
    /// </summary>
    private void ProcessStripDrag(MouseEventArgs e, Panel panel)
    {
        var pointerInWindow = e.GetPosition(this);

        if (IsInTabStrip(e.GetPosition(TabStrip)))
        {
            // 先做边缘自动滚动：滚动会改变 panel 坐标系的映射（内容在指针下滑动），
            // 所以滚完按滚动增量修正指针在 panel 里的位置，再交给重排换算——被拖标签的
            // 偏移会随滚动自洽（panel 坐标 + 偏移 − 滚动量 = 窗口坐标，恒贴鼠标）。
            var scrolled = ScrollStripTowardsElement();
            var pointerInPanel = e.GetPosition(panel);
            if (Math.Abs(scrolled) > 0.01)
            {
                pointerInPanel.X += scrolled;
            }

            UpdateDragPosition(pointerInPanel);
            return;
        }

        // 跨窗口命中测试用**物理屏幕坐标**：PointToScreen / PointFromScreen 会按各窗口
        // 自己的 DPI 换算；直接拿 DIP 相加，在 125%/150% 缩放或多屏不同 DPI 下会偏。
        var pointerScreen = PointToScreen(pointerInWindow);
        var target = _windows.FindWindowAtTabStrip(pointerScreen, this);
        if (target is not null && target.IsIncognito == IsIncognito
            && target.TabIndexAtScreenPoint(pointerScreen) is { } insertAt)
        {
            HandOffToWindow(target, insertAt, pointerScreen);
            return;
        }

        DetachToNewWindow(pointerInWindow);
    }

    /// <summary>标签拖进另一个窗口的标签条：立即合并，并把拖拽状态交接给目标窗口。</summary>
    private void HandOffToWindow(MainWindow target, int insertAt, Point pointerScreen)
    {
        if (_handOffPending || _dragTab is not { } tab)
        {
            return;
        }

        _handOffPending = true;
        var grabOffset = _dragGrabOffset;
        Serilog.Log.Debug(
            "拖拽合并：窗口 #{Source} 标签 {TabId} → 窗口 #{Target}「{Title}」，位置 {Index}",
            GetHashCode(), tab.Id, target.GetHashCode(), target.Title, insertAt);

        EndDrag();
        _windows.MoveTabToWindow(
            tab,
            target,
            insertAt,
            window => window.TakeOverStripDrag(tab, grabOffset, pointerScreen));
    }

    /// <summary>标签拖出标签条：立即分窗，新窗口落在指针处并继续跟随鼠标。</summary>
    private void DetachToNewWindow(Point pointerInWindow)
    {
        if (_handOffPending || _dragTab is not { } tab)
        {
            return;
        }

        _handOffPending = true;
        var grabOffset = _dragGrabOffset;
        var location = NewWindowLocationAround(pointerInWindow, grabOffset);
        Serilog.Log.Debug(
            "拖拽分窗：窗口 #{Source} 标签 {TabId}，phase={Phase} → 新窗口 ({X},{Y})",
            GetHashCode(), tab.Id, _dragPhase, location.X, location.Y);

        EndDrag();
        _windows.MoveTabToNewWindow(
            tab,
            location,
            window => window.TakeOverDetachedDrag(tab, grabOffset));
    }

    /// <summary>
    /// 让新窗口的标签条正好出现在指针下方：指针到标签左边缘的距离保持为抓取偏移，
    /// 这样分窗瞬间标签不会从鼠标下跳开（Show 之后 TakeOverDetachedDrag 还会按实际布局再校正一次）。
    /// </summary>
    private Point NewWindowLocationAround(Point pointerInWindow, double grabOffset)
    {
        return new Point(
            Math.Max(SystemParameters.VirtualScreenLeft, Left + pointerInWindow.X - grabOffset - TabLeftInWindow()),
            Math.Max(SystemParameters.VirtualScreenTop, Top + pointerInWindow.Y - DetachedStripAnchorY));
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

        var phase = _dragPhase;
        var dragged = _dragTab;
        var grabOffset = _dragGrabOffset;
        var element = _dragElement;
        var dropOffset = _dragTransform?.X ?? 0;
        var stillInStrip = IsInTabStrip(e.GetPosition(TabStrip));
        var pointerInWindow = e.GetPosition(this);
        var pointerScreen = PointToScreen(pointerInWindow);

        EndDrag();
        e.Handled = true;

        if (dragged is null)
        {
            return;
        }

        // 松手兜底：移动过程中已经实时处理过分窗 / 合并；只有「刚越过边界就松手」
        // （还没产生下一次 MouseMove）才需要在这里补一次。
        // 注意：还在本窗口标签条里松手时不要找别的窗口——窗口重叠时会误判成合并。
        if (phase == DragPhase.Detached || !stillInStrip)
        {
            var target = _windows.FindWindowAtTabStrip(pointerScreen, this);
            if (target is not null && target.IsIncognito == IsIncognito
                && target.TabIndexAtScreenPoint(pointerScreen) is { } insertAt)
            {
                _windows.MoveTabToWindow(dragged, target, insertAt);
                return;
            }
        }

        if (phase == DragPhase.InStrip && !stillInStrip)
        {
            _windows.MoveTabToNewWindow(dragged, NewWindowLocationAround(pointerInWindow, grabOffset));
            return;
        }

        // 条内松手：Chrome 式落回——把拖拽结束时的偏移当补偿衰减到 0，标签从鼠标下滑回槽位。
        // （EndDrag 已摘掉变换，这里用保存的偏移重新挂一个，衰减完 animator 会自己摘掉。）
        if (element is not null)
        {
            _slide.BeginGlide(element, dropOffset);
        }
    }

    /// <summary>
    /// 从别的窗口接管「标签条内」的拖拽（标签已经 Adopt 到本窗口）。
    /// <paramref name="screenPoint"/> 是物理屏幕点，用于把标签立即贴回指针位置。
    /// </summary>
    public void TakeOverStripDrag(BrowserTab tab, double grabOffset, Point screenPoint)
    {
        if (!BeginTransferredDrag(tab, grabOffset))
        {
            return;
        }

        _dragPhase = DragPhase.InStrip;
        // 目标窗口可能还有落回动画在跑（例如刚放下一个标签又立刻接管新的）：先还权再接管。
        _slide.CancelGlide(_dragElement!);
        _dragTransform = new TranslateTransform();
        _dragElement!.RenderTransform = _dragTransform;
        Panel.SetZIndex(_dragElement, DraggingZIndex);
        _dragElement.Opacity = 0.9;
        _dragScrollTimer.Start();

        if (!_dragElement.CaptureMouse())
        {
            Serilog.Log.Debug("接管拖拽(标签条)失败：鼠标捕获被拒绝，窗口 #{Window} 标签 {TabId}", GetHashCode(), tab.Id);
            EndDrag();
            return;
        }

        Serilog.Log.Debug("接管拖拽(标签条)：窗口 #{Window} 标签 {TabId}", GetHashCode(), tab.Id);

        if (ResolveTabStripPanel() is { } panel)
        {
            UpdateDragPosition(panel.PointFromScreen(screenPoint));
        }
    }

    /// <summary>
    /// 从游标接管「已分窗」的拖拽：先把窗口摆到「标签条正好在指针下方」，
    /// 之后窗口跟着鼠标走，直到再进入某个标签条。
    /// </summary>
    public void TakeOverDetachedDrag(BrowserTab tab, double grabOffset)
    {
        if (!BeginTransferredDrag(tab, grabOffset))
        {
            return;
        }

        _dragPhase = DragPhase.Detached;

        if (!_dragElement!.CaptureMouse())
        {
            Serilog.Log.Debug("接管拖拽(分窗)失败：鼠标捕获被拒绝，窗口 #{Window} 标签 {TabId}", GetHashCode(), tab.Id);
            EndDrag();
            return;
        }

        // 窗口刚 Show 时左右位置可能还没最终生效；用「标签按钮在窗口里的实际左边缘」
        // 反向校正一次，保证指针落在标签的同一抓取点上，而不是贴到窗口边上。
        var cursor = NativeMethods.GetCursorPosition();
        var desired = new Point(grabOffset + TabLeftInWindow(), DetachedStripAnchorY);
        var current = PointFromScreen(cursor);
        Left += current.X - desired.X;
        Top += current.Y - desired.Y;

        _detachedAnchor = PointFromScreen(cursor);
        Serilog.Log.Debug(
            $"接管拖拽(分窗)：窗口 #{GetHashCode()} 标签 {tab.Id}，"
            + $"光标=({cursor.X},{cursor.Y})，窗口=({Left},{Top})，锚点=({_detachedAnchor.X},{_detachedAnchor.Y})");
    }

    /// <summary>标签条里第一个标签在窗口坐标系中的左边缘。</summary>
    private double TabLeftInWindow()
    {
        var panel = ResolveTabStripPanel();
        if (panel is null)
        {
            return 0;
        }

        var origin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
        var slots = EnumerateTabSlots();
        return origin.X + (slots.Count > 0 ? slots[0].Left : 0);
    }

    /// <summary>Adopt 之后标签按钮要等一次布局才存在；接管前必须先把它找出来。</summary>
    private bool BeginTransferredDrag(BrowserTab tab, double grabOffset)
    {
        // 交接要等新窗口 Loaded（约 100ms）。若这期间用户已经松手，就不要再进入拖拽状态，
        // 否则标签会永远停在半透明的「拖动中」外观，因为再也没有 MouseUp 来收尾。
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            Serilog.Log.Debug("跳过拖拽接管：鼠标已松开，标签 {TabId}", tab.Id);
            return false;
        }

        UpdateLayout();
        var element = FindTabElement(tab);
        if (element is null)
        {
            Serilog.Log.Warning("拖拽接管失败：找不到标签 {TabId} 的按钮", tab.Id);
            return false;
        }

        _dragTab = tab;
        _dragElement = element;
        _dragGrabOffset = grabOffset;
        _dragStartPoint = default;
        _isDragging = true;
        _handOffPending = false;
        _lastReorderX = null;
        return true;
    }

    private FrameworkElement? FindTabElement(BrowserTab tab)
    {
        var panel = ResolveTabStripPanel();
        if (panel is null)
        {
            return null;
        }

        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(child.DataContext, tab))
            {
                return FindDescendant<Button>(child) ?? child;
            }
        }

        return null;
    }

    /// <summary>Detached：把窗口按鼠标位移挪动，让指针始终停在标签条上的锚点。</summary>
    private void FollowDetachedWindow(MouseEventArgs e)
    {
        // 用物理光标位置重新换算，而不是 e.GetPosition(this)：
        // 同一次移动会被「标签按钮的 Preview」和「窗口冒泡」各处理一次，
        // 后者拿到的偏移会让窗口位移叠加两次，越拖越偏。
        var local = PointFromScreen(NativeMethods.GetCursorPosition());
        var dx = local.X - _detachedAnchor.X;
        var dy = local.Y - _detachedAnchor.Y;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            return;
        }

        Left += dx;
        Top += dy;
    }

    /// <summary>Detached：指针进入别的窗口标签条 → 立即合并，拖拽回到目标窗口。</summary>
    private void MergeFromDetachedIfNeeded(MouseEventArgs e)
    {
        var pointerScreen = NativeMethods.GetCursorPosition();
        var target = _windows.FindWindowAtTabStrip(pointerScreen, this);
        if (target is null || target.IsIncognito != IsIncognito)
        {
            return;
        }

        if (target.TabIndexAtScreenPoint(pointerScreen) is { } insertAt)
        {
            Serilog.Log.Debug(
                "Detached 命中标签条：窗口 #{Source} → #{Target}「{Title}」",
                GetHashCode(), target.GetHashCode(), target.Title);
            HandOffToWindow(target, insertAt, pointerScreen);
        }
    }

    /// <summary>本窗口是否无痕（拖拽合并时用来拒绝跨隐私会话迁移）。</summary>
    public bool IsIncognito => _viewModel.IsIncognito;

    /// <summary>拖拽自动滚动：被拖标签的视觉边缘越过滚动视口时持续滚动（定时器驱动，见 <see cref="ScrollStripTowardsElement"/>）。</summary>
    private void OnDragScrollTick(object? sender, EventArgs e)
    {
        if (_isDragging && _dragPhase == DragPhase.InStrip)
        {
            ScrollStripTowardsElement();
        }
    }

    /// <summary>
    /// 标签多到溢出时，拖到标签条两端会自动滚动。判据对齐 Chrome 的 <c>TabDragScrollHandler</c>：
    /// 被拖标签的**视觉边缘**越过滚动视口边缘就滚，越得深滚得越快。
    /// 返回实际滚动量（DIP）——滚动会改变 panel 坐标系的映射，调用方要用它修正指针的 panel 坐标。
    /// </summary>
    private double ScrollStripTowardsElement()
    {
        if (_dragElement is null || _dragTransform is null || _dragTab is null || _tabStripPanel is null
            || TabScroller is not { } scroller)
        {
            return 0;
        }

        // 视觉左边缘 = 被拖标签所在槽位的左边缘 + 拖拽偏移 − 当前滚动量（换算到视口坐标）。
        // 槽位几何在每次换位后都已 UpdateLayout 落地，定时器 tick 之间没有未落地的布局。
        var index = _tabs.Tabs.IndexOf(_dragTab);
        var slots = EnumerateTabSlots();
        if (index < 0 || index >= slots.Count)
        {
            return 0;
        }

        var offset = scroller.HorizontalOffset;
        var visualLeft = slots[index].Left + _dragTransform.X - offset;
        var visualRight = visualLeft + _dragElement.ActualWidth;

        double step = 0;
        if (visualLeft < 0)
        {
            step = -Math.Clamp(-visualLeft * 0.5, 4, 24);
        }
        else if (visualRight > scroller.ActualWidth)
        {
            step = Math.Clamp((visualRight - scroller.ActualWidth) * 0.5, 4, 24);
        }

        if (step == 0)
        {
            return 0;
        }

        var before = scroller.HorizontalOffset;
        scroller.ScrollToHorizontalOffset(before + step);
        return scroller.HorizontalOffset - before;
    }

    /// <summary>
    /// 跨窗口合并前快照标签条位置（与 <see cref="CompleteTabSlide"/> 配对：
    /// 原位标签给新来的标签让位时平滑滑开，Chrome attach 同款）。
    /// </summary>
    public void BeginTabSlideCapture()
    {
        if (ResolveTabStripPanel() is { } panel)
        {
            _slide.CapturePositions(panel);
        }
    }

    /// <summary>跨窗口合并后结算让位动画（此时布局已落地）。</summary>
    public void CompleteTabSlide()
    {
        if (ResolveTabStripPanel() is { } panel)
        {
            _slide.ApplyCompensations(panel, excluded: null);
        }
    }

    /// <summary>
    /// 屏幕点（物理像素）是否落在本窗口的标签条上——跨窗口合并的判定区。
    /// 用窗口几何而不是 HitTest：拖动时鼠标被原窗口捕获，命中测试拿不到别的窗口。
    /// <para>
    /// 输入必须是 <c>PointToScreen</c> 的物理像素；这里用 <c>PointFromScreen</c>
    /// 让每个窗口按自己的 DPI 换算，多屏不同缩放时也不会错位。
    /// </para>
    /// </summary>
    public bool ContainsTabStripPoint(Point screenPoint)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return false;
        }

        Point local;
        try
        {
            local = PointFromScreen(screenPoint);
        }
        catch (InvalidOperationException)
        {
            // 窗口还没挂到 PresentationSource（刚创建 / 正在关闭）：不算命中。
            return false;
        }

        if (double.IsNaN(local.X) || double.IsNaN(local.Y))
        {
            return false;
        }

        return TabReorder.IsInStrip(local.X, local.Y, ActualWidth, TabStrip.ActualHeight);
    }

    /// <summary>物理屏幕点换算成本窗口 DIP 后，落在标签条的哪个插入位置；不可用时返回 <c>null</c>。</summary>
    public int? TabIndexAtScreenPoint(Point screenPoint)
    {
        try
        {
            return TabIndexAtWindowX(PointFromScreen(screenPoint).X);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

    /// <summary>
    /// 指针是否仍算「在标签条里」。宽高各留 8px 容差（避免手抖就拖出窗口）；
    /// 但水平方向一旦离开窗口，就算离开标签条——否则把标签拖到窗口外侧时，
    /// 因为 Y 还在标签条高度内，会被当成「原地放下」而什么都不发生。
    /// </summary>
    private bool IsInTabStrip(Point point) =>
        TabReorder.IsInStrip(point.X, point.Y, TabStrip.ActualWidth, TabStrip.ActualHeight);
}
