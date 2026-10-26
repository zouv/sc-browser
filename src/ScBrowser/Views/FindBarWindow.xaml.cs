using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScBrowser.Views;

/// <summary>
/// 页内查找条（浮动窗口）：只负责输入框 / 匹配计数 / 上一下一 / 关闭这几个控件，
/// 把事件转成对外的回调；搜索会话与防抖逻辑都留在 <see cref="MainWindow"/>。
/// <para>
/// 为什么是独立窗口而不是 Popup：Popup 的 HWND 拿不到键盘焦点（光标会闪但打字落到网页），
/// 普通顶层 Window 才能稳定接收输入。靠右停靠由 <see cref="MainWindow"/> 用 Owner 的位置计算。
/// </para>
/// </summary>
public partial class FindBarWindow : Window
{
    public FindBarWindow() => InitializeComponent();

    /// <summary>输入框文本变化（MainWindow 据此防抖重搜）。</summary>
    public event EventHandler? SearchTextChanged;

    /// <summary>下一个匹配（Enter / F3）。</summary>
    public event EventHandler? StepForward;

    /// <summary>上一个匹配（Shift+Enter / Shift+F3）。</summary>
    public event EventHandler? StepBackward;

    /// <summary>请求关闭（Esc / 关闭按钮）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>输入框文本；设置时会触发 <see cref="SearchTextChanged"/>。</summary>
    public string Text
    {
        get => FindBox.Text;
        set => FindBox.Text = value;
    }

    /// <summary>匹配计数状态（如 <c>1/2</c> / 无结果）。</summary>
    public string Status
    {
        get => FindStatus.Text;
        set => FindStatus.Text = value;
    }

    /// <summary>聚焦输入框并全选。</summary>
    public void FocusAndSelect()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchTextChanged?.Invoke(this, EventArgs.Empty);

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Step((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.F3:
                Step((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void Step(bool forward) =>
        (forward ? StepForward : StepBackward)?.Invoke(this, EventArgs.Empty);

    private void FindNext_Click(object sender, RoutedEventArgs e) => StepForward?.Invoke(this, EventArgs.Empty);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => StepBackward?.Invoke(this, EventArgs.Empty);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
