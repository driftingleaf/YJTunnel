using System.Diagnostics;
using System.IO;
using YJTunnel.Client;
using YJTunnel.Protocol;

namespace YJTunnel.Client.Services;

/// <summary>
/// 隧道生命周期管理：持有当前 TunnelClient 实例，应用配置/启停/状态聚合。
/// TunnelClient 不可重入（Dispose 后不可复用），因此"应用新配置"= 停旧实例 + 建新实例。
/// 所有事件从后台线程触发，UI 层自行派发到 Dispatcher。
/// </summary>
public sealed class TunnelService : IDisposable
{
    public static TunnelService Instance { get; } = new();

    private TunnelClient? _client;
    private AppConfig _config = new();
    private readonly object _gate = new();

    /// <summary>状态快照变化（后台线程）。</summary>
    public event Action<TunnelStatus>? StatusChanged;

    /// <summary>登录成功、服务端回填分配结果（后台线程）——远端端口可能被服务端调整，需回写配置。</summary>
    public event Action<LoginRespMessage>? LoginSucceeded;

    public TunnelStatus Status { get; private set; } = new() { State = TunnelState.Idle };

    public bool IsRunning
    {
        get { lock (_gate) return _client is not null; }
    }

    /// <summary>当前生效的配置（供诊断复制）。</summary>
    public AppConfig Config => _config;

    /// <summary>应用配置并（重新）启动隧道。配置落盘 + 实例重建 + Start。</summary>
    public void ApplyAndStart(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            StopCore();
            _config = config;
            AppConfigStore.Save(config);
            var client = new TunnelClient(config.Tunnel, clientId: Environment.MachineName, appVersion: VersionText.Value);
            client.StatusChanged += OnStatusChanged;
            client.LoginSucceeded += OnLoginSucceeded;
            client.Start();
            _client = client;
        }
    }

    /// <summary>停止隧道（保留配置）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            Status = new TunnelStatus { State = TunnelState.Stopped };
            StatusChanged?.Invoke(Status);
        }
    }

    private void StopCore()
    {
        if (_client is null) return;
        var client = _client;
        _client = null;
        client.StatusChanged -= OnStatusChanged;
        client.LoginSucceeded -= OnLoginSucceeded;
        try { client.StopAsync().GetAwaiter().GetResult(); } catch { /* 退出路径尽最大努力 */ }
        client.Dispose();
    }

    private void OnStatusChanged(TunnelStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void OnLoginSucceeded(LoginRespMessage resp) => LoginSucceeded?.Invoke(resp);

    public void Dispose()
    {
        lock (_gate) StopCore();
    }
}

/// <summary>程序集版本（登录上报与关于页展示）。</summary>
public static class VersionText
{
    public static string Value { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>诊断信息（托盘菜单/关于页一键复制）。</summary>
    public static string BuildDiagnostics(TunnelService service)
    {
        var s = service.Status;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("YJTunnel 客户端诊断信息");
        sb.AppendLine("----------------------");
        sb.AppendLine($"版本:       {Value}");
        sb.AppendLine($"主机名:     {Environment.MachineName}");
        sb.AppendLine($"系统:       {Environment.OSVersion.VersionString}");
        sb.AppendLine($"状态:       {s.State}");
        sb.AppendLine($"当前服务端: {(string.IsNullOrEmpty(s.ActiveServer) ? "-" : s.ActiveServer)}");
        sb.AppendLine($"重连次数:   {s.ReconnectCount}");
        if (s.PingLatency is { } latency) sb.AppendLine($"延迟:       {latency.TotalMilliseconds:F0} ms");
        if (s.ConnectedSince is { } since) sb.AppendLine($"在线始于:   {since:yyyy-MM-dd HH:mm:ss}");
        if (s.LastPongAt is { } pong) sb.AppendLine($"最后心跳:   {pong:yyyy-MM-dd HH:mm:ss}");
        if (s.LastError is { } err) sb.AppendLine($"最近错误:   {err.Message}");
        sb.AppendLine("映射:");
        foreach (var m in s.Mappings)
            sb.AppendLine($"  {m.LocalPort} -> {m.RemotePort}  [{m.Status}] {m.Msg}");
        sb.AppendLine($"诊断时间:   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return sb.ToString();
    }
}
