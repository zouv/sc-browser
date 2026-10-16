using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using ScBrowser.Infrastructure;

namespace ScBrowser.Views;

/// <summary>
/// 「关于」对话框：应用图标、版本与用户数据目录（排障时最常问的就是数据在哪）。
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        // 不能用 new BitmapImage(.ico)：那样取到的是图标的第一帧（16px），
        // 放大到 52px 会明显发虚。用图标解码器挑出最大的那帧（256px）交给 WPF 缩小才清晰。
        var decoder = new IconBitmapDecoder(
            new Uri("pack://application:,,,/Assets/sc-browser.ico"),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        AppIcon.Source = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        VersionText.Text = $"版本 {typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        DataFolderText.Text = AppPaths.AppDataRoot;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute 才会交给资源管理器打开目录。
            Process.Start(new ProcessStartInfo(AppPaths.AppDataRoot) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开目录不值得弹错误框打断用户，关于框本身已经给出路径。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
