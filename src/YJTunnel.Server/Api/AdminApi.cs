using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using YJTunnel.Server;
using TunnelHost = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Server.Api;

/// <summary>登录请求（管理台密码与 Web 管理台共用，账号默认 admin）。</summary>
public sealed class ApiLoginRequest
{
    public string? Username { get; set; }
    public string Password { get; set; } = "";
}

/// <summary>改端口请求。</summary>
public sealed class ApiPortChangeRequest
{
    public string MappingKey { get; set; } = "";
    public int NewPort { get; set; }
}

/// <summary>踢下线/封禁请求：mode = kick（仅踢）| temp（临时）| permanent（永久）。</summary>
public sealed class ApiKickRequest
{
    public string Mode { get; set; } = "kick";
    public int? Minutes { get; set; }
    public string? Note { get; set; }
}

/// <summary>启用/禁用客户（解除封禁 = enabled=true，同时清 BannedUntil）。</summary>
public sealed class ApiCustomerStatusRequest
{
    public bool Enabled { get; set; }
}

/// <summary>待审核处理：approve=true 通过，false 拒绝。</summary>
public sealed class ApiPendingActionRequest
{
    public bool Approve { get; set; }
}

/// <summary>统一结果包装（错误时 Error 非空）。</summary>
public sealed class ApiResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public static ApiResult Success() => new() { Ok = true };
    public static ApiResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>客户列表响应：客户档案 + 当前在线 clientId 集合。</summary>
public sealed class ApiCustomersResponse
{
    public List<CustomerRecord> Customers { get; set; } = new();
    public List<string> OnlineIds { get; set; } = new();
}

/// <summary>服务端信息（控制台页头/诊断用）。</summary>
public sealed class ApiServerInfo
{
    public string Version { get; set; } = "";
    public int ControlPort { get; set; }
    public bool AutoRegister { get; set; }
    public List<string> AllowPortRanges { get; set; } = new();
    public bool AdminEnabled { get; set; }
}

