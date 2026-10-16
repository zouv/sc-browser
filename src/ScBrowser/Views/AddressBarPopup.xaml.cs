using System.Windows;
using System.Windows.Input;
using ScBrowser.Services;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 地址栏下拉的浮层窗口。只负责显示与鼠标选中，键盘导航由 <see cref="MainWindow"/> 处理
/// （焦点始终留在地址栏里，弹出层不激活自己）。
/// </summary>
public partial class AddressBarPopup : Window
{
    public AddressBarPopup(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>用户用鼠标点选了某一项。</summary>
    public event EventHandler<SuggestionItem>? SuggestionChosen;

    /// <summary>把下拉摆到地址栏正下方（宽度与地址栏一致）。</summary>
    public void SetBounds(Point screenTopLeft, double width)
    {
        Left = screenTopLeft.X;
        Top = screenTopLeft.Y;
        Width = width;
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuggestionItem item)
        {
            e.Handled = true;
            SuggestionChosen?.Invoke(this, item);
        }
    }
}
