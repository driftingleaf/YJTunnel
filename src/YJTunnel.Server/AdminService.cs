using System.Net;
using System.Text;
using YJTunnel;
using YJTunnel.Server;
using TunnelHost = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Server;

/// <summary>总览统计数据（总览页 5s 轮询）。</summary>
public sealed class OverviewData
{
    public int OnlineCount { get; set; }
    public int CustomerCount { get; set; }
    public int PendingCount { get; set; }
    public int PortUsed { get; set; }
    public List<PortUsageRow> Ports { get; set; } = new();
    public List<BindFailureRow> BindFailures { get; set; } = new();
    public List<OnlineSessionRow> Sessions { get; set; } = new();
}

public sealed class PortUsageRow
{
    public int RemotePort { get; set; }
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string MappingKey { get; set; } = "";
    public int LocalPort { get; set; }
    public bool Online { get; set; }
    public string BindStatus { get; set; } = ""; // ok / occupied / offline
}

public sealed class BindFailureRow
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Msg { get; set; } = "";
}

public sealed class OnlineSessionRow
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string LoginAt { get; set; } = "";
    public string LastAlive { get; set; } = "";
    public long Connections { get; set; }
}

/// <summary>
/// 管理台服务门面：认证/锁定、总览、客户 CRUD、端口表、待审核、事件查询（§六）。
/// Razor Pages 经由 DI 单例使用；所有管理操作记 admin_op 审计事件。
/// </summary>
public sealed class AdminService
{
    private readonly TunnelHost _server;
    private readonly AdminOptions _options;

    private readonly object _authLock = new();
    private readonly Dictionary<string, LoginAttempts> _attempts = new();

    private sealed class LoginAttempts
    {
        public int Failures;
        public DateTime LockedUntil = DateTime.MinValue;
    }

    public AdminService(TunnelHost server, AdminOptions options)
    {
        _server = server;
        _options = options;
    }

    // ------------------------------------------------------------------ 认证

    /// <summary>登录校验（账号密码 + 失败锁定）。成功返回 null，失败返回原因文本。</summary>
    public string? Login(string username, string password, string remoteIp)
    {
        lock (_authLock)
        {
            var key = $"{username}|{remoteIp}";
            var attempt = _attempts.TryGetValue(key, out var a) ? a : _attempts[key] = new LoginAttempts();
            if (DateTime.Now < attempt.LockedUntil)
                return $"失败次数过多，已锁定至 {attempt.LockedUntil:HH:mm:ss}（{_options.LockoutMinutes} 分钟）";

            if (username != _options.Username || !string.Equals(password, _options.Password, StringComparison.Ordinal))
            {
                attempt.Failures++;
                Audit("admin_login_fail", $"登录失败 {attempt.Failures}/{_options.LockoutFailures}（{remoteIp}）", remoteIp);
                if (attempt.Failures >= _options.LockoutFailures)
                {
                    attempt.LockedUntil = DateTime.Now.AddMinutes(_options.LockoutMinutes);
                    attempt.Failures = 0;
                    Audit("admin_locked", $"账号锁定 {(_options.LockoutMinutes)} 分钟（{remoteIp}）", remoteIp);
                }
                return "账号或密码错误";
            }

            attempt.Failures = 0;
            Audit("admin_login_ok", $"登录成功（{remoteIp}）", remoteIp);
            return null;
        }
    }

