using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YJTunnel.Admin.Services;

/// <summary>控制台配置（仅存服务器地址，不保存密码）：%APPDATA%\YJTunnel\admin.json。</summary>
public sealed class AdminConfig
{
    /// <summary>连接形态：remote（远程管理）/ local（本机一体机，内嵌隧道引擎）。</summary>
    public string Mode { get; set; } = "remote";

    /// <summary>本机一体机模式的配置文件路径（tunnelserver.json）。</summary>
    public string LocalConfigPath { get; set; } = "";

    /// <summary>一体机附带的 YJTunnelServer.exe 路径（留空则按发布布局自动查找）。</summary>
    public string LocalServerExe { get; set; } = "";
    public string ServerAddr { get; set; } = "";
    public int AdminPort { get; set; } = 5103;

    /// <summary>启动时自动尝试登录的服务器（无密码则仍显示登录框）。</summary>
    public bool AutoConnect { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerAddr);
}

public static class AdminConfigStore
{
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YJTunnel");

    public static string DefaultPath => Path.Combine(DefaultDir, "admin.json");

    public static AdminConfig Load()
    {
        try
        {
            var path = DefaultPath;
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), AdminJson.Default.AdminConfig);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 损坏配置回退默认（保留原文件）
            try { File.Copy(DefaultPath, DefaultPath + ".bad", overwrite: true); } catch { }
        }
        return new AdminConfig();
    }

    public static void Save(AdminConfig config)
    {
        Directory.CreateDirectory(DefaultDir);
        var tmp = DefaultPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, AdminJson.Default.AdminConfig));
        File.Move(tmp, DefaultPath, overwrite: true);
    }
}

/// <summary>API DTO（与服务端 /api 返回的 camelCase 对应，反序列化大小写不敏感）。</summary>
public sealed class LoginResponse
{
    public bool Ok { get; set; }
    public string? User { get; set; }
    public string? Error { get; set; }
}

public sealed class SessionResponse
{
    public bool Authenticated { get; set; }
    public string? User { get; set; }
}

public sealed class ApiResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public static ApiResultDto Success() => new() { Ok = true };
    public static ApiResultDto Fail(string error) => new() { Ok = false, Error = error };
}

public sealed class ServerInfoDto
{
    public string Version { get; set; } = "";
    public int ControlPort { get; set; }
    public bool AutoRegister { get; set; }
    public List<string> AllowPortRanges { get; set; } = new();
    public bool AdminEnabled { get; set; }
}

public sealed class OverviewDto
{
    public int OnlineCount { get; set; }
    public int CustomerCount { get; set; }
    public int PendingCount { get; set; }
    public int PortUsed { get; set; }
    public List<PortUsageDto> Ports { get; set; } = new();
    public List<BindFailureDto> BindFailures { get; set; } = new();
    public List<OnlineSessionDto> Sessions { get; set; } = new();
}

public sealed class PortUsageDto
{
    public int RemotePort { get; set; }
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string MappingKey { get; set; } = "";
    public int LocalPort { get; set; }
    public bool Online { get; set; }
    public string BindStatus { get; set; } = "";
}

public sealed class BindFailureDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Msg { get; set; } = "";
}

public sealed class OnlineSessionDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string LoginAt { get; set; } = "";
    public string LastAlive { get; set; } = "";
    public long Connections { get; set; }
}

public sealed class CustomersResponse
{
    public List<CustomerDto> Customers { get; set; } = new();
    public List<string> OnlineIds { get; set; } = new();
}

public sealed class CustomerDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public string? LastLoginAt { get; set; }
    public DateTimeOffset? BannedUntil { get; set; }
    public List<MappingDto> Mappings { get; set; } = new();
}

public sealed class MappingDto
{
    public string Key { get; set; } = "";
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
    public string Note { get; set; } = "";
}

public sealed class PendingDto
{
    public string ClientId { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public string? FirstSeenAt { get; set; }
    public string? LastTryAt { get; set; }
    public bool Rejected { get; set; }
    public List<MappingDto> Mappings { get; set; } = new();
}

public sealed class EventDto
{
    public string Time { get; set; } = "";
    public string Level { get; set; } = "";
    public string Type { get; set; } = "";
    public string Msg { get; set; } = "";
    public string? ClientId { get; set; }
    public string? RemoteIp { get; set; }
}

// ---------------- 请求体（源生成，AOT 安全） ----------------

public sealed class LoginRequestDto
{
    public string? Username { get; set; }
    public string Password { get; set; } = "";
}

public sealed class PortChangeRequestDto
{
    public string MappingKey { get; set; } = "";
    public int NewPort { get; set; }
}

public sealed class KickRequestDto
{
    public string Mode { get; set; } = "kick";
    public int? Minutes { get; set; }
}

public sealed class StatusRequestDto
{
    public bool Enabled { get; set; }
}

public sealed class PendingActionRequestDto
{
    public bool Approve { get; set; }
}

/// <summary>AOT 源生成 JSON 上下文（API 全部 DTO）。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AdminConfig))]
[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(ApiResultDto))]
[JsonSerializable(typeof(ServerInfoDto))]
[JsonSerializable(typeof(OverviewDto))]
[JsonSerializable(typeof(CustomersResponse))]
[JsonSerializable(typeof(List<PendingDto>))]
[JsonSerializable(typeof(List<EventDto>))]
[JsonSerializable(typeof(PortChangeRequestDto))]
[JsonSerializable(typeof(KickRequestDto))]
[JsonSerializable(typeof(StatusRequestDto))]
[JsonSerializable(typeof(PendingActionRequestDto))]
internal sealed partial class AdminJson : JsonSerializerContext;
