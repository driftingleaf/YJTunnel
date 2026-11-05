using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YJTunnel.Protocol;

/// <summary>
/// 控制帧载荷 JSON 消息（统一 camelCase 序列化）。
/// </summary>
public static class TunnelJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 大小写不敏感：兼容旧 FastTunnel PascalCase JSON 同形迁移（§八）
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>登录请求（C→S）。key = localPort 字符串，是映射的稳定标识。</summary>
public sealed class LoginMessage
{
    public string Ver { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Token { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string? MachineName { get; set; }

    /// <summary>公司名称（ServerHost 从 TB_System/本地缓存解析，服务端用于自动命名客户档案；可空）。</summary>
    public string? CompanyName { get; set; }

    public List<LoginMapping> Mappings { get; set; } = new();
}

/// <summary>登录请求中的单个映射申请。RequestedRemotePort=0 表示不指定（服务端自动分配）。</summary>
public sealed class LoginMapping
{
    public string Key { get; set; } = "";
    public string LocalIp { get; set; } = "";
    public int LocalPort { get; set; }
    public int RequestedRemotePort { get; set; }
}

/// <summary>登录应答（S→C）。SessionSecret 为 32 字节的 Base64。整体 code 非 0 时 Mappings 为空。</summary>
public sealed class LoginRespMessage
{
    public int Code { get; set; }
    public string Msg { get; set; } = "";
    public string? SessionSecret { get; set; }
    public List<LoginRespMapping> Mappings { get; set; } = new();
}

/// <summary>登录应答中的单个映射结果。Status 取 ErrorCodes（Ok/PortOccupied/ServerError 等）。</summary>
public sealed class LoginRespMapping
{
    public string Key { get; set; } = "";
    public int RemotePort { get; set; }
    public int Status { get; set; }
    public string Msg { get; set; } = "";
}

/// <summary>会话级错误（S→C，Error 帧）。</summary>
public sealed class ErrorMessage
{
    public int Code { get; set; }
    public string Msg { get; set; } = "";
}

/// <summary>
/// Open 帧（S→C）编解码：Payload = attachId(8B 大端) + mappingIdx(4B 大端)。
/// </summary>
public static class OpenFrame
{
    public const int PayloadLength = WireConstants.AttachIdLength + 4;

    public static byte[] Encode(long attachId, int mappingIdx)
    {
        var buf = new byte[PayloadLength];
        BinaryPrimitives.WriteInt64BigEndian(buf, attachId);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(WireConstants.AttachIdLength), mappingIdx);
        return buf;
    }

    public static (long AttachId, int MappingIdx) Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
            throw new ProtocolViolationException($"Open 帧 Payload 长度应为 {PayloadLength}，实际 {payload.Length}");
        var attachId = BinaryPrimitives.ReadInt64BigEndian(payload);
        var mappingIdx = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(WireConstants.AttachIdLength));
        return (attachId, mappingIdx);
    }
}

/// <summary>协议违规（超长帧、未知 Type、首字节非魔数、attachId 无效等），一律立即断开连接。</summary>
public class ProtocolViolationException : Exception
{
    public ProtocolViolationException(string message) : base(message) { }
}
