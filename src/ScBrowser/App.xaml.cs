using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScBrowser.Browser;
using ScBrowser.Infrastructure;
using ScBrowser.Services;
using ScBrowser.Storage;
using ScBrowser.ViewModels;
using ScBrowser.Views;
using Serilog;

namespace ScBrowser;

/// <summary>
/// 应用入口：组装依赖、配置日志、捕获全局异常。
/// 这里刻意不写业务——启动流程是「建目录 → 建宿主 → 注册服务 → 显示主窗口」。
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        ConfigureGlobalExceptionHandling();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, _, configuration) => configuration
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogFolder, "sc-browser-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true))
            .ConfigureServices(ConfigureServices)
            .Build();

        _host.Start();

        // ---- 启动期一次性初始化（顺序有依赖，别随意调换）----
        var services = _host.Services;

        // 1) 建表（幂等）
        services.GetRequiredService<BrowserDatabase>().Initialize();

        // 2) 应用主题：必须在主窗口显示前，避免先亮后暗闪一下。
        //    主题不只换 WPF 颜色字典——网页内容的 prefers-color-scheme 也要跟着走，
        //    订阅在这里而不是让 ThemeService 去依赖窗口/标签层（服务层不该反向依赖浏览器层）。
        var settings = services.GetRequiredService<SettingsService>();
        var theme = services.GetRequiredService<ThemeService>();
        var windows = services.GetRequiredService<BrowserWindowManager>();
        theme.Applied += (_, applied) => windows.ApplyWebTheme(applied);
        theme.Apply(settings.Current.Theme);

        // 3) 触发订阅式服务构造——它们靠构造函数订阅事件，不被别处引用就不会被创建。
        //    顺序要紧：这两个都订阅 BrowserWindowManager.TabManagerCreated，
        //    必须在创建第一个窗口之前完成订阅，否则会漏掉首个窗口的标签页。
        _ = services.GetRequiredService<DownloadManager>();   // 订阅各窗口的下载事件
        _ = services.GetRequiredService<WebMessageRouter>();  // 订阅内部页面消息

        windows.CreateWindow(new BrowserWindowOptions(IsPrimary: true)).Show();
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddLogging();

        // ---- 存储层 ----
        services.AddSingleton<BrowserDatabase>();
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<BookmarkRepository>();
        services.AddSingleton<DownloadRepository>();

        // ---- 应用服务 ----
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<StartupCommandRunner>();

        // ---- 浏览器引擎层 ----
        // 注意：TabManager / MainViewModel / MainWindow 都是「一个窗口一份」，
        // 由 BrowserWindowManager 手工组装，**不注册到容器**——否则容器会给出
        // 单例或错误的实例，多窗口下立刻出错。
        services.AddSingleton<BrowserEnvironmentProvider>();
        services.AddSingleton<BrowserWindowManager>();
        services.AddSingleton<ExtensionService>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<WebMessageRouter>();
    }

    /// <summary>
    /// 兜底异常处理：桌面应用里未捕获异常会直接杀进程，
    /// 这里至少保证「记进日志 + 告诉用户」而不是无声消失。
    /// </summary>
    private void ConfigureGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "UI 线程未处理异常");
            MessageBox.Show(
                $"发生了未处理的错误：\n\n{args.Exception.Message}",
                "ScBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "非 UI 线程未处理异常");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        finally
        {
            Log.CloseAndFlush();
            _host?.Dispose();
        }

        base.OnExit(e);
    }
}
