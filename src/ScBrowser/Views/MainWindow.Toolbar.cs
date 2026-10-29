using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：地址栏右侧工具栏的「自定义工具栏」能力（对齐 Chrome）——
/// 拼图菜单（全部已启用扩展 + 行内图钉）、工具栏右键菜单（固定勾选 + 主页按钮开关）、
/// 固定扩展按钮的右键菜单（取消固定 / 管理扩展程序）。
/// </summary>
public partial class MainWindow
{
    /// <summary>图钉字形：未固定（线框）与已固定（实心），Segoe MDL2 码位（\u 转义，避免源码出现私用区字符）。</summary>
    private const string PinGlyph = "\\uE718";
    private const string PinnedGlyph = "\\uE840";

    /// <summary>扩展是否已固定到工具栏（按 id 比较，按钮集合每次刷新会重建）。</summary>
    private bool IsPinnedExtension(string id) =>
        _viewModel.ExtensionButtons.Any(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    private Style? MenuStyle => FindResource("BrowserMenuStyle") as Style;
    private Style? MenuItemStyle => FindResource("BrowserMenuItemStyle") as Style;

    /// <summary>
    /// 延迟到本次输入事件处理完之后再打开右键菜单。若在 MouseRightButtonUp 里同步
    /// <c>IsOpen = true</c>，弹出层会在同一事件处理期间抢鼠标捕获，结果是菜单看得见、
    /// 点不动（用户报告过）。所有代码里打开的右键菜单都必须走这里。
    /// </summary>
    private void OpenContextMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => menu.IsOpen = true));
    }

    // --------------------------------------------------------------------- //
    // 拼图菜单（Chrome 同款：全部已启用扩展，行内图钉固定 / 取消）
    // --------------------------------------------------------------------- //

    private void ExtensionPuzzle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor
            || (_viewModel.ExtensionButtons.Count == 0 && _viewModel.UnpinnedExtensions.Count == 0))
        {
            return;
        }

        var menu = new ContextMenu { Style = MenuStyle, PlacementTarget = anchor, StaysOpen = true };
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        foreach (var (extension, pinned) in AllEnabledExtensions())
        {
            menu.Items.Add(BuildExtensionRow(extension, pinned, menu, anchor));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildManageExtensionsItem());

        menu.IsOpen = true;
    }

    /// <summary>全部已启用扩展（已固定的在前，与工具栏顺序一致），带固定状态。</summary>
    private IEnumerable<(MainViewModel.ExtensionButton Extension, bool Pinned)> AllEnabledExtensions() =>
        _viewModel.ExtensionButtons.Select(b => (b, true))
            .Concat(_viewModel.UnpinnedExtensions.Select(b => (b, false)));

    /// <summary>
    /// 拼图菜单里的一行：图标 + 名称 + 右侧图钉。点行本体打开弹窗；点图钉切换固定
    /// （图钉用 PreviewMouseLeftButtonDown 拦截并标记 Handled，行点击就不会跟着触发）。
    /// </summary>
    private MenuItem BuildExtensionRow(
        MainViewModel.ExtensionButton extension,
        bool pinned,
        ContextMenu menu,
        FrameworkElement anchor)
    {
        var icon = new Image { Source = extension.Icon, Width = 16, Height = 16, Stretch = Stretch.Uniform };

        var name = new TextBlock
        {
            Text = extension.Name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 16, 0),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var pin = new TextBlock
        {
            Text = pinned ? PinnedGlyph : PinGlyph,
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = pinned
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextSecondaryBrush"),
            ToolTip = pinned ? "取消固定" : "固定到工具栏",
        };
        pin.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;   // 阻止行点击
            _viewModel.ToggleExtensionPinned(extension);
            menu.IsOpen = false;   // 固定态已在工具栏即时生效，直接收起
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) };
        header.Children.Add(icon);
        header.Children.Add(name);
        header.Children.Add(pin);

        var item = new MenuItem { Header = header, Style = MenuItemStyle };
        item.Click += (_, _) =>
        {
            OpenExtension(extension, anchor);
            menu.IsOpen = false;
        };
        return item;
    }

    // --------------------------------------------------------------------- //
    // 工具栏右键：自定义工具栏（Chrome 同款）
    // --------------------------------------------------------------------- //

    private void Toolbar_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 地址栏（文本框自带右键菜单）与主菜单按钮上的右键不弹自定义菜单。
        if (e.OriginalSource is DependencyObject origin
            && (IsDescendantOf(origin, AddressBar) || IsDescendantOf(origin, MenuButton)))
        {
            return;
        }

        // 右键在某个固定扩展按钮上 → 该扩展的菜单（取消固定 / 管理扩展程序）。
        if (FindExtensionButton(e.OriginalSource as DependencyObject) is { } extension)
        {
            var menu = new ContextMenu { Style = MenuStyle };
            menu.Items.Add(new MenuItem
            {
                Header = "取消固定",
                Style = MenuItemStyle,
            });
            ((MenuItem)menu.Items[0]!).Click += (_, _) => _viewModel.ToggleExtensionPinned(extension);
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildManageExtensionsItem());
            OpenContextMenu(menu, Toolbar);
            e.Handled = true;
            return;
        }

        OpenContextMenu(BuildCustomizeMenu(), Toolbar);
        e.Handled = true;
    }

    /// <summary>自定义工具栏菜单：每个扩展的固定勾选 + 主页按钮开关 + 管理扩展程序。</summary>
    private ContextMenu BuildCustomizeMenu()
    {
        var menu = new ContextMenu { Style = MenuStyle };

        var title = new MenuItem { Header = "自定义工具栏", Style = MenuItemStyle, IsEnabled = false };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());

        foreach (var (extension, pinned) in AllEnabledExtensions())
        {
            var item = new MenuItem
            {
                Header = extension.Name,
                Icon = new Image { Source = extension.Icon, Width = 16, Height = 16 },
                IsCheckable = true,
                IsChecked = pinned,
                Style = MenuItemStyle,
            };
            item.Click += (_, _) => _viewModel.ToggleExtensionPinned(extension);
            menu.Items.Add(item);
        }

        if (_viewModel.HasAnyExtensions)
        {
            menu.Items.Add(new Separator());
        }

        var home = new MenuItem
        {
            Header = "主页按钮",
            IsCheckable = true,
            IsChecked = _viewModel.IsHomeButtonVisible,
            Style = MenuItemStyle,
        };
        home.Click += (_, _) => _viewModel.ToggleHomeButton();
        menu.Items.Add(home);

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildManageExtensionsItem());
        return menu;
    }

    private MenuItem BuildManageExtensionsItem()
    {
        var item = new MenuItem { Header = "管理扩展程序", Style = MenuItemStyle };
        item.Click += (_, _) => _viewModel.OpenExtensionsCommand.Execute(null);
        return item;
    }

    /// <summary>从可视树往上找「被右键的固定扩展按钮」对应的扩展；找不到返回 <c>null</c>。</summary>
    private static MainViewModel.ExtensionButton? FindExtensionButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: MainViewModel.ExtensionButton extension })
            {
                return extension;
            }
        }

        return null;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
