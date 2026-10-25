using System.IO;

namespace ScBrowser.Infrastructure;

/// <summary>路径安全判定（防目录穿越）的单一实现。CRX 解包与内部页面资源供给共用。</summary>
public static class PathSafety
{
    /// <summary>
    /// <paramref name="path"/> 是否位于 <paramref name="directory"/> 之内（两者都应是 <c>Path.GetFullPath</c> 之后的完整路径）。
    /// <para>
    /// 必须拿「目录 + 分隔符」做前缀比较：只比目录本身的话，<c>...\Extensions\foo-evil\x.js</c>
    /// 也以 <c>...\Extensions\foo</c> 开头，同级的「同前缀目录」就会被当成目录内部而放行。
    /// </para>
    /// </summary>
    public static bool IsUnderDirectory(string path, string directory)
    {
        var root = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
