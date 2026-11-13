using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using YJTunnel;
using YJTunnel.Server;
using YJTunnel.Server.Api;

// ---------------------------------------------------------------- 配置装载

var configPath = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    ?? Path.Combine(AppContext.BaseDirectory, "tunnelserver.json");

// 首次部署自愈（v1.9）：缺配置自动从 configs 模板生成——直接运行发布目录、免 install 脚本场景同样生效
if (!TunnelServerHostConfig.EnsureConfigFile(configPath))
{
    Console.WriteLine($"[错误] 未找到配置文件 {configPath}，且程序目录无 configs/tunnelserver.json 模板。" +
        "请手工复制 configs/tunnelserver.json 到程序目录后重试。");
    return 1;
}

var config = TunnelServerHostConfig.Load(configPath);

// 首次启动凭据自愈（开源版）：占位符自动替换为强随机值（写回配置并打印）
TunnelServerHostConfig.ReplaceCredentialPlaceholders(configPath);

if (config.TunnelServer.DataDir == ".")
    config.TunnelServer.DataDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
if (config.TunnelServer.Admin is { Enabled: true } adminOpts &&
    adminOpts.AllowedIps.Any(e => string.Equals(e?.Trim(), "*", StringComparison.Ordinal)))
{
    Console.WriteLine("[警告] 管理台 AllowedIps=*：任意来源均可访问管理台。请务必确认 5103 未对公网放行！" +
        "（密码是唯一屏障，建议仅在内网/受控环境使用）");
}

// ---------------------------------------------------------------- 隧道服务端（TcpListener :5003，控制+数据附加）

var tunnelServer = new TunnelServer(config.TunnelServer);
await tunnelServer.StartAsync();
Console.WriteLine($"[YJTunnel] 隧道服务已启动：端口 {config.TunnelServer.ControlPort}，白名单段 " +
    string.Join(",", config.TunnelServer.AllowPorts.Select(r => $"{r.Start}-{r.End}")) +
    $"，AutoRegister={(config.TunnelServer.AutoRegister ? "开（迁移期）" : "关")}，数据目录 {config.TunnelServer.DataDir}");

// ---------------------------------------------------------------- 管理台（Kestrel :5103，HTTP + IP 白名单）

if (!config.TunnelServer.Admin.Enabled)
{
    Console.WriteLine("[YJTunnel] 管理台未启用，仅运行隧道服务。Ctrl+C 退出。");
    var done = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
    done.Wait();
    await tunnelServer.DisposeAsync();
    return 0;
}

var adminOptions = config.TunnelServer.Admin;
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "YJTunnel");
// 一体机模式（--local-only）：管理台仅绑定回环，避免把管理网页暴露到局域网/公网
var localOnly = args.Contains("--local-only", StringComparer.OrdinalIgnoreCase);
builder.WebHost.UseUrls(localOnly ? $"http://127.0.0.1:{adminOptions.Port}" : $"http://*:{adminOptions.Port}");

builder.Services.AddSingleton(tunnelServer);
builder.Services.AddSingleton(new AdminService(tunnelServer, adminOptions));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Login/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8); // Cookie 8h（§六）
        options.SlidingExpiration = false;
        options.Cookie.Name = "yj-tunnel-admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // API 客户端（桌面控制台）未认证时返回 401 JSON，而非 302 跳登录页
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TunnelAdmin", policy => policy.RequireClaim("role", "admin"));
});
builder.Services.AddRazorPages();

var app = builder.Build();

// 一体机模式：父进程（管理控制台）退出时自动停机，避免残留孤儿服务端
var parentPidArg = args.FirstOrDefault(a => a.StartsWith("--parent-pid=", StringComparison.Ordinal));
if (parentPidArg is not null && int.TryParse(parentPidArg["--parent-pid=".Length..], out var parentPid))
{
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                if (parent.HasExited) break;
            }
            catch
            {
                break; // 父进程不存在
            }
        }
        app.Lifetime.StopApplication();
    });
}

// Kestrel 侧全局异常隔离（§5.5：Web 崩溃不影响隧道，sc failure 兜底自动重启）
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] 未处理异常 {context.Request.Path}：{ex.Message}");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("服务器内部错误，请查看服务端日志");
        }
    }
});

// IP 白名单（为空=仅回环；先于认证，未登录访问也不放行）
app.Use(async (context, next) =>
{
    var admin = context.RequestServices.GetRequiredService<AdminService>();
    var remote = context.Connection.RemoteIpAddress;
    if (!admin.IsIpAllowed(remote))
    {
        Console.WriteLine($"[Admin] 拒绝白名单外 IP 访问：{remote}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("403：IP 不在管理台白名单内");
        return;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// 端口分配表 CSV 导出（授权后）
app.MapGet("/ports.csv", [Authorize(Policy = "TunnelAdmin")] (AdminService admin) =>
{
    var bytes = admin.ExportPortsCsv();
    return Results.File(bytes, "text/csv", "ports.csv");
}).ExcludeFromDescription();

// 端口绑定状态轮询（授权后）：改端口后页面自动刷新用（v1.15）
app.MapGet("/ports-status", [Authorize(Policy = "TunnelAdmin")] (AdminService admin) =>
    Results.Json(new { ports = admin.GetOverview().Ports })).ExcludeFromDescription();

app.MapRazorPages();

// 桌面管理控制台 JSON API（v1.21）：与 Web 管理台共用 AdminService/认证/IP 白名单
app.MapAdminApi(config.TunnelServer);

// 停机时释放隧道资源
app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("[YJTunnel] 正在停止隧道服务…");
    tunnelServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
});

Console.WriteLine($"[YJTunnel] 管理台已启动：http://*:{adminOptions.Port}（IP 白名单：" +
    (adminOptions.AllowedIps.Count == 0 ? "仅回环" : string.Join(",", adminOptions.AllowedIps)) + "）");
app.Run();
return 0;
