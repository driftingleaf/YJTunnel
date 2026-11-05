using System.Buffers.Binary;
using System.Net.Sockets;
using YJTunnel.Protocol;
using YJTunnel.Transport;

namespace YJTunnel.Client;

/// <summary>
/// 数据附加通道工厂：客户端收到 Open 后，向服务端建立 0xC2 数据连接
/// （attachId(8B) + sessionSecret(32B)，随后纯字节流双向互泵）。
/// </summary>
public static class DataChannelFactory
{
    /// <summary>建立到服务端的数据附加连接并完成鉴权头写入，返回已就绪的 socket。</summary>
    public static async Task<Socket> OpenAsync(string serverAddr, int serverPort, long attachId, byte[] sessionSecret, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        var client = await TcpTransport.ConnectAsync(serverAddr, serverPort, connectTimeout, ct);
        var socket = client.Client;
        try
        {
            var head = new byte[1 + WireConstants.AttachIdLength + WireConstants.SessionSecretLength];
            head[0] = WireConstants.MagicData;
            BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(1), attachId);
            sessionSecret.AsSpan(0, Math.Min(sessionSecret.Length, WireConstants.SessionSecretLength))
                .CopyTo(head.AsSpan(1 + WireConstants.AttachIdLength));
            await socket.SendAsync(head, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            client.Dispose();
            throw;
        }
    }
}
