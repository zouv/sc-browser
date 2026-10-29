using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScBrowser.Browser;
using ScBrowser.Storage;

namespace ScBrowser.Views;

/// <summary>
/// 主窗口外壳（分册）：书签栏交互与地址栏星标菜单。
/// <para>书签栏数据源是 <see cref="ViewModels.MainViewModel.BarBookmarks"/>（根目录下的书签与文件夹），
/// 书签变更事件统一驱动刷新。文件夹左键点击弹出子项菜单，右键提供编辑/删除/打开全部。</para>
/// </summary>
public partial class MainWindow
{
    // --------------------------------------------------------------------- //
    // 地址栏星标菜单
    // --------------------------------------------------------------------- //

    /// <summary>点星标（或按 Ctrl+D）：弹出书签菜单（Chrome 同款，未收藏时先收藏再弹）。</summary>
    public void OpenBookmarkMenu()
    {
        var tab = _viewModel.ActiveTab;
        if (tab is null || !_viewModel.CanBookmarkActiveTab)
        {
            return;
        }

        var menu = new BookmarkMenuWindow(
            _viewModel,
            tab.DisplayUrl,
            tab.Title,
            _viewModel.BookmarkOfActiveTab)
        {
            Owner = this,
        };

        // 菜单右缘对齐星标按钮右缘、下缘对齐按钮底部（Chrome 星标菜单从星标下展开）。
        var anchor = BookmarkStarButton.PointToScreen(new Point(BookmarkStarButton.ActualWidth, BookmarkStarButton.ActualHeight));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(BookmarkStarButton);
        menu.Left = anchor.X / dpi.DpiScaleX - menu.Width;
        menu.Top = anchor.Y / dpi.DpiScaleY;
        menu.Show();
    }

    private void BookmarkStar_Click(object sender, RoutedEventArgs e) => OpenBookmarkMenu();

    // --------------------------------------------------------------------- //
    // 书签栏
    // --------------------------------------------------------------------- //

