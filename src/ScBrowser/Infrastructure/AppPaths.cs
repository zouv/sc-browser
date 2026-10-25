using System.IO;

namespace ScBrowser.Infrastructure;

/// <summary>
/// 应用路径的<b>单一配置源</b>。所有落盘位置都从这里取，禁止在各处硬编码路径。
/// 根目录：<c>%LOCALAPPDATA%\sc-browser</c>
/// </summary>
public static class AppPaths
{
    /// <summary>覆盖数据根目录的环境变量（<b>仅供开发/测试</b>）。</summary>
    public const string DataDirOverrideVariable = "SCBROWSER_DATA_DIR";

    /// <summary>
    /// 应用数据根目录。
    /// <para>
    /// 设置了 <see cref="DataDirOverrideVariable"/> 时改用它：开发时可以起一个数据完全隔离的测试实例，
    /// 不与日常使用的那份数据（以及正在运行的那个实例）抢同一个 WebView2 用户数据目录。
    /// 单实例锁也按数据根目录区分（见 <c>Services/SingleInstance.cs</c>），两者可同时运行。
    /// </para>
    /// </summary>
    public static string AppDataRoot { get; } = ResolveAppDataRoot();

    private static string ResolveAppDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sc-browser");
    }

    /// <summary>WebView2 用户数据目录（Cookie / 缓存 / 扩展安装态都在这里，由 WebView2 托管）。</summary>
    public static string UserDataFolder => Path.Combine(AppDataRoot, "UserData");

    /// <summary>应用自己管理的扩展目录（.crx 解包后落到这里，便于集中管理）。</summary>
    public static string ManagedExtensionsFolder => Path.Combine(AppDataRoot, "Extensions");

    /// <summary>日志目录。</summary>
    public static string LogFolder => Path.Combine(AppDataRoot, "logs");

    /// <summary>SQLite 数据库文件（历史 / 书签）。</summary>
    public static string DatabaseFile => Path.Combine(AppDataRoot, "browser.db");

    /// <summary>会话文件（退出时保存的标签页状态）。</summary>
    public static string SessionFile => Path.Combine(AppDataRoot, "session.json");

    /// <summary>随程序分发的内部页面（WebUI）根目录。</summary>
    public static string WebUiFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "ui");

    /// <summary>确保所有运行时目录存在（应用启动时调用一次）。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(UserDataFolder);
        Directory.CreateDirectory(ManagedExtensionsFolder);
        Directory.CreateDirectory(LogFolder);
    }
}
