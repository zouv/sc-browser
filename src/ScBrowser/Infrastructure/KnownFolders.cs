using System.IO;
using System.Runtime.InteropServices;

namespace ScBrowser.Infrastructure;

/// <summary>系统已知文件夹。不要自己拼 <c>%USERPROFILE%\Downloads</c>——用户可以把「下载」挪到别的盘。</summary>
public static class KnownFolders
{
    /// <summary>FOLDERID_Downloads：{374DE290-123F-4565-9164-39C4925E467B}。</summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>系统「下载」文件夹；查询失败时退回 <c>%USERPROFILE%\Downloads</c>。</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                var path = GetKnownFolderPath(DownloadsFolderId);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // 查询失败退回下面的默认值。
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
        }
    }

    private static string? GetKnownFolderPath(Guid folderId)
    {
        var result = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var path);
        if (result != 0 || path == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            // 调用方负责释放（见 SHGetKnownFolderPath 文档）。
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
