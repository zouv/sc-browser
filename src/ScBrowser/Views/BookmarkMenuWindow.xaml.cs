using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScBrowser.Storage;
using ScBrowser.ViewModels;

namespace ScBrowser.Views;

/// <summary>
/// 地址栏星标的弹出菜单（Chrome 星标菜单同款）：名称输入 + 文件夹下拉 + 完成/移除。
/// 独立顶层窗口（airspace），失焦即关闭。数据读写都经 <see cref="MainViewModel"/>，
/// 书签变更事件会驱动书签栏与星标状态自动刷新。
/// </summary>
public partial class BookmarkMenuWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>收藏的页面地址（新建时用）。</summary>
    private readonly string _url;

    /// <summary>已存在的书签（编辑已有收藏时非空）。</summary>
    private readonly BookmarkEntry? _entry;

    /// <summary>新建文件夹模式（书签栏右键菜单入口）：隐藏网址、保存时建文件夹。</summary>
    private readonly bool _folderMode;

    public BookmarkMenuWindow(
        MainViewModel viewModel,
        string url,
        string title,
        BookmarkEntry? entry,
        bool folderMode = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _url = url;
        _entry = entry;
        _folderMode = folderMode;

        TitleText.Text = folderMode ? "新建文件夹" : entry is null ? "添加书签" : "编辑书签";
        NameBox.Text = folderMode ? "新建文件夹" : entry?.Title ?? title;
        RemoveButton.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;

        // 文件夹模式与编辑文件夹时没有网址字段；编辑书签时可以改网址（Chrome 的「编辑书签」对话框同款）。
        var showUrl = entry is not null && !entry.IsFolder;
        UrlLabel.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;
        if (showUrl)
        {
            UrlBox.Text = entry.Url;
        }

        // 文件夹下拉：书签栏（根目录）+ 各文件夹，选中当前所在位置。
        var folders = viewModel.BookmarkFolders;
        FolderBox.Items.Add(new ComboBoxItem { Content = "书签栏", Tag = 0L });
        foreach (var folder in folders)
        {
            FolderBox.Items.Add(new ComboBoxItem { Content = folder.Title, Tag = folder.Id });
        }

        var selectedId = entry?.ParentId ?? 0;
        foreach (var item in FolderBox.Items.Cast<ComboBoxItem>())
        {
            if ((long)item.Tag == selectedId)
            {
                FolderBox.SelectedItem = item;
                break;
            }
        }

        if (FolderBox.SelectedItem is null && FolderBox.Items.Count > 0)
        {
            FolderBox.SelectedIndex = 0;
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var parentId = (FolderBox.SelectedItem as ComboBoxItem)?.Tag as long? ?? 0;
        if (_folderMode)
        {
            _ = _viewModel.AddFolderAsync(name, parentId);
        }
        else if (_entry is null)
        {
            _ = _viewModel.AddBookmarkAsync(_url, name, parentId);
        }
        else
        {
            var url = UrlBox.Text.Trim();
            if (url.Length == 0)
            {
                return;
            }

            _ = _viewModel.EditBookmarkAsync(_entry.Id, name, url, parentId);
        }

        Close();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is not null)
        {
            _ = _viewModel.RemoveBookmarkAsync(_entry.Id);
        }

        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e) => Close();
}
