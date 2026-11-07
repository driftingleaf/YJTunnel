using System.Net;
using System.Net.Sockets;
using YJTunnel;
using YJTunnel.Server;
using Xunit;
// 引用 YJTunnel.Server 项目后，命名空间 "YJTunnel.Server" 会遮蔽
// YJTunnel.Server.TunnelServer 类型（file-scoped 命名空间下 using 别名归编译单元、
// 优先级低于外层命名空间成员，CS0118），故此处使用完整限定名。
using TunnelHostServer = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Tests;

/// <summary>测试基础设施：快速时序、随机空闲端口、本地回声服务。</summary>
public static class TestHarness
{
    /// <summary>测试用快速时序（生产值见 TunnelTimingOptions 默认；§4.3 固定值不进配置）。</summary>
    public static TunnelTimingOptions FastTiming() => new()
    {
        LoginTimeout = TimeSpan.FromSeconds(3),
        ControlReadTimeout = TimeSpan.FromSeconds(3),
        SessionIdleTimeout = TimeSpan.FromSeconds(2),
        HeartbeatSweepInterval = TimeSpan.FromMilliseconds(300),
        AttachTimeout = TimeSpan.FromSeconds(3),
        BindRetryDelays = new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400) },
        PingInterval = TimeSpan.FromMilliseconds(300),
        PongTimeout = TimeSpan.FromSeconds(2),
        StableSessionReset = TimeSpan.FromSeconds(1),
        FailuresBeforeFailover = 2,
        PendingRetryInterval = TimeSpan.FromMilliseconds(500),
        BannedRetryInterval = TimeSpan.FromMilliseconds(400),
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };

    /// <summary>在白名单段内探测 count 个当前空闲端口（bind 试探后立即释放）。</summary>
    public static List<int> FindFreePorts(int start, int count)
    {
        var ports = new List<int>();
        var port = start;
        while (ports.Count < count && port < start + 2000)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            try
            {
                l.Start();
                l.Stop();
                ports.Add(port);
            }
            catch (SocketException)
            {
                // 被占，继续下一个
            }
            port++;
        }
        return ports;
    }

    /// <summary>等待条件成立（100ms 轮询），超时抛 TimeoutException。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"等待超时：{what ?? "条件"}");
    }

    /// <summary>启动一个本地回声服务（收到什么回什么；对端半关闭则正确回写 EOF）。返回监听端口。</summary>
    public static async Task<EchoServer> StartEchoServerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new EchoServer(listener);
        _ = Task.Run(server.AcceptLoopAsync);
        return await Task.FromResult(server);
    }
}

/// <summary>回声服务：echo 全部字节；读到 EOF 后 Shutdown(Send)（配合半关闭传播验证）。</summary>
public sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public EchoServer(TcpListener listener) => _listener = listener;

    internal async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(socket));
        }
    }

    private static async Task HandleAsync(Socket socket)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await socket.ReceiveAsync(buf);
                if (n == 0) break;
                await socket.SendAsync(buf.AsMemory(0, n));
            }
        }
        catch { /* reset */ }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Send); } catch { }
            try { socket.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>测试用服务端包装：临时数据目录 + 随机控制端口 + 快速时序。</summary>
public sealed class TestTunnelServer : IDisposable, IAsyncDisposable
{
    public TunnelServerOptions Options { get; }
    public TunnelHostServer Server { get; }
    public string DataDir { get; }
    public int ControlPort { get; }

    private TestTunnelServer(TunnelServerOptions options, TunnelHostServer server, string dataDir, int port)
    {
        Options = options;
        Server = server;
        DataDir = dataDir;
        ControlPort = port;
    }

    public static TestTunnelServer Start(Action<TunnelServerOptions>? configure = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "yj-tunnel-test-" + Guid.NewGuid().ToString("N"));
        var port = FindFreePort(20000);
        var options = new TunnelServerOptions
        {
            ControlPort = port,
            BindAddress = "127.0.0.1",
            Tokens = new List<string> { "test-token" },
            AllowPorts = new List<PortRange> { new(22000, 22999) },
            AutoRegister = true,
            DataDir = dataDir,
            Timing = TestHarness.FastTiming(),
        };
        configure?.Invoke(options);
        var server = new TunnelHostServer(options);
        server.StartAsync().GetAwaiter().GetResult();
        return new TestTunnelServer(options, server, dataDir, port);
    }

    private static int FindFreePort(int start)
    {
        var ports = TestHarness.FindFreePorts(start, 1);
        Assert.True(ports.Count == 1);
        return ports[0];
    }

    public void Dispose()
    {
        try { Server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); } catch { }
        try { Directory.Delete(DataDir, true); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Server.DisposeAsync(); } catch { }
        try { Directory.Delete(DataDir, true); } catch { }
    }
}
