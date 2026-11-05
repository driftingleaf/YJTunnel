// YJTunnel 最小客户端示例：
// 读取同目录 appsettings.json → 启动隧道客户端 → 打印连接状态与端口分配 → Ctrl+C 优雅退出。
//
// 运行：dotnet run（先按需修改 appsettings.json 的服务器地址/Token/转发列表）
using System.Text.Json;
using YJTunnel;
using YJTunnel.Client;
using YJTunnel.Protocol;

var baseDir = AppContext.BaseDirectory;
var config = LoadConfig(Path.Combine(baseDir, "appsettings.json"));

// ClientId：客户端在服务端的唯一身份；留空则首次生成并持久化到 clientid.txt
config.ClientId = EnsureClientId(baseDir, config.ClientId);

Console.WriteLine($"YJTunnel 客户端示例");
Console.WriteLine($"  服务器：{config.Server.ServerAddr}:{config.Server.ServerPort}");
Console.WriteLine($"  ClientId：{config.ClientId}");
Console.WriteLine($"  转发：{string.Join(", ", config.Forwards.Select(f => $"本地 {f.LocalIp}:{f.LocalPort} -> 远端 {(f.RemotePort > 0 ? f.RemotePort.ToString() : "自动分配")}"))}");
Console.WriteLine();

using var client = new TunnelClient(config, config.ClientId, appVersion: "sample-1.0.0");

client.StatusChanged += status =>
{
    var ping = status.PingLatency is { } pl ? $"  延迟 {pl.TotalMilliseconds:0}ms" : "";
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status.State}" +
        (status.LastError != null ? $"（{status.LastError.Message}）" : "") + ping);

    foreach (var m in status.Mappings)
        Console.WriteLine($"           {m.Key}: {m.RemotePort} [{m.Status}]");
};

client.LoginSucceeded += resp =>
{
    Console.WriteLine("登录成功，端口分配：");
    foreach (var m in resp.Mappings)
        Console.WriteLine($"  {m.Key} -> 公网端口 {m.RemotePort}（{m.Status}）");
};

// Ctrl+C：优雅停止（停止重连循环并断开会话）
using var exit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };

client.Start();
Console.WriteLine("运行中，按 Ctrl+C 退出…");
exit.Wait();

await client.StopAsync();
Console.WriteLine("已退出。");

// ------------------------------------------------------------------

static TunnelConfig LoadConfig(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"[错误] 未找到配置文件 {path}");
        Environment.Exit(1);
    }

    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    return JsonSerializer.Deserialize<TunnelConfig>(File.ReadAllText(path), options)
        ?? throw new InvalidDataException($"配置解析失败：{path}");
}

static string EnsureClientId(string dir, string? configured)
{
    // 显式配置优先；否则同一台机器重复运行保持同一身份（服务端按 clientId 记忆端口分配与公司名）
    if (!string.IsNullOrWhiteSpace(configured)) return configured;

    var file = Path.Combine(dir, "clientid.txt");
    if (File.Exists(file))
    {
        var id = File.ReadAllText(file).Trim();
        if (id.Length > 0) return id;
    }
    var generated = Guid.NewGuid().ToString("N");
    File.WriteAllText(file, generated);
    return generated;
}
