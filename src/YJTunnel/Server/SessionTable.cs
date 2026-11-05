using System.Collections.Concurrent;
using System.Net.Sockets;

namespace YJTunnel.Server;

/// <summary>
/// 一个已登录的控制连接会话。
/// </summary>
public sealed class TunnelSession : IDisposable
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public long Epoch { get; init; }
    public string ClientId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string RemoteIp { get; init; } = "";
    public byte[] SessionSecret { get; init; } = Array.Empty<byte>();

    public Socket? Socket { get; set; }
    public NetworkStream? Stream { get; set; }

    /// <summary>控制连接写锁（Ping/Pong/Open/Error 并发写串行化）。</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public DateTimeOffset LoginAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAliveUtc { get; set; } = DateTimeOffset.UtcNow;

    public volatile bool Closed;

    /// <summary>本会话累计转发的来访连接数（统计用）。</summary>
    public long ConnectionsServed;

    /// <summary>当前待配对的来访连接数（内存保护计数，v1.18：超过 MaxPendingPerSession 拒绝新来访）。</summary>
    public int ActivePending;

    public void Touch() => LastAliveUtc = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        Closed = true;
        try { Stream?.Close(); } catch { }
        try { Socket?.Close(); } catch { }
        // 不释放 WriteLock（审核修复#6）：会话销毁与并发写（Ping/Open/Error）存在竞态，
        // Dispose 信号量会让并发方的 Release()/WaitAsync() 抛 ObjectDisposedException
        // （此前靠调用方 catch-all 兜底，脆弱）。SemaphoreSlim 未用 AvailableWaitHandle 时无内核句柄，不释放无泄漏。
    }
}

/// <summary>
/// 会话表：sessionId 索引 + clientId → 最新会话索引。
/// </summary>
public sealed class SessionTable
{
    private readonly ConcurrentDictionary<Guid, TunnelSession> _byId = new();
    private readonly ConcurrentDictionary<string, TunnelSession> _byClientId = new();

    public void Add(TunnelSession session)
    {
        _byId[session.SessionId] = session;
        _byClientId[session.ClientId] = session;
    }

    public void Remove(TunnelSession session)
    {
        _byId.TryRemove(session.SessionId, out _);
        // 只有当表里仍是该会话时才移除 clientId 索引（新会话已顶替时不能删）
        if (_byClientId.TryGetValue(session.ClientId, out var latest) && latest.SessionId == session.SessionId)
            _byClientId.TryRemove(session.ClientId, out _);
    }

    public TunnelSession? GetById(Guid sessionId) => _byId.GetValueOrDefault(sessionId);

    public TunnelSession? GetByClient(string clientId) => _byClientId.GetValueOrDefault(clientId);

    public List<TunnelSession> GetAll() => _byId.Values.ToList();

    public bool IsAlive(Guid sessionId)
        => _byId.TryGetValue(sessionId, out var s) && !s.Closed;
}

/// <summary>
/// 心跳收割器（§5.5）：每 Timing.HeartbeatSweepInterval 扫描 lastAliveUtc 超过
/// SessionIdleTimeout 的会话 → 强断（关闭控制连接触发资源释放，回收半开连接，根因 2 服务端侧）。
/// </summary>
public sealed class HeartbeatSweeper : IDisposable
{
    private readonly SessionTable _sessions;
    private readonly EventLog _events;
    private readonly TunnelTimingOptions _timing;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public HeartbeatSweeper(SessionTable sessions, EventLog events, TunnelTimingOptions timing)
    {
        _sessions = sessions;
        _events = events;
        _timing = timing;
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(SweepLoopAsync);
    }

    private async Task SweepLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_timing.HeartbeatSweepInterval, _cts.Token);
                foreach (var session in _sessions.GetAll())
                {
                    var idle = DateTimeOffset.UtcNow - session.LastAliveUtc;
                    if (idle > _timing.SessionIdleTimeout && !session.Closed)
                    {
                        _events.Session(TunnelEventLevel.Warn, "heartbeat_reaped",
                            $"会话心跳超时（空闲 {idle.TotalSeconds:0}s ≥ {_timing.SessionIdleTimeout.TotalSeconds:0}s），强制断开",
                            session.ClientId, session.RemoteIp);
                        session.Dispose(); // 关闭控制连接，控制循环 finally 释放监听器
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 扫描异常不退出循环
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }
}