    /// <summary>
    /// 管理台来源白名单判定：回环恒放行；条目 "*" = 放行所有来源（仅限内网/受控环境）；
    /// 其余条目按精确 IP 匹配（远端为 IPv4-mapped IPv6 时归一化后比对）。
    /// </summary>
    public bool IsIpAllowed(IPAddress? remote)
    {
        if (remote == null)
            return false;
        if (IPAddress.IsLoopback(remote))
            return true;

        // 双栈监听下远端可能呈 ::ffff:a.b.c.d，归一化成 IPv4 再比对，否则白名单静默失灵
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        var remoteStr = remote.ToString();

        if (_options.AllowedIps.Count == 0)
            return false; // 空=仅回环（已由上方 IsLoopback 放行）

        if (_options.AllowedIps.Any(e => string.Equals(e?.Trim(), "*", StringComparison.Ordinal)))
            return true;

        return _options.AllowedIps.Any(e => string.Equals(e?.Trim(), remoteStr, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ 总览

    public OverviewData GetOverview()
    {
        var customers = _server.Customers.GetAllCustomers();
        var listeners = _server.Listeners.GetAll();
        var sessions = _server.Sessions.GetAll();

        var onlineIds = sessions.Select(s => s.ClientId).ToHashSet();
        var data = new OverviewData
        {
            OnlineCount = sessions.Count,
            CustomerCount = customers.Count,
            PendingCount = _server.Customers.GetAllPending().Count(p => !p.Rejected),
        };

        foreach (var customer in customers)
        {
            var online = onlineIds.Contains(customer.ClientId);
            foreach (var mapping in customer.Mappings)
            {
                // 绑定状态语义（v1.14）：ok=已绑定；occupied=端口被其他客户的监听持有（真冲突）；
                // pending=客户在线但端口尚未绑定（重连窗口/绑定重试中/绑定失败待重试）——不再误标为"被占"
                var entry = listeners.FirstOrDefault(l => l.RemotePort == mapping.RemotePort);
                string status;
                if (!online)
                    status = "offline";
                else if (entry != null && entry.ClientId == customer.ClientId)
                    status = "ok";
                else if (entry != null)
                    status = "occupied";
                else
                    status = "pending";

                data.Ports.Add(new PortUsageRow
                {
                    RemotePort = mapping.RemotePort,
                    ClientId = customer.ClientId,
                    Name = customer.Name,
                    MappingKey = mapping.Key,
                    LocalPort = mapping.LocalPort,
                    Online = online,
                    BindStatus = status,
                });
            }
        }
        data.Ports.Sort((a, b) => a.RemotePort.CompareTo(b.RemotePort));
        data.PortUsed = data.Ports.Count;

        foreach (var session in sessions)
        {
            var customer = customers.FirstOrDefault(c => c.ClientId == session.ClientId);
            data.Sessions.Add(new OnlineSessionRow
            {
                ClientId = session.ClientId,
                Name = customer?.Name ?? "",
                RemoteIp = session.RemoteIp,
                MachineName = session.MachineName,
                AppVersion = session.AppVersion,
                LoginAt = session.LoginAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                LastAlive = session.LastAliveUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Connections = session.ConnectionsServed,
            });
        }

        // 绑定失败告警：客户在线但端口未绑定
        data.BindFailures = data.Ports
            .Where(p => p.Online && p.BindStatus == "occupied")
            .Select(p => new BindFailureRow { ClientId = p.ClientId, Name = p.Name, Msg = $"端口 {p.RemotePort} 绑定失败" })
            .ToList();

        return data;
    }

    // ------------------------------------------------------------------ 客户管理

    public IReadOnlyList<CustomerRecord> GetCustomers() => _server.Customers.GetAllCustomers();

    public CustomerRecord? GetCustomer(string clientId) => _server.Customers.FindCustomer(clientId);

    /// <summary>
    /// 改端口前置校验（v1.14，审核补充）：保存前拦截"端口被占/重复分配/越界/黑名单"，
    /// 把"保存成功但绑定失败（被占）"的事后状态提前为明确的报错。返回错误文本或 null。
    /// </summary>
    public string? ValidatePortChange(CustomerRecord record, string mappingKey, int newPort)
    {
        if (newPort < 1 || newPort > 65535)
            return $"端口 {newPort} 无效（应为 1-65535）";

        var ranges = string.Join(" / ", _server.Options.AllowPorts.Select(r => $"{r.Start}-{r.End}"));        if (!_server.Allocator.IsAllowed(newPort))
            return $"端口 {newPort} 不在白名单段（{ranges}）内，或属黑名单端口";

        var selfConflict = record.Mappings.FirstOrDefault(m => m.Key != mappingKey && m.RemotePort == newPort);
        if (selfConflict != null)
            return $"端口 {newPort} 已被本客户的另一映射（本地端口 {selfConflict.LocalPort}）占用";

        var owner = _server.Customers.FindOwnerOfPort(newPort);
        if (owner != null && owner.ClientId != record.ClientId)
            return $"端口 {newPort} 已分配给客户「{owner.Name}」，不能重复分配";

        // 本客户自身已持有的端口（如原样填回当前端口）：不视为占用，由调用方拦截"新旧相同"
        if (record.Mappings.Any(m => m.RemotePort == newPort))
            return null;

        if (!_server.Allocator.IsFree(newPort))
            return $"端口 {newPort} 被服务器上的其他程序占用，请换一个端口";

        return null;
    }

    /// <summary>保存客户（新增/编辑）。返回错误文本或 null。</summary>
    public async Task<string?> SaveCustomerAsync(CustomerRecord record, string operatorIp, bool notifyClient = true)
    {        if (string.IsNullOrWhiteSpace(record.ClientId))
            return "clientId 不能为空";
        record.ClientId = record.ClientId.Trim();

        // 端口唯一性校验：不允许与其他客户的端口冲突（D14：修改已分配端口必须二次确认由前端弹窗承担）
        var all = _server.Customers.GetAllCustomers();
        foreach (var other in all.Where(c => c.ClientId != record.ClientId))
        {
            var conflict = other.Mappings.FirstOrDefault(m =>
                record.Mappings.Any(rm => rm.RemotePort == m.RemotePort));
            if (conflict != null)
                return $"端口 {conflict.RemotePort} 已分配给客户「{other.Name}」（{other.ClientId}），不能重复分配";
        }

        var exists = all.Any(c => c.ClientId == record.ClientId);
        _server.Customers.UpsertCustomer(record);
        Audit("admin_save_customer", $"{(exists ? "编辑" : "新增")}客户 {record.ClientId}（{record.Name}）", operatorIp);

        if (notifyClient)
            await _server.NotifyConfigChangedAsync(record.ClientId); // 在线客户端 <10s 内重连取新配置
        return null;
    }

    public async Task<bool> DeleteCustomerAsync(string clientId, string operatorIp)
    {
        var removed = _server.Customers.DeleteCustomer(clientId);
        if (removed)
        {
            Audit("admin_delete_customer", $"删除客户 {clientId}", operatorIp);
            await _server.KickClientAsync(clientId, "档案已被删除");
        }
        return removed;
    }

    /// <summary>
    /// 踢下线（v1.19）：断开当前会话；可选临时封禁（banMinutes>0，期内登录被拒）或永久封禁（banPermanent=true，等同禁用客户）。
    /// 客户端将自动重连（封禁期内登录被拒，到期/解封后自动恢复）。
    /// </summary>
    public async Task KickAsync(string clientId, string operatorIp, int? banMinutes = null, bool banPermanent = false)
    {
        var record = _server.Customers.FindCustomer(clientId);
        var action = banPermanent ? "永久封禁（禁用）"
            : banMinutes is > 0 ? $"临时封禁 {banMinutes.Value} 分钟"
            : "仅踢下线";

        if (banPermanent && record != null)
        {
            record.Enabled = false;
            _server.Customers.UpsertCustomer(record);
        }
        else if (banMinutes is > 0 && record != null)
        {
            record.BannedUntil = DateTimeOffset.Now.AddMinutes(banMinutes.Value);
            _server.Customers.UpsertCustomer(record);
        }

        Audit("admin_kick", $"{action} {clientId}", operatorIp);
        await _server.KickClientAsync(clientId,
            banPermanent ? "已被永久封禁" : banMinutes is > 0 ? $"已被封禁 {banMinutes.Value} 分钟" : "管理员踢下线");
    }

    // ------------------------------------------------------------------ 待审核

    public IReadOnlyList<PendingRecord> GetPending() => _server.Customers.GetAllPending();

    /// <summary>通过待审核：建档 + 自动分配端口。返回错误文本或 null。</summary>
    public async Task<string?> ApprovePendingAsync(string clientId, string operatorIp)
    {
        var pending = _server.Customers.FindPending(clientId);
        if (pending == null)
            return "申请不存在";

        var customer = new CustomerRecord
        {
            ClientId = clientId,
            // 优先用客户端上报的公司名（v1.7 特性贯通到审核路径），缺省占位
            Name = pending.CompanyName ?? $"未命名-{(clientId.Length > 8 ? clientId[..8] : clientId)}",
            Enabled = true,
            MachineName = pending.MachineName,
            AppVersion = pending.AppVersion,
            Mappings = new List<MappingRecord>(),
        };
        // 审核通过即为每个申请映射自动分配端口（请求端口可用则采纳）
        foreach (var mapping in pending.Mappings)
        {
            var remote = mapping.RemotePort > 0 && _server.Allocator.IsUsable(mapping.RemotePort)
                ? mapping.RemotePort
                : _server.Allocator.AllocateFree();
            if (remote == null)
                return $"端口段已耗尽，无法为映射 {mapping.Key} 分配";
            customer.Mappings.Add(new MappingRecord
            {
                Key = mapping.Key,
                LocalPort = mapping.LocalPort,
                RemotePort = remote.Value,
            });
        }
        _server.Customers.UpsertCustomer(customer);
        _server.Customers.RemovePending(clientId);
        Audit("admin_approve", $"审核通过 {clientId}，分配端口 {string.Join(",", customer.Mappings.Select(m => m.RemotePort))}", operatorIp);
        _ = operatorIp;
        await Task.CompletedTask;
        return null;
    }

    /// <summary>拒绝待审核：标记 rejected（该 clientId 再登录直接拒绝，不再入待审核列表）。</summary>
    public void RejectPending(string clientId, string operatorIp)
    {
        var pending = _server.Customers.FindPending(clientId);
        if (pending == null) return;
        pending.Rejected = true;
        _server.Customers.UpsertPending(pending);
        Audit("admin_reject", $"拒绝申请 {clientId}", operatorIp);
    }

    // ------------------------------------------------------------------ 事件与导出

    public List<Dictionary<string, object?>> QueryEvents(string? clientId, string? type, int limit)
        => _server.Events.Query(clientId, type, limit);

    /// <summary>端口分配表 CSV 导出（UTF-8 BOM，Excel 直开）。</summary>
    public byte[] ExportPortsCsv()
    {
        var overview = GetOverview();
        var sb = new StringBuilder();
        sb.AppendLine("远端端口,客户ID,客户名称,映射Key,本地端口,状态");
        foreach (var p in overview.Ports)
        {
            var status = p.BindStatus switch
            {
                "ok" => "在线已绑定",
                "occupied" => "绑定失败(被占)",
                _ => "离线",
            };
            sb.AppendLine($"{p.RemotePort},{p.ClientId},{EscapeCsv(p.Name)},{p.MappingKey},{p.LocalPort},{status}");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string EscapeCsv(string? value)
        => (value ?? "").Contains(',') || (value ?? "").Contains('"')
            ? "\"" + (value ?? "").Replace("\"", "\"\"") + "\""
            : (value ?? "");

    private void Audit(string type, string message, string? operatorIp = null)
        => _server.Events.Session(TunnelEventLevel.Info, type, message, remoteIp: operatorIp);
}
