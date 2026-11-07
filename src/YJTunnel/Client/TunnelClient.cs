using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using YJTunnel.Protocol;
using YJTunnel.Transport;

namespace YJTunnel.Client;

/// <summary>登录被服务端拒绝（code = ErrorCodes）。</summary>
public sealed class LoginRejectedException : Exception
{
    public int Code { get; }
    public LoginRejectedException(int code, string msg) : base(msg) => Code = code;
}

/// <summary>会话结束原因（决定重连延迟策略）。</summary>
internal enum SessionEndReason
{
    Normal,
    /// <summary>服务端下发了 ConfigChanged：跳过退避立即重连。</summary>
    ConfigChanged,
}

/// <summary>
/// 隧道客户端（§七）：单一重连循环，正常返回与异常同视为断线（修复根因 3 的热循环）；
/// 退避 1→2→4→8→16→30s 上限 + 抖动，连接存活 &gt;60s 才重置退避；
/// 主备端点顺序尝试，连续失败 3 次切换；LoginDenied/ClientDisabled 终态，PendingApproval 5 分钟重试。
/// </summary>
public sealed class TunnelClient : IDisposable
{
    private readonly TunnelConfig _config;
    private readonly string _clientId;
    private readonly string _appVersion;
    private readonly TunnelTimingOptions _timing;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _loop;
    private volatile bool _disposed;

    private TunnelStatus _status = new();
    private byte[] _sessionSecret = Array.Empty<byte>();
    private ServerEndpoint _activeEndpoint = new();
    /// <summary>
    /// 本会话随登录上报的映射快照（过滤 LocalPort≤0 后的列表，与服务端编号基准一致）。
    /// Open 帧的 mappingIdx 必须索引此快照而非 _config.Forwards——
    /// 否则配置含 LocalPort=0 行时索引错位，拨错本地端口（审核修复#5）。
    /// </summary>
    private ForwardConfig[] _activeForwards = Array.Empty<ForwardConfig>();
    private long _pingTimestamp;

    /// <summary>状态快照变化（UI 订阅；参数为最新快照）。</summary>
    public event Action<TunnelStatus>? StatusChanged;

    /// <summary>登录成功（携带服务端分配结果，VM 持久化远端端口回写 LiteDB）。</summary>
    public event Action<LoginRespMessage>? LoginSucceeded;

    public TunnelClient(TunnelConfig config, string clientId, string appVersion = "", TunnelTimingOptions? timing = null)
    {
        _config = config;
        _clientId = clientId;
        _appVersion = appVersion;
        _timing = timing ?? new TunnelTimingOptions();
    }

    /// <summary>当前状态快照（引用原子替换）。</summary>
    public TunnelStatus Status => _status;

    /// <summary>当前配置（停止状态下可由宿主替换 Forwards 后重新 Start）。</summary>
    public TunnelConfig Config => _config;

