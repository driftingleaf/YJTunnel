using System.Collections.ObjectModel;
using System.ComponentModel;
using YJTunnel.Client.Services;
using YJTunnel.Protocol;

namespace YJTunnel.Client.ViewModels;

/// <summary>
/// 主窗口视图模型：隧道状态 + 映射列表 + 配置编辑。
/// StatusChanged 由后台线程触发，所有可绑定属性必须经 <see cref="SyncStatus"/> 在 UI 线程写入。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly TunnelService _service = TunnelService.Instance;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? ToastRequested;   // UI 层订阅：显示 Toast（UI 线程）
    public event Action? TrayStateRefreshRequested;

    public ObservableCollection<MappingItemViewModel> Mappings { get; } = new();

    public AppConfig Config { get; private set; } = AppConfigStore.Load();

    // ---- 状态横幅 ----

    private string _stateText = "未启动";
    public string StateText { get => _stateText; private set { _stateText = value; Raise(); } }

    private string _stateDetail = "";
    public string StateDetail { get => _stateDetail; private set { _stateDetail = value; Raise(); } }

    private string _stateTone = "neutral";   // neutral/info/success/warning/danger
    public string StateTone { get => _stateTone; private set { _stateTone = value; Raise(); } }

    private string _serverText = "-";
    public string ServerText { get => _serverText; private set { _serverText = value; Raise(); } }

    private string _companyText = "";
    public string CompanyText { get => _companyText; private set { _companyText = value; Raise(); } }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { _isRunning = value; Raise(); Raise(nameof(CanToggle)); } }

    /// <summary>关闭窗口时最小化到托盘（false=直接退出）。</summary>
    public bool MinimizeOnClose => Config.MinimizeToTray;

    public bool CanToggle => !string.IsNullOrWhiteSpace(Config.ServerAddr)
                          && !string.IsNullOrWhiteSpace(Config.Token)
                          && Config.Forwards.Any(f => f.Enabled && f.LocalPort > 0 && f.RemotePort > 0)
                          || _isRunning;   // 已运行时允许"停止"

    private string _startStopText = "启动";
    public string StartStopText { get => _startStopText; private set { _startStopText = value; Raise(); } }

    /// <summary>配置有未应用修改（提示用户"保存并应用"）。</summary>
    public void MarkDirty() => Raise(nameof(CanToggle));

    public MainViewModel()
    {
        _service.StatusChanged += s => Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncStatus(s));
        _service.LoginSucceeded += resp => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnLoginSucceeded(resp));
        LoadMappingsFromConfig();
    }

    /// <summary>窗口打开时调用：同步当前状态（错过后补齐）。</summary>
    public void Attach() => SyncStatus(_service.Status);

    // ---- 状态同步 ----

    private void SyncStatus(TunnelStatus s)
    {
        (StateTone, StateText, StateDetail, StateWhen) = s.State switch
        {
            TunnelState.Idle => ("neutral", "未启动", "配置完成后点击「启动」接入隧道", ""),
            TunnelState.Connecting => ("info", "连接中…", $"正在连接 {s.ActiveServer}", "请稍候"),
            TunnelState.Connected => ("success", "在线", $"{s.ActiveServer} · {Config.CompanyName} · 会话已加密",
                s.ConnectedSince is { } cs ? $"已连接 {FormatDuration(DateTimeOffset.Now - cs)}" : ""),
            TunnelState.Reconnecting => ("warning", "断线重连中", $"连接意外断开 · {s.ActiveServer}", $"第 {s.ReconnectCount + 1} 次重连"),
            TunnelState.Failed => ("danger", "连接被拒绝", s.LastError?.Message ?? "请检查令牌与配置", ""),
            TunnelState.PendingApproval => ("warning", "等待管理员审核", "服务端已收到接入请求，等待审核通过", ""),
            TunnelState.Banned => ("danger", "已被封禁", s.LastError?.Message ?? "封禁期内暂停接入，到期自动恢复", ""),
            TunnelState.Stopped => ("neutral", "已停止", "", ""),
            _ => ("neutral", s.State.ToString(), "", ""),
        };
        ServerText = string.IsNullOrEmpty(s.ActiveServer) ? "-" : s.ActiveServer;
        IsRunning = s.State is TunnelState.Connected or TunnelState.Connecting
                        or TunnelState.Reconnecting or TunnelState.PendingApproval or TunnelState.Banned;
        StartStopText = IsRunning ? "停 止" : "启 动";

        // 指标卡数据源
        UptimeMinutes = s.ConnectedSince is { } since ? (int)((DateTimeOffset.Now - since).TotalMinutes) : 0;
        LatencyMs = s.PingLatency is { } latency ? (int)Math.Round(latency.TotalMilliseconds) : 0;
        OpenPorts = s.State == TunnelState.Connected ? s.Mappings.Count(m => m.Status == 0) : 0;

        // 行的公网地址前缀：直接用配置的服务端地址（域名），不依赖在线状态
        var cfgHost = string.IsNullOrWhiteSpace(Config.ServerAddr)
            ? (ServerText == "-" ? null : ServerText.Split(':')[0])
            : Config.ServerAddr.Trim();
        foreach (var vm in Mappings) { vm.PublicHost = cfgHost; vm.RaisePublicHost(); }

        SyncMappingStatuses(s);
        TrayStateRefreshRequested?.Invoke();
    }

    /// <summary>指标卡：在线分钟数 / 延迟 ms / 已开放端口数。</summary>
    public int UptimeMinutes { get; private set; }
    public int LatencyMs { get; private set; }
    public int OpenPorts { get; private set; }

    /// <summary>有效映射行数（排除占位行）。</summary>
    public int MappingCount => Mappings.Count(m => m.LocalPort > 0 || m.RemotePort > 0);

    /// <summary>Hero 标题旁的时长/倒计时胶囊文本（在线=已连接时长）。</summary>
    public string StateWhen { get; private set; } = "";

    private static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m {d.Seconds}s";

    private void SyncMappingStatuses(TunnelStatus s)
    {
        var cfgHost = string.IsNullOrWhiteSpace(Config.ServerAddr)
            ? (ServerText == "-" ? null : ServerText.Split(':')[0])
            : Config.ServerAddr.Trim();
        foreach (var vm in Mappings)
        {
            vm.PublicHost = cfgHost;
            vm.RaisePublicHost();
            var m = s.Mappings.FirstOrDefault(x => x.Key == vm.Key);
            vm.ApplyServerStatus(m?.Status, m?.Msg);
        }
        Raise(nameof(MappingCount));
    }

    private void OnLoginSucceeded(LoginRespMessage resp)
    {
        // 服务端可能调整了远端端口（AutoRegister 场景），回写配置持久化
        var changed = false;
        foreach (var m in resp.Mappings)
        {
            var vm = Mappings.FirstOrDefault(x => x.Key == m.Key);
            if (vm is not null && vm.RemotePort != m.RemotePort)
            {
                vm.RemotePort = m.RemotePort;
                var item = Config.Forwards.FirstOrDefault(f => f.LocalPort == vm.LocalPort && f.RemotePort != m.RemotePort);
                if (item is not null) { item.RemotePort = m.RemotePort; changed = true; }
            }
        }
        if (changed)
        {
            AppConfigStore.Save(Config);
            Log.Info("服务端回填远端端口已回写配置");
        }
    }

    // ---- 映射列表 ----

    private void LoadMappingsFromConfig()
    {
        Mappings.Clear();
        foreach (var f in Config.Forwards)
            Mappings.Add(new MappingItemViewModel(f, NotifyMappingChanged));
        if (Mappings.Count == 0)
            Mappings.Add(MappingItemViewModel.CreatePlaceholder(NotifyMappingChanged));
        Raise(nameof(MappingCount));
    }

    private void NotifyMappingChanged()
    {
        // 占位行（端口均 0）不计入有效性
        MarkDirty();
        TrayStateRefreshRequested?.Invoke();
    }

    public void ApplyMappingsToConfig()
    {
        Config.Forwards = Mappings
            .Where(m => m.LocalPort > 0 || m.RemotePort > 0)   // 丢弃空占位行
            .Select(m => new MappingItem
            {
                Enabled = m.Enabled,
                LocalIp = m.LocalIp,
                LocalPort = m.LocalPort,
                RemotePort = m.RemotePort,
                Note = m.Note,
            })
            .ToList();
        if (Config.Forwards.Count == 0)
            Config.Forwards.Add(new MappingItem());   // 保留一行占位，避免表格全空
        LoadMappingsFromConfig();
    }

    // ---- 映射行增删 ----

    public void AddMappingRow()
    {
        Mappings.Add(new MappingItemViewModel(new MappingItem(), NotifyMappingChanged));
    }

    public void RemoveMappingRow(MappingItemViewModel row)
    {
        Mappings.Remove(row);
        NotifyMappingChanged();
    }

    /// <summary>公网地址（在线时）：每条映射一行 server:remotePort（本地 127.0.0.1 直连意图明确，不在此列）。</summary>
    public string BuildPublicAddresses()
    {
        var status = _service.Status;
        if (status.State != TunnelState.Connected || string.IsNullOrEmpty(status.ActiveServer)) return "";
        var server = status.ActiveServer.Split(':')[0];   // ActiveServer 可能是 addr:port（控制端口），公网地址不带它
        var lines = Mappings
            .Where(m => m.Enabled && m.RemotePort > 0)
            .Select(m => $"{server}:{m.RemotePort}");
        var list = lines.ToList();
        return list.Count == 0 ? "" : string.Join(Environment.NewLine, list);
    }

    // ---- 启停 ----

    public string StartStopValidation()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Config.ServerAddr)) errors.Add("服务端地址未填写");
        if (string.IsNullOrWhiteSpace(Config.Token)) errors.Add("接入令牌未填写");
        var valid = Config.Forwards.Count(f => f.Enabled && f.LocalPort > 0 && f.RemotePort > 0);
        if (valid == 0) errors.Add("没有启用的端口映射（需填写本地端口与公网端口）");
        return errors.Count == 0 ? "" : string.Join("；", errors);
    }

    public void StartTunnel()
    {
        ApplyMappingsToConfig();
        var err = StartStopValidation();
        if (err != "")
        {
            ToastRequested?.Invoke(err);
            return;
        }
        try
        {
            _service.ApplyAndStart(Config);
            Log.Info("隧道启动");
        }
        catch (Exception ex)
        {
            Log.Error("隧道启动失败", ex);
            ToastRequested?.Invoke($"启动失败：{ex.Message}");
        }
    }

    public void StopTunnel()
    {
        try
        {
            _service.Stop();
            Log.Info("隧道停止");
        }
        catch (Exception ex)
        {
            Log.Error("隧道停止失败", ex);
        }
    }

    // ---- 配置 ----

    public void SaveConfig(AppConfig updated, bool restartIfRunning)
    {
        Config = updated;
        AppConfigStore.Save(Config);
        CompanyText = updated.CompanyName;
        LoadMappingsFromConfig();
        if (restartIfRunning || _service.IsRunning)
            StartTunnel();
        ToastRequested?.Invoke("配置已保存");
    }

    public string BuildDiagnostics() => VersionText.BuildDiagnostics(_service);

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>映射行视图模型（可编辑单元格 + 服务端回填状态）。</summary>
public sealed class MappingItemViewModel : INotifyPropertyChanged
{
    private bool _enabled;
    private string _localIp = "127.0.0.1";
    private int _localPort;
    private int _remotePort;
    private string _note = "";
    private string _statusText = "";
    private string _statusTone = "neutral";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>服务端分配的映射键（login resp 后有效；key=localPort 字符串约定）。</summary>
    public string Key => LocalPort > 0 ? LocalPort.ToString() : "";