    /// <summary>书签栏条目：书签左键 = 当前标签打开；文件夹左键 = 弹出子项菜单（Chrome 同款）。</summary>
    private void BookmarkBarItem_Click(object sender, RoutedEventArgs e)    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkEntry entry)
        {
            return;
        }

        if (entry.IsFolder)
        {
            var menu = BuildFolderMenu(entry);
            menu.PlacementTarget = (Button)sender;
            menu.IsOpen = true;
            return;
        }

        _viewModel.ActiveTab?.Navigate(entry.Url);
    }

    /// <summary>书签栏右键菜单：编辑 / 删除（文件夹另有「打开全部」）。</summary>
    private void BookmarkBarItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkEntry entry)
        {
            return;
        }

        e.Handled = true;

        var menu = new ContextMenu { Style = (Style)FindResource("BrowserMenuStyle") };
        menu.Resources.Add(typeof(MenuItem), (Style)FindResource("BrowserMenuItemStyle"));

        if (!entry.IsFolder)
        {
            var openNew = new MenuItem { Header = "在新标签页中打开" };
            openNew.Click += (_, _) => _ = _tabs.CreateAsync(entry.Url, activate: true);
            menu.Items.Add(openNew);
            menu.Items.Add(new Separator());
        }
        else
        {
            var openAll = new MenuItem { Header = "打开文件夹内全部书签" };
            openAll.Click += (_, _) => _ = _viewModel.OpenFolderAllAsync(entry.Id);
            menu.Items.Add(openAll);
            menu.Items.Add(new Separator());
        }

        var edit = new MenuItem { Header = "编辑…" };
        edit.Click += (_, _) => EditBarEntry(entry);
        menu.Items.Add(edit);

        var remove = new MenuItem { Header = "删除" };
        remove.Click += (_, _) => RemoveBarEntry(entry);
        menu.Items.Add(remove);

        // 延迟打开（见 BookmarksBar_RightClick 的注释）。
        menu.PlacementTarget = (Button)sender;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => menu.IsOpen = true));
    }

    /// <summary>文件夹的子项菜单：子书签 → 打开；子文件夹 → 嵌套子菜单；末尾「打开全部」。</summary>
    private ContextMenu BuildFolderMenu(BookmarkEntry folder)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("BrowserMenuStyle") };
        menu.Resources.Add(typeof(MenuItem), (Style)FindResource("BrowserMenuItemStyle"));

        var children = _viewModel.AllBookmarks
            .Where(e => e.ParentId == folder.Id)
            .OrderBy(e => e.SortOrder)
            .ToList();

        foreach (var child in children)
        {
            if (child.IsFolder)
            {
                var item = new MenuItem { Header = child.Title };
                item.SubmenuOpened += (_, _) =>
                {
                    item.Items.Clear();
                    foreach (var sub in BuildFolderMenuItems(child))
                    {
                        item.Items.Add(sub);
                    }
                };
                menu.Items.Add(item);
            }
            else
            {
                var item = new MenuItem { Header = child.Title, Tag = child.Url };
                item.Click += (_, _) => _viewModel.ActiveTab?.Navigate(child.Url);
                menu.Items.Add(item);
            }
        }

        if (children.Count == 0)
        {
            var empty = new MenuItem { Header = "（空文件夹）", IsEnabled = false };
            menu.Items.Add(empty);
        }

        menu.Items.Add(new Separator());
        var openAll = new MenuItem { Header = "打开文件夹内全部书签" };
        openAll.Click += (_, _) => _ = _viewModel.OpenFolderAllAsync(folder.Id);
        menu.Items.Add(openAll);

        return menu;
    }

    private List<MenuItem> BuildFolderMenuItems(BookmarkEntry folder)
    {
        var items = new List<MenuItem>();
        var children = _viewModel.AllBookmarks
            .Where(e => e.ParentId == folder.Id)
            .OrderBy(e => e.SortOrder)
            .ToList();

        foreach (var child in children)
        {
            if (child.IsFolder)
            {
                var item = new MenuItem { Header = child.Title };
                item.SubmenuOpened += (_, _) =>
                {
                    item.Items.Clear();
                    foreach (var sub in BuildFolderMenuItems(child))
                    {
                        item.Items.Add(sub);
                    }
                };
                items.Add(item);
            }
            else
            {
                var item = new MenuItem { Header = child.Title };
                item.Click += (_, _) => _viewModel.ActiveTab?.Navigate(child.Url);
                items.Add(item);
            }
        }

        if (children.Count == 0)
        {
            items.Add(new MenuItem { Header = "（空文件夹）", IsEnabled = false });
        }

        return items;
    }

    /// <summary>编辑书签栏条目（名称 / 网址 / 文件夹）。</summary>
    private void EditBarEntry(BookmarkEntry entry)
    {
        var menu = new BookmarkMenuWindow(_viewModel, entry.Url, entry.Title, entry) { Owner = this };
        var anchor = BookmarksBar.PointToScreen(new Point(0, BookmarksBar.ActualHeight));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(BookmarksBar);
        menu.Left = anchor.X / dpi.DpiScaleX;
        menu.Top = anchor.Y / dpi.DpiScaleY;
        menu.Show();
    }

    private void RemoveBarEntry(BookmarkEntry entry)
    {
        var message = entry.IsFolder
            ? $"删除文件夹「{entry.Title}」及其中的全部内容？"
            : $"删除书签「{entry.Title}」？";
        if (MessageBox.Show(this, message, "删除书签", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        _ = _viewModel.RemoveBookmarkAsync(entry.Id);
    }

    /// <summary>
    /// 书签栏空白区域右键：Chrome 同款菜单——添加当前页 / 新建文件夹 / 打开书签管理器 / 显示开关。
    /// 条目自身的右键由 <see cref="BookmarkBarItem_RightClick"/> 处理（那里置了 Handled，不会走到这）。
    /// </summary>
    private void BookmarksBar_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("BrowserMenuStyle") };
        menu.Resources.Add(typeof(MenuItem), (Style)FindResource("BrowserMenuItemStyle"));

        var addPage = new MenuItem
        {
            Header = "添加当前页为书签",
            IsEnabled = _viewModel.CanBookmarkActiveTab,
        };
        addPage.Click += (_, _) => OpenBookmarkMenu();
        menu.Items.Add(addPage);

        var newFolder = new MenuItem { Header = "新建文件夹…" };
        newFolder.Click += (_, _) =>
        {
            var window = new BookmarkMenuWindow(_viewModel, string.Empty, string.Empty, null, folderMode: true)
            {
                Owner = this,
            };
            var anchor = BookmarksBar.PointToScreen(new Point(0, BookmarksBar.ActualHeight));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(BookmarksBar);
            window.Left = anchor.X / dpi.DpiScaleX;
            window.Top = anchor.Y / dpi.DpiScaleY;
            window.Show();
        };
        menu.Items.Add(newFolder);

        menu.Items.Add(new Separator());

        var manager = new MenuItem { Header = "打开书签管理器" };
        manager.Click += (_, _) => _viewModel.OpenBookmarksCommand.Execute(null);
        menu.Items.Add(manager);

        menu.Items.Add(new Separator());

        var toggle = new MenuItem
        {
            Header = "显示书签栏",
            IsCheckable = true,
            IsChecked = _settings.Current.ShowBookmarksBar,
        };
        toggle.Click += (_, _) => ToggleBookmarksBar();
        menu.Items.Add(toggle);

        // 延迟打开：右键事件处理期间同步打开弹出层会抢鼠标捕获，菜单点不动（见 MainWindow.Toolbar.cs 的注释）。
        menu.PlacementTarget = BookmarksBar;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => menu.IsOpen = true));
        e.Handled = true;
    }

    /// <summary>Ctrl+Shift+B：切换书签栏的「始终显示」（Chrome 同款快捷键）。</summary>
    public void ToggleBookmarksBar() => _viewModel.ToggleBookmarksBar();

    /// <summary>Ctrl+Shift+Delete：清除浏览数据对话框。</summary>
    public void ShowClearBrowsingDataDialog()
    {
        new ClearBrowsingDataDialog(_viewModel) { Owner = this }.ShowDialog();
    }
}
