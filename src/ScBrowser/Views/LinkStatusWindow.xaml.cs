using System.Windows;
using ScBrowser.Interop;

namespace ScBrowser.Views;

/// <summary>
/// 链接悬停地址气泡（Chrome 底部状态提示的等价物）。
/// <para>
/// 必须是独立顶层窗口：WebView2 是独立子 HWND，WPF 元素无法覆盖在网页上（airspace）。
/// 又因为它盖在网页上，窗口必须点击穿透且永不激活——否则会挡住链接点击、抢走页面焦点。
/// </para>
/// </summary>
public partial class LinkStatusWindow : Window
{
    public LinkStatusWindow() => InitializeComponent();

    /// <summary>气泡里显示的地址。空字符串由调用方决定是否隐藏窗口，本窗口不做判断。</summary>
    public string Text
    {
        get => StatusText.Text;
        set => StatusText.Text = value;
    }

    /// <summary>文本最大宽度；由主窗口按内容区宽度设置，避免长地址把气泡撑出窗口。</summary>
    public double MaxTextWidth
    {
        set => StatusText.MaxWidth = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 句柄就绪后再改扩展样式：构造期间还没有 HWND。
        NativeMethods.MakeClickThrough(this);
    }
}
