using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YJTunnel.Server;

/// <summary>
/// 服务端持久化 JSON 源生成上下文（v1.22，AOT 兼容）：
/// customers.json / pending.json / tunnelserver.json / events.jsonl 全部走这里，
/// 不再依赖反射序列化（本机一体机模式以 NativeAOT 发布，反射默认被禁用）。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<CustomerRecord>))]
[JsonSerializable(typeof(List<PendingRecord>))]
[JsonSerializable(typeof(TunnelServerHostConfig))]
[JsonSerializable(typeof(EventEntry))]
[JsonSerializable(typeof(EventData))]
internal sealed partial class ServerJson : JsonSerializerContext
{
    /// <summary>存储类（customers/pending/events）：缩进 + 中文不转义，方便人工排查。</summary>
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>配置类（tunnelserver.json）：允许注释/尾逗号、大小写不敏感（与旧配置兼容）。</summary>
    private static readonly JsonSerializerOptions ConfigOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>events.jsonl：单行 JSON（不缩进），中文不转义。</summary>
    private static readonly JsonSerializerOptions EventOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ServerJson Store { get; } = new(StoreOptions);
    public static ServerJson Events { get; } = new(EventOptions);
    public static ServerJson Config { get; } = new(ConfigOptions);
}

/// <summary>events.jsonl 单行（字段名与历史格式保持一致）。</summary>
internal sealed class EventEntry
{
    public string Time { get; set; } = "";
    public string Level { get; set; } = "";
    public string Type { get; set; } = "";
    public string Msg { get; set; } = "";
    public string? ClientId { get; set; }
    public string? RemoteIp { get; set; }
    public EventData? Data { get; set; }
}

/// <summary>port_stats 事件附加数据（原匿名对象改强类型，AOT 安全）。</summary>
public sealed class EventData
{
    public int Port { get; set; }
    public long Connections { get; set; }
    public long BytesUp { get; set; }
    public long BytesDown { get; set; }
}
