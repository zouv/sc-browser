using System.IO;

namespace ScBrowser.Infrastructure;

/// <summary>
/// 原子写文件：先写同目录下的临时文件，再一次性替换目标。
/// <para>
/// 为什么不用 <c>File.WriteAllText</c> 直接覆盖：写到一半进程崩溃 / 断电，目标文件就只剩半截，
/// 下次启动 JSON 解析失败——设置会退回默认值、会话直接丢失。替换（MoveFileEx + REPLACE_EXISTING）
/// 在同一卷上是原子的：读到的要么是旧的完整文件，要么是新的完整文件。
/// </para>
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
