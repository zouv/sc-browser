using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using ScBrowser.Browser;

namespace ScBrowser.Views.Controls;

/// <summary>
/// 标签页内容区宿主：把各标签的 WebView2 控件挂进可视化树，并按当前激活页切换显示。
/// <para>
/// <b>为什么所有 WebView2 都留在树上、只切 Visible 而不销毁</b>：销毁重建会丢失页面
/// 状态（滚动位置、表单、视频进度）并造成切换卡顿。全部保留 + 切换可见性是主流做法，
/// 代价就是每标签一份渲染进程内存（详见 <see cref="BrowserTab"/> 的进程模型说明）。
/// </para>
/// <para>
/// <b>airspace 提示</b>：WebView2 是基于 HWND 的控件，任何 WPF 元素都无法绘制在它之上。
/// 因此本控件只负责「填满内容区」，一切需要盖在网页之上的浮层（地址栏下拉等）
/// 必须走独立的顶层窗口（本项目尚未实现这类浮层，见 <c>ROADMAP.md</c> P1-5）。
/// </para>
/// </summary>
public sealed class BrowserContentHost : Grid, IBrowserTabHost
{
    public void AttachTab(BrowserTab tab)
    {
        // 惰性恢复的标签还没有引擎（View 为 null）；引擎创建后 BrowserTab 会再发一次
        // EngineAttached，那时才真正挂进来。
        if (tab.View is not { } view || Children.Contains(view))
        {
            return;
        }

        // 新挂入的控件默认按当前激活状态决定可见性——惰性标签是在「被激活」时才创建引擎的。
        view.Visibility = tab.IsActive ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(view);
    }

    public void DetachTab(BrowserTab tab)
    {
        if (tab.View is { } view && Children.Contains(view))
        {
            Children.Remove(view);
        }
    }

    public void SetActiveTab(BrowserTab? tab)
    {
        foreach (var child in Children)
        {
            if (child is WebView2 view)
            {
                view.Visibility = tab is not null && ReferenceEquals(view, tab.View)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}