    public Action? Changed { get; set; }

    public MappingItemViewModel(MappingItem item, Action? changed = null)
    {
        _enabled = item.Enabled;
        _localIp = item.LocalIp;
        _localPort = item.LocalPort;
        _remotePort = item.RemotePort;
        _note = item.Note;
        Changed = changed;
    }

    public static MappingItemViewModel CreatePlaceholder(Action? changed = null)
        => new(new MappingItem(), changed);

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); Raise(nameof(DisplayName)); Changed?.Invoke(); } }
    public string LocalIp { get => _localIp; set { _localIp = value; Raise(nameof(LocalIp)); Raise(nameof(LocalTarget)); Raise(nameof(PublicAddress)); Raise(nameof(PublicHostText)); Changed?.Invoke(); } }
    public int LocalPort { get => _localPort; set { _localPort = value; RaiseAll(); } }
    public int RemotePort { get => _remotePort; set { _remotePort = value; RaiseAll(); } }
    public string Note { get => _note; set { _note = value; Raise(nameof(Note)); Raise(nameof(DisplayName)); Changed?.Invoke(); } }

    /// <summary>卡片主标题：备注优先，否则"本地服务 {LocalPort}"；未启用加前缀。</summary>
    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Note) ? $"本地服务 {(LocalPort > 0 ? LocalPort : "…")}" : Note;
            return Enabled ? name : $"{name}（已停用）";
        }
    }

    /// <summary>卡片副标题：本地指向。</summary>
    public string LocalTarget => $"{LocalIp}:{(LocalPort > 0 ? LocalPort : "?")}";

    /// <summary>公网访问地址（在线时有意义；未配置时提示待填）。</summary>
    public string PublicAddress => RemotePort > 0 ? $"{PublicHost}:{RemotePort}" : "公网端口待填";

    /// <summary>由 VM 统一设置的当前服务端主机（ActiveServer 去端口）。</summary>
    public string? PublicHost { get; set; }

    /// <summary>流向胶囊 PUB 格显示的域名（未配置时显示 …）。</summary>
    public string PublicHostText => string.IsNullOrEmpty(PublicHost) ? "…" : PublicHost;

    /// <summary>PublicHost 由 VM 批量赋值后调用（属性 setter 不可用于派生值通知）。</summary>
    public void RaisePublicHost() => Raise(nameof(PublicHostText));

    /// <summary>NumberField.ValueText 是 string 绑定；空串=0。</summary>
    public string LocalPortText
    {
        get => _localPort > 0 ? _localPort.ToString() : "";
        set { _ = int.TryParse(value?.Trim(), out var v); LocalPort = Math.Clamp(v, 0, 65535); }
    }

    public string RemotePortText
    {
        get => _remotePort > 0 ? _remotePort.ToString() : "";
        set { _ = int.TryParse(value?.Trim(), out var v); RemotePort = Math.Clamp(v, 0, 65535); }
    }

    /// <summary>自动生成说明（备注 + 端口语义）。</summary>
    public string Description
    {
        get
        {
            if (LocalPort <= 0 || RemotePort <= 0) return "";
            var label = string.IsNullOrWhiteSpace(Note) ? "本地服务" : Note;
            return $"{label}：访问公网 {RemotePort} → 本机 {LocalIp}:{LocalPort}";
        }
    }

    /// <summary>本行状态描述（未在线时显示"未连接"；在线时显示服务端回执）。</summary>
    public string StatusText { get => _statusText; private set { _statusText = value; Raise(); } }
    public string StatusTone { get => _statusTone; private set { _statusTone = value; Raise(); Raise(nameof(StatusBrush)); } }

    /// <summary>状态文字颜色（行内不用 Badge，直接给文字着色更轻）。</summary>
    public Avalonia.Media.IBrush StatusBrush => StatusTone switch
    {
        "success" => StatusBrushes.Success,
        "danger" => StatusBrushes.Danger,
        "warning" => StatusBrushes.Warning,
        _ => StatusBrushes.Muted,
    };

    private static class StatusBrushes
    {
        public static readonly Avalonia.Media.IBrush Success = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E9E57"));
        public static readonly Avalonia.Media.IBrush Danger = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D6484C"));
        public static readonly Avalonia.Media.IBrush Warning = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D9822B"));
        public static readonly Avalonia.Media.IBrush Muted = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#808890"));
    }

    /// <summary>TunnelStatus.Mappings 的 Key 是 localPort 字符串。</summary>
    public void ApplyServerStatus(int? status, string? msg)
    {
        if (LocalPort <= 0) { StatusText = ""; return; }
        if (status is null)
        {
            StatusText = "";   // 未登录/无回执
            return;
        }
        (StatusTone, StatusText) = status switch
        {
            0 => ("success", "已开放"),
            5 => ("danger", "端口被占用"),
            6 => ("danger", "端口被禁止"),
            8 => ("danger", "服务端错误"),
            _ => ("warning", msg is { Length: > 0 } ? msg : $"状态 {status}"),
        };
    }

    private void RaiseAll()
    {
        Raise(nameof(Enabled));
        Raise(nameof(LocalPort));
        Raise(nameof(LocalPortText));
        Raise(nameof(RemotePort));
        Raise(nameof(RemotePortText));
        Raise(nameof(Key));
        Raise(nameof(Description));
        Raise(nameof(DisplayName));
        Raise(nameof(LocalTarget));
        Raise(nameof(PublicAddress)); Raise(nameof(PublicHostText));
        Changed?.Invoke();
    }

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
