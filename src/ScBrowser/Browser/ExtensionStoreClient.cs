using System.IO;
using System.Net.Http;

namespace ScBrowser.Browser;

/// <summary>
/// 从「扩展商店」下载 CRX。
/// <para>
/// 本项目没有商店集成，只能走商店公开的更新接口：给出扩展 ID，换回一个 .crx。
/// 支持 Chrome 网上应用店与 Edge 加载项商店——两者的 ID 规则相同，只有下载端点不同。
/// </para>
/// </summary>
public static class ExtensionStoreClient
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// 从用户输入里取出扩展 ID：支持直接粘贴 32 位 ID，也支持商店详情页链接
    /// （<c>.../detail/&lt;名称&gt;/&lt;id&gt;</c>）。
    /// </summary>
    public static string? ExtractId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        if (CrxPackage.LooksLikeExtensionId(text))
        {
            return text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (CrxPackage.LooksLikeExtensionId(segments[i]))
            {
                return segments[i];
            }
        }

        return null;
    }

    /// <summary>输入的链接是否指向 Edge 加载项商店。</summary>
    public static bool IsEdgeStore(string? input) =>
        input is not null
        && input.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 下载扩展包到临时文件，返回路径（调用方负责删除）。
    /// </summary>
    /// <param name="input">商店链接或扩展 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string> DownloadAsync(string input, CancellationToken cancellationToken = default)
    {
        var id = ExtractId(input) ?? throw new ArgumentException("无法从输入中识别扩展 ID。", nameof(input));

        var url = IsEdgeStore(input)
            ? $"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&x=id%3D{id}%26installsource%3Dondemand%26uc"
            : $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=131.0.0.0&acceptformat=crx2,crx3&x=id%3D{id}%26uc";

        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"商店返回 {(int)response.StatusCode}，扩展可能不存在或已下架。");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"scbrowser-ext-{Guid.NewGuid():N}.crx");
        await using (var file = File.Create(tempPath))
        {
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (!CrxPackage.LooksLikeCrx(tempPath))
        {
            File.Delete(tempPath);
            throw new InvalidDataException("下载到的内容不是有效的 CRX 包。");
        }

        return tempPath;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        return client;
    }
}
