using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace YJTunnel.Admin.Services;

/// <summary>会话失效（401）：UI 层据此回到登录界面。</summary>
public sealed class AdminAuthException : Exception
{
    public AdminAuthException() : base("登录已失效，请重新连接") { }
}

/// <summary>
/// 服务端 /api 客户端：Cookie 会话 + JSON 源生成序列化（AOT 兼容）。
/// 一个实例对应一个服务器地址；本控制台为单服务器模式。
/// </summary>
public sealed class AdminApiClient : IAdminBackend
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();

    public Uri BaseUri { get; }
    public string EndpointText => $"{BaseUri.Host}:{BaseUri.Port}";
    public bool IsLocal => false;
    public bool IsConnected => IsAuthenticated;
    public int LatencyMs => LastLatencyMs;
    public bool IsAuthenticated { get; private set; }
    public string? User { get; private set; }

    /// <summary>最近一次请求耗时（状态栏延迟显示）。</summary>
    public int LastLatencyMs { get; private set; }

    public AdminApiClient(string host, int port)
    {
        host = host.Trim().TrimEnd('/');
        BaseUri = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(host.TrimEnd('/') + "/")
            : new Uri($"http://{host}:{port}/");

        _http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
    }

    // ------------------------------------------------------------------ 认证

    public async Task<(bool ok, string? error)> LoginAsync(string password, string? username = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var resp = await _http.PostAsync(new Uri(BaseUri, "api/login"),
            JsonContent(new LoginRequestDto { Username = username, Password = password }, AdminJson.Default.LoginRequestDto), ct);
        LastLatencyMs = (int)sw.ElapsedMilliseconds;

        var body = await resp.Content.ReadAsStringAsync(ct);
        LoginResponse? result = null;
        try { result = JsonSerializer.Deserialize(body, AdminJson.Default.LoginResponse); } catch { }

        if (resp.IsSuccessStatusCode && result?.Ok == true)
        {
            IsAuthenticated = true;
            User = result.User;
            return (true, null);
        }

        IsAuthenticated = false;
        return (false, result?.Error ?? $"连接失败（HTTP {(int)resp.StatusCode}）");
    }

    /// <summary>探测 Cookie 是否仍有效（静默刷新失败时用）。</summary>
    public async Task<bool> CheckSessionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(BaseUri, "api/session"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var s = JsonSerializer.Deserialize(body, AdminJson.Default.SessionResponse);
            IsAuthenticated = s?.Authenticated == true;
            User = s?.User;
            return IsAuthenticated;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 只读

    public Task<ServerInfoDto?> GetInfoAsync(CancellationToken ct = default)
        => GetJsonAsync("api/info", AdminJson.Default.ServerInfoDto, ct);

    public async Task<OverviewDto?> GetOverviewAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await GetJsonAsync("api/overview", AdminJson.Default.OverviewDto, ct);
        LastLatencyMs = (int)sw.ElapsedMilliseconds;
        return result;
    }

    public Task<CustomersResponse?> GetCustomersAsync(CancellationToken ct = default)
        => GetJsonAsync("api/customers", AdminJson.Default.CustomersResponse, ct);

    public Task<List<PendingDto>?> GetPendingAsync(CancellationToken ct = default)
        => GetJsonAsync("api/pending", AdminJson.Default.ListPendingDto, ct);

    public Task<List<EventDto>?> GetEventsAsync(int limit = 100, CancellationToken ct = default)
        => GetJsonAsync($"api/events?limit={limit}", AdminJson.Default.ListEventDto, ct);

    // ------------------------------------------------------------------ 写操作

    public Task<ApiResultDto> ChangePortAsync(string clientId, string mappingKey, int newPort, CancellationToken ct = default)
        => PostResultAsync($"api/customers/{Uri.EscapeDataString(clientId)}/port",
            new PortChangeRequestDto { MappingKey = mappingKey, NewPort = newPort },
            AdminJson.Default.PortChangeRequestDto, ct);

    /// <summary>mode = kick | temp | permanent。</summary>
    public Task<ApiResultDto> KickAsync(string clientId, string mode, int? minutes = null, CancellationToken ct = default)
        => PostResultAsync($"api/customers/{Uri.EscapeDataString(clientId)}/kick",
            new KickRequestDto { Mode = mode, Minutes = minutes },
            AdminJson.Default.KickRequestDto, ct);

    public Task<ApiResultDto> SetStatusAsync(string clientId, bool enabled, CancellationToken ct = default)
        => PostResultAsync($"api/customers/{Uri.EscapeDataString(clientId)}/status",
            new StatusRequestDto { Enabled = enabled },
            AdminJson.Default.StatusRequestDto, ct);

    public Task<ApiResultDto> PendingActionAsync(string clientId, bool approve, CancellationToken ct = default)
        => PostResultAsync($"api/pending/{Uri.EscapeDataString(clientId)}",
            new PendingActionRequestDto { Approve = approve },
            AdminJson.Default.PendingActionRequestDto, ct);

    /// <summary>请求服务端优雅停机（仅回环 + 已认证；一体机退出时用）。</summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsync(new Uri(BaseUri, "api/shutdown"), new StringContent(""), ct);
        }
        catch { /* 服务端可能已退出 */ }
    }

    // ------------------------------------------------------------------ 内部

    private async Task<T?> GetJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(BaseUri, path), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            IsAuthenticated = false;
            throw new AdminAuthException();
        }
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, typeInfo);
    }

    private async Task<ApiResultDto> PostResultAsync<T>(string path, T payload, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(new Uri(BaseUri, path), JsonContent(payload, typeInfo), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            IsAuthenticated = false;
            throw new AdminAuthException();
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        ApiResultDto? result = null;
        try { result = JsonSerializer.Deserialize(body, AdminJson.Default.ApiResultDto); } catch { }

        if (result != null && result.Ok) return result;
        return new ApiResultDto
        {
            Ok = false,
            Error = result?.Error ?? $"请求失败（HTTP {(int)resp.StatusCode}）",
        };
    }

    private static StringContent JsonContent<T>(T payload, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(payload, typeInfo), Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
