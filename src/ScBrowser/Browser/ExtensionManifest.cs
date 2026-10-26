using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScBrowser.Browser;

/// <summary>从扩展目录的 <c>manifest.json</c> 解析出的展示信息。</summary>
/// <param name="Id">由 manifest 的 <c>key</c> 算出的扩展 ID；没有 key 时为 <c>null</c>。</param>
/// <param name="IconPath">图标文件的绝对路径；没有图标时为 <c>null</c>。</param>
/// <param name="PopupPath">弹窗页面相对路径（<c>action.default_popup</c>）。</param>
/// <param name="OptionsPath">选项页相对路径。</param>
public sealed record ExtensionMetadata(
    string Folder,
    string? Id,
    string Name,
    string Description,
    string Version,
    int ManifestVersion,
    IReadOnlyList<string> Permissions,
    string? IconPath,
    string? PopupPath,
    string? OptionsPath,
    string? UpdateUrl,
    string? PublicKeyBase64);

/// <summary>
/// 读取扩展的 manifest。WebView2 只告诉我们 id / name / 是否启用，
/// 图标、描述、权限、弹窗、选项页这些都得自己解析——本类就是唯一的解析处。
/// </summary>
public static class ExtensionManifest
{
    private const string ManifestFile = "manifest.json";

    /// <summary>读取扩展目录的元数据；manifest 缺失或损坏时返回 <c>null</c>。</summary>
    public static ExtensionMetadata? Read(string folder)
    {
        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            if (root is null)
            {
                return null;
            }

            var messages = LoadMessages(folder);
            var key = root["key"]?.GetValue<string>();

            return new ExtensionMetadata(
                folder,
                string.IsNullOrWhiteSpace(key) ? null : TryComputeId(key),
                Localize(root["name"]?.GetValue<string>(), messages) ?? "未命名扩展",
                Localize(root["description"]?.GetValue<string>(), messages) ?? string.Empty,
                root["version"]?.GetValue<string>() ?? "0",
                root["manifest_version"]?.GetValue<int>() ?? 2,
                ReadPermissions(root),
                ResolveIcon(folder, root),
                ReadPopup(root),
                ReadOptions(root),
                root["update_url"]?.GetValue<string>(),
                key);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把公钥写进 manifest 的 <c>key</c> 字段。
    /// <para>
    /// 这一步是「扩展 ID 跟商店一致」的关键：Chromium 优先用 <c>key</c> 计算 ID，
    /// 没有 key 时会按安装路径算——那样每次换个目录 ID 就变了，依赖固定 ID 的功能全废。
    /// </para>
    /// </summary>
    public static void EnsureKey(string folder, string publicKeyBase64)
    {
        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
        if (root is null)
        {
            return;
        }

        // 已有 key 就不覆盖：manifest 自带的 key 必须与签名公钥一致，
        // 不一致时宁可让 WebView2 用我们写入的值（后面还会做签名校验，内容不可伪造）。
        root["key"] = publicKeyBase64;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        File.WriteAllText(manifestPath, root.ToJsonString(options), new UTF8Encoding(false));
    }

    private static string? TryComputeId(string base64Key)
    {
        try
        {
            return CrxPackage.ComputeExtensionId(Convert.FromBase64String(base64Key));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadPermissions(JsonObject root)
    {
        var result = new List<string>();

        foreach (var field in new[] { "permissions", "host_permissions" })
        {
            if (root[field] is not JsonArray array)
            {
                continue;
            }

            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? ReadPopup(JsonObject root)
    {
        foreach (var field in new[] { "action", "browser_action", "page_action" })
        {
            if (root[field] is JsonObject action && action["default_popup"]?.GetValue<string>() is { Length: > 0 } popup)
            {
                return popup;
            }
        }

        return null;
    }

    private static string? ReadOptions(JsonObject root)
    {
        if (root["options_page"]?.GetValue<string>() is { Length: > 0 } page)
        {
            return page;
        }

        return root["options_ui"] is JsonObject ui && ui["page"]?.GetValue<string>() is { Length: > 0 } uiPage
            ? uiPage
            : null;
    }

    /// <summary>从 <c>icons</c> / <c>action.default_icon</c> 里挑尺寸最大的一张。</summary>
    private static string? ResolveIcon(string folder, JsonObject root)
    {
        var candidates = new List<string>();

        if (root["icons"] is JsonObject icons)
        {
            foreach (var (_, node) in icons)
            {
                if (node?.GetValue<string>() is { Length: > 0 } path)
                {
                    candidates.Add(path);
                }
            }
        }

        foreach (var field in new[] { "action", "browser_action" })
        {
            if (root[field] is not JsonObject action)
            {
                continue;
            }

            if (action["default_icon"] is JsonValue single && single.TryGetValue<string>(out var path))
            {
                candidates.Add(path);
            }
            else if (action["default_icon"] is JsonObject map)
            {
                foreach (var (_, node) in map)
                {
                    if (node?.GetValue<string>() is { Length: > 0 } iconPath)
                    {
                        candidates.Add(iconPath);
                    }
                }
            }
        }

        var rootPath = Path.GetFullPath(folder);
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(Path.Combine(rootPath, candidate.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full)
                && Infrastructure.PathSafety.IsUnderDirectory(full, rootPath))
            {
                return full;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ //
    // _locales 多语言
    // ------------------------------------------------------------------ //

    /// <summary>当前界面语言 → 该扩展的 messages；没有 _locales 时返回空表。</summary>
    private static Dictionary<string, string> LoadMessages(string folder)
    {
        var localesRoot = Path.Combine(folder, "_locales");
        if (!Directory.Exists(localesRoot))
        {
            return [];
        }

        foreach (var locale in CandidateLocales())
        {
            var file = Path.Combine(localesRoot, locale, "messages.json");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (root is null)
                {
                    continue;
                }

                var messages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, node) in root)
                {
                    if (node is JsonObject entry && entry["message"]?.GetValue<string>() is { } message)
                    {
                        messages[key] = message;
                    }
                }

                return messages;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 某个语言包坏了就换下一个候选。
            }
        }

        // 没有与界面语言匹配的翻译时，退回扩展自带的任意一个语言包（总比显示 __MSG_x__ 强）。
        foreach (var directory in Directory.EnumerateDirectories(localesRoot))
        {
            var file = Path.Combine(directory, "messages.json");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (root is null)
                {
                    continue;
                }

                var messages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, node) in root)
                {
                    if (node is JsonObject entry && entry["message"]?.GetValue<string>() is { } message)
                    {
                        messages[key] = message;
                    }
                }

                return messages;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 换下一个。
            }
        }

        return [];
    }

    private static IEnumerable<string> CandidateLocales()
    {
        var culture = CultureInfo.CurrentUICulture;
        yield return culture.Name.Replace('-', '_');          // zh_CN
        yield return culture.TwoLetterISOLanguageName;        // zh
        yield return "en";
        yield return "en_US";
    }

    /// <summary>把 <c>__MSG_key__</c> 换成当前语言的消息；找不到就原样保留。</summary>
    private static string? Localize(string? text, Dictionary<string, string> messages)
    {
        if (string.IsNullOrEmpty(text) || messages.Count == 0 || !text.Contains("__MSG_", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (var (key, message) in messages)
        {
            text = text.Replace($"__MSG_{key}__", message, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
