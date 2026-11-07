namespace YJTunnel;

/// <summary>隧道客户端运行状态机。ConnectedConnected=已登录且心跳正常。</summary>
public enum TunnelState
{
    /// <summary>未启动。</summary>
    Idle,

    /// <summary>连接/登录中。</summary>
    Connecting,

    /// <summary>已登录在线。</summary>
    Connected,

    /// <summary>断线重连中（退避循环）。</summary>
    Reconnecting,

    /// <summary>终态失败（LoginDenied/ClientDisabled），不自动重试。</summary>
    Failed,

    /// <summary>等待服务端审核（PendingApproval），5 分钟间隔重试。</summary>
    PendingApproval,

    /// <summary>处于封禁期（Banned），按封禁间隔重试，到期自动恢复。</summary>
    Banned,

    /// <summary>已手动停止。</summary>
    Stopped,
}

/// <summary>单个映射的运行时绑定状态（服务端 LoginResp 回传结果 + 断线后重置）。</summary>
public sealed class MappingStatus
{
    /// <summary>映射 key（localPort 字符串）。</summary>
    public string Key { get; set; } = "";

    public int LocalPort { get; set; }

    /// <summary>服务端实际分配的远端端口（0=未分配）。</summary>
    public int RemotePort { get; set; }

    /// <summary>绑定状态码（ErrorCodes：Ok/PortOccupied/ServerError...）。</summary>
    public int Status { get; set; }

    public string Msg { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 隧道客户端对外状态快照（线程安全快照，事件通知 UI）。
/// </summary>
public sealed class TunnelStatus
{
    public TunnelState State { get; set; } = TunnelState.Idle;

    /// <summary>最近一次错误（连接失败/登录被拒/协议错误）。</summary>
    public Exception? LastError { get; set; }

    /// <summary>累计重连次数（成功登录后不清零，供 UI 观察链路质量）。</summary>
    public int ReconnectCount { get; set; }

    /// <summary>最近一次收到 Pong 的本地时间。</summary>
    public DateTimeOffset? LastPongAt { get; set; }

    /// <summary>当前会话登录成功的时间。</summary>
    public DateTimeOffset? ConnectedSince { get; set; }

    /// <summary>当前活动服务器描述（主/备 + 地址）。</summary>
    public string ActiveServer { get; set; } = "";

    /// <summary>心跳往返延迟（最近一次 Pong - 对应 Ping 发出时刻）。</summary>
    public TimeSpan? PingLatency { get; set; }

    /// <summary>每映射绑定状态（key = localPort 字符串）。</summary>
    public IReadOnlyList<MappingStatus> Mappings { get; set; } = Array.Empty<MappingStatus>();

    /// <summary>当前登录会话的会话密文提示（Base64 前 8 字符，仅诊断用）。</summary>
    public string SessionSecretHint { get; set; } = "";
}
