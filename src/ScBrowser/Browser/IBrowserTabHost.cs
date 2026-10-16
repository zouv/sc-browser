namespace ScBrowser.Browser;

/// <summary>
/// 标签页内容区的「宿主」抽象：由视图层实现，负责把 <see cref="BrowserTab"/> 的
/// WebView2 控件挂进可视化树、切换显示、以及从树上摘除。
/// <para>
/// 之所以抽成接口：<see cref="TabManager"/> 只管标签页的业务生命周期（状态、集合、
/// 当前页），不依赖任何 WPF 控件；可视化树的挂载/摘除由视图实现。
/// </para>
/// </summary>
public interface IBrowserTabHost
{
    /// <summary>把标签页的 WebView2 控件加入可视化树（初始隐藏）。</summary>
    void AttachTab(BrowserTab tab);

    /// <summary>从可视化树移除标签页的 WebView2 控件。</summary>
    void DetachTab(BrowserTab tab);

    /// <summary>切换当前显示的标签页；<c>null</c> 表示无激活页（如最后一个标签被关闭）。</summary>
    void SetActiveTab(BrowserTab? tab);
}
