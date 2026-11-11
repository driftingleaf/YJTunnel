using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YJTunnel.Protocol;

/// <summary>
/// 控制帧载荷 JSON 消息（统一 camelCase 序列化）。
/// AOT：Serialize/Deserialize 走源生成上下文（TunnelJsonContext），反射路径仅为泛型兜底。
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

    // 方法级抑制：协议类型全部走上方源生成路径；反射兜底仅服务非协议类型（测试辅助等）
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "协议类型走 TunnelJsonContext 源生成")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "协议类型走 TunnelJsonContext 源生成")]
    public static string Serialize<T>(T value)
        => value switch
        {
            LoginMessage m => JsonSerializer.Serialize(m, TunnelJsonContext.Default.LoginMessage),
            LoginRespMessage m => JsonSerializer.Serialize(m, TunnelJsonContext.Default.LoginRespMessage),
            ErrorMessage m => JsonSerializer.Serialize(m, TunnelJsonContext.Default.ErrorMessage),
            TunnelConfig m => JsonSerializer.Serialize(m, TunnelJsonContext.Default.TunnelConfig),
            _ => JsonSerializer.Serialize(value, Options),
        };

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "协议类型走 TunnelJsonContext 源生成")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "协议类型走 TunnelJsonContext 源生成")]
    public static T? Deserialize<T>(string json)
    {
        if (typeof(T) == typeof(LoginMessage)) return (T?)(object?)JsonSerializer.Deserialize(json, TunnelJsonContext.Default.LoginMessage);
        if (typeof(T) == typeof(LoginRespMessage)) return (T?)(object?)JsonSerializer.Deserialize(json, TunnelJsonContext.Default.LoginRespMessage);
        if (typeof(T) == typeof(ErrorMessage)) return (T?)(object?)JsonSerializer.Deserialize(json, TunnelJsonContext.Default.ErrorMessage);
        if (typeof(T) == typeof(TunnelConfig)) return (T?)(object?)JsonSerializer.Deserialize(json, TunnelJsonContext.Default.TunnelConfig);
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}

/// <summary>AOT 源生成 JSON 元数据（协议消息 + 客户端配置）。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LoginMessage))]
[JsonSerializable(typeof(LoginRespMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(TunnelConfig))]
internal sealed partial class TunnelJsonContext : JsonSerializerContext;

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
