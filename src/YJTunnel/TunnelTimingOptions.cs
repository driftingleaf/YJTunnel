namespace YJTunnel;

/// <summary>
/// 固定时序参数（§4.3：默认值即生产值，代码内固定不进配置文件；仅测试可通过程序注入缩短）。
/// 放根命名空间：服务端与客户端共用（服务端固定值 + 客户端心跳/退避参数）。
/// </summary>
public sealed class TunnelTimingOptions
{
    /// <summary>登录超时：服务端等待客户端首字节/登录帧的最长时间。</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>控制连接读超时：服务端侧两帧之间的最长间隔。</summary>
    public TimeSpan ControlReadTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>会话空闲判死阈值：lastAliveUtc 超过该时长被心跳收割。</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>心跳收割扫描间隔。</summary>
    public TimeSpan HeartbeatSweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>attach 配对超时：Open 发出后数据附加连接到达的最长等待。</summary>
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>端口绑定失败重试延迟序列。</summary>
    public TimeSpan[] BindRetryDelays { get; set; } =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

    /// <summary>未认证连接每 IP 并发上限。</summary>
    public int UnauthConnectionsPerIp { get; set; } = 16;

    /// <summary>客户端心跳发送间隔（20s 发）。</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>客户端心跳判死：超过该时长无 Pong 判定死亡（60s 判死）。</summary>
    public TimeSpan PongTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>会话稳定运行多久后重置客户端退避（D5：连接存活 >60s 才重置）。</summary>
    public TimeSpan StableSessionReset { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>主备切换阈值：当前端点连续失败次数达到后切换下一端点。</summary>
    public int FailuresBeforeFailover { get; set; } = 3;

    /// <summary>待审核（PendingApproval）状态下的重试间隔。</summary>
    public TimeSpan PendingRetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>封禁（Banned）状态下的重试间隔（封禁到期后自动恢复连接）。</summary>
    public TimeSpan BannedRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>连接/登录的网络操作超时。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
