namespace YJTunnel.Admin.Services;

/// <summary>
/// 管理后端抽象：同一套 UI 支持两种运行形态——
/// 远程（HTTP /api 连已运行的服务端）与 本机一体机（内嵌隧道引擎，进程内直调 AdminService）。
/// </summary>
public interface IAdminBackend : IAsyncDisposable
{
    /// <summary>展示用的端点文本（远程 host:port / 本机 :5003）。</summary>
    string EndpointText { get; }

    /// <summary>true = 本机一体机模式（内置隧道引擎）。</summary>
    bool IsLocal { get; }

    bool IsConnected { get; }

    /// <summary>最近一次请求耗时（远程有意义；本机 0）。</summary>
    int LatencyMs { get; }

    Task<OverviewDto?> GetOverviewAsync(CancellationToken ct = default);
    Task<CustomersResponse?> GetCustomersAsync(CancellationToken ct = default);
    Task<List<PendingDto>?> GetPendingAsync(CancellationToken ct = default);
    Task<List<EventDto>?> GetEventsAsync(int limit = 100, CancellationToken ct = default);
    Task<ApiResultDto> ChangePortAsync(string clientId, string mappingKey, int newPort, CancellationToken ct = default);
    Task<ApiResultDto> KickAsync(string clientId, string mode, int? minutes = null, CancellationToken ct = default);
    Task<ApiResultDto> SetStatusAsync(string clientId, bool enabled, CancellationToken ct = default);
    Task<ApiResultDto> PendingActionAsync(string clientId, bool approve, CancellationToken ct = default);
}
