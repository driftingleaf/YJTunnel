using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace YJTunnel.Protocol;

/// <summary>一个已解码的控制帧。Payload 不含 Type 字节。</summary>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// 帧编解码器。帧格式：Len(4B 大端，≤64KB，含 Type 字节) | Type(1B) | Payload。
/// 任何违规（长度越界、流中断）抛 <see cref="ProtocolViolationException"/> 或 IOException，由调用方断开连接。
/// </summary>
public static class FrameCodec
{
    private static readonly HashSet<byte> KnownTypes = new(
        Enum.GetValues<FrameType>().Select(t => (byte)t));

    /// <summary>从流中读取一帧。流结束返回 null（对端正常关闭）；违规抛异常。</summary>
    public static async Task<Frame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct))
            return null;

        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 1 || len > WireConstants.MaxFrameSize)
            throw new ProtocolViolationException($"帧长度越界：{len}");

        var body = new byte[len];
        if (!await ReadExactAsync(stream, body, ct))
            throw new ProtocolViolationException("帧头已到但帧体被截断");

        var type = body[0];
        if (!KnownTypes.Contains(type))
            throw new ProtocolViolationException($"未知帧类型 0x{type:X2}");

        return new Frame((FrameType)type, body.AsMemory(1));
    }

    /// <summary>向流中写一帧（含 Type 字节）。</summary>
    public static async Task WriteFrameAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var len = 1 + payload.Length;
        if (len > WireConstants.MaxFrameSize)
            throw new ProtocolViolationException($"帧长度越界：{len}");

        var buf = new byte[4 + len];
        BinaryPrimitives.WriteInt32BigEndian(buf, len);
        buf[4] = (byte)type;
        payload.Span.CopyTo(buf.AsSpan(5));
        await stream.WriteAsync(buf, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读取一帧并反序列化为 JSON 消息；帧类型不匹配抛 <see cref="ProtocolViolationException"/>。</summary>
    public static async Task<T?> ReadJsonAsync<T>(Stream stream, FrameType expectType, CancellationToken ct = default)
    {
        var frame = await ReadFrameAsync(stream, ct)
            ?? throw new ProtocolViolationException($"期望 {expectType} 帧但连接已关闭");
        if (frame.Type != expectType)
            throw new ProtocolViolationException($"期望 {expectType} 帧但收到 {frame.Type}");
        var json = System.Text.Encoding.UTF8.GetString(frame.Payload.Span);
        return TunnelJson.Deserialize<T>(json);
    }

    /// <summary>序列化 JSON 消息并写为一帧。</summary>
    public static Task WriteJsonAsync<T>(Stream stream, FrameType type, T message, CancellationToken ct = default)
        => WriteFrameAsync(stream, type, System.Text.Encoding.UTF8.GetBytes(TunnelJson.Serialize(message)), ct);

    /// <summary>读满 buffer；流在半途关闭返回 false。</summary>
    public static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], ct);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }
}
