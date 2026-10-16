using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;

namespace ScBrowser.Services;

/// <summary>应用设置的可序列化模型。新增字段务必给默认值，保证旧配置文件能平滑升级。</summary>
public sealed class AppSettings
{
    /// <summary>搜索引擎模板，<c>{0}</c> 为 URL 编码后的查询词。</summary>
    public string SearchTemplate { get; set; } = "https://www.bing.com/search?q={0}";

    /// <summary>主题：<c>System</c> / <c>Light</c> / <c>Dark</c>。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>主页地址（默认新标签页）。</summary>
    public string HomePage { get; set; } = InternalPageRegistry.NewTabUrl;

    /// <summary>启动时恢复上次会话的标签页。</summary>
    public bool RestoreSessionOnStartup { get; set; } = true;

    /// <summary>默认下载目录；留空表示使用系统「下载」文件夹。</summary>
    public string DownloadsFolder { get; set; } = string.Empty;

    /// <summary>下载前询问保存位置。</summary>
    public bool AskWhereToSaveDownloads { get; set; }

    /// <summary>是否启用浏览器扩展。<b>改动需要重启应用才会生效</b>（环境创建时的选项）。</summary>
    public bool EnableExtensions { get; set; } = true;

    /// <summary>关闭窗口时保留会话（下次可恢复）。</summary>
    public bool RememberSessionOnExit { get; set; } = true;
}

/// <summary>
/// 设置读写。配置单一源，落盘于 <c>%LOCALAPPDATA%\sc-browser\settings.json</c>。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        Load();
    }

    private static string SettingsFile => Path.Combine(AppPaths.AppDataRoot, "settings.json");

    /// <summary>设置发生变化（换肤、改搜索词等）时触发，供界面即时响应。</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>当前设置的快照引用；只读使用，修改请走 <see cref="Update"/>。</summary>
    public AppSettings Current => _current;

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is not null)
            {
                _current = loaded;
                Normalize(_current);
            }
        }
        catch (Exception ex)
        {
            // 配置损坏不应导致应用起不来：退回默认值并记日志。
            _logger.LogError(ex, "读取设置失败，已回退为默认设置");
            _current = new AppSettings();
        }
    }

    /// <summary>修改设置并立即落盘。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            Normalize(_current);
            Save();
        }

        Changed?.Invoke(this, _current);
    }

    /// <summary>把设置整体替换（设置页「保存」场景）。</summary>
    public void Replace(AppSettings settings)
    {
        lock (_gate)
        {
            Normalize(settings);
            _current = settings;
            Save();
        }

        Changed?.Invoke(this, _current);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_current, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入设置失败");
        }
    }

    /// <summary>修正非法/越界取值，避免坏配置把应用带进奇怪状态。</summary>
    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SearchTemplate) || !settings.SearchTemplate.Contains("{0}"))
        {
            settings.SearchTemplate = "https://www.bing.com/search?q={0}";
        }

        if (settings.Theme is not ("System" or "Light" or "Dark"))
        {
            settings.Theme = "System";
        }

        if (string.IsNullOrWhiteSpace(settings.HomePage))
        {
            settings.HomePage = InternalPageRegistry.NewTabUrl;
        }

        // 把搜索模板同步给地址栏解析器（进程内唯一真相源）。
        AddressBarResolver.SearchTemplate = settings.SearchTemplate;
    }
}