    /// <summary>启动客户端（幂等；内部启动单一重连循环）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop != null) return;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        Publish(new TunnelStatus { State = TunnelState.Stopped, Mappings = _status.Mappings });
    }

    // ------------------------------------------------------------------ 主循环

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var endpoints = BuildEndpoints();
        var backoff = new Backoff();
        var endpointIdx = 0;
        var failStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var reason = SessionEndReason.Normal;
            try
            {
                Publish(WithState(TunnelState.Connecting));
                reason = await RunSessionAsync(endpoints[endpointIdx], ct);
            }
            catch (LoginRejectedException ex)
            {
                if (ex.Code == ErrorCodes.PendingApproval)
                {
                    Publish(WithState(TunnelState.PendingApproval, ex));
                    await DelaySafeAsync(_timing.PendingRetryInterval, ct);
                    continue;
                }
                if (ex.Code == ErrorCodes.Banned)
                {
                    // 临时封禁：按封禁间隔重试，到期自动恢复（审核/管理台踢下线选封禁时进入此分支）
                    Publish(WithState(TunnelState.Banned, ex));
                    await DelaySafeAsync(_timing.BannedRetryInterval, ct);
                    continue;
                }
                // LoginDenied / ClientDisabled：终态，不进退避重连
                Publish(WithState(TunnelState.Failed, ex));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 任何异常同视为断线（修复根因 3：绝不热循环——统一走下方退避）
                Publish(WithState(TunnelState.Reconnecting, ex));
            }

            if (ct.IsCancellationRequested) break;

            // 连接存活 >60s 才重置退避（D5）
            if (Stopwatch.GetElapsedTime(startedAt) > _timing.StableSessionReset)
                backoff.Reset();

            var next = new TunnelStatus
            {
                State = TunnelState.Reconnecting,
                LastError = _status.LastError,
                ReconnectCount = _status.ReconnectCount + 1,
                LastPongAt = _status.LastPongAt,
                ConnectedSince = null,
                ActiveServer = _status.ActiveServer,
                Mappings = _status.Mappings,
                SessionSecretHint = _status.SessionSecretHint,
            };
            Publish(next);

            // 主备端点：当前端点连续失败达阈值且配置了备用 → 切下一个
            failStreak++;
            if (failStreak >= _timing.FailuresBeforeFailover && endpoints.Count > 1)
            {
                endpointIdx = (endpointIdx + 1) % endpoints.Count;
                failStreak = 0;
            }

            var delay = reason == SessionEndReason.ConfigChanged
                ? TimeSpan.Zero // 服务端配置变更：立即重连取新配置
                : backoff.Next();
            await DelaySafeAsync(delay, ct);
        }
    }

    private List<ServerEndpoint> BuildEndpoints()
    {
        var list = new List<ServerEndpoint>();
        if (!string.IsNullOrWhiteSpace(_config.Server.ServerAddr) && _config.Server.ServerPort > 0)
            list.Add(_config.Server);
        var backup = _config.BackupServer;
        if (backup != null && !string.IsNullOrWhiteSpace(backup.ServerAddr) && backup.ServerPort > 0 && !IsSame(backup, _config.Server))
            list.Add(backup);
        if (list.Count == 0)
            list.Add(new ServerEndpoint { ServerAddr = "127.0.0.1", ServerPort = 5003 });
        return list;
    }

    private static bool IsSame(ServerEndpoint a, ServerEndpoint b)
        => string.Equals(a.ServerAddr?.Trim(), b.ServerAddr?.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.ServerPort == b.ServerPort;

    // ------------------------------------------------------------------ 单会话

    private async Task<SessionEndReason> RunSessionAsync(ServerEndpoint endpoint, CancellationToken ct)
    {
        _activeEndpoint = endpoint;
        var client = await TcpTransport.ConnectAsync(endpoint.ServerAddr, endpoint.ServerPort, _timing.ConnectTimeout, ct);
        var socket = client.Client;
        socket.NoDelay = true;
        TcpTransport.ApplyControlConnectionKeepAlive(socket);

        try
        {
            await socket.SendAsync(new[] { WireConstants.MagicControl }, ct);
            using var stream = new NetworkStream(socket, ownsSocket: false);

            // 登录（key=localPort 字符串；RemotePort=0 表示服务端自动分配）
            // sessionForwards 是本会话的映射基准：服务端按此列表顺序编号 mappingIdx
            var sessionForwards = _config.Forwards.Where(f => f.LocalPort > 0).ToArray();
            var login = new LoginMessage
            {
                Ver = WireConstants.ProtocolVersion,
                ClientId = _clientId,
                Token = _config.Token,
                AppVersion = _appVersion,
                MachineName = TryGetMachineName(),
                CompanyName = string.IsNullOrWhiteSpace(_config.CompanyName) ? null : _config.CompanyName.Trim(),
                Mappings = sessionForwards
                    .Select(f => new LoginMapping
                    {
                        Key = f.LocalPort.ToString(),
                        LocalIp = string.IsNullOrWhiteSpace(f.LocalIp) ? "127.0.0.1" : f.LocalIp.Trim(),
                        LocalPort = f.LocalPort,
                        RequestedRemotePort = f.RemotePort > 0 ? f.RemotePort : 0,
                    }).ToList(),
            };
            await WriteFrameAsync(stream, FrameType.Login, Encoding.UTF8.GetBytes(TunnelJson.Serialize(login)), ct);

            // 等待登录应答（宽限：服务端端口绑定重试最长 1+2+4s/映射）
            using var respCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            respCts.CancelAfter(TimeSpan.FromSeconds(30));
            LoginRespMessage resp;
            try
            {
                resp = await FrameCodec.ReadJsonAsync<LoginRespMessage>(stream, FrameType.LoginResp, respCts.Token)
                    ?? throw new IOException("登录应答解析失败");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("等待登录应答超时");
            }

            if (resp.Code != ErrorCodes.Ok)
                throw new LoginRejectedException(resp.Code, resp.Msg);

            _sessionSecret = Convert.FromBase64String(resp.SessionSecret ?? "");
            _activeForwards = sessionForwards; // Open 的 mappingIdx 以此列表为基准（审核修复#5）
            var mappings = resp.Mappings.Select(m => new MappingStatus
            {
                Key = m.Key,
                RemotePort = m.RemotePort,
                Status = m.Status,
                Msg = m.Msg,
                LocalPort = ParseLocalPort(m.Key),
            }).ToList();
            Publish(new TunnelStatus
            {
                State = TunnelState.Connected,
                ConnectedSince = DateTimeOffset.Now,
                ActiveServer = $"{endpoint.ServerAddr}:{endpoint.ServerPort}",
                Mappings = mappings,
                SessionSecretHint = Convert.ToHexString(_sessionSecret, 0, Math.Min(4, _sessionSecret.Length)),
            });
            LoginSucceeded?.Invoke(resp);

            // 心跳任务（应用层 Ping 20s；60s 无 Pong 由读超时判死）
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pingTask = Task.Run(() => PingLoopAsync(stream, pingCts.Token), pingCts.Token);

            try
            {
                return await ReadLoopAsync(stream, ct);
            }
            finally
            {
                pingCts.Cancel();
                try { await pingTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
        }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task<SessionEndReason> ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(_timing.PongTimeout);
            Frame? frame;
            try
            {
                frame = await FrameCodec.ReadFrameAsync(stream, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 60s 内无任何帧（Pong 未到）→ 判死重连（根因 2 客户端侧）
                throw new TimeoutException($"心跳超时（{_timing.PongTimeout.TotalSeconds:0}s 无 Pong）");
            }
            if (frame == null)
                return SessionEndReason.Normal; // 服务端关闭

            switch (frame.Value.Type)
            {
                case FrameType.Pong:
                    var ts = Interlocked.Exchange(ref _pingTimestamp, 0);
                    var snapshot = _status;
                    Publish(new TunnelStatus
                    {
                        State = snapshot.State,
                        LastError = snapshot.LastError,
                        ReconnectCount = snapshot.ReconnectCount,
                        LastPongAt = DateTimeOffset.Now,
                        PingLatency = ts != 0 ? Stopwatch.GetElapsedTime(ts) : snapshot.PingLatency,
                        ConnectedSince = snapshot.ConnectedSince,
                        ActiveServer = snapshot.ActiveServer,
                        Mappings = snapshot.Mappings,
                        SessionSecretHint = snapshot.SessionSecretHint,
                    });
                    break;

                case FrameType.Open:
                    var (attachId, mappingIdx) = OpenFrame.Decode(frame.Value.Payload.Span);
                    _ = Task.Run(() => HandleOpenAsync(attachId, mappingIdx, ct), ct);
                    break;

                case FrameType.Error:
                    var err = TunnelJson.Deserialize<ErrorMessage>(
                        Encoding.UTF8.GetString(frame.Value.Payload.Span));
                    if (err?.Code == ErrorCodes.ConfigChanged)
                        return SessionEndReason.ConfigChanged;
                    throw new IOException($"服务端错误：{err?.Code} {err?.Msg}");

                default:
                    throw new ProtocolViolationException($"客户端收到非法帧 {frame.Value.Type}");
            }
        }
        return SessionEndReason.Normal;
    }

    private async Task PingLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_timing.PingInterval, ct);
                Interlocked.Exchange(ref _pingTimestamp, Stopwatch.GetTimestamp());
                await WriteFrameAsync(stream, FrameType.Ping, ReadOnlyMemory<byte>.Empty, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 写失败：读循环会感知断线，这里退出即可
                return;
            }
        }
    }

    /// <summary>收到 Open：向服务端建数据附加连接 → 连本地服务 → 双向互泵（半关闭传播）。</summary>
    private async Task HandleOpenAsync(long attachId, int mappingIdx, CancellationToken ct)
    {
        // fire-and-forget 任务必须自吞异常（审核修复#7：否则成为未观察异常，静默无感知）
        try
        {
            var forward = mappingIdx >= 0 && mappingIdx < _activeForwards.Length ? _activeForwards[mappingIdx] : null;
            if (forward == null)
                return; // 配置已变化，无此映射

            var dataSocket = await DataChannelFactory.OpenAsync(
                _activeEndpoint.ServerAddr, _activeEndpoint.ServerPort,
                attachId, _sessionSecret, _timing.ConnectTimeout, ct);

            Socket localSocket;
            try
            {
                var localIp = string.IsNullOrWhiteSpace(forward.LocalIp) ? "127.0.0.1" : forward.LocalIp.Trim();
                var local = await TcpTransport.ConnectAsync(localIp, forward.LocalPort, _timing.ConnectTimeout, ct);
                localSocket = local.Client;
                TcpTransport.ApplyDataConnectionKeepAlive(localSocket);
            }
            catch
            {
                // 本地服务不可达：关闭数据连接（服务端看到 EOF 后关闭来访连接）
                dataSocket.Dispose();
                return;
            }

            await StreamPump.CopyBothAsync(dataSocket, localSocket, ct);
        }
        catch
        {
            // 数据通道建立失败：服务端 attach 超时（10s）后关闭来访连接，用户侧重试
        }
    }

    private async Task WriteFrameAsync(NetworkStream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await FrameCodec.WriteFrameAsync(stream, type, payload, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    private static int ParseLocalPort(string key)
        => int.TryParse(key, out var port) ? port : 0;

    private static string TryGetMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }

    private TunnelStatus WithState(TunnelState state, Exception? error = null)
    {
        var prev = _status;
        return new TunnelStatus
        {
            State = state,
            LastError = error ?? prev.LastError,
            ReconnectCount = prev.ReconnectCount,
            LastPongAt = prev.LastPongAt,
            ConnectedSince = state == TunnelState.Connected ? prev.ConnectedSince : null,
            ActiveServer = prev.ActiveServer,
            Mappings = prev.Mappings,
            PingLatency = prev.PingLatency,
            SessionSecretHint = prev.SessionSecretHint,
        };
    }

    private void Publish(TunnelStatus status)
    {
        _status = status;
        try { StatusChanged?.Invoke(status); }
        catch { /* 订阅方异常不影响循环 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
