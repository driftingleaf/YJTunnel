using System.Buffers.Binary;
using System.Text;
using YJTunnel.Protocol;

namespace YJTunnel.Tests.Protocol;

public class FrameCodecTests
{
    private static (MemoryStream, byte[]) RoundTripPipe()
    {
        var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("hello-隧道");
        return (stream, payload);
    }

    [Fact]
    public async Task RoundTrip_AllFrameTypes()
    {
        foreach (FrameType type in Enum.GetValues<FrameType>())
        {
            var stream = new MemoryStream();
            var payload = type switch
            {
                FrameType.Ping or FrameType.Pong => ReadOnlyMemory<byte>.Empty,
                _ => Encoding.UTF8.GetBytes($"payload-{type}"),
            };
            await FrameCodec.WriteFrameAsync(stream, type, payload);

            stream.Position = 0;
            var frame = await FrameCodec.ReadFrameAsync(stream);
            Assert.NotNull(frame);
            Assert.Equal(type, frame.Value.Type);
            Assert.True(payload.Span.SequenceEqual(frame.Value.Payload.Span));
        }
    }

    [Fact]
    public async Task RoundTrip_MaxFrameSize()
    {
        var stream = new MemoryStream();
        var payload = new byte[WireConstants.MaxFrameSize - 1]; // Len = 1 + payload ≤ 64KB
        new Random(42).NextBytes(payload);
        await FrameCodec.WriteFrameAsync(stream, FrameType.Open, payload);

        stream.Position = 0;
        var frame = await FrameCodec.ReadFrameAsync(stream);
        Assert.NotNull(frame);
        Assert.Equal(payload.Length, frame.Value.Payload.Length);
    }

    [Fact]
    public async Task OversizedFrame_Throws()
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, WireConstants.MaxFrameSize + 1);
        stream.Write(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task UnknownFrameType_Throws()
    {
        var stream = new MemoryStream();
        var body = new byte[] { 0x7F, 0x01, 0x02 }; // Type=0x7F 未知
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ZeroLengthFrame_Throws()
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 0);
        stream.Write(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task StreamClosed_ReturnsNull()
    {
        var stream = new MemoryStream();
        stream.Position = 0;
        Assert.Null(await FrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task TruncatedBody_Throws()
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 100);
        stream.Write(header);
        stream.Write(new byte[10]); // 只写了 10/100 字节
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public void OpenFrame_RoundTrip()
    {
        var payload = OpenFrame.Encode(0x1122334455667788L, 42);
        Assert.Equal(12, payload.Length);
        var (attachId, idx) = OpenFrame.Decode(payload);
        Assert.Equal(0x1122334455667788L, attachId);
        Assert.Equal(42, idx);
    }

    [Fact]
    public void OpenFrame_WrongLength_Throws()
    {
        Assert.Throws<ProtocolViolationException>(() => OpenFrame.Decode(new byte[11]));
    }

    [Fact]
    public void Json_CamelCase_RoundTrip_IgnoresCase()
    {
        var login = new LoginMessage
        {
            Ver = "1.0",
            ClientId = "cid",
            Token = "tk",
            AppVersion = "1.2.3",
            Mappings = { new LoginMapping { Key = "1433", LocalIp = "127.0.0.1", LocalPort = 1433, RequestedRemotePort = 2001 } },
        };
        var json = TunnelJson.Serialize(login);
        Assert.Contains("\"clientId\"", json); // camelCase 输出

        // 大小写不敏感（旧 PascalCase 兼容）
        var pascal = "{\"Ver\":\"1.0\",\"ClientId\":\"cid2\",\"Token\":\"tk\",\"Mappings\":[{\"Key\":\"9001\",\"LocalPort\":9001,\"RequestedRemotePort\":0}]}";
        var parsed = TunnelJson.Deserialize<LoginMessage>(pascal);
        Assert.NotNull(parsed);
        Assert.Equal("cid2", parsed.ClientId);
        Assert.Single(parsed.Mappings);
        Assert.Equal(9001, parsed.Mappings[0].LocalPort);
    }
}