/// <summary>
/// 管理控制台 JSON API（v1.21）：桌面控制台专用，与 Web 管理台共用 AdminService 与 Cookie 认证。
/// 所有端点位于 IP 白名单中间件之后；除登录/会话探测外均要求 TunnelAdmin 策略。
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app, TunnelServerSection options)
    {
        // ---------------- 认证 ----------------

        app.MapPost("/api/login", async (HttpContext http, AdminService admin, ApiLoginRequest req) =>
        {
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "-";
            var user = string.IsNullOrWhiteSpace(req.Username) ? "admin" : req.Username.Trim();
            var error = admin.Login(user, req.Password, remote);
            if (error != null)
                return Results.Json(new { ok = false, error }, statusCode: StatusCodes.Status401Unauthorized);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, user),
                new Claim("role", "admin"),
            }, CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });

            return Results.Json(new { ok = true, user });
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapPost("/api/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Json(new { ok = true });
        }).AllowAnonymous().ExcludeFromDescription();

        // 会话探测：控制台启动时校验 Cookie 是否仍有效（匿名可访问，未登录返回 authenticated=false）
        app.MapGet("/api/session", (HttpContext http) =>
        {
            var authed = http.User.Identity?.IsAuthenticated == true;
            return Results.Json(new { authenticated = authed, user = authed ? http.User.Identity?.Name : null });
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------------- 只读数据 ----------------

        app.MapGet("/api/info", () => Results.Json(new ApiServerInfo
        {
            Version = typeof(AdminApi).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            ControlPort = options.ControlPort,
            AutoRegister = options.AutoRegister,
            AllowPortRanges = options.AllowPorts.Select(r => $"{r.Start}-{r.End}").ToList(),
            AdminEnabled = options.Admin.Enabled,
        })).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        app.MapGet("/api/overview", (AdminService admin) => Results.Json(admin.GetOverview()))
            .RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        app.MapGet("/api/customers", (AdminService admin) =>
        {
            var overview = admin.GetOverview();
            return Results.Json(new ApiCustomersResponse
            {
                Customers = admin.GetCustomers().ToList(),
                OnlineIds = overview.Sessions.Select(s => s.ClientId).ToList(),
            });
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        app.MapGet("/api/pending", (AdminService admin) => Results.Json(admin.GetPending()))
            .RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        app.MapGet("/api/events", (AdminService admin, string? clientId, string? type, int? limit) =>
            Results.Json(admin.QueryEvents(clientId, type, Math.Clamp(limit ?? 100, 1, 1000))))
            .RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        // ---------------- 写操作（与 Web 页面同一 AdminService 路径） ----------------

        // 改端口：前置校验（占用/唯一性/白名单）→ 保存 → 在线客户端 <10s 推送重连
        app.MapPost("/api/customers/{clientId}/port", async (string clientId, ApiPortChangeRequest req,
            HttpContext http, AdminService admin) =>
        {
            var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "-";
            var record = admin.GetCustomer(clientId);
            if (record == null) return Results.Json(ApiResult.Fail("客户不存在"), statusCode: 404);

            var mapping = record.Mappings.FirstOrDefault(m => m.Key == req.MappingKey);
            if (mapping == null) return Results.Json(ApiResult.Fail("映射不存在（客户端可能已更换本地端口）"), statusCode: 404);

            var validationError = admin.ValidatePortChange(record, req.MappingKey, req.NewPort);
            if (validationError != null) return Results.Json(ApiResult.Fail(validationError), statusCode: 400);

            mapping.RemotePort = req.NewPort;
            var saveError = await admin.SaveCustomerAsync(record, remoteIp);
            return saveError != null
                ? Results.Json(ApiResult.Fail(saveError), statusCode: 400)
                : Results.Json(ApiResult.Success());
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        // 踢下线/封禁三档
        app.MapPost("/api/customers/{clientId}/kick", async (string clientId, ApiKickRequest req,
            HttpContext http, AdminService admin) =>
        {
            var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "-";
            if (admin.GetCustomer(clientId) == null)
                return Results.Json(ApiResult.Fail("客户不存在"), statusCode: 404);

            switch (req.Mode)
            {
                case "kick":
                    await admin.KickAsync(clientId, remoteIp);
                    break;
                case "temp" when req.Minutes is > 0 and <= 525600:
                    await admin.KickAsync(clientId, remoteIp, req.Minutes.Value);
                    break;
                case "permanent":
                    await admin.KickAsync(clientId, remoteIp, banPermanent: true);
                    break;
                default:
                    return Results.Json(ApiResult.Fail("mode 必须是 kick / temp / permanent；temp 需 1-525600 分钟"),
                        statusCode: 400);
            }
            return Results.Json(ApiResult.Success());
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        // 启用/禁用客户（解除封禁 = enabled=true）
        app.MapPost("/api/customers/{clientId}/status", async (string clientId, ApiCustomerStatusRequest req,
            HttpContext http, AdminService admin) =>
        {
            var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "-";
            var record = admin.GetCustomer(clientId);
            if (record == null) return Results.Json(ApiResult.Fail("客户不存在"), statusCode: 404);

            record.Enabled = req.Enabled;
            if (req.Enabled) record.BannedUntil = null;
            var saveError = await admin.SaveCustomerAsync(record, remoteIp);
            return saveError != null
                ? Results.Json(ApiResult.Fail(saveError), statusCode: 400)
                : Results.Json(ApiResult.Success());
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        // 本机控制台退出时优雅停机（仅回环 + 已认证）：触发 ApplicationStopping → 释放隧道监听
        app.MapPost("/api/shutdown", (HttpContext http, IHostApplicationLifetime lifetime) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote == null || !System.Net.IPAddress.IsLoopback(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                lifetime.StopApplication();
            });
            return Results.Json(new { ok = true });
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();

        // 待审核：通过 / 拒绝
        app.MapPost("/api/pending/{clientId}", async (string clientId, ApiPendingActionRequest req,
            HttpContext http, AdminService admin) =>
        {
            var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "-";
            if (req.Approve)
            {
                var error = await admin.ApprovePendingAsync(clientId, remoteIp);
                return error != null
                    ? Results.Json(ApiResult.Fail(error), statusCode: 400)
                    : Results.Json(ApiResult.Success());
            }

            admin.RejectPending(clientId, remoteIp);
            return Results.Json(ApiResult.Success());
        }).RequireAuthorization("TunnelAdmin").ExcludeFromDescription();
    }
}
