using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using ScBrowser.Browser;

namespace ScBrowser.Services;

/// <summary>
/// 启动命令行参数处理：
/// <code>
///   ScBrowser.exe https://example.com  D:\path\to\page.html     ← 在标签页中打开（可多个）
///   ScBrowser.exe --install-extension "D:\path\to\unpacked-ext"
///   ScBrowser.exe --install-extension "D:\path\to\ext.crx"
/// </code>
/// 参数既可能来自本进程启动，也可能是二次启动时由 <see cref="SingleInstance"/> 转发过来的。
/// 参数不存在时不做任何事，正常启动。
/// </summary>
public sealed class StartupCommandRunner
{
    private const string InstallExtensionOption = "--install-extension";

    private readonly ExtensionService _extensions;
    private readonly ILogger<StartupCommandRunner> _logger;

    public StartupCommandRunner(ExtensionService extensions, ILogger<StartupCommandRunner> logger)
    {
        _extensions = extensions;
        _logger = logger;
    }

    /// <summary>本进程自己的命令行参数（不含 exe 路径）。</summary>
    public static IReadOnlyList<string> OwnArgs => Environment.GetCommandLineArgs().Skip(1).ToList();

    /// <summary>参数里是否带了需要执行的命令（目前只有扩展安装）。</summary>
    public static bool HasCommands(IReadOnlyList<string> args) =>
        args.Any(a => a.StartsWith(InstallExtensionOption, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从参数里取出要打开的地址：本地文件转成 <c>file:///</c>，其余按地址栏规则解析。
    /// 选项（<c>--xxx</c>）与 <c>--install-extension</c> 的取值不算地址。
    /// </summary>
    public static IReadOnlyList<string> ExtractUrls(IReadOnlyList<string> args)
    {
        var urls = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals(InstallExtensionOption, StringComparison.OrdinalIgnoreCase))
            {
                i++;                                   // 跳过它的取值
                continue;
            }

            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            urls.Add(LooksLikeLocalFile(arg)
                ? new Uri(Path.GetFullPath(arg)).AbsoluteUri
                : AddressBarResolver.Resolve(arg));
        }

        return urls;
    }

    private static bool LooksLikeLocalFile(string arg)
    {
        try
        {
            return Path.IsPathFullyQualified(arg) && File.Exists(arg);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 执行命令行里的扩展安装请求。必须在至少有一个标签页初始化完成后调用（扩展按 profile 管理）——
    /// profile 由调用方从首个标签页上取（见 <c>MainWindow.OnLoaded</c>）。
    /// </summary>
    public async Task RunAsync(CoreWebView2Profile? profile, IReadOnlyList<string>? arguments = null)
    {
        var args = arguments ?? OwnArgs;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? path;

            if (arg.StartsWith(InstallExtensionOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                // --install-extension=<路径>
                path = arg[(InstallExtensionOption.Length + 1)..];
            }
            else if (arg.Equals(InstallExtensionOption, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                // --install-extension <路径>
                path = args[++i];
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("{Option} 缺少路径参数", InstallExtensionOption);
                continue;
            }

            await InstallAsync(profile, path).ConfigureAwait(true);
        }
    }

    private async Task InstallAsync(CoreWebView2Profile? profile, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = await _extensions.InstallFromFolderAsync(profile, path).ConfigureAwait(true);
                if (info is not null)
                {
                    _logger.LogInformation("命令行安装扩展成功：{Name}（{Id}）", info.Name, info.Id);
                }

                return;
            }

            if (File.Exists(path) && Path.GetExtension(path).Equals(".crx", StringComparison.OrdinalIgnoreCase))
            {
                var info = await _extensions.InstallFromCrxAsync(profile, path).ConfigureAwait(true);
                if (info is not null)
                {
                    _logger.LogInformation("命令行安装 CRX 成功：{Name}（{Id}）", info.Name, info.Id);
                }

                return;
            }

            _logger.LogError("命令行安装扩展失败：路径不存在或类型不支持 — {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "命令行安装扩展失败：{Path}", path);
        }
    }
}
