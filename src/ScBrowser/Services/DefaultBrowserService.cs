using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ScBrowser.Services;

/// <summary>
/// 把自己注册成默认浏览器（按用户，写 HKCU，不需要管理员权限）。
/// <para>
/// Windows 10/11 都不允许程序**静默**改默认浏览器：这里只注册 ProgID 与「能力」声明，
/// 再打开系统设置让用户点选。注册后系统才会认可本程序可以处理 http/https。
/// </para>
/// </summary>
public sealed class DefaultBrowserService
{
    /// <summary>本应用的 ProgID（URL 关联与「默认应用」里显示的名字都靠它）。</summary>
    public const string ProgId = "ScBrowserHTML";

    /// <summary>「已注册应用」里的名字。</summary>
    public const string RegisteredAppName = "ScBrowser";

    private readonly ILogger<DefaultBrowserService> _logger;

    public DefaultBrowserService(ILogger<DefaultBrowserService> logger) => _logger = logger;

    /// <summary>注册 ProgID 与 URL 关联；失败只记日志（注册表权限/策略限制）。</summary>
    public bool Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            _logger.LogWarning("无法确定程序路径，跳过默认浏览器注册");
            return false;
        }

        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");

            classes.CreateSubKey(@"shell\open\command").SetValue(null, $"\"{exe}\" \"%1\"");
            classes.CreateSubKey("DefaultIcon").SetValue(null, $"\"{exe}\",0");
            classes.CreateSubKey("Application").SetValue("ApplicationName", RegisteredAppName);

            using var capabilities = Registry.CurrentUser.CreateSubKey($@"Software\{RegisteredAppName}\Capabilities");
            capabilities.SetValue("ApplicationName", RegisteredAppName);
            capabilities.SetValue("ApplicationDescription", "ScBrowser — 基于 WebView2 的轻量浏览器");
            capabilities.SetValue("ApplicationIcon", $"{exe},0");

            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue("http", ProgId);
            associations.SetValue("https", ProgId);
            associations.SetValue(".htm", ProgId);
            associations.SetValue(".html", ProgId);

            using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            registered.SetValue(RegisteredAppName, $@"Software\{RegisteredAppName}\Capabilities");

            _logger.LogInformation("已注册为可选的默认浏览器（ProgID {ProgId}）", ProgId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "注册默认浏览器失败");
            return false;
        }
    }

    /// <summary>打开系统「默认应用」设置页；Windows 11 会直接定位到本应用。</summary>
    public void OpenWindowsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开默认应用设置失败");
        }
    }
}
