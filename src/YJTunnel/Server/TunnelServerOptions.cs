namespace YJTunnel.Server;

/// <summary>远端端口白名单段（闭区间）。</summary>
public sealed class PortRange
{
    public int Start { get; set; }
    public int End { get; set; }

    public PortRange() { }
    public PortRange(int start, int end) { Start = start; End = end; }

    public bool Contains(int port) => port >= Start && port <= End;
}

/// <summary>
/// 隧道服务端配置（对应 tunnelserver.json 的 TunnelServer 段核心；
/// 宿主侧 TunnelServerSection 继承后追加 Admin/Log 子段）。
/// </summary>
public class TunnelServerOptions
{
    /// <summary>控制+数据附加复用监听端口。</summary>
    public int ControlPort { get; set; } = 5003;

    /// <summary>监听地址。</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>合法登录令牌集合。</summary>
    public List<string> Tokens { get; set; } = new();

    /// <summary>远端端口白名单段。</summary>
    public List<PortRange> AllowPorts { get; set; } = new() { new PortRange(2001, 4999) };

    /// <summary>黑名单端口（更新源/业务端口/管理台等）。</summary>
    public List<int> DenyPorts { get; set; } = new() { 5001, 5008, 5103, 8088, 22, 3389 };

    /// <summary>迁移期 true：合法 token 的未知 clientId 自动建档；全部迁完改 false（未知 clientId 进待审核）。</summary>
    public bool AutoRegister { get; set; } = true;

    /// <summary>数据目录（customers.json / pending.json / logs 所在目录），默认程序当前目录。</summary>
    public string DataDir { get; set; } = ".";

    /// <summary>额外控制监听端口（预案：个别客户出口防火墙仅放行特定端口时追加，如 443）。</summary>
    public List<int> ExtraListenPorts { get; set; } = new();

    /// <summary>固定时序参数（§4.3：代码内固定不进配置文件；仅测试可通过程序注入覆盖）。</summary>
    public TunnelTimingOptions Timing { get; set; } = new();

    /// <summary>全局待配对 attach 上限（v1.18 内存保护）：超出直接关闭来访连接，防外部连接洪水撑爆内存。</summary>
    public int MaxPendingAttaches { get; set; } = 1024;

    /// <summary>单会话待配对 attach 上限（v1.18 内存保护）：单客户流量异常不影响其他客户。</summary>
    public int MaxPendingPerSession { get; set; } = 256;
}
