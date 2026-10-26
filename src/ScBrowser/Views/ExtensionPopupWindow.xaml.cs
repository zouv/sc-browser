using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ScBrowser.Browser;

namespace ScBrowser.Views;

/// <summary>
/// 扩展的工具栏弹窗：用一个独立的 WebView2 加载 <c>chrome-extension://&lt;id&gt;/&lt;popup&gt;</c>。
/// <para>
/// 为什么要单开一个窗口：WebView2 是子 HWND，任何 WPF 元素都盖不到网页上（airspace），
/// 弹窗必须自己是一个顶层窗口；同时它用的是同一个 WebView2 环境与 profile，
/// 扩展的 <c>chrome.*</c> 能力才在弹窗里可用。
/// </para>
/// <para>
/// <b>chrome.tabs 兼容层</b>：WebView2 不提供 <c>chrome.tabs</c>。这里在文档创建前注入
/// <see cref="ExtensionApiBridge.PolyfillScript"/>，把 query / getCurrent / create / update
/// 转给宿主，由宿主用当前窗口的真实标签回答（见 <see cref="HandleMessage"/>）。
/// 只覆盖我们自己承载的扩展页面；后台 service worker 拿不到。
/// </para>
/// </summary>
public partial class ExtensionPopupWindow : Window
{
    private readonly WebView2 _view = new();
    private bool _everActivated;
    private bool _closed;
    private TabManager? _owner;

    public ExtensionPopupWindow()
    {
        InitializeComponent();
        Root.Children.Add(_view);
    }

    /// <summary>
    /// 打开弹窗。环境必须是全应用那唯一一个；<paramref name="options"/> 用于无痕窗口。
    /// </summary>
    public async Task OpenAsync(
        CoreWebView2Environment environment,
        CoreWebView2ControllerOptions? options,
        string url,
        Point anchorScreen,
        TabManager? owner = null)
    {
        _owner = owner;
        Left = anchorScreen.X;
        Top = anchorScreen.Y;

        try
        {
            if (options is null)
            {
                await _view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            }
            else
            {
                await _view.EnsureCoreWebView2Async(environment, options).ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // 环境/运行时异常：弹窗没有内容，直接关掉，交给调用方提示。
            Close();
            return;
        }

        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionApiBridge.PolyfillScript)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 注入失败只是少了 chrome.tabs 兼容层，弹窗本身仍可用。
        }

        core.WebMessageReceived += (_, e) => HandleMessage(core, e);

        // 弹窗里点链接在本窗口内导航（不弹裸窗）。
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        core.NavigationCompleted += (_, e) =>
            Serilog.Log.Debug("扩展弹窗加载完成：{Url} 成功={Ok} 错误={Error}", core.Source, e.IsSuccess, e.WebErrorStatus);

        core.Navigate(url);
    }

    /// <summary>处理注入脚本发来的 chrome.tabs / windows 请求。</summary>
    private void HandleMessage(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
            if (requestId is null && root.TryGetProperty("requestId", out var idProp))
            {
                requestId = idProp.GetString();
            }

            if (string.IsNullOrEmpty(type) || !type.StartsWith(ExtensionApiBridge.MessagePrefix, StringComparison.Ordinal))
            {
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadProp) ? payloadProp : default;

            object? result = type switch
            {
                "scb.tabs.query" => new[] { DescribeActiveTab() },
                "scb.tabs.getCurrent" => DescribeActiveTab(),
                "scb.windows.getCurrent" => new { id = 1, focused = true, type = "normal", incognito = _owner?.IsIncognito ?? false },
                "scb.tabs.create" => CreateTab(payload),
                "scb.tabs.update" => UpdateTab(payload),
                _ => null,
            };

            Reply(core, requestId, ok: true, result, error: null);
        }
        catch (Exception ex)
        {
            if (requestId is not null)
            {
                Reply(core, requestId, ok: false, result: null, error: ex.Message);
            }
        }
    }

    private object? DescribeActiveTab()
    {
        var tab = _owner?.ActiveTab;
        return new
        {
            id = tab?.Id ?? 1,
            index = tab is null ? 0 : _owner!.Tabs.IndexOf(tab),
            windowId = 1,
            active = true,
            highlighted = true,
            pinned = false,
            url = tab?.DisplayUrl ?? string.Empty,
            title = tab?.Title ?? string.Empty,
            favIconUrl = string.Empty,
        };
    }

    private object? CreateTab(JsonElement payload)
    {
        var url = payload.ValueKind == JsonValueKind.Object
                  && payload.TryGetProperty("url", out var urlProp)
                  && urlProp.ValueKind == JsonValueKind.String
            ? urlProp.GetString()
            : null;

        if (_owner is not null && !string.IsNullOrWhiteSpace(url))
        {
            _ = _owner.CreateAsync(url, activate: true);
        }

        return new { id = 0, index = 0, windowId = 1, active = true, url = url ?? string.Empty };
    }

    private object? UpdateTab(JsonElement payload)
    {
        var url = payload.ValueKind == JsonValueKind.Object
                  && payload.TryGetProperty("url", out var urlProp)
                  && urlProp.ValueKind == JsonValueKind.String
            ? urlProp.GetString()
            : null;

        if (_owner?.ActiveTab is { } tab && !string.IsNullOrWhiteSpace(url))
        {
            tab.NavigateFromAddressBar(url);
        }

        return DescribeActiveTab();
    }

    private static void Reply(CoreWebView2 core, string? requestId, bool ok, object? result, string? error)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        // 补丁脚本只听 scb.response，不区分 ok：失败时把 error 包成结果，扩展至少不会挂起。
        var json = JsonSerializer.Serialize(new
        {
            type = "scb.response",
            requestId,
            result = ok ? result : new { error },
        });

        core.PostWebMessageAsJson(json);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _everActivated = true;
    }

    /// <summary>点别处就收起；和站点信息浮层一样，只在确实激活过之后才认失活。</summary>
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_everActivated && !_closed)
        {
            _closed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        try
        {
            _view.Dispose();
        }
        catch (Exception)
        {
            // 关闭期释放失败无需上抛。
        }

        base.OnClosed(e);
    }
}
