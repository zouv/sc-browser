using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScBrowser.Tests;

/// <summary>
/// 标签条拖拽修复所依赖的两个 WPF 布局行为（飘闪根因的机械验证）：
/// ① <c>ObservableCollection.Move</c> 会**同步**重排 ItemsControl 面板的 Children（容器实例复用）；
/// ② 此时各容器的 Arrange 位置还是**旧顺序**的（错位几何陷阱），面板 <c>UpdateLayout()</c> 后才落地为新顺序。
/// 生产代码 <c>UpdateDragPosition</c> 的「Move → UpdateLayout → 再读槽位」正是建立在这两个行为之上。
/// </summary>
public class TabStripLayoutTests
{
    [Fact]
    public void 集合换位后Children立即重排但几何未落地_UpdateLayout才落地()
    {
        WpfTestHost.Run(() =>
        {
            // 与真实标签条同构：item 是数据对象（宽度不同 = 标题长短不同）、
            // ItemTemplate 生成标签视觉、容器是带 DataContext 的 ContentPresenter（面板的直接子元素）。
            var items = new ObservableCollection<object>
            {
                new TestTab(220), new TestTab(72), new TestTab(160),
            };

            var itemTemplate = new DataTemplate();
            var templateFactory = new FrameworkElementFactory(typeof(Border));
            templateFactory.SetBinding(Border.WidthProperty, new Binding(nameof(TestTab.Width)));
            templateFactory.SetValue(Border.HeightProperty, 30.0);
            itemTemplate.VisualTree = templateFactory;

            var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
            panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var control = new ItemsControl
            {
                ItemsSource = items,
                ItemsPanel = new ItemsPanelTemplate(panelFactory),
                ItemTemplate = itemTemplate,
            };

            // 用屏外不可见的 HwndSource 当宿主：元素树挂上真实 PresentationSource，
            // 模板解析与布局管线才与真窗口一致（WS_POPUP 不带 WS_VISIBLE，不会闪窗）。
            using var source = new HwndSource(new HwndSourceParameters("sc-browser-test")
            {
                Width = 1000,
                Height = 100,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000),
            });
            source.RootVisual = control;
            control.UpdateLayout();

            var panel = FindDescendant<Panel>(control);
            Assert.True(panel is not null, DumpTree(control));
            Assert.True(panel.Children.Count == 3, DumpTree(control));

            // 落地几何：0 / 220 / 292（StackPanel 无间隙）
            AssertSlotLefts(panel, [0, 220, 292], control);
            var firstContainer = panel.Children[0];

            // ① 模型换位 → Children 顺序立即变化，且容器实例被复用（同一实例挪位）
            items.Move(0, 2);
            Assert.Same(firstContainer, panel.Children[2]);

            // ② 但 Arrange 位置还是旧顺序的（错位几何陷阱：新顺序 + 旧坐标）——
            //    这正是旧实现「预换位几何」错出宽度差的来源。
            //    Children[0]=72 容器（原在 220）、[1]=160 容器（原在 292）、[2]=220 容器（原在 0）。
            Assert.Equal(220, VisualTreeHelper.GetOffset(panel.Children[0]).X);
            Assert.Equal(292, VisualTreeHelper.GetOffset(panel.Children[1]).X);
            Assert.Equal(0, VisualTreeHelper.GetOffset(panel.Children[2]).X);

            // ③ 面板 UpdateLayout 后才落地为新顺序的槽位几何。
            panel.UpdateLayout();
            AssertSlotLefts(panel, [0, 72, 232], control);
        });
    }

    private static void AssertSlotLefts(Panel panel, double[] expected, DependencyObject dumpRoot)
    {
        var actual = panel.Children.Cast<FrameworkElement>().Select(c => VisualTreeHelper.GetOffset(c).X).ToArray();
        Assert.True(
            actual.SequenceEqual(expected),
            $"{DumpTree(dumpRoot)}\nactual={string.Join(",", actual)}");
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string DumpTree(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject node, int depth)
        {
            var fe = node as FrameworkElement;
            sb.AppendLine($"{new string(' ', depth * 2)}{node.GetType().Name}"
                + (fe is not null
                    ? $" w={fe.ActualWidth:0.#} h={fe.ActualHeight:0.#} off=({VisualTreeHelper.GetOffset(fe).X:0.#},{VisualTreeHelper.GetOffset(fe).Y:0.#})"
                    : string.Empty));
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
            }
        }

        Walk(root, 0);
        return sb.ToString();
    }

    /// <summary>模拟一个标签：宽度由「标题长度」决定，彼此不等宽。</summary>
    public sealed record TestTab(double Width);
}

/// <summary>
/// WPF 测试宿主：所有 WPF 元素操作都派发到同一条 STA 线程上跑，并在其上创建一个
/// <see cref="Application"/>——没有它，控件的主题样式（默认模板）解析不到，ItemsControl 永远没有模板。
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<System.Windows.Threading.Dispatcher> Dispatcher = new(() =>
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        return System.Windows.Threading.Dispatcher.FromThread(thread)!;
    });

    public static void Run(Action action)
    {
        Exception? failure = null;
        Dispatcher.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        Assert.Null(failure);
    }
}
