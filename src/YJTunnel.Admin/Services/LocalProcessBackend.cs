using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using YJTunnel.Server;

namespace YJTunnel.Admin.Services;

/// <summary>
/// 本机一体机模式（v1.23）：启动随包附带的 YJTunnelServer.exe 子进程（隐藏窗口），
/// 随后控制台通过本地 HTTP 连接它——隧道引擎与 **Web 管理台** 均由该进程提供，
/// 关闭控制台时默认优雅停止服务端进程（/api/shutdown，仅回环+认证）。
/// </summary>
public sealed class LocalProcessBackend : IAdminBackend
{
    private readonly AdminApiClient _client;
    private Process? _process;
    private StreamWriter? _logWriter;

    public string ConfigPath { get; }
    public int ControlPort { get; }
    public int AdminPort { get; }
    public string Token { get; }
    public string WebUrl => $"http://127.0.0.1:{AdminPort}/";

    public string EndpointText => $"本机 :{ControlPort}";
    public bool IsLocal => true;
    public bool IsConnected => _client.IsConnected;
    public int LatencyMs => _client.LatencyMs;

    private LocalProcessBackend(AdminApiClient client, Process process, StreamWriter logWriter,
        TunnelServerSection options, string configPath)
    {
        _client = client;
        _process = process;
        _logWriter = logWriter;
        ConfigPath = configPath;
        ControlPort = options.ControlPort;
        AdminPort = options.Admin.Port;
        Token = options.Tokens.FirstOrDefault() ?? "";
    }

    /// <summary>启动一体机：配置自愈 → 拉起服务端进程 → 等待管理台就绪 → 登录。</summary>
    public static async Task<(LocalProcessBackend? backend, string? error)> StartAsync(
        string configPath, string? serverExeHint)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return (null, "请填写配置文件路径");

            if (!TunnelServerHostConfig.EnsureConfigFile(configPath))
                return (null, "无法创建配置文件，请检查路径是否可写");

            TunnelServerHostConfig.ReplaceCredentialPlaceholders(configPath);
            var config = TunnelServerHostConfig.Load(configPath);
            var section = config.TunnelServer;

            if (!section.Admin.Enabled || string.IsNullOrWhiteSpace(section.Admin.Password))
            {
                return (null,
                    "一体机模式需要 Web 管理台：请在配置文件中设置 Admin.Enabled=true 并填写 Admin.Password" +
                    "（一体机由服务端进程同时提供隧道与管理网页）");
            }

            var exe = ResolveServerExe(serverExeHint);
            if (exe is null)
            {
                return (null,
                    "未找到 YJTunnel.Server.exe。请将其放在控制台目录的 server\\ 子目录，或修改 admin.json 的 LocalServerExe 指向它");
            }

            var logPath = Path.Combine(Path.GetTempPath(), "yjtunnel-admin-server.log");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(Path.GetFullPath(configPath));
            psi.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");
            psi.ArgumentList.Add("--local-only");

            var process = Process.Start(psi);
            if (process is null)
                return (null, "服务端进程启动失败");

            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logWriter) logWriter.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logWriter) logWriter.WriteLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var port = section.Admin.Port;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    var tail = ReadTail(logPath);
                    logWriter.Dispose();
                    return (null,
                        $"服务端进程已退出（代码 {process.ExitCode}）。{tail}\n" +
                        "常见原因：控制端口被已安装的服务占用（先 sc stop YJTunnel），或配置端口无效。");
                }

                if (await CanConnectAsync(port, 400))
                {
                    var client = new AdminApiClient("127.0.0.1", port);
                    var (ok, error) = await client.LoginAsync(section.Admin.Password, section.Admin.Username);
                    if (!ok)
                    {
                        await client.DisposeAsync();
                        try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                        logWriter.Dispose();
                        return (null, $"服务端已启动但管理台登录失败：{error}");
                    }

                    return (new LocalProcessBackend(client, process, logWriter, section, configPath), null);
                }

                await Task.Delay(250);
            }

            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            var timeoutTail = ReadTail(logPath);
            logWriter.Dispose();
            return (null, $"启动超时：管理台端口 {port} 未就绪。{timeoutTail}");
        }
        catch (Exception ex)
        {
            return (null, $"启动失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ IAdminBackend 透传

    public Task<OverviewDto?> GetOverviewAsync(CancellationToken ct = default) => _client.GetOverviewAsync(ct);
    public Task<CustomersResponse?> GetCustomersAsync(CancellationToken ct = default) => _client.GetCustomersAsync(ct);
    public Task<List<PendingDto>?> GetPendingAsync(CancellationToken ct = default) => _client.GetPendingAsync(ct);
    public Task<List<EventDto>?> GetEventsAsync(int limit = 100, CancellationToken ct = default) => _client.GetEventsAsync(limit, ct);
    public Task<ApiResultDto> ChangePortAsync(string clientId, string mappingKey, int newPort, CancellationToken ct = default)
        => _client.ChangePortAsync(clientId, mappingKey, newPort, ct);
    public Task<ApiResultDto> KickAsync(string clientId, string mode, int? minutes = null, CancellationToken ct = default)
        => _client.KickAsync(clientId, mode, minutes, ct);
    public Task<ApiResultDto> SetStatusAsync(string clientId, bool enabled, CancellationToken ct = default)
        => _client.SetStatusAsync(clientId, enabled, ct);
    public Task<ApiResultDto> PendingActionAsync(string clientId, bool approve, CancellationToken ct = default)
        => _client.PendingActionAsync(clientId, approve, ct);

    public async ValueTask DisposeAsync()
    {
        try { await _client.ShutdownAsync(); } catch { /* 忽略 */ }

        var proc = _process;
        _process = null;
        if (proc is not null)
        {
            try
            {
                if (!proc.WaitForExit(5000))
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* 忽略 */ }
            proc.Dispose();
        }

        await _client.DisposeAsync();
        _logWriter?.Dispose();
        _logWriter = null;
    }

    // ------------------------------------------------------------------ 工具

    private static string? ResolveServerExe(string? hint)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(hint))
            candidates.Add(Path.IsPathRooted(hint) ? hint : Path.Combine(AppContext.BaseDirectory, hint));

        foreach (var name in new[] { "YJTunnel.Server.exe", "YJTunnelServer.exe" })
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "server", name));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<bool> CanConnectAsync(int port, int timeoutMs)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path);
            return text.Length <= 500 ? text : "…" + text[^500..];
        }
        catch
        {
            return "";
        }
    }
}
