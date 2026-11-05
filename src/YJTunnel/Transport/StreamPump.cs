using System.Net.Sockets;

namespace YJTunnel.Transport;

/// <summary>
/// 数据泵（§5.5，根因 4 修复）：每 attach 两条独立搬运；任一方向读到 0 → 对端 Shutdown(Send)；
/// 两方向均结束才关闭（正确半关闭传播）。64KB 缓冲。
/// </summary>
public static class StreamPump
{
    public const int BufferSize = 64 * 1024;

    /// <summary>
    /// 双向搬运两个已连接 socket 的数据，直至双方读尽或出错。完成后关闭两个 socket。
    /// onBytes(方向 0=a→b / 1=b→a, 字节数) 可选用于流量统计。
    /// </summary>
    public static async Task CopyBothAsync(Socket a, Socket b, CancellationToken ct = default, Action<int, long>? onBytes = null)
    {
        var ab = Task.Run(() => PumpOneAsync(a, b, 0, ct, onBytes), ct);
        var ba = Task.Run(() => PumpOneAsync(b, a, 1, ct, onBytes), ct);
        try
        {
            await Task.WhenAll(ab, ba);
        }
        catch
        {
            // 单方向异常不影响另一方向排空
        }
        finally
        {
            SafeDispose(a);
            SafeDispose(b);
        }
    }

    private static async Task PumpOneAsync(Socket from, Socket to, int direction, CancellationToken ct, Action<int, long>? onBytes)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, BufferSize), ct);
                if (read == 0)
                    break;
                if (onBytes != null) onBytes(direction, read);
                await to.SendAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外部取消
        }
        catch
        {
            // 连接重置等：该方向到此为止
        }
        finally
        {
            // 半关闭：本方向读尽 → 停止对端的发送（对端仍可继续发来数据由另一方向处理）
            try { to.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private static void SafeDispose(Socket socket)
    {
        try { socket.Close(); } catch { }
        try { socket.Dispose(); } catch { }
    }
}
