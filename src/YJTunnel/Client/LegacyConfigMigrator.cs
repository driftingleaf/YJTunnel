using System.Text.Json;
using YJTunnel.Protocol;

namespace YJTunnel.Client;

/// <summary>
/// 旧 FastTunnel 配置 → 新 Tunnel 配置迁移（§八 LiteDB 迁移规则，纯函数便于单测）：
/// ① 属性同形反序列化（Server/BackupServer/Forwards 字段名一致）；
/// ② ServerPort==5001 改写 5003（BackupServer 同）；
/// ③ Forwards 原样保留（RemotePort 作为首次注册的显式请求值）；
/// ④ Token 一律取新 appsettings Tunnel:Token（D15：不继承旧 LiteDB 占位值，防全员 LoginDenied）。
/// 旧键由调用方保留不删（回滚保障）。
/// </summary>
public static class LegacyConfigMigrator
{
    /// <summary>旧 FastTunnel 服务端口（迁移时改写为新端口 5003）。</summary>
    public const int LegacyServerPort = 5001;

    /// <summary>新隧道服务端口。</summary>
    public const int NewServerPort = 5003;

    /// <summary>旧 FastTunnelSetting JSON → 新 TunnelConfig；解析失败返回 null。</summary>
    public static TunnelConfig? FromLegacyJson(string legacyJson, string newToken)
    {
        try
        {
            var config = TunnelJson.Deserialize<TunnelConfig>(legacyJson);
            if (config == null)
                return null;

            config.Token = newToken; // D15：Token 不继承旧值
            if (config.Server != null)
                config.Server.ServerPort = NormalizePort(config.Server.ServerPort);
            if (config.BackupServer != null)
                config.BackupServer.ServerPort = NormalizePort(config.BackupServer.ServerPort);
            foreach (var forward in config.Forwards)
            {
                if (string.IsNullOrWhiteSpace(forward.LocalIp))
                    forward.LocalIp = "127.0.0.1";
            }
            return config;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>端口改写规则：5001 → 5003，其余原样（自定义端口客户的现场配置不受影响）。</summary>
    public static int NormalizePort(int port) => port == LegacyServerPort ? NewServerPort : port;
}
