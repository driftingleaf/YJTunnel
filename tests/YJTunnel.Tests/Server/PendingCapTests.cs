using System.Net;
using System.Net.Sockets;
using YJTunnel.Protocol;
using YJTunnel.Server;

namespace YJTunnel.Tests.Server;

/// <summary>
/// 内存保护（v1.18）：pending 附加连接有全局/每会话上限——
/// 外部对远端端口的连接洪水不会撑爆内存（每条 pending 持有来访 socket + 10s 生命周期）。
/// 裸登录客户端登录后保持连接但忽略 Open（不建数据通道），pending 只能等超时释放。
/// </summary>
public class PendingCapTests
{
    [Fact]
    public async Task 来访洪水_超过每会话上限的来访被拒绝()
    {
        using var server = TestTunnelServer.Start(o =>
        {
            o.MaxPendingAttaches = 4;
            o.MaxPendingPerSession = 4;
        });

        // 裸登录客户端：登录成功后保持控制连接、忽略 Open 帧
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.Connect(IPAddress.Loopback, server.ControlPort);
        sock.NoDelay = true;
        await sock.SendAsync(new byte[] { WireConstants.MagicControl });
        using (var ns = new NetworkStream(sock, ownsSocket: false))
        {
            await FrameCodec.WriteJsonAsync(ns, FrameType.Login, new LoginMessage
            {
                Ver = WireConstants.ProtocolVersion,
                ClientId = "flood",
                Token = "test-token",
                Mappings = { new LoginMapping { Key = "20000", LocalIp = "127.0.0.1", LocalPort = 20000 } },
            });
            var resp = await FrameCodec.ReadJsonAsync<LoginRespMessage>(ns, FrameType.LoginResp);
            Assert.NotNull(resp);
            Assert.Equal(ErrorCodes.Ok, resp!.Code);
        }

        var remotePort = server.Server.Customers.FindCustomer("flood")!.Mappings.Single().RemotePort;

        // 洪水 10 条来访连接（上限 4）
        var visitors = new List<Socket>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                v.Connect(IPAddress.Loopback, remotePort);
                v.NoDelay = true;
                visitors.Add(v);
            }
            await Task.Delay(300); // 让服务端处理完 accept/open/限额

            // 存活（未被限额关闭）的来访连接必须 ≤ 4：
            // 服务端已 Shutdown 的 socket，第二次写会抛异常（第一次可能进内核缓冲）
            var alive = 0;
            foreach (var v in visitors)
            {
                try
                {
                    await v.SendAsync(new byte[] { 1 });
                    await Task.Delay(50);
                    await v.SendAsync(new byte[] { 2 });
                    alive++;
                }
                catch { /* 已被服务端关闭 */ }
            }
            Assert.InRange(alive, 1, 4);
        }
        finally
        {
            foreach (var v in visitors) v.Dispose();
            sock.Dispose();
            server.Dispose();
        }
    }
}
