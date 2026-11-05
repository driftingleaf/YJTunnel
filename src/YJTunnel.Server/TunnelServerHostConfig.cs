using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YJTunnel.Server;

namespace YJTunnel.Server;

/// <summary>管理台配置（tunnelserver.json → Admin 段）。</summary>
public sealed class AdminOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 5103;

    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";

    public int LockoutFailures { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>IP 白名单；为空则仅允许回环访问。</summary>
    public List<string> AllowedIps { get; set; } = new();
}

/// <summary>日志配置。</summary>
public sealed class LogOptions
{
    public string Dir { get; set; } = "logs";
    public int EventRetentionDays { get; set; } = 90;
}

/// <summary>
/// tunnelserver.json 完整配置模型（§9.1）：Admin/Log 段嵌套在 TunnelServer 内。
/// </summary>
public sealed class TunnelServerHostConfig
{
    public TunnelServerSection TunnelServer { get; set; } = new();

    public static TunnelServerHostConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"未找到配置文件 {path}（首次部署请将 configs/tunnelserver.json 复制到程序目录；Token 与管理台密码已内置，无需修改）", path);

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<TunnelServerHostConfig>(json, JsonOptions)
            ?? throw new InvalidDataException($"配置文件解析失败：{path}");
        Validate(config, path);
        return config;
    }

    /// <summary>
    /// 首次部署自愈（§10.5 坑①，v1.9）：程序根目录缺配置时，自动从 configs\tunnelserver.json 模板生成
    /// （Token 与管理台密码已内置，AllowedIps 默认 *）。直接运行发布目录、免 install 脚本场景同样生效。
    /// 返回 false = 模板也不存在，需人工提供配置。
    /// </summary>
    public static bool EnsureConfigFile(string path)
    {
        if (File.Exists(path))
            return true;

        var template = Path.Combine(AppContext.BaseDirectory, "configs", "tunnelserver.json");
        if (!File.Exists(template))
            return false;

        File.Copy(template, path, overwrite: false);
        Console.WriteLine($"[配置] 未找到 {Path.GetFileName(path)}，已自动从 configs\\tunnelserver.json 生成" +
            "（Token 与管理台密码已内置；AllowedIps 默认 * 放行所有来源，务必确认 5103 未对公网放行）");
        return true;
    }

    /// <summary>
    /// 首次启动凭据自愈（开源版）：配置含 __GENERATE_TOKEN__ / __GENERATE_PASSWORD__ 占位符时，
    /// 生成强随机值写回配置文件并打印到控制台。已配置真实凭据的文件不受影响（幂等）。
    /// </summary>
    public static void ReplaceCredentialPlaceholders(string path)
    {
        var text = File.ReadAllText(path);
        if (!text.Contains("__GENERATE_TOKEN__") && !text.Contains("__GENERATE_PASSWORD__"))
            return;

        if (text.Contains("__GENERATE_TOKEN__"))
        {
            var token = "yjt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            text = text.Replace("__GENERATE_TOKEN__", token);
            Console.WriteLine($"[配置] 已生成登录 Token：{token}");
            Console.WriteLine("       （客户端 appsettings 的 Tunnel:Token 需与此值一致）");
        }
        if (text.Contains("__GENERATE_PASSWORD__"))
        {
            var password = "admin-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            text = text.Replace("__GENERATE_PASSWORD__", password);
            Console.WriteLine($"[配置] 已生成管理台账号密码：admin / {password}");
        }
        File.WriteAllText(path, text, Encoding.UTF8);
        Console.WriteLine("[配置] 凭据已写入配置文件，请妥善保管");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static void Validate(TunnelServerHostConfig config, string path)
    {
        var section = config.TunnelServer;
        if (section == null)
            throw new InvalidDataException($"配置缺少 TunnelServer 段：{path}");
        if (section.Tokens.Count == 0)
            throw new InvalidDataException($"配置缺少 TunnelServer:Tokens（至少一个登录令牌）：{path}");
        if (section.Admin is { Enabled: true } && string.IsNullOrWhiteSpace(section.Admin.Password))
            throw new InvalidDataException($"管理台已启用但 Admin:Password 为空：{path}");
    }
}

/// <summary>_tunnelserver.json 的 TunnelServer 段（隧道核心 + 管理台 + 日志）。</summary>
public sealed class TunnelServerSection : TunnelServerOptions
{
    public AdminOptions Admin { get; set; } = new();
    public LogOptions Log { get; set; } = new();
}
