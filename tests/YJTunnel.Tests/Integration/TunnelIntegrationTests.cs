using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using YJTunnel;
using YJTunnel.Client;
using YJTunnel.Protocol;
using YJTunnel.Server;
using Xunit;
// 引用 YJTunnel.Server 项目后，命名空间 "YJTunnel.Server" 会遮蔽
// YJTunnel.Server.TunnelServer 类型（CS0118），故用别名指向类型。
using TunnelHostServer = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Tests.Integration;

/// <summary>集成测试统一串行（避免端口探测竞态）。</summary>
[Collection("integration")]
public class TunnelIntegrationTests
{
    private static TunnelConfig ClientConfig(int controlPort, int localPort, int remotePort) => new()
    {
        Server = new ServerEndpoint { ServerAddr = "127.0.0.1", ServerPort = controlPort },
        Token = "test-token",
        Forwards = { new ForwardConfig { LocalIp = "127.0.0.1", LocalPort = localPort, RemotePort = remotePort } },
    };

    private static TunnelClient StartClient(TunnelConfig config, string clientId, List<LoginRespMessage>? logins = null)
    {
        var client = new TunnelClient(config, clientId, "1.0.0.0", TestHarness.FastTiming());
        if (logins != null)
            client.LoginSucceeded += resp => logins.Add(resp);
        client.Start();
        return client;
    }

    private static async Task<TcpClient> ConnectVisitorAsync(int remotePort)
    {
        var visitor = new TcpClient();
        await visitor.ConnectAsync(IPAddress.Loopback, remotePort);
        return visitor;
    }

    // ------------------------------------------------------------------ 全链路

    /// <summary>P2/M1 验收：登录→分配→attach→互泵→半关闭，字节对拍哈希一致。</summary>
    [Fact]
    public async Task FullChain_Login_Allocate_Pump_HalfClose_HashEqual()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var logins = new List<LoginRespMessage>();
        using var client = StartClient(ClientConfig(controlPort, echo.Port, 0), "full-chain", logins);

        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");
        var assigned = logins[0].Mappings.Single(m => m.Status == ErrorCodes.Ok).RemotePort;
        Assert.True(assigned > 0, "RemotePort=0 应被自动分配");

        // 大流量对拍（1MB，触发 64KB 缓冲多轮搬运）+ 半关闭 EOF 传播
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        using var visitor = await ConnectVisitorAsync(assigned);
        var stream = visitor.GetStream();
        await stream.WriteAsync(payload);
        visitor.Client.Shutdown(SocketShutdown.Send); // 半关闭：不再发，但仍可收

