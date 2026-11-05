using System.Text;
using System.Text.Json;
using YJTunnel;
using YJTunnel.Client;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "YJTunnelClient");

// 隧道客户端单例：整个服务生命周期共用一条重连循环
var config = AppConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
var clientId = AppConfigLoader.EnsureClientId(Path.Combine(AppContext.BaseDirectory));
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ => new TunnelClient(config, clientId, appVersion: "sample-1.0.0"));
builder.Services.AddHostedService<TunnelClientHostedService>();

var host = builder.Build();
host.Run();

/// <summary>持有 TunnelClient 生命周期：服务启动即连，服务停止即断。</summary>
public sealed class TunnelClientHostedService(TunnelClient client) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Start();
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // TunnelClient.StopAsync() 无参：内部自行等待重连循环退出
        await client.StopAsync();
    }
}

public static class AppConfigLoader
{
    public static TunnelConfig Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<TunnelConfig>(File.ReadAllText(path, Encoding.UTF8), options)
            ?? throw new InvalidDataException($"配置解析失败：{path}");
    }

    public static string EnsureClientId(string dir)
    {
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
}
