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
/// 必须走独立的顶层窗口，见 <c>Views/OverlayWindow</c>。
/// </para>
/// </summary>
public sealed class BrowserContentHost : Grid, IBrowserTabHost
{
    public void AttachTab(BrowserTab tab)
    {
        if (Children.Contains(tab.View))
        {
            return;
        }

        tab.View.Visibility = Visibility.Collapsed;
        Children.Add(tab.View);
    }

    public void DetachTab(BrowserTab tab)
    {
        if (Children.Contains(tab.View))
        {
            Children.Remove(tab.View);
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
