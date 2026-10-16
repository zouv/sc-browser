using System.IO;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;

namespace ScBrowser.Services;

/// <summary>
/// 启动命令行参数处理。目前支持扩展安装——便于批量部署与自动化验证：
/// <code>
///   ScBrowser.exe --install-extension "D:\path\to\unpacked-ext"
///   ScBrowser.exe --install-extension "D:\path\to\ext.crx"
/// </code>
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

    /// <summary>执行命令行请求。必须在至少有一个标签页初始化完成后调用（扩展按 profile 管理）。</summary>
    public async Task RunAsync()
    {
        var args = Environment.GetCommandLineArgs();

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            string? path;

            if (arg.StartsWith(InstallExtensionOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                // --install-extension=<路径>
                path = arg[(InstallExtensionOption.Length + 1)..];
            }
            else if (arg.Equals(InstallExtensionOption, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
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

            await InstallAsync(path).ConfigureAwait(true);
        }
    }

    private async Task InstallAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = await _extensions.InstallFromFolderAsync(path).ConfigureAwait(true);
                _logger.LogInformation("命令行安装扩展成功：{Name}（{Id}）", info.Name, info.Id);
                return;
            }

            if (File.Exists(path) && Path.GetExtension(path).Equals(".crx", StringComparison.OrdinalIgnoreCase))
            {
                var info = await _extensions.InstallFromCrxAsync(path).ConfigureAwait(true);
                _logger.LogInformation("命令行安装 CRX 成功：{Name}（{Id}）", info.Name, info.Id);
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
