using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YJTunnel;

namespace YJTunnel.Client.Services;

/// <summary>
/// 客户端应用配置（独立于隧道 TunnelConfig：额外保存 UI 偏好与上次窗口状态）。
/// 存放于 %APPDATA%\YJTunnel\client.json，首启自动生成。
/// </summary>
public sealed class AppConfig
{
    /// <summary>服务端地址（IP 或域名）。</summary>
    public string ServerAddr { get; set; } = "";

    /// <summary>服务端控制端口（默认 5003）。</summary>
    public int ServerPort { get; set; } = 5003;

    /// <summary>备用服务端地址（可空）。</summary>
    public string? BackupServerAddr { get; set; }

    /// <summary>备用服务端端口（可空）。</summary>
    public int? BackupServerPort { get; set; }

    /// <summary>接入令牌（服务端 Tokens 之一）。</summary>
    public string Token { get; set; } = "";

    /// <summary>公司名称（登录上报，管理台展示）。</summary>
    public string CompanyName { get; set; } = "";

    /// <summary>映射列表。</summary>
    public List<MappingItem> Forwards { get; set; } = new();

    /// <summary>关闭窗口时最小化到托盘（而非退出）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>开机自启（写 HKCU Run 键）。</summary>
    public bool AutoStart { get; set; }

    // ---- AOT：源生成 JSON 上下文 ----

    [JsonIgnore]
    public TunnelConfig Tunnel => new()
    {
        Server = new ServerEndpoint { ServerAddr = ServerAddr, ServerPort = ServerPort },
        BackupServer = string.IsNullOrEmpty(BackupServerAddr)
            ? null
            : new ServerEndpoint { ServerAddr = BackupServerAddr, ServerPort = BackupServerPort ?? ServerPort },
        Token = Token,
        CompanyName = CompanyName,
        Forwards = Forwards
            .Where(f => f.Enabled && f.LocalPort > 0 && f.RemotePort > 0)
            .Select(f => new ForwardConfig
            {
                LocalIp = string.IsNullOrWhiteSpace(f.LocalIp) ? "127.0.0.1" : f.LocalIp,
                LocalPort = f.LocalPort,
                RemotePort = f.RemotePort,
            })
            .ToList(),
    };
}

/// <summary>一条端口映射（UI 层，含启用开关；隧道层只取 Enabled 且端口有效的行）。</summary>
public sealed class MappingItem
{
    public bool Enabled { get; set; } = true;

    public string LocalIp { get; set; } = "127.0.0.1";

    public int LocalPort { get; set; }

    public int RemotePort { get; set; }

    /// <summary>备注（如"用友U8""文件服务"）。</summary>
    public string Note { get; set; } = "";
}

/// <summary>AppConfig 的 JSON 读写（源生成，AOT 兼容）。</summary>
public static class AppConfigStore
{
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YJTunnel");

    public static string DefaultPath => Path.Combine(DefaultDir, "client.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch
        {
            // 配置损坏时回退默认值（保留损坏文件便于用户找回）
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* 忽略 */ }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // 先写临时文件再替换，避免写一半损坏配置
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>AOT 源生成 JSON 元数据。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
