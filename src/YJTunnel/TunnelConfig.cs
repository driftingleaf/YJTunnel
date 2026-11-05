namespace YJTunnel;

/// <summary>
/// 隧道客户端配置。属性名与旧 FastTunnel DefaultClientConfig 对齐（Server/BackupServer/Token/Forwards），
/// 旧 LiteDB 配置可同形反序列化直接迁移（§八）。
/// </summary>
public sealed class TunnelConfig
{
    /// <summary>主服务器（公网入口，优先使用）。</summary>
    public ServerEndpoint Server { get; set; } = new();

    /// <summary>备用服务器（主连接连续失败 3 次后切换）。</summary>
    public ServerEndpoint? BackupServer { get; set; }

    /// <summary>与服务端 Tokens 一致的登录令牌。</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// 客户端在服务端的唯一身份（GUID）。留空由宿主生成并持久化；服务端按此记忆端口分配与客户档案。
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// 公司名称（ServerHost 启动时从 TB_System/本地缓存解析，随登录上报，服务端用于自动命名客户档案）。
    /// 留空则服务端沿用"未命名-{clientId前8位}"占位，管理台可手工改名。
    /// </summary>
    public string CompanyName { get; set; } = "";

    /// <summary>端口映射列表。RemotePort=0 表示由服务端自动分配（仅首次注册前可显式指定，D6）。</summary>
    public List<ForwardConfig> Forwards { get; set; } = new();
}

/// <summary>服务器端点（属性名与旧 SuiDaoServer 对齐）。</summary>
public sealed class ServerEndpoint
{
    public string ServerAddr { get; set; } = "";
    public int ServerPort { get; set; }
}

/// <summary>
/// 端口映射（属性名与旧 ForwardConfig 对齐）。LocalIp 留空时按 127.0.0.1 处理。
/// </summary>
public sealed class ForwardConfig
{
    public string LocalIp { get; set; } = "127.0.0.1";

    public int LocalPort { get; set; }

    /// <summary>远端端口。0=服务端自动分配；已分配后以服务端记录为准（请求被忽略并记事件）。</summary>
    public int RemotePort { get; set; }
}
