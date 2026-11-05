using System.Net;
using System.Net.Sockets;
using YJTunnel.Transport;

namespace YJTunnel.Server;

/// <summary>
/// 监听注册表条目：remotePort → 归属信息（§5.1，修复根因 1 的核心）。
/// 会话注销/心跳收割只清理自己 sessionId 名下的条目——旧会话删不掉新会话的条目。
/// </summary>
public sealed class ListenerEntry
{
    public int RemotePort { get; init; }
    public string ClientId { get; init; } = "";
    public Guid SessionId { get; init; }
    public long Epoch { get; init; }
    public Socket? Listener { get; internal set; }
    public DateTimeOffset BoundAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int MappingIdx { get; internal set; }
}

/// <summary>绑定结果。</summary>
public sealed record BindResult(bool Ok, int Status, string Msg);

/// <summary>
/// 远端端口监听注册表。所有监听 socket 绑定前设 SO_REUSEADDR；绑定失败按 1s/2s/4s 重试，
/// 仍失败回 PortOccupied（绝不静默 continue）。
/// </summary>
public sealed class ListenerRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ListenerEntry> _entries = new();
    private readonly TunnelServerOptions _options;
    private readonly EventLog _events;

    /// <summary>
    /// 绑定串行门（审核修复#4）："接管判定→socket.Bind→登记"必须原子。
    /// 否则两个并发 BindAsync 可在彼此登记前都通过判定，而 Windows 的 SO_REUSEADDR
    /// 允许同端口第二个 bind 成功 → 双监听 + 先登记者 socket 泄漏。
    /// 重试等待（最长 1+2+4s）也在门内——端口冲突本就是异常路径，串行代价可接受。
    /// </summary>
    private readonly SemaphoreSlim _bindGate = new(1, 1);

    /// <summary>来访连接回调（TunnelServer 注入），参数 = 远端端口、来访 socket。</summary>
    private Func<int, Socket, Task>? _onVisitor;

    /// <summary>会话是否存活（TunnelServer 注入，接管判定用）。</summary>
    private Func<Guid, bool>? _isSessionAlive;

    /// <summary>接管旧会话时的强断回调（TunnelServer 注入）。</summary>
    private Func<Guid, string, Task>? _killSession;

    public ListenerRegistry(TunnelServerOptions options, EventLog events)
    {
        _options = options;
        _events = events;
    }

    public void Attach(Func<int, Socket, Task> onVisitor, Func<Guid, bool> isSessionAlive, Func<Guid, string, Task> killSession)
    {
        _onVisitor = onVisitor;
        _isSessionAlive = isSessionAlive;
        _killSession = killSession;
    }

    /// <summary>端口是否被本注册表持有（分配器可用性检查用）。</summary>
    public bool IsOwned(int port)
    {
        lock (_lock) return _entries.ContainsKey(port);
    }

    public ListenerEntry? Get(int port)
    {
        lock (_lock) return _entries.GetValueOrDefault(port);
    }

    public List<ListenerEntry> GetAll()
    {
        lock (_lock) return _entries.Values.ToList();
    }

    /// <summary>
    /// 为会话绑定远端端口监听。接管矩阵（§4.4）：
    /// 同 sessionId → 重建；同 clientId 旧会话 → 强断旧会话后接管；
    /// 异 clientId 且对端死/半开 → 强断收割后接管；异 clientId 且对端存活 → PortOccupied。
    /// </summary>
    public async Task<BindResult> BindAsync(int remotePort, string clientId, Guid sessionId, long epoch, int mappingIdx)
    {
        await _bindGate.WaitAsync();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // 1. 接管判定（每次重试都重新判定，端口可能已被释放）
                var takeover = await TryTakeoverAsync(remotePort, clientId, sessionId, epoch);
                if (!takeover.Ok)
                    return takeover;

                // 2. 绑定
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                TcpTransport.SetReuseAddress(socket); // 修复 TIME_WAIT 子连接占位
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, remotePort));
                    socket.Listen(64);
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    if (attempt < _options.Timing.BindRetryDelays.Length)
                    {
                        _events.Session(TunnelEventLevel.Warn, "bind_retry",
                            $"端口 {remotePort} 绑定失败（第 {attempt + 1} 次）：{ex.SocketErrorCode}，{_options.Timing.BindRetryDelays[attempt].TotalSeconds:0}s 后重试",
                            clientId);
                        await Task.Delay(_options.Timing.BindRetryDelays[attempt]);
                        continue;
                    }
                    _events.Session(TunnelEventLevel.Error, "bind_port_occupied",
                        $"端口 {remotePort} 重试后仍绑定失败：{ex.SocketErrorCode}", clientId);
                    return new BindResult(false, Protocol.ErrorCodes.PortOccupied, $"端口 {remotePort} 被占用");
                }

                var entry = new ListenerEntry
                {
                    RemotePort = remotePort,
                    ClientId = clientId,
                    SessionId = sessionId,
                    Epoch = epoch,
                    Listener = socket,
                    MappingIdx = mappingIdx,
                };
                lock (_lock) _entries[remotePort] = entry;
                _ = RunAcceptLoopAsync(entry);
                return new BindResult(true, Protocol.ErrorCodes.Ok, "");
            }
        }
        finally
        {
            _bindGate.Release();
        }
    }

    /// <summary>接管判定：清理可接管的旧条目；不可接管返回失败结果。</summary>
    private async Task<BindResult> TryTakeoverAsync(int port, string clientId, Guid sessionId, long epoch)
    {
        ListenerEntry? old;
        lock (_lock) old = _entries.GetValueOrDefault(port);
        if (old == null || old.Listener == null)
            return new BindResult(true, Protocol.ErrorCodes.Ok, "");

        if (old.SessionId == sessionId)
        {
            // 同会话重建：释放旧 socket 后重绑
            RemoveEntry(old);
            return new BindResult(true, Protocol.ErrorCodes.Ok, "");
        }

        if (old.ClientId == clientId)
        {
            // 同 clientId 旧会话：epoch 接管，强断旧会话后重绑
            _events.Session(TunnelEventLevel.Info, "takeover",
                $"端口 {port} 由同客户新会话接管（epoch {old.Epoch} → {epoch}）", clientId);
            await (_killSession?.Invoke(old.SessionId, "被同客户新会话接管") ?? Task.CompletedTask);
            RemoveEntry(old);
            return new BindResult(true, Protocol.ErrorCodes.Ok, "");
        }

        // 异 clientId：对端死/半开（>90s 无活动或会话不在表）→ 强断收割后接管；存活 → 冲突
        var alive = _isSessionAlive?.Invoke(old.SessionId) ?? false;
        if (!alive)
        {
            _events.Session(TunnelEventLevel.Warn, "takeover",
                $"端口 {port} 原持有人 {old.ClientId} 的会话已死，收割后移交 {clientId}", clientId);
            await (_killSession?.Invoke(old.SessionId, "会话已死，端口被收割") ?? Task.CompletedTask);
            RemoveEntry(old);
            return new BindResult(true, Protocol.ErrorCodes.Ok, "");
        }

        return new BindResult(false, Protocol.ErrorCodes.PortOccupied,
            $"端口 {port} 被在线客户 {old.ClientId} 持有");
    }

    private void RemoveEntry(ListenerEntry entry)
    {
        lock (_lock)
        {
            // 只移除仍是该条目本身的记录（防止误删新条目）
            if (_entries.TryGetValue(entry.RemotePort, out var cur) && cur.SessionId == entry.SessionId)
                _entries.Remove(entry.RemotePort);
        }
        entry.Listener?.Dispose();
        entry.Listener = null;
    }

    /// <summary>
    /// 释放会话名下的全部监听（修复根因 1：绝不清理其他会话的条目）。
    /// </summary>
    public void ReleaseSession(Guid sessionId)
    {
        List<ListenerEntry> mine;
        lock (_lock) mine = _entries.Values.Where(e => e.SessionId == sessionId).ToList();
        foreach (var entry in mine)
        {
            RemoveEntry(entry);
            _events.Session(TunnelEventLevel.Info, "listener_released",
                $"端口 {entry.RemotePort} 随会话注销释放", entry.ClientId);
        }
    }

    private async Task RunAcceptLoopAsync(ListenerEntry entry)
    {
        var socket = entry.Listener!;
        while (IsEntryLive(entry))
        {
            Socket visitor;
            try
            {
                visitor = await socket.AcceptAsync();
            }
            catch (ObjectDisposedException)
            {
                return; // 条目被释放/接管，正常退出
            }
            catch (SocketException)
            {
                if (!IsEntryLive(entry)) return;
                continue;
            }
            catch
            {
                return; // 未知异常：退出循环（条目由会话注销/接管统一回收），避免死循环刷异常
            }
            if (_onVisitor == null)
            {
                visitor.Dispose();
                continue;
            }
            // 每个来访连接独立处理，不阻塞 accept 循环
            _ = Task.Run(() => _onVisitor(entry.RemotePort, visitor));
        }
    }

    private bool IsEntryLive(ListenerEntry entry)
    {
        lock (_lock)
            return _entries.TryGetValue(entry.RemotePort, out var cur)
                && cur.SessionId == entry.SessionId
                && cur.Listener != null;
    }

    public void Dispose()
    {
        List<ListenerEntry> all;
        lock (_lock)
        {
            all = _entries.Values.ToList();
            _entries.Clear();
        }
        foreach (var entry in all)
            entry.Listener?.Dispose();
        _bindGate.Dispose();
    }
}
