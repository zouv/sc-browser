using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScBrowser.Infrastructure;
using ScBrowser.Interop;

namespace ScBrowser.Services;

/// <summary>
/// 单实例：同一份用户数据只允许一个进程；再次启动时把命令行参数转交给已在运行的实例后退出。
/// <para>
/// <b>为什么必须单实例</b>：两个进程共用同一个 WebView2 用户数据目录，若环境参数不一致会直接创建失败；
/// 即便一致，两份进程各自读写同一个 SQLite / settings.json / session.json 也会互相覆盖。
/// 「从别的程序点开一个链接」「设为默认浏览器」也都以此为前提——链接应开在已有窗口里。
/// </para>
/// <para>
/// 锁与管道名都带上数据根目录的哈希：用 <see cref="AppPaths.DataDirOverrideVariable"/> 起的测试实例
/// 与日常实例互不干扰。管道用 <see cref="PipeOptions.CurrentUserOnly"/>，别的用户的进程连不上。
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsMutex;

    private SingleInstance(Mutex mutex, bool ownsMutex, string pipeName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pipeName = pipeName;
    }

    /// <summary>本进程是否为（这份数据的）首个实例。</summary>
    public bool IsPrimary => _ownsMutex;

    /// <summary>抢占单实例锁。</summary>
    public static SingleInstance Acquire()
    {
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.AppDataRoot.ToLowerInvariant())))[..16];

        var mutex = new Mutex(initiallyOwned: true, $@"Local\ScBrowser-{key}", out var createdNew);
        return new SingleInstance(mutex, createdNew, $"ScBrowser-{key}");
    }

    /// <summary>
    /// 把参数转交给首个实例。首个实例可能刚启动、管道还没开始监听，所以在超时内重试。
    /// </summary>
    /// <returns>是否送达。</returns>
    public bool TryForward(IReadOnlyList<string> args, TimeSpan timeout)
    {
        NativeMethods.AllowAnyProcessToSetForeground();

        var payload = JsonSerializer.Serialize(args);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.Write(payload);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // 首个实例还没开始监听，继续等。
            }
            catch (IOException)
            {
                // 管道正忙（另一次转发进行中）或刚好断开，稍后重试。
                Thread.Sleep(100);
            }
        }

        return false;
    }

    /// <summary>
    /// 首个实例开始接收转发（后台线程循环监听）。<paramref name="onArgs"/> 在后台线程上调用，
    /// 调用方负责切回 UI 线程。
    /// </summary>
    public void StartListening(Action<string[]> onArgs, ILogger logger)
    {
        if (!IsPrimary)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);
                    var args = JsonSerializer.Deserialize<string[]>(json) ?? [];
                    onArgs(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 单次转发坏掉（对方中途退出、内容不是 JSON）不该让监听停掉。
                    logger.LogWarning(ex, "接收二次启动转发的参数失败");
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