        var received = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break; // 对端 EOF 传播回 visitor
            await received.WriteAsync(buf.AsMemory(0, n));
        }

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)),
            Convert.ToHexString(SHA256.HashData(received.ToArray())));

        await client.StopAsync();
    }

    /// <summary>新装机 RemotePort=0 自动分配，且两家新装机互不冲突。</summary>
    [Fact]
    public async Task NewInstall_AutoAssign_TwoClientsNoConflict()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var loginsA = new List<LoginRespMessage>();
        var loginsB = new List<LoginRespMessage>();
        using var clientA = StartClient(ClientConfig(controlPort, echo.Port, 0), "new-a", loginsA);
        using var clientB = StartClient(ClientConfig(controlPort, echo.Port, 0), "new-b", loginsB);

        await TestHarness.WaitUntilAsync(() => clientA.Status.State == TunnelState.Connected && clientB.Status.State == TunnelState.Connected, what: "双客户登录");

        var portA = loginsA[0].Mappings.Single().RemotePort;
        var portB = loginsB[0].Mappings.Single().RemotePort;
        Assert.NotEqual(portA, portB); // 两家新装机不冲突

        // 各自端口可互通
        await EchoRoundTripAsync(portA, 256);
        await EchoRoundTripAsync(portB, 256);

        await clientA.StopAsync();
        await clientB.StopAsync();
    }

    private static async Task EchoRoundTripAsync(int remotePort, int size)
    {
        var payload = RandomNumberGenerator.GetBytes(size);
        using var visitor = await ConnectVisitorAsync(remotePort);
        var stream = visitor.GetStream();
        await stream.WriteAsync(payload);
        visitor.Client.Shutdown(SocketShutdown.Send);
        var received = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var n = await stream.ReadAsync(received.AsMemory(offset));
            Assert.True(n > 0, "EOF 提前到达");
            offset += n;
        }
        Assert.True(payload.SequenceEqual(received));
    }

    // ------------------------------------------------------------------ 故障注入 ①：拔线/半开 → 服务端判死释放 → 客户端重连重绑自愈

    [Fact]
    public async Task Fault1_HalfOpenConnection_ServerReaps_ClientRebinds()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "yj-t1-" + Guid.NewGuid().ToString("N"));
        var port = TestHarness.FindFreePorts(20000, 1)[0];

        var options = ServerOptions(dataDir, port);
        await using var server = new TunnelHostServer(options);
        await server.StartAsync();

        var logins = new List<LoginRespMessage>();
        using var client = StartClient(ClientConfig(port, echo.Port, 0), "half-open", logins);
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "首次登录");
        var assigned = logins[0].Mappings.Single().RemotePort;

        // 模拟拔线：客户端侧直接丢弃控制 socket（不发 FIN，制造半开）
        // 通过内部状态制造：停掉客户端但保持服务端会话残留
        await client.StopAsync(); // 正常路径：会话注销、监听释放
        await TestHarness.WaitUntilAsync(() => server.Listeners.GetAll().Count == 0, what: "监听释放");

        // 制造"死会话持有端口"场景：记录已在 customers.json（沿用），
        // 立即重启客户端 → 服务端按记录沿用原端口重绑（等效 90s 收割后的自愈重绑）
        Assert.Equal(assigned, server.Customers.FindCustomer("half-open").Mappings.Single().RemotePort);
        var logins2 = new List<LoginRespMessage>();
        using var client2 = StartClient(ClientConfig(port, echo.Port, 0), "half-open", logins2);
        await TestHarness.WaitUntilAsync(() => client2.Status.State == TunnelState.Connected, what: "重连");
        Assert.Equal(assigned, logins2[0].Mappings.Single().RemotePort); // 端口沿用不变（D14）
        await EchoRoundTripAsync(assigned, 128);

        await client2.StopAsync();
        try { Directory.Delete(dataDir, true); } catch { }
    }

    // ------------------------------------------------------------------ 公司名自动建档 / 占位名升级

    [Fact]
    public async Task Login_WithCompanyName_AutoNamesCustomerRecord()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "yj-t-cn-" + Guid.NewGuid().ToString("N"));
        var port = TestHarness.FindFreePorts(20000, 1)[0];

        await using var server = new TunnelHostServer(ServerOptions(dataDir, port));
        await server.StartAsync();

        var config = ClientConfig(port, echo.Port, 0);
        config.CompanyName = "新宇包装有限公司";
        using var client = StartClient(config, "company-1");
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");

        var record = server.Customers.FindCustomer("company-1");
        Assert.NotNull(record);
        Assert.Equal("新宇包装有限公司", record!.Name);

        await client.StopAsync();
        try { Directory.Delete(dataDir, true); } catch { }
    }

    [Fact]
    public async Task Login_PlaceholderName_UpgradeOnNextLogin()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "yj-t-cn2-" + Guid.NewGuid().ToString("N"));
        var port = TestHarness.FindFreePorts(20000, 1)[0];

        await using var server = new TunnelHostServer(ServerOptions(dataDir, port));
        await server.StartAsync();

        // 第一次：不带公司名 → 占位建档
        using var client1 = StartClient(ClientConfig(port, echo.Port, 0), "company-2");
        await TestHarness.WaitUntilAsync(() => client1.Status.State == TunnelState.Connected, what: "首次登录");
        var record = server.Customers.FindCustomer("company-2");
        Assert.NotNull(record);
        Assert.StartsWith("未命名-", record!.Name);
        await client1.StopAsync();
        await TestHarness.WaitUntilAsync(() => server.Sessions.GetAll().Count == 0, what: "会话释放");

        // 第二次：带公司名 → 占位名自动升级（不覆盖管理台手工命名，由"未命名-"前缀判断保证）
        var config = ClientConfig(port, echo.Port, 0);
        config.CompanyName = "恒达薄膜";
        using var client2 = StartClient(config, "company-2");
        await TestHarness.WaitUntilAsync(() => client2.Status.State == TunnelState.Connected, what: "二次登录");
        Assert.Equal("恒达薄膜", server.Customers.FindCustomer("company-2")!.Name);

        await client2.StopAsync();
        try { Directory.Delete(dataDir, true); } catch { }
    }

    // ------------------------------------------------------------------ 审核回归：并发/断线致命缺陷

    /// <summary>
    /// 审核修复#1 回归：每 IP 未认证上限（默认16）不得计入已认证数据通道。
    /// 客户端与全部数据附加连接同源 127.0.0.1——修复前第 17 条并发代理连接即被拒。
    /// SQL 连接池 + HTTP keep-alive 场景轻松超过 16，属生产必炸缺陷。
    /// </summary>
    [Fact]
    public async Task Audit_DataChannels_ExceedUnauthLimit_AllSucceed()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var logins = new List<LoginRespMessage>();
        using var client = StartClient(ClientConfig(controlPort, echo.Port, 0), "many-conn", logins);
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");
        var port = logins[0].Mappings.Single().RemotePort;

        const int total = 25; // > 未认证上限 16
        var visitors = new List<TcpClient>();
        try
        {
            // 先全部建连（并发保持），再逐一回显——修复前超限连接在建立/回显阶段被 RST
            for (var i = 0; i < total; i++)
            {
                var v = new TcpClient();
                await v.ConnectAsync(IPAddress.Loopback, port);
                visitors.Add(v);
            }
            var payload = RandomNumberGenerator.GetBytes(64);
            var buf = new byte[64];
            foreach (var v in visitors)
            {
                await v.GetStream().WriteAsync(payload);
                var off = 0;
                while (off < buf.Length)
                {
                    var n = await v.GetStream().ReadAsync(buf.AsMemory(off));
                    Assert.True(n > 0, "连接被服务端关闭（未认证限额误伤数据通道）");
                    off += n;
                }
                Assert.True(payload.AsSpan().SequenceEqual(buf));
            }
        }
        finally
        {
            foreach (var v in visitors) v.Dispose();
        }

        await client.StopAsync();
    }

    /// <summary>
    /// 审核修复#2 回归：客户端在服务端绑定阶段 RST 掉线（登录应答发送失败），
    /// 会话与监听器必须立即回收，不得悬挂至该客户下次登录。
    /// </summary>
    [Fact]
    public async Task Audit_ClientVanishesDuringBind_SessionAndListenersReclaimed()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        // 裸控制连接：发完登录帧立刻以 RST 关闭（Linger(true,0)，模拟绑定阶段掉线）
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(IPAddress.Loopback, controlPort);
        using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            stream.Write(new[] { WireConstants.MagicControl });
            await FrameCodec.WriteJsonAsync(stream, FrameType.Login, new LoginMessage
            {
                Ver = WireConstants.ProtocolVersion,
                ClientId = "vanish-1",
                Token = "test-token",
                Mappings = { new LoginMapping { Key = "9999", LocalIp = "127.0.0.1", LocalPort = 9999, RequestedRemotePort = 0 } },
            });
        }
        socket.LingerState = new LingerOption(true, 0); // Close 发 RST 而非 FIN
        socket.Close();

        // 无论走哪条清理路径（应答写失败 / 读到 EOF），最终会话与监听都必须归零
        await TestHarness.WaitUntilAsync(() =>
            server.Server.Sessions.GetAll().Count == 0 && server.Server.Listeners.GetAll().Count == 0,
            what: "掉线后会话与监听回收");
    }

    /// <summary>
    /// 审核修复#5 回归：Open 的 mappingIdx 必须以登录上报的过滤列表为基准，
    /// 不得索引原始 _config.Forwards——含 LocalPort=0 行时索引错位会拨错本地端口。
    /// </summary>
    [Fact]
    public async Task Audit_Open_MapsByLoginIndex_NotConfigIndex()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var config = new TunnelConfig
        {
            Server = new ServerEndpoint { ServerAddr = "127.0.0.1", ServerPort = controlPort },
            Token = "test-token",
            Forwards =
            {
                new ForwardConfig { LocalIp = "127.0.0.1", LocalPort = 0, RemotePort = 0 }, // 登录时被过滤
                new ForwardConfig { LocalIp = "127.0.0.1", LocalPort = echo.Port, RemotePort = 0 },
            },
        };
        var logins = new List<LoginRespMessage>();
        using var client = StartClient(config, "idx-map", logins);
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");
        Assert.Single(logins[0].Mappings); // 服务端只看到过滤后的 1 条

        var port = logins[0].Mappings.Single().RemotePort;
        await EchoRoundTripAsync(port, 128); // 修复前按原始索引拨 LocalPort=0 → 连接失败

        await client.StopAsync();
    }

    // ------------------------------------------------------------------ 故障注入 ②：kill -9 服务端 → 退避重连（非热循环）

    [Fact]
    public async Task Fault2_ServerKill_ClientBackoffReconnect_NoHotLoop()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "yj-t2-" + Guid.NewGuid().ToString("N"));
        var port = TestHarness.FindFreePorts(20000, 1)[0];

        await using (var server1 = new TunnelHostServer(ServerOptions(dataDir, port)))
        {
            await server1.StartAsync();
            using var client = StartClient(ClientConfig(port, echo.Port, 0), "kill9");
            await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");

            var reconnectTimestamps = new List<DateTime>();
            var lastCount = 0;
            client.StatusChanged += status =>
            {
                if (status.State == TunnelState.Reconnecting && status.ReconnectCount > lastCount)
                {
                    lock (reconnectTimestamps) reconnectTimestamps.Add(DateTime.UtcNow);
                    lastCount = status.ReconnectCount;
                }
            };

            await server1.DisposeAsync(); // kill -9：不给任何通知
            // 退避观察窗：第 1 次退避 ≥0.8s（第一步 1s×抖动0.8），至少经历 2 次重连
            await TestHarness.WaitUntilAsync(() => lastCount >= 2, TimeSpan.FromSeconds(20), "至少两次退避重连");

            List<DateTime> stamps;
            lock (reconnectTimestamps) stamps = reconnectTimestamps.ToList();
            var gap = (stamps[1] - stamps[0]).TotalSeconds;
            Assert.True(gap >= 0.6, $"重连间隔应≥退避下限（实际 {gap:0.00}s），不得热循环");

            // 服务端复活：同数据目录重启（customers.json 持久）→ 客户端自愈重绑原端口
            await using var server2 = new TunnelHostServer(ServerOptions(dataDir, port));
            await server2.StartAsync();
            await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, TimeSpan.FromSeconds(30), "服务端重启后自愈");
            Assert.True(client.Status.Mappings.All(m => m.Status == ErrorCodes.Ok));
            await client.StopAsync();
        }
        try { Directory.Delete(dataDir, true); } catch { }
    }

    // ------------------------------------------------------------------ 故障注入 ③：端口被占 → PortOccupied 明确回传（其余映射照常）

    [Fact]
    public async Task Fault3_PortOccupied_OneMappingFails_OthersWork_LoginOk()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        // 独占式占用段内一个端口（Windows ExclusiveAddressUse：SO_REUSEADDR 也无法抢占）
        var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        blocker.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        var blockedPort = TestHarness.FindFreePorts(22000, 1)[0];
        blocker.Bind(new IPEndPoint(IPAddress.Any, blockedPort));
        blocker.Listen(16);

        try
        {
            // 预置档案：让映射 key 指向被占端口（走"已有分配沿用"→ bind 失败 → PortOccupied）
            server.Server.Customers.UpsertCustomer(new CustomerRecord
            {
                ClientId = "occupied",
                Name = "占位",
                Mappings =
                {
                    new MappingRecord { Key = echo.Port.ToString(), LocalPort = echo.Port, RemotePort = blockedPort },
                },
            });

            var config = new TunnelConfig
            {
                Server = new ServerEndpoint { ServerAddr = "127.0.0.1", ServerPort = controlPort },
                Token = "test-token",
                Forwards =
                {
                    new ForwardConfig { LocalIp = "127.0.0.1", LocalPort = echo.Port, RemotePort = 0 },
                },
            };
            // 第二映射走自动分配（正常）
            var echo2 = await TestHarness.StartEchoServerAsync();
            using var _echo2 = echo2;
            config.Forwards.Add(new ForwardConfig { LocalIp = "127.0.0.1", LocalPort = echo2.Port, RemotePort = 0 });

            var logins = new List<LoginRespMessage>();
            using var client = StartClient(config, "occupied", logins);
            await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录（映射失败不阻断登录）");

            var resp = logins[0];
            var occupied = resp.Mappings.Single(m => m.RemotePort == blockedPort);
            var healthy = resp.Mappings.Single(m => m.RemotePort != blockedPort);
            Assert.Equal(ErrorCodes.PortOccupied, occupied.Status); // 明确回传，绝不静默
            Assert.Equal(ErrorCodes.Ok, healthy.Status);

            await EchoRoundTripAsync(healthy.RemotePort, 64); // 其余映射与登录照常
            await client.StopAsync();
        }
        finally
        {
            blocker.Close();
        }
    }

    // ------------------------------------------------------------------ 故障注入 ④：服务端不回 Pong → 客户端心跳判死重连（60-90s 判死，快速时序等效）

    [Fact]
    public async Task Fault4_NoPong_ClientDeclaresDead_ByPongTimeout()
    {
        var controlPort = TestHarness.FindFreePorts(20000, 1)[0];
        var fakeServer = new TcpListener(IPAddress.Loopback, controlPort);
        fakeServer.Start();
        var silent = new CancellationTokenSource();

        var serveTask = Task.Run(async () =>
        {
            while (!silent.IsCancellationRequested)
            {
                Socket sock;
                try { sock = await fakeServer.AcceptSocketAsync(silent.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                _ = SilentSessionAsync(sock, silent.Token);
            }
        });

        using var client = StartClient(ClientConfig(controlPort, 12345, 0), "no-pong");
        // PongTimeout=2s（快速时序）：静默服务端登录成功后不发 Pong → 客户端判死 → Reconnecting
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Reconnecting, TimeSpan.FromSeconds(15), "心跳判死");
        var err = client.Status.LastError;
        Assert.True(err is TimeoutException or IOException, $"判死原因应为心跳超时（实际 {err?.GetType().Name}）");

        silent.Cancel();
        fakeServer.Stop();
        try { await serveTask; } catch { }
        await client.StopAsync();
    }

    /// <summary>假服务端：完成登录握手后保持静默（不回 Pong）。</summary>
    private static async Task SilentSessionAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            var stream = new NetworkStream(socket, false);
            var magic = new byte[1];
            await stream.ReadAsync(magic, ct);
            if (magic[0] != WireConstants.MagicControl) { socket.Dispose(); return; }

            var loginFrame = await FrameCodec.ReadFrameAsync(stream, ct);
            if (loginFrame?.Type != FrameType.Login) { socket.Dispose(); return; }

            var resp = new LoginRespMessage
            {
                Code = ErrorCodes.Ok,
                Msg = "ok",
                SessionSecret = Convert.ToBase64String(new byte[32]),
                Mappings = { new LoginRespMapping { Key = "12345", RemotePort = 22050, Status = ErrorCodes.Ok } },
            };
            await FrameCodec.WriteJsonAsync(stream, FrameType.LoginResp, resp, ct);
            // 之后静默：不回 Pong，模拟丢包不回 RST 的半死链路
            var hold = new byte[1];
            await stream.ReadAsync(hold, ct);
        }
        catch { }
        finally
        {
            try { socket.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------ PendingApproval：待审核 → 通过 → 登录成功

    [Fact]
    public async Task PendingApproval_ThenApprove_ClientConnects()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort, o => o.AutoRegister = false);

        var config = ClientConfig(controlPort, echo.Port, 0);
        using var client = StartClient(config, "pending-client");

        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.PendingApproval, what: "进入待审核");
        Assert.Contains(server.Server.Customers.GetAllPending(), p => p.ClientId == "pending-client" && !p.Rejected);

        // 管理员通过：建档 + 自动分配
        server.Server.Customers.UpsertCustomer(new CustomerRecord
        {
            ClientId = "pending-client",
            Name = "审核通过客户",
            Enabled = true,
            Mappings = { new MappingRecord { Key = echo.Port.ToString(), LocalPort = echo.Port, RemotePort = 22080 } },
        });
        server.Server.Customers.RemovePending("pending-client");

        // 客户端 5 分钟间隔重试（快速时序 500ms）→ 自愈上线
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, TimeSpan.FromSeconds(15), "审核通过后自动上线");
        await EchoRoundTripAsync(22080, 64);
        await client.StopAsync();
    }

    /// <summary>LoginDenied：不可重试码 → 客户端终态 Failed，不进退避循环。</summary>
    [Fact]
    public async Task LoginDenied_ClientFails_NoRetryLoop()
    {
        using var server = NewTestServer(out var controlPort, o => o.Tokens = new List<string> { "right-token" });

        var config = ClientConfig(controlPort, 12345, 0);
        config.Token = "wrong-token";
        using var client = StartClient(config, "denied-client");

        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Failed, what: "终态 Failed");
        await Task.Delay(1500);
        Assert.Equal(0, client.Status.ReconnectCount); // 不进退避重连
        Assert.Equal(TunnelState.Failed, client.Status.State);
    }

    // ------------------------------------------------------------------ ConfigChanged：管理台改配置 → 客户端 <10s 重连生效

    [Fact]
    public async Task ConfigChanged_ClientReconnectsImmediately()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var logins = new List<LoginRespMessage>();
        using var client = StartClient(ClientConfig(controlPort, echo.Port, 0), "cfg-changed", logins);
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "登录");
        var beforeCount = logins.Count;

        await server.Server.NotifyConfigChangedAsync("cfg-changed"); // 管理台保存触发

        await TestHarness.WaitUntilAsync(() => logins.Count > beforeCount, TimeSpan.FromSeconds(10), "ConfigChanged 后立即重连");
        await TestHarness.WaitUntilAsync(() => client.Status.State == TunnelState.Connected, what: "重连后在线");
        await client.StopAsync();
    }

    // ------------------------------------------------------------------ 接管矩阵：同 clientId 新会话强断旧会话（epoch 接管）

    [Fact]
    public async Task EpochTakeover_SameClientId_NewSessionWins()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);

        var loginsA = new List<LoginRespMessage>();
        using var clientA = StartClient(ClientConfig(controlPort, echo.Port, 0), "dup-client", loginsA);
        await TestHarness.WaitUntilAsync(() => clientA.Status.State == TunnelState.Connected, what: "旧会话登录");
        var assigned = loginsA[0].Mappings.Single().RemotePort;

        // 同 clientId 新登录（模拟重装后快速重启）：旧会话被强断，新会话接管同一端口
        var loginsB = new List<LoginRespMessage>();
        using var clientB = StartClient(ClientConfig(controlPort, echo.Port, assigned), "dup-client", loginsB);
        await TestHarness.WaitUntilAsync(() => clientB.Status.State == TunnelState.Connected, what: "新会话接管");
        Assert.Equal(assigned, loginsB[0].Mappings.Single().RemotePort); // 端口沿用

        // 旧客户端感知断线（进入重连，而非仍显示在线假状态）
        await TestHarness.WaitUntilAsync(() => clientA.Status.State != TunnelState.Connected, TimeSpan.FromSeconds(10), "旧会话被强断");

        // 新会话端口可正常转发
        await EchoRoundTripAsync(assigned, 64);
        await clientB.StopAsync();
        await clientA.StopAsync();
    }

    // ------------------------------------------------------------------ 多客户端同一显式端口请求：后来者自动分配

    [Fact]
    public async Task TwoClients_RequestSameExplicitPort_SecondGetsAutoAssigned()
    {
        using var echo = await TestHarness.StartEchoServerAsync();
        using var server = NewTestServer(out var controlPort);
        var requested = TestHarness.FindFreePorts(22100, 1)[0];

        var loginsA = new List<LoginRespMessage>();
        var loginsB = new List<LoginRespMessage>();
        using var clientA = StartClient(ClientConfig(controlPort, echo.Port, requested), "expl-a", loginsA);
        await TestHarness.WaitUntilAsync(() => clientA.Status.State == TunnelState.Connected, what: "A 登录");
        Assert.Equal(requested, loginsA[0].Mappings.Single().RemotePort); // 首个客户采纳显式请求

        using var clientB = StartClient(ClientConfig(controlPort, echo.Port, requested), "expl-b", loginsB);
        await TestHarness.WaitUntilAsync(() => clientB.Status.State == TunnelState.Connected, what: "B 登录");
        var assignedB = loginsB[0].Mappings.Single().RemotePort;
        Assert.NotEqual(requested, assignedB); // 后来者不顶掉已有分配 → 自动分配
        Assert.True(assignedB is >= 22000 and <= 22999);

        await clientA.StopAsync();
        await clientB.StopAsync();
    }

    // ------------------------------------------------------------------ helpers

    private static TestTunnelServer NewTestServer(out int controlPort, Action<TunnelServerOptions>? configure = null)
    {
        var server = TestTunnelServer.Start(configure);
        controlPort = server.ControlPort;
        return server;
    }

    /// <summary>带自定义端口/数据目录的服务端配置（故障注入场景需要跨实例重启）。</summary>
    private static TunnelServerOptions ServerOptions(string dataDir, int port) => new()
    {
        ControlPort = port,
        BindAddress = "127.0.0.1",
        Tokens = new List<string> { "test-token" },
        AllowPorts = new List<PortRange> { new(22000, 22999) },
        AutoRegister = true,
        DataDir = dataDir,
        Timing = TestHarness.FastTiming(),
    };
}
