using System.Net;
using System.Net.Sockets;

namespace YJTunnel.Transport;

/// <summary>
/// Socket 传输层公共助手：TCP keepalive best-effort 降级、SO_REUSEADDR、带超时连接。
/// Win7/2008R2 对按连接 keepalive 参数支持不全——所有设置一律 try/catch 降级（D4：应用层心跳为唯一权威判活）。
/// </summary>
public static class TcpTransport
{
    /// <summary>
    /// 设置 TCP keepalive（best-effort，失败静默降级）。idle=空闲多久后发探测，interval=探测间隔。
    /// 参数强制下限（idle≥5s、interval≥1s）。Windows 走 SIO_KEEPALIVE_VALS（XP+ 支持，Win7 可用）；
    /// 注意：任何 socket 选项设置失败都可能让 .NET 把 Socket.Connected 标记为 false（连接实际仍存活），
    /// 因此此处绝不能用会失败的组合，失败时仅保底 SO_KEEPALIVE（应用层心跳才是权威判活，D4）。
    /// </summary>
    public static void SetKeepAliveBestEffort(Socket socket, TimeSpan idle, TimeSpan interval, int count)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch
        {
            return; // 连 SO_KEEPALIVE 都失败的极端环境：完全交给应用层心跳
        }

        var idleMs = (int)Math.Max(idle.TotalMilliseconds, 5000);
        var intervalMs = (int)Math.Max(interval.TotalMilliseconds, 1000);

        if (OperatingSystem.IsWindows())
        {
            // SIO_KEEPALIVE_VALS = 0x98000004：onoff(int32) + idleMs(int32) + intervalMs(int32)，小端
            try
            {
                var vals = new byte[12];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(vals, 1);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(vals.AsSpan(4), idleMs);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(vals.AsSpan(8), intervalMs);
                socket.IOControl(unchecked((int)0x98000004), vals, null);
            }
            catch
            {
                // 旧系统不支持 SIO_KEEPALIVE_VALS：保底 SO_KEEPALIVE 已开（系统默认参数）
            }
        }
        else
        {
            // Linux：TCP_KEEPIDLE=4 / TCP_KEEPINTVL=5 / TCP_KEEPCNT=6（秒）
            TrySet(socket, 4, idleMs / 1000);
            TrySet(socket, 5, intervalMs / 1000);
            TrySet(socket, 6, count);
        }
    }

    private static void TrySet(Socket socket, int option, int value)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)option, value); }
        catch { /* best-effort */ }
    }

    /// <summary>控制连接 keepalive 参数：应用层心跳 20s 已是主要保活手段，TCP 级仅兜底半开（参数必须远宽于心跳间隔）。</summary>
    public static void ApplyControlConnectionKeepAlive(Socket socket)
        => SetKeepAliveBestEffort(socket, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), 3);

    /// <summary>数据连接读写的 keepalive 默认参数（防 SQL 空闲池连接被 NAT 静默老化）。</summary>
    public static void ApplyDataConnectionKeepAlive(Socket socket)
        => SetKeepAliveBestEffort(socket, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 3);

    /// <summary>监听 socket 绑定前设置 SO_REUSEADDR（修复 TIME_WAIT 子连接占位导致重绑失败）。</summary>
    public static void SetReuseAddress(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch
        {
            // 平台不支持时忽略（Windows 上 SO_REUSEADDR 对监听端口语义可用）
        }
    }

    /// <summary>带连接超时的 TCP 连接（同步 DNS + 连接，超时抛 TimeoutException/SocketException）。</summary>
    public static async Task<TcpClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接 {host}:{port} 超时（{timeout.TotalSeconds:0}s）");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>判断 IPAddress 是否回环。</summary>
    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address);
}
