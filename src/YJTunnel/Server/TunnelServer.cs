using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using YJTunnel.Protocol;
using YJTunnel.Transport;
using ProtocolViolationException = YJTunnel.Protocol.ProtocolViolationException;

namespace YJTunnel.Server;

/// <summary>待配对的 attach（来访连接已到、等待客户端数据附加连接）。</summary>
public sealed class PendingAttach
{
    public required TunnelSession Session { get; init; }
    public required Socket Visitor { get; init; }
    public required int RemotePort { get; init; }
    public required int MappingIdx { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 隧道服务端（§五）：单端口控制+数据附加分流、登录端口分配（服务端权威）、
/// 监听注册表接管、半关闭数据泵、心跳收割、事件日志限流。
/// </summary>
public sealed class TunnelServer : IAsyncDisposable
{
    private readonly TunnelServerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<long, PendingAttach> _pendingAttaches = new();
    private readonly object _unauthLock = new();
    private readonly Dictionary<string, int> _unauthPerIp = new();
    private long _epochCounter;

    /// <summary>
    /// 登录串行门：查档/自动命名/端口分配/入库是跨客户共享状态的"读-改-写"复合操作，
    /// 且 PortAllocator 无内部锁、新分配在入库前对其他登录不可见——并发登录会重复分配同一自动端口，
    /// 必须互斥（v1.8 修复，由 NewInstall_AutoAssign_TwoClientsNoConflict 用例暴露）。
    /// </summary>
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private Task? _acceptLoop;
    private volatile bool _started;
    private volatile bool _stopped;

    public CustomerStore Customers { get; }
    public EventLog Events { get; }
    public SessionTable Sessions { get; } = new();
    public ListenerRegistry Listeners { get; }
    public PortAllocator Allocator { get; }

    /// <summary>服务端选项（管理台校验提示用：白名单段/黑名单等）。</summary>
    public TunnelServerOptions Options => _options;

    public TunnelServer(TunnelServerOptions options)
    {
        _options = options;
        Events = new EventLog(options.DataDir);
        Customers = new CustomerStore(options.DataDir);
        Listeners = new ListenerRegistry(options, Events);
        Allocator = new PortAllocator(Customers, options, Listeners.IsOwned);
        Listeners.Attach(HandleVisitorAsync, Sessions.IsAlive, KillSessionAsync);
    }

    public bool IsClientOnline(string clientId) => Sessions.GetByClient(clientId) is { Closed: false };

    // ------------------------------------------------------------------ 启停

    public Task StartAsync(CancellationToken external = default)
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        var bindAddr = IPAddress.TryParse(_options.BindAddress, out var addr) ? addr : IPAddress.Any;
        var ports = new[] { _options.ControlPort }.Concat(_options.ExtraListenPorts).Distinct();
        foreach (var port in ports)
        {
            var listener = new TcpListener(bindAddr, port);
            listener.Start(128);
            _listeners.Add(listener);
            Events.Session(TunnelEventLevel.Info, "server_start", $"监听 {bindAddr}:{port}（控制+数据附加）");
        }
        Sweeper = new HeartbeatSweeper(Sessions, Events, _options.Timing);
        Sweeper.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    internal HeartbeatSweeper? Sweeper { get; private set; }

    private async Task AcceptLoopAsync()
    {
        var tasks = _listeners.Select(AcceptOneListenerAsync).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* 正常停机 */ }
    }

    private async Task AcceptOneListenerAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return; // 监听已停/释放
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleConnectionSafeAsync(socket));
        }
    }

    private async Task HandleConnectionSafeAsync(Socket socket)
    {
        var remoteIp = "-";
        try
        {
            remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "-";
        }
        catch { /* 已断开 */ }

        if (!TryBeginUnauth(remoteIp))
        {
            Events.ConnectionFail("unauth_limit", $"来自 {remoteIp} 的未认证连接超过并发上限，拒绝", remoteIp: remoteIp);
            SafeClose(socket);
            return;
        }

        // 名额在认证成功时提前释放（审核修复#1）：每 IP 未认证上限只应约束"尚未证明身份"的连接，
        // 数据附加连接（每条被代理连接一条、来自客户端同一 IP）若占用名额直到连接结束，
        // 客户并发代理连接数将被误限在 16 以内（SQL 连接池 + HTTP keep-alive 轻松超限 → 随机断连）。
        var unauthGuard = new UnauthGuard(remoteIp, EndUnauth);
        try
        {
            await HandleConnectionAsync(socket, remoteIp, unauthGuard);
        }
        catch (ProtocolViolationException ex)
        {
            Events.ConnectionFail("protocol_error", $"协议违规：{ex.Message}", remoteIp: remoteIp);
        }
        catch (Exception ex)
        {
            Events.ConnectionFail("conn_error", $"连接异常：{ex.Message}", remoteIp: remoteIp);
        }
        finally
        {
            unauthGuard.Release();
        }
    }

    /// <summary>未认证名额守卫：幂等释放（认证成功提前释放一次，连接结束时兜底释放）。</summary>
    private sealed class UnauthGuard(string ip, Action<string> release)
    {
        private int _released;
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release(ip);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, string remoteIp, UnauthGuard unauthGuard)
    {
        socket.NoDelay = true;
        using var stream = new NetworkStream(socket, ownsSocket: true);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readCts.CancelAfter(_options.Timing.LoginTimeout);

        // 连接首字节分流：0xC1=控制（帧流）；0xC2=数据附加（attachId+secret，随后纯字节流）
        var magicBuf = new byte[1];
        var read = await stream.ReadAsync(magicBuf, readCts.Token);
        if (read == 0)
        {
            SafeClose(socket);
            return;
        }

        switch (magicBuf[0])
        {
            case WireConstants.MagicControl:
                await HandleControlAsync(socket, stream, remoteIp, unauthGuard);
                return;
            case WireConstants.MagicData:
                await HandleDataAttachAsync(socket, stream, remoteIp, unauthGuard);
                return;
            default:
                throw new ProtocolViolationException($"首字节非魔数：0x{magicBuf[0]:X2}");
        }
    }

    private bool TryBeginUnauth(string ip)
    {
        lock (_unauthLock)
        {
            var count = _unauthPerIp.GetValueOrDefault(ip);
            if (count >= _options.Timing.UnauthConnectionsPerIp)
                return false;
            _unauthPerIp[ip] = count + 1;
            return true;
        }
    }

    private void EndUnauth(string ip)
    {
        lock (_unauthLock)
        {
            if (_unauthPerIp.TryGetValue(ip, out var count))
                _unauthPerIp[ip] = Math.Max(0, count - 1);
        }
    }

    // ------------------------------------------------------------------ 控制连接

    private async Task HandleControlAsync(Socket socket, NetworkStream stream, string remoteIp, UnauthGuard unauthGuard)
    {
        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        loginCts.CancelAfter(_options.Timing.LoginTimeout);

        LoginMessage? login;
        Frame frame;
        try
        {
            frame = await FrameCodec.ReadFrameAsync(stream, loginCts.Token)
                ?? throw new ProtocolViolationException("未收到登录帧连接即关闭");
            if (frame.Type != FrameType.Login)
                throw new ProtocolViolationException($"首帧必须为 Login，实际 {frame.Type}");
            var json = Encoding.UTF8.GetString(frame.Payload.Span);
            login = TunnelJson.Deserialize<LoginMessage>(json)
                ?? throw new ProtocolViolationException("Login 帧解析失败");
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            throw new ProtocolViolationException("登录超时");
        }

        // 逐级校验：协议版本 → Token → 客户档案
        if (!VersionMatches(login.Ver))
        {
            await DenyLoginAsync(stream, ErrorCodes.LoginDenied,
                $"协议版本不匹配（服务端要求主版本 {MajorVersion(WireConstants.ProtocolVersion)}，客户端 {login.Ver}）");
            return;
        }
        if (!_options.Tokens.Contains(login.Token))
        {
            Events.Session(TunnelEventLevel.Warn, "login_denied", "Token 无效", login.ClientId, remoteIp);
            await DenyLoginAsync(stream, ErrorCodes.LoginDenied, "Token 无效");
            return;
        }

        var companyName = SanitizeCompanyName(login.CompanyName);

        // 登录串行门（v1.8）：临界区 = 查档→自动命名→端口分配→入库→会话建立。
        // PortAllocator 无内部锁、新分配在入库前对其他登录不可见，并发登录会重复分配同一自动端口；
        // 同 clientId 重登的 epoch 接管一并串行化。绑定的重试等待在门外，不阻塞其他客户登录。
        await _loginGate.WaitAsync(_cts.Token);
        long epoch;
        byte[] secret;
        LoginRespMessage resp;
        List<(int Idx, LoginMapping Map, int RemotePort)> assigned;
        TunnelSession session;
        try
        {
            CustomerRecord customer = Customers.FindCustomer(login.ClientId);
            if (customer == null)
            {
                if (_options.AutoRegister)
                {
                    // 迁移期：合法 token 的未知 clientId 自动建档（公司名优先，缺省占位）
                    customer = new CustomerRecord
                    {
                        ClientId = login.ClientId,
                        Name = companyName ?? $"未命名-{(login.ClientId.Length > 8 ? login.ClientId[..8] : login.ClientId)}",
                        Enabled = true,
                    };
                    Customers.UpsertCustomer(customer);
                    Events.Session(TunnelEventLevel.Info, "auto_registered",
                        companyName != null ? $"AutoRegister 自动建档：{companyName}" : "AutoRegister 自动建档", login.ClientId, remoteIp);
                }
                else
                {
                    var pending = Customers.FindPending(login.ClientId) ?? new PendingRecord { ClientId = login.ClientId };

                    // 已被管理员拒绝的 clientId：直接拒绝，不再入待审核列表（与 PendingRecord.Rejected 注释语义一致）
                    if (pending.Rejected)
                    {
                        Events.Session(TunnelEventLevel.Warn, "login_rejected", "该 clientId 已被拒绝接入", login.ClientId, remoteIp);
                        await DenyLoginAsync(stream, ErrorCodes.ClientDisabled, "该客户端已被拒绝接入");
                        return;
                    }

                    pending.CompanyName = companyName ?? pending.CompanyName;
                    pending.AppVersion = login.AppVersion;
                    pending.FirstSeenAt ??= DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    pending.LastTryAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    pending.Mappings = login.Mappings.Select(m => new MappingRecord
                    {
                        Key = m.Key,
                        LocalPort = m.LocalPort,
                        RemotePort = m.RequestedRemotePort,
                    }).ToList();
                    Customers.UpsertPending(pending);
                    Events.Session(TunnelEventLevel.Warn, "login_pending", "未知 clientId，进入待审核", login.ClientId, remoteIp);
                    await DenyLoginAsync(stream, ErrorCodes.PendingApproval, "等待管理员审核");
                    return;
                }
            }

            // 自动补名：档案仍是占位名（未命名-* / 空）且客户端本次上报了公司名 → 替换
            // （管理台手工改过名的档案不含占位前缀，不会被覆盖）
            if (companyName != null &&
                (string.IsNullOrEmpty(customer.Name) || customer.Name.StartsWith("未命名-", StringComparison.Ordinal)))
            {
                var oldName = customer.Name;
                customer.Name = companyName;
                Customers.UpsertCustomer(customer);
                Events.Session(TunnelEventLevel.Info, "auto_named",
                    $"客户档案自动命名：{oldName} → {companyName}", login.ClientId, remoteIp);
            }

            if (!customer.Enabled)
            {
                Events.Session(TunnelEventLevel.Warn, "client_disabled", "客户被禁用，拒绝登录", login.ClientId, remoteIp);
                await DenyLoginAsync(stream, ErrorCodes.ClientDisabled, "客户已被禁用");
                return;
            }

            // 封禁检查（v1.19）：临时封禁未到期 → 拒绝（客户端按封禁间隔重试）；已到期 → 自动清除并放行
            if (customer.BannedUntil is { } bannedUntil)
            {
                if (bannedUntil > DateTimeOffset.Now)
                {
                    var leftMinutes = Math.Ceiling((bannedUntil - DateTimeOffset.Now).TotalMinutes);
                    Events.Session(TunnelEventLevel.Warn, "login_banned",
                        $"客户端处于封禁期（剩余 {leftMinutes:0} 分钟）", login.ClientId, remoteIp);
                    await DenyLoginAsync(stream, ErrorCodes.Banned,
                        $"封禁中，剩余 {leftMinutes:0} 分钟后可自动恢复");
                    return;
                }
                customer.BannedUntil = null;
                Customers.UpsertCustomer(customer);
                Events.Session(TunnelEventLevel.Info, "ban_expired", "封禁已到期，自动解除", login.ClientId, remoteIp);
            }

            // 同 clientId 旧会话：epoch 接管，强断旧会话（其监听器随注销释放）
            var oldSession = Sessions.GetByClient(login.ClientId);
            epoch = Interlocked.Increment(ref _epochCounter);
            if (oldSession is { Closed: false })
            {
                Events.Session(TunnelEventLevel.Info, "takeover",
                    $"同客户新登录（epoch {epoch}），强断旧会话 {oldSession.SessionId:N}", login.ClientId, remoteIp);
                oldSession.Dispose();
            }

            // 端口分配（§4.4，服务端权威；分配即入库）
            resp = new LoginRespMessage { Code = ErrorCodes.Ok, Msg = "ok" };
            assigned = new List<(int Idx, LoginMapping Map, int RemotePort)>();
            foreach (var (mapping, idx) in login.Mappings.Select((m, i) => (m, i)))
            {
                var record = customer.Mappings.FirstOrDefault(r => r.Key == mapping.Key);
                if (record != null)
                {
                    // 该客户该 key 已有分配：沿用，绝不改变（D14）
                    if (mapping.RequestedRemotePort != 0 && mapping.RequestedRemotePort != record.RemotePort)
                        Events.Session(TunnelEventLevel.Info, "port_request_ignored",
                            $"映射 {mapping.Key} 请求端口 {mapping.RequestedRemotePort} 与已分配 {record.RemotePort} 不同，按已分配执行", login.ClientId);
                    assigned.Add((idx, mapping, record.RemotePort));
                    continue;
                }
                if (mapping.RequestedRemotePort != 0 && Allocator.IsUsable(mapping.RequestedRemotePort))
                {
                    // 新 key + 请求端口可用：采纳并入库（存量自动导入路径）
                    customer.Mappings.Add(new MappingRecord { Key = mapping.Key, LocalPort = mapping.LocalPort, RemotePort = mapping.RequestedRemotePort });
                    assigned.Add((idx, mapping, mapping.RequestedRemotePort));
                    Events.Session(TunnelEventLevel.Info, "port_adopted",
                        $"映射 {mapping.Key} 采纳请求端口 {mapping.RequestedRemotePort}", login.ClientId);
                    continue;
                }
                // 未请求/请求不可用/段外/黑名单 → 自动分配段内空闲端口
                var auto = Allocator.AllocateFree();
                if (auto == null)
                {
                    resp.Mappings.Add(new LoginRespMapping
                    {
                        Key = mapping.Key,
                        Status = ErrorCodes.ServerError,
                        Msg = "无可用远端端口",
                    });
                    Events.Session(TunnelEventLevel.Error, "alloc_exhausted", $"映射 {mapping.Key} 无可用端口", login.ClientId);
                    continue;
                }
                customer.Mappings.Add(new MappingRecord { Key = mapping.Key, LocalPort = mapping.LocalPort, RemotePort = auto.Value });
                assigned.Add((idx, mapping, auto.Value));
                Events.Session(TunnelEventLevel.Info, "port_auto_assigned",
                    $"映射 {mapping.Key}（请求 {mapping.RequestedRemotePort}）自动分配端口 {auto.Value}", login.ClientId);
            }

            customer.MachineName = login.MachineName;
            customer.AppVersion = login.AppVersion;
            customer.LastLoginAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Customers.UpsertCustomer(customer);

            // 建立会话
            secret = RandomNumberGenerator.GetBytes(WireConstants.SessionSecretLength);
            session = new TunnelSession
            {
                Epoch = epoch,
                ClientId = login.ClientId,
                MachineName = login.MachineName ?? "",
                AppVersion = login.AppVersion ?? "",
                RemoteIp = remoteIp,
                SessionSecret = secret,
                Socket = socket,
                Stream = stream,
            };
            Sessions.Add(session);
            unauthGuard.Release(); // 已认证：立刻归还未认证名额（审核修复#1）
            TcpTransport.ApplyControlConnectionKeepAlive(socket);
        }
        finally
        {
            _loginGate.Release();
        }

        // 绑定监听（含接管与重试），结果直接决定映射状态
        foreach (var (idx, mapping, remotePort) in assigned)
        {
            // 会话已被接管/断开（同客户快速重连的典型时序）：放弃剩余绑定。
            // 否则残留 BindAsync 会走"同 clientId 接管"路径杀死新会话（审核修复#3）
            if (session.Closed)
                break;
            var bind = await Listeners.BindAsync(remotePort, login.ClientId, session.SessionId, epoch, idx);
            resp.Mappings.Add(new LoginRespMapping
            {
                Key = mapping.Key,
                RemotePort = remotePort,
                Status = bind.Status,
                Msg = bind.Msg,
            });
        }

        // 绑定阶段会话已死（含上面 break 的情况）：立即回收会话与监听器。
        // 若不回收，Sessions/Listeners 将悬挂至该客户下次登录接管——期间端口保持绑定但一律拒绝来访（审核修复#2）
        if (session.Closed)
        {
            Sessions.Remove(session);
            Listeners.ReleaseSession(session.SessionId);
            session.Dispose();
            Events.Session(TunnelEventLevel.Warn, "login_aborted",
                "会话在登录绑定阶段被接管/断开，回收会话与监听", login.ClientId, remoteIp);
            return;
        }

        resp.SessionSecret = Convert.ToBase64String(secret);
        try
        {
            await SendFrameAsync(session, FrameType.LoginResp,
                Encoding.UTF8.GetBytes(TunnelJson.Serialize(resp)));
        }
        catch (Exception)
        {
            // 客户端在绑定期间 RST 掉线：ControlLoopAsync 不会进入（其 finally 不会执行），
            // 必须在此回收，否则会话与监听器泄漏（审核修复#2）
            Sessions.Remove(session);
            Listeners.ReleaseSession(session.SessionId);
            session.Dispose();
            Events.Session(TunnelEventLevel.Warn, "login_resp_failed",
                "登录应答发送失败（客户端已离开），回收会话与监听", login.ClientId, remoteIp);
            throw;
        }
        Events.Session(TunnelEventLevel.Info, "login_ok",
            $"登录成功（epoch {epoch}，{resp.Mappings.Count(m => m.Status == ErrorCodes.Ok)}/{login.Mappings.Count} 映射绑定）",
            login.ClientId, remoteIp);

        await ControlLoopAsync(session);
    }

    private async Task ControlLoopAsync(TunnelSession session)
    {
        var stream = session.Stream!;
        try
        {
            while (!_cts.IsCancellationRequested && !session.Closed)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                readCts.CancelAfter(_options.Timing.ControlReadTimeout);
                Frame? frame;
                try
                {
                    frame = await FrameCodec.ReadFrameAsync(stream, readCts.Token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    Events.Session(TunnelEventLevel.Warn, "read_timeout",
                        $"控制连接 {_options.Timing.ControlReadTimeout.TotalSeconds:0}s 无帧，断开", session.ClientId, session.RemoteIp);
                    break;
                }
                if (frame == null)
                    break; // 对端关闭

                session.Touch();
                switch (frame.Value.Type)
                {
                    case FrameType.Ping:
                        await SendFrameAsync(session, FrameType.Pong, ReadOnlyMemory<byte>.Empty);
                        break;
                    case FrameType.Pong:
                        break; // 客户端不会发，容忍
                    default:
                        throw new ProtocolViolationException($"控制连接收到非法帧 {frame.Value.Type}");
                }
            }
        }
        catch (ProtocolViolationException ex)
        {
            Events.ConnectionFail("protocol_error", $"控制连接协议违规：{ex.Message}", session.ClientId, session.RemoteIp);
        }
        catch
        {
            // 网络异常按断线处理
        }
        finally
        {
            Sessions.Remove(session);
            // 修复根因 1：只清理自己 sessionId 名下的监听器，绝不影响其他会话
            Listeners.ReleaseSession(session.SessionId);
            session.Dispose();
            Events.Session(TunnelEventLevel.Info, "logout", "会话注销", session.ClientId, session.RemoteIp);
        }
    }

    private static async Task DenyLoginAsync(NetworkStream stream, int code, string msg)
    {
        try
        {
            var resp = new LoginRespMessage { Code = code, Msg = msg };
            await FrameCodec.WriteJsonAsync(stream, FrameType.LoginResp, resp);
        }
        catch { /* 对端可能已断开 */ }
        finally
        {
            try { stream.Close(); } catch { }
        }
    }

    /// <summary>向会话控制连接写帧（写锁串行化，防 Ping/Open 交叉错帧）。</summary>
    private async Task SendFrameAsync(TunnelSession session, FrameType type, ReadOnlyMemory<byte> payload)
    {
        await session.WriteLock.WaitAsync(_cts.Token);
        try
        {
            await FrameCodec.WriteFrameAsync(session.Stream!, type, payload, _cts.Token);
        }
        finally
        {
            try { session.WriteLock.Release(); }
            catch (ObjectDisposedException) { /* 防御：销毁竞态 */ }
        }
    }

    // ------------------------------------------------------------------ 数据面

    /// <summary>来访连接（远端端口收到外部访问）→ 生成 attachId、缓存来访、发 Open 给客户端。</summary>
    private async Task HandleVisitorAsync(int remotePort, Socket visitor)
    {
        try
        {
            var remoteIp = (visitor.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "-";

            // 内存保护（v1.18）：pending 无上限可被外部连接洪水撑爆——全局 + 每会话双上限
            if (_pendingAttaches.Count >= _options.MaxPendingAttaches)
            {
                Events.ConnectionFail("pending_limit", "全局待配对 attach 超上限，拒绝来访连接", remoteIp: remoteIp);
                SafeClose(visitor);
                return;
            }

            var entry = Listeners.Get(remotePort);
            var session = entry == null ? null : Sessions.GetById(entry.SessionId);
            if (session == null || session.Closed)
            {
                SafeClose(visitor);
                return;
            }

            if (session.ActivePending >= _options.MaxPendingPerSession)
            {
                Events.ConnectionFail("pending_limit",
                    $"会话待配对 attach 超上限（{_options.MaxPendingPerSession}），拒绝来访连接", session.ClientId, remoteIp);
                SafeClose(visitor);
                return;
            }

            var attachIdBytes = RandomNumberGenerator.GetBytes(WireConstants.AttachIdLength);
            var attachId = BitConverter.ToInt64(attachIdBytes, 0);
            var pending = new PendingAttach
            {
                Session = session,
                Visitor = visitor,
                RemotePort = remotePort,
                MappingIdx = entry!.MappingIdx,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            if (!_pendingAttaches.TryAdd(attachId, pending))
            {
                SafeClose(visitor);
                return;
            }
            Interlocked.Increment(ref session.ActivePending);

            // attach 配对超时：超时未配对则关闭来访连接
            _ = Task.Delay(_options.Timing.AttachTimeout, _cts.Token).ContinueWith(_ =>
            {
                if (_pendingAttaches.TryRemove(attachId, out var expired))
                {
                    Events.ConnectionFail("attach_timeout", $"端口 {remotePort} 来访连接等待数据通道超时", session.ClientId);
                    ReleasePending(expired);
                }
            }, TaskScheduler.Default);

            var payload = OpenFrame.Encode(attachId, pending.MappingIdx);
            try
            {
                await SendFrameAsync(session, FrameType.Open, payload);
            }
            catch
            {
                if (_pendingAttaches.TryRemove(attachId, out var dead))
                    ReleasePending(dead);
            }
        }
        catch (Exception ex)
        {
            Events.ConnectionFail("conn_error", $"来访连接处理异常：{ex.Message}");
            SafeClose(visitor);
        }
    }

    /// <summary>释放一条待配对来访：递减会话计数 + 关闭来访 socket。</summary>
    private static void ReleasePending(PendingAttach expired)
    {
        Interlocked.Decrement(ref expired.Session.ActivePending);
        SafeClose(expired.Visitor);
    }

    /// <summary>数据附加连接（0xC2 + attachId + sessionSecret）→ 与来访连接配对互泵。</summary>
    private async Task HandleDataAttachAsync(Socket socket, NetworkStream stream, string remoteIp, UnauthGuard unauthGuard)
    {
        var head = new byte[WireConstants.AttachIdLength + WireConstants.SessionSecretLength];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readCts.CancelAfter(_options.Timing.LoginTimeout);
        if (!await FrameCodec.ReadExactAsync(stream, head, readCts.Token))
            return;

        var attachId = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(head);
        var secret = new ReadOnlySpan<byte>(head, WireConstants.AttachIdLength, WireConstants.SessionSecretLength);

        if (!_pendingAttaches.TryGetValue(attachId, out var pending)
            || !CryptographicOperations.FixedTimeEquals(
                secret, pending.Session.SessionSecret.AsSpan(0, Math.Min(secret.Length, pending.Session.SessionSecret.Length))))
        {
            Events.ConnectionFail("attach_invalid", $"attachId 无效或会话密钥不符（来自 {remoteIp}）");
            SafeClose(socket);
            return;
        }
        if (!_pendingAttaches.TryRemove(attachId, out var paired))
        {
            // 并发同 attachId，仅第一个生效
            SafeClose(socket);
            return;
        }
        Interlocked.Decrement(ref paired.Session.ActivePending);
        unauthGuard.Release(); // 配对成功=已认证：归还未认证名额（审核修复#1）

        var visitor = paired.Visitor;
        Events.CountConnection(paired.RemotePort);
        Interlocked.Increment(ref paired.Session.ConnectionsServed);

        // 数据连接 best-effort keepalive（防 NAT 老化，D15 注）
        TcpTransport.ApplyDataConnectionKeepAlive(socket);

        long up = 0, down = 0;
        var counting = new Action<int, long>((dir, bytes) =>
        {
            if (dir == 0) Interlocked.Add(ref down, bytes); else Interlocked.Add(ref up, bytes);
        });
        try
        {
            await StreamPump.CopyBothAsync(socket, visitor, _cts.Token, counting);
        }
        finally
        {
            Events.CountBytes(paired.RemotePort, up, down);
        }
    }

    // ------------------------------------------------------------------ 管理操作

    /// <summary>踢下线：发 Error 后断开（客户端将按正常退避重连）。</summary>
    public async Task KickClientAsync(string clientId, string reason)
    {
        var session = Sessions.GetByClient(clientId);
        if (session == null || session.Closed)
            return;
        try
        {
            await SendFrameAsync(session, FrameType.Error,
                Encoding.UTF8.GetBytes(TunnelJson.Serialize(new ErrorMessage { Code = ErrorCodes.ServerError, Msg = reason })));
        }
        catch { /* 忽略发送失败 */ }
        session.Dispose();
    }

    /// <summary>配置变更通知：下发 ConfigChanged 并断开，客户端立即重连取新配置。</summary>
    public async Task NotifyConfigChangedAsync(string clientId)
    {
        var session = Sessions.GetByClient(clientId);
        if (session == null || session.Closed)
            return;
        try
        {
            await SendFrameAsync(session, FrameType.Error,
                Encoding.UTF8.GetBytes(TunnelJson.Serialize(new ErrorMessage { Code = ErrorCodes.ConfigChanged, Msg = "配置已变更，请重新登录" })));
        }
        catch { /* 忽略发送失败 */ }
        session.Dispose();
        Events.Session(TunnelEventLevel.Info, "config_changed", "管理台修改配置，已通知客户端重连", clientId);
    }

    private async Task KillSessionAsync(Guid sessionId, string reason)
    {
        var session = Sessions.GetById(sessionId);
        if (session != null && !session.Closed)
        {
            session.Dispose();
            _ = reason;
            await Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ 停机

    private static void SafeClose(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
    }

    private static bool VersionMatches(string clientVer)
        => int.TryParse(clientVer.Split('.')[0], out var major)
            && int.TryParse(WireConstants.ProtocolVersion.Split('.')[0], out var required)
            && major == required;

    private static int MajorVersion(string ver)
        => int.TryParse(ver.Split('.')[0], out var major) ? major : -1;

    /// <summary>获取登录串行门（内部完成异步等待）；调用方 using 释放。</summary>
    private async Task<IDisposable> AcquireLoginGateAsync(CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct);
        return new LoginGateReleaser(_loginGate);
    }

    /// <summary>SemaphoreSlim 的 IDisposable 包装（供 await using 使用）。</summary>
    private sealed class LoginGateReleaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>
    /// 公司名清洗：去首尾空白、控制字符折为空格、截断至 32 字符；无效返回 null。
    /// </summary>
    private static string? SanitizeCompanyName(string? raw)
    {
        if (raw == null) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var name = sb.ToString().Trim();
        if (name.Length == 0) return null;
        return name.Length <= 32 ? name : name[..32];
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _cts.Cancel();

        foreach (var listener in _listeners)
            listener.Stop();
        try { if (_acceptLoop != null) await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }

        foreach (var session in Sessions.GetAll())
        {
            session.Dispose();
            Listeners.ReleaseSession(session.SessionId);
        }
        foreach (var pending in _pendingAttaches.Values)
            SafeClose(pending.Visitor);
        _pendingAttaches.Clear();

        Sweeper?.Dispose();
        Listeners.Dispose();
        Events.Dispose();
        Customers.Dispose();
        _cts.Dispose();
    }
}
