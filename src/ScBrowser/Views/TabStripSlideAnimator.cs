using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScBrowser.Views;

/// <summary>
/// Chrome 式标签让位动画的视图层实现。
/// <para>
/// 标签条是 StackPanel 布局，模型换位后每个容器的布局槽位**立即**变化；
/// 本类给每个容器挂一个补偿 <c>TranslateTransform</c>，把视觉位置从旧槽位平滑滑到新槽位
/// （补偿量见 <see cref="TabReorder.SlideCompensation"/>），随后按指数衰减到 0、
/// 衰减完摘掉变换——这就是 Chrome 里其他标签「让位」滑动的同款效果（BoundsAnimator，200ms EASE_OUT）。
/// </para>
/// <para>
/// 衰减按「真实经过时间」算：快速连续换位时把残余补偿叠进新补偿即可自然重定向，
/// 不打断动画；拖拽中的标签被排除在外（它的位置由拖拽自己控制）。
/// </para>
/// </summary>
internal sealed class TabStripSlideAnimator
{
    /// <summary>衰减时间常数：3 个 τ（≈200ms）后基本到位，与 Chrome 的 200ms 让位动画相当。</summary>
    private static readonly TimeSpan DecayTau = TimeSpan.FromMilliseconds(66);

    /// <summary>每个容器的当前补偿变换；字典为空 = 没有动画在跑（定时器停）。</summary>
    private readonly Dictionary<FrameworkElement, TranslateTransform> _compensations = [];

    /// <summary>换位前的布局位置快照（按容器身份对应，与顺序无关）。</summary>
    private readonly Dictionary<FrameworkElement, double> _snapshot = [];

    private readonly DispatcherTimer _timer;
    private long _lastTick;

    public TabStripSlideAnimator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    /// <summary>换位前快照：记录每个容器当前的布局位置（<c>GetOffset</c> 不含渲染变换，正是所需）。</summary>
    public void CapturePositions(Panel panel)
    {
        _snapshot.Clear();
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            _snapshot[child] = VisualTreeHelper.GetOffset(child).X;
        }
    }

    /// <summary>
    /// 换位后结算（布局已落地）：按「旧位置 → 新位置」给其他标签设置/叠加滑动补偿并开始衰减。
    /// <paramref name="excluded"/> 是被拖的那个容器——它跟着鼠标走，不由本类补偿。
    /// </summary>
    public void ApplyCompensations(Panel panel, DependencyObject? excluded)
    {
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(child, excluded) || !_snapshot.TryGetValue(child, out var oldPos))
            {
                continue;
            }

            var currentComp = _compensations.TryGetValue(child, out var transform) ? transform.X : 0;
            SetCompensation(child, TabReorder.SlideCompensation(oldPos, VisualTreeHelper.GetOffset(child).X, currentComp));
        }

        _snapshot.Clear();
    }

    /// <summary>松手落回：把拖拽结束时的偏移当补偿衰减到 0，标签从鼠标下滑回槽位。</summary>
    public void BeginGlide(FrameworkElement element, double offsetX) => SetCompensation(element, offsetX);

    /// <summary>取消某个元素的滑动补偿（例如又开始拖它），把渲染变换权还回去。</summary>
    public void CancelGlide(FrameworkElement element)
    {
        if (!_compensations.Remove(element, out var transform))
        {
            return;
        }

        if (ReferenceEquals(element.RenderTransform, transform))
        {
            element.RenderTransform = null;
        }

        StopTimerIfIdle();
    }

    /// <summary>窗口关闭时清理：停表并摘掉全部残留变换，避免引用死元素。</summary>
    public void Reset()
    {
        _timer.Stop();
        foreach (var (element, transform) in _compensations)
        {
            if (ReferenceEquals(element.RenderTransform, transform))
            {
                element.RenderTransform = null;
            }
        }

        _compensations.Clear();
        _snapshot.Clear();
    }

    private void SetCompensation(FrameworkElement element, double comp)
    {
        if (Math.Abs(comp) < 0.5)
        {
            CancelGlide(element);
            return;
        }

        if (!_compensations.TryGetValue(element, out var transform))
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
            _compensations[element] = transform;
        }

        transform.X = comp;

        // 重置时间基准：第一帧的 dt 从设补偿那一刻起算，避免大步长把动画跳没。
        _lastTick = Environment.TickCount64;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var dt = now - _lastTick;
        _lastTick = now;

        // 系统休眠/调试挂起后 dt 会非常大；一步衰减完即可，不能让 factor 算出 NaN。
        if (dt <= 0 || dt > 1000)
        {
            return;
        }

        var factor = Math.Exp(-dt / DecayTau.TotalMilliseconds);
        foreach (var (element, transform) in _compensations.ToArray())
        {
            transform.X *= factor;
            if (Math.Abs(transform.X) < 0.5)
            {
                if (ReferenceEquals(element.RenderTransform, transform))
                {
                    element.RenderTransform = null;
                }

                _compensations.Remove(element);
            }
        }

        StopTimerIfIdle();
    }

    private void StopTimerIfIdle()
    {
        if (_compensations.Count == 0)
        {
            _timer.Stop();
        }
    }
}
