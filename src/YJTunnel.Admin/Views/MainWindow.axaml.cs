using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TKCode.Arc;
using YJTunnel.Admin.Services;
using YJTunnel.Admin.ViewModels;

namespace YJTunnel.Admin.Views;

public partial class MainWindow : Window
{
    private enum Page { Overview, Customers, Pending, Logs }

    private readonly AdminConfig _config = AdminConfigStore.Load();
    private IAdminBackend? _backend;
    private LocalProcessBackend? _local;
    private readonly DispatcherTimer _poll;
    private Page _page = Page.Overview;
    private bool _forceClose;
    private TrayIcon? _tray;
    private List<CustomerRow> _customerRows = new();
    private bool _refreshing;
    private string _loginMode = "remote";

    public MainWindow()
    {
        InitializeComponent();
        Title = "YJTunnel 管理控制台";

        LoginHost.Text = _config.ServerAddr;
        LoginPort.Text = _config.AdminPort.ToString();
        LocalConfigInput.Text = string.IsNullOrWhiteSpace(_config.LocalConfigPath)
            ? LocalConfigStore.DefaultPath
            : _config.LocalConfigPath;

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _poll.Tick += async (_, _) => await RefreshCoreAsync(silent: true);

        Closing += OnWindowClosing;
        Opened += async (_, _) =>
        {
            UpdateTrayIcon();
            if (Program.StartMinimized) Hide();

            // 上次使用一体机模式：启动即自动拉起隧道引擎
            if (_config.Mode == "local")
            {
                SetLoginMode("local");
                await StartLocalAsync(auto: true);
            }
            else
            {
                LoginPassword.Focus();
            }
        };
    }

    // ================================================================ 登录 / 模式

    private void SetLoginMode(string mode)
    {
        _loginMode = mode;
        var isLocal = mode == "local";
        RemoteLoginPanel.IsVisible = !isLocal;
        LocalLoginPanel.IsVisible = isLocal;
        ModeRemoteButton.Classes.Set("primary", !isLocal);
        ModeLocalButton.Classes.Set("primary", isLocal);
        LoginError.IsVisible = false;
        if (!isLocal) LoginPassword.Focus();
    }

    private void OnModeRemoteClick(object? sender, RoutedEventArgs e) => SetLoginMode("remote");

    private void OnOverlayCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_backend?.IsConnected != true) return;
        LoginOverlay.IsVisible = false;
        _poll.Start();
        SetStatus(true, _backend.IsLocal ? $"本机一体机 · 控制端口 {_local?.ControlPort}" : $"已连接 {_backend.EndpointText}");
        _ = RefreshCoreAsync(silent: true);
    }

    private void OnSwitchConnectionClick(object? sender, RoutedEventArgs e)
        => ShowLoginOverlay(null);
    private void OnModeLocalClick(object? sender, RoutedEventArgs e) => SetLoginMode("local");

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var host = LoginHost.Text?.Trim() ?? "";
        if (host.Length == 0)
        {
            ShowLoginError("请填写服务器地址");
            return;
        }
        if (!int.TryParse(LoginPort.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowLoginError("管理台端口无效");
            return;
        }
        if (string.IsNullOrEmpty(LoginPassword.Text))
        {
            ShowLoginError("请填写管理密码");
            return;
        }

        LoginButton.IsEnabled = false;
        LoginButton.Content = "连接中…";
        LoginError.IsVisible = false;
        try
        {
            var api = new AdminApiClient(host, port);
            var (ok, error) = await api.LoginAsync(LoginPassword.Text);
            if (!ok)
            {
                await api.DisposeAsync();
                ShowLoginError(error ?? "连接失败");
                return;
            }

            _config.Mode = "remote";
            _config.ServerAddr = host;
            _config.AdminPort = port;
            AdminConfigStore.Save(_config);

            await AttachBackendAsync(api);
        }
        catch (Exception ex)
        {
            ShowLoginError($"连接失败：{ex.Message}");
            Log.Error("登录失败", ex);
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "连 接";
        }
    }

    private async void OnBrowseLocalConfigClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 tunnelserver.json",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("YJTunnel 配置") { Patterns = new[] { "*.json" } },
            },
        });
        if (files.Count > 0)
            LocalConfigInput.Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
    }

    private async void OnLocalStartClick(object? sender, RoutedEventArgs e)
        => await StartLocalAsync(auto: false);

    private async Task StartLocalAsync(bool auto)
    {
        if (_backend?.IsLocal == true)
        {
            // 已在一体机模式运行：启动阶段的重复调用直接返回；手动再次启动给出明确提示
            if (auto) { LoginOverlay.IsVisible = false; return; }
            ShowLoginError("本机一体机正在运行；如需更换配置，请退出控制台后重新启动");
            return;
        }

        var path = LocalConfigInput.Text?.Trim() ?? "";
        if (path.Length == 0)
        {
            ShowLoginError("请填写配置文件路径");
            return;
        }

        LocalStartButton.IsEnabled = false;
        LocalStartButton.Content = "启动中…";
        LoginError.IsVisible = false;
        try
        {
            var (backend, error) = await LocalProcessBackend.StartAsync(path, _config.LocalServerExe);
            if (backend is null)
            {
                // 注意先切模式再显示错误：SetLoginMode 会清空错误提示
                if (auto) SetLoginMode("local");
                ShowLoginError(error ?? "启动失败");
                return;
            }

            _local = backend;
            _config.Mode = "local";
            _config.LocalConfigPath = path;
            AdminConfigStore.Save(_config);

            ShowLocalInfo(backend);
            await AttachBackendAsync(backend);
        }
        catch (Exception ex)
        {
            ShowLoginError($"启动失败：{ex.Message}");
            Log.Error("一体机启动失败", ex);
        }
        finally
        {
            LocalStartButton.IsEnabled = true;
            LocalStartButton.Content = "启动一体机";
        }
    }

    private async Task AttachBackendAsync(IAdminBackend backend)
    {
        if (_backend is not null) await _backend.DisposeAsync();
        _backend = backend;

        LoginOverlay.IsVisible = false;
        LoginPassword.Text = "";
        SetStatus(true, backend.IsLocal ? $"本机一体机 · 控制端口 {_local?.ControlPort}" : $"已连接 {backend.EndpointText}");
        _poll.Start();
        Log.Info($"管理后端已连接：{backend.EndpointText}（local={backend.IsLocal}）");
        await RefreshCoreAsync(silent: false);
    }

    private void ShowLoginError(string message)
    {
        LoginError.Text = message;
        LoginError.IsVisible = true;
    }

    private void ShowLoginOverlay(string? hint)
    {
        _poll.Stop();
        LoginOverlay.IsVisible = true;
        SetLoginMode(_config.Mode == "local" ? "local" : "remote");
        OverlayCancelButton.IsVisible = _backend?.IsConnected == true;

        if (_backend?.IsConnected == true)
        {
            // 切换连接期间当前后端仍在运行：状态保持“已连接”，避免误导
            SetStatus(true, _backend.IsLocal
                ? $"本机一体机 · 控制端口 {_local?.ControlPort}"
                : $"已连接 {_backend.EndpointText}");
        }
        else
        {
            SetStatus(false, hint ?? "未连接");
        }
        if (!string.IsNullOrEmpty(hint)) ShowLoginError(hint);
    }

    // ================================================================ 数据刷新

    private async Task RefreshCoreAsync(bool silent)
    {
        if (_backend is null || !_backend.IsConnected || _refreshing) return;
        _refreshing = true;

        try
        {
            var overview = await _backend.GetOverviewAsync();
            if (overview is null) return;
            ApplyOverview(overview);

            var customers = await _backend.GetCustomersAsync();
            if (customers is not null)
            {
                var onlineSet = customers.OnlineIds.ToHashSet();
                RenderCustomers(customers.Customers, onlineSet, overview);
            }

            var pending = await _backend.GetPendingAsync();
            if (pending is not null) RenderPending(pending);

            if (!silent)
            {
                if (_page == Page.Logs) await RefreshEventsAsync();
                OverviewUpdatedText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
            }

            SetStatus(true, _backend.IsLocal
                ? $"本机一体机 · 控制端口 {_local?.ControlPort}"
                : $"已连接 {_backend.EndpointText}");
        }
        catch (AdminAuthException)
        {
            ShowLoginOverlay("登录已失效，请重新连接");
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新失败：{ex.Message}");
            SetStatus(false, $"连接异常：{ex.Message}");
            if (!silent) ShowToast($"刷新失败：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyOverview(OverviewDto o)
    {
        MetricOnline.Text = o.OnlineCount.ToString();
        MetricPorts.Text = o.PortUsed.ToString();
        MetricPending.Text = o.PendingCount.ToString();
        MetricCustomers.Text = o.CustomerCount.ToString();
        SessionsHint.Text = o.OnlineCount > 0 ? $"{o.OnlineCount} 台客户端已接入" : "";

        NavPendingBadge.IsVisible = o.PendingCount > 0;
        NavPendingCount.Text = o.PendingCount.ToString();
        PendingCountBadge.IsVisible = o.PendingCount > 0;
        PendingCountText.Text = $"{o.PendingCount} 条待处理";

        var sessions = o.Sessions.Select(s => new SessionRow
        {
            Initial = FirstChar(s.Name),
            Name = string.IsNullOrWhiteSpace(s.Name) ? s.ClientId : s.Name,
            ClientId = s.ClientId,
            Machine = s.MachineName,
            Ip = s.RemoteIp,
            Version = s.AppVersion,
            Connections = s.Connections.ToString(),
            Uptime = FormatAgo(s.LoginAt),
        }).ToList();
        SessionsList.ItemsSource = sessions;
        SessionsEmpty.IsVisible = sessions.Count == 0;

        var failures = o.BindFailures.Select(f => $"{f.Name}（{f.ClientId}）：{f.Msg}").ToList();
        BindFailureSection.IsVisible = failures.Count > 0;
        BindFailureList.ItemsSource = failures;

        var local = _backend?.IsLocal == true;
        LocalInfoCard.IsVisible = local;
        ServerAddrText.Text = local
            ? $"本机 :{_local?.ControlPort}"
            : _backend?.EndpointText ?? "未连接";
        ServerSubText.Text = local
            ? $"一体机模式 · {o.OnlineCount} 台在线"
            : $"已连接 · {o.OnlineCount} 台在线";

        CustomersHint.Text = $"{o.CustomerCount} 位客户 · {o.PortUsed} 条映射 · 点击端口可修改";
    }

    private void RenderCustomers(List<CustomerDto> customers, HashSet<string> online, OverviewDto overview)
    {
        var portStatus = overview.Ports
            .GroupBy(p => p.ClientId + "|" + p.MappingKey)
            .ToDictionary(g => g.Key, g => g.First());

        _customerRows = customers.Select(c =>
        {
            var row = new CustomerRow
            {
                ClientId = c.ClientId,
                Name = string.IsNullOrWhiteSpace(c.Name) ? c.ClientId : c.Name,
                Initial = FirstChar(c.Name),
                Subtitle = $"{c.MachineName ?? "-"} · {c.ClientId}",
                Online = online.Contains(c.ClientId),
                Disabled = !c.Enabled,
                Banned = c.BannedUntil is { } until && until > DateTimeOffset.Now,
            };
            var (badgeText, badgeBg, badgeFg) = row.Disabled
                ? ("已禁用", Palette.RedSoft, Palette.Red)
                : row.Banned
                    ? ($"封禁至 {c.BannedUntil!.Value.ToLocalTime():MM-dd HH:mm}", Palette.RedSoft, Palette.Red)
                    : row.Online
                        ? ("在线", Palette.GreenSoft, Palette.Green)
                        : ("离线", Palette.Panel2, Palette.Ink3);
            row = new CustomerRow
            {
                ClientId = row.ClientId, Name = row.Name, Initial = row.Initial, Subtitle = row.Subtitle,
                Online = row.Online, Disabled = row.Disabled, Banned = row.Banned,
                BadgeText = badgeText, BadgeBg = badgeBg, BadgeFg = badgeFg,
            };
            foreach (var m in c.Mappings)
            {
                portStatus.TryGetValue(c.ClientId + "|" + m.Key, out var usage);
                var ok = usage?.BindStatus == "ok" && row.Online;
                var failed = usage?.BindStatus == "occupied";
                row.Ports.Add(new PortRow
                {
                    CustomerId = c.ClientId,
                    Key = m.Key,
                    Title = m.Note,
                    LocalText = m.LocalPort.ToString(),
                    PublicText = m.RemotePort > 0 ? m.RemotePort.ToString() : "待分配",
                    RemotePort = m.RemotePort,
                    Online = ok,
                    Failed = failed,
                    ChipBg = failed ? Palette.RedSoft : ok ? Palette.GreenSoft : Palette.Panel2,
                    ChipFg = failed ? Palette.Red : ok ? Palette.Green : Palette.Ink3,
                    ToolTip = $"{m.Note}：本地 {m.LocalPort} → 公网 {m.RemotePort}（点击修改端口）",
                });
            }
            return row;
        }).ToList();

        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        var q = CustomerSearch.Text?.Trim() ?? "";
        var rows = q.Length == 0
            ? _customerRows
            : _customerRows.Where(r =>
                    r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || r.ClientId.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || r.Ports.Any(p => p.PublicText.Contains(q) || p.LocalText.Contains(q)))
                .ToList();
        CustomersList.ItemsSource = rows;
        CustomersEmpty.IsVisible = rows.Count == 0;
        CustomersEmpty.Text = _customerRows.Count == 0 ? "暂无客户" : "没有匹配的客户";
    }

    private void RenderPending(List<PendingDto> pending)
    {
        var rows = pending.Where(p => !p.Rejected).Select(p => new PendingRow
        {
            ClientId = p.ClientId,
            Name = string.IsNullOrWhiteSpace(p.CompanyName) ? p.ClientId : p.CompanyName!,
            Initial = FirstChar(p.CompanyName),
            Machine = p.MachineName ?? "-",
            Time = string.IsNullOrEmpty(p.FirstSeenAt) ? "-" : FormatAgo(p.FirstSeenAt),
        }).ToList();

        PendingList.ItemsSource = rows;
        PendingEmpty.IsVisible = rows.Count == 0;
    }

    private async Task RefreshEventsAsync()
    {
        if (_backend is null) return;
        try
        {
            var events = await _backend.GetEventsAsync(200);
            var rows = (events ?? new List<EventDto>())
                .Select(e => EventRow.From(e.Time, e.Type, e.Msg))
                .ToList();
            EventsList.ItemsSource = rows;
            EventsEmpty.IsVisible = rows.Count == 0;
            LogsHint.Text = $"最近 {rows.Count} 条操作与事件";
        }
        catch (AdminAuthException)
        {
            ShowLoginOverlay("登录已失效，请重新连接");
        }
        catch (Exception ex)
        {
            ShowToast($"读取日志失败：{ex.Message}");
        }
    }

    // ================================================================ 页面切换

    private void OnNavOverviewClick(object? sender, RoutedEventArgs e) => SwitchPage(Page.Overview);
    private void OnNavCustomersClick(object? sender, RoutedEventArgs e) => SwitchPage(Page.Customers);
    private void OnNavPendingClick(object? sender, RoutedEventArgs e) => SwitchPage(Page.Pending);
    private void OnNavLogsClick(object? sender, RoutedEventArgs e) => SwitchPage(Page.Logs);

    private void SwitchPage(Page page)
    {
        _page = page;
        PageOverview.IsVisible = page == Page.Overview;
        PageCustomers.IsVisible = page == Page.Customers;
        PagePending.IsVisible = page == Page.Pending;
        PageLogs.IsVisible = page == Page.Logs;

        NavOverview.Classes.Set("active", page == Page.Overview);
        NavCustomers.Classes.Set("active", page == Page.Customers);
        NavPending.Classes.Set("active", page == Page.Pending);
        NavLogs.Classes.Set("active", page == Page.Logs);

        var title = page switch
        {
            Page.Overview => "总览",
            Page.Customers => "客户与端口",
            Page.Pending => "待审核",
            _ => "日志",
        };
        PageTitleText.Text = title;
        Title = $"YJTunnel 管理控制台 - {title}";

        _ = page == Page.Logs ? RefreshEventsAsync() : RefreshCoreAsync(silent: true);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshCoreAsync(silent: false);
        if (_page == Page.Logs) await RefreshEventsAsync();
        ShowToast("已刷新");
    }

    private void OnCustomerSearchChanged(object? sender, TextChangedEventArgs e) => ApplyCustomerFilter();

    // ================================================================ 行内操作

    private async void OnPortChipClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not PortRow row || _backend is null) return;

        var dlg = new PortDialog(string.IsNullOrWhiteSpace(row.Title) ? $"本地端口 {row.LocalText}" : row.Title, row.RemotePort);
        var newPort = await dlg.ShowDialog<int?>(this);
        if (newPort is not { } port || port == row.RemotePort) return;

        try
        {
            var result = await _backend.ChangePortAsync(row.CustomerId, row.Key, port);
            if (result.Ok)
            {
                ShowToast($"端口已改为 {port}，在线客户端 <10s 内生效");
                await RefreshCoreAsync(silent: true);
            }
            else
            {
                ShowToast(result.Error ?? "修改失败");
            }
        }
        catch (AdminAuthException) { ShowLoginOverlay("登录已失效，请重新连接"); }
        catch (Exception ex) { ShowToast($"修改失败：{ex.Message}"); }
    }

    private async void OnKickClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not CustomerRow row || _backend is null) return;

        var dlg = new KickDialog(row.Name);
        var choice = await dlg.ShowDialog<KickChoice?>(this);
        if (choice is not { } c) return;

        try
        {
            var result = await _backend.KickAsync(row.ClientId, c.Mode, c.Minutes);
            if (result.Ok)
            {
                ShowToast(c.Mode switch
                {
                    "temp" => $"已临时封禁 {c.Minutes} 分钟",
                    "permanent" => "已永久封禁",
                    _ => "已踢下线",
                });
                await RefreshCoreAsync(silent: true);
            }
            else
            {
                ShowToast(result.Error ?? "操作失败");
            }
        }
        catch (AdminAuthException) { ShowLoginOverlay("登录已失效，请重新连接"); }
        catch (Exception ex) { ShowToast($"操作失败：{ex.Message}"); }
    }

    private async void OnUnbanClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not CustomerRow row || _backend is null) return;
        try
        {
            var result = await _backend.SetStatusAsync(row.ClientId, true);
            ShowToast(result.Ok ? "已解除封禁/启用" : result.Error ?? "操作失败");
            if (result.Ok) await RefreshCoreAsync(silent: true);
        }
        catch (AdminAuthException) { ShowLoginOverlay("登录已失效，请重新连接"); }
        catch (Exception ex) { ShowToast($"操作失败：{ex.Message}"); }
    }

    private async void OnApproveClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not PendingRow row || _backend is null) return;
        try
        {
            var result = await _backend.PendingActionAsync(row.ClientId, true);
            ShowToast(result.Ok ? "已通过，端口已自动分配" : result.Error ?? "操作失败");
            await RefreshCoreAsync(silent: true);
        }
        catch (AdminAuthException) { ShowLoginOverlay("登录已失效，请重新连接"); }
        catch (Exception ex) { ShowToast($"操作失败：{ex.Message}"); }
    }

    private async void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not PendingRow row || _backend is null) return;
        try
        {
            var result = await _backend.PendingActionAsync(row.ClientId, false);
            ShowToast(result.Ok ? "已拒绝该接入申请" : result.Error ?? "操作失败");
            await RefreshCoreAsync(silent: true);
        }
        catch (AdminAuthException) { ShowLoginOverlay("登录已失效，请重新连接"); }
        catch (Exception ex) { ShowToast($"操作失败：{ex.Message}"); }
    }

    // ================================================================ 本地模式信息

    private void ShowLocalInfo(LocalProcessBackend local)
    {
        LocalInfoText.Text = $"控制端口 {local.ControlPort} · Web {local.WebUrl} · Token {local.Token}";
    }

    private void OnOpenWebClick(object? sender, RoutedEventArgs e)
    {
        if (_local is not { } local) return;
        _ = OpenUrlAsync(local.WebUrl);
    }

    private async void OnCopyTokenClick(object? sender, RoutedEventArgs e)
    {
        if (_local is null) return;
        await SetClipboardTextAsync(_local.Token);
        ShowToast("客户端 Token 已复制");
    }

    // ================================================================ 窗口 / 托盘

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        Hide();
        UpdateTrayIcon();
    }

    public void ForceClose()
    {
        _forceClose = true;
        _poll.Stop();
        try { _backend?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* 退出路径尽力而为 */ }
        _backend = null;
        Close();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void EnsureTray()
    {
        if (_tray is not null) return;
        if (App.Current is not { } app) return;

        var icons = TrayIcon.GetIcons(app);
        if (icons is null)
        {
            icons = new TrayIcons();
            TrayIcon.SetIcons(app, icons);
        }

        var menu = new NativeMenu();
        var show = new NativeMenuItem("打开控制台");
        show.Click += (_, _) => ShowFromTray();
        menu.Add(show);

        var refresh = new NativeMenuItem("立即刷新");
        refresh.Click += async (_, _) => await RefreshCoreAsync(silent: false);
        menu.Add(refresh);

        var copyAddr = new NativeMenuItem("复制服务器地址");
        copyAddr.Click += async (_, _) =>
        {
            if (_backend is null) { ShowToast("尚未连接服务器"); return; }
            await SetClipboardTextAsync(_backend.IsLocal
                ? $"本机:{_local?.ControlPort}"
                : _backend.EndpointText);
            ShowToast("服务器地址已复制");
        };
        menu.Add(copyAddr);

        var openWeb = new NativeMenuItem("打开 Web 管理台");
        openWeb.Click += (_, _) => OpenWebAdmin();
        menu.Add(openWeb);

        var switchConn = new NativeMenuItem("切换连接…");
        switchConn.Click += (_, _) =>
        {
            ShowFromTray();
            ShowLoginOverlay(null);
        };
        menu.Add(switchConn);

        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => App.RequestShutdown();
        menu.Add(exit);

        _tray = new TrayIcon
        {
            Icon = MakeTrayIcon(false),
            ToolTipText = "YJTunnel 管理控制台",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowFromTray();
        icons.Add(_tray);
    }

    private void UpdateTrayIcon()
    {
        EnsureTray();
        if (_tray is null) return;
        var connected = _backend?.IsConnected == true;
        _tray.Icon = MakeTrayIcon(connected);
        _tray.ToolTipText = connected
            ? _backend!.IsLocal
                ? $"YJTunnel 管理控制台 - 本机一体机 :{_local?.ControlPort}"
                : $"YJTunnel 管理控制台 - 已连接 {_backend.EndpointText}"
            : "YJTunnel 管理控制台 - 未连接";
    }

    private void OpenWebAdmin()
    {
        if (_backend is null) return;
        string url = _backend.IsLocal
            ? (_local?.WebUrl ?? "")
            : _backend.EndpointText.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? _backend.EndpointText
                : $"http://{_backend.EndpointText}/";
        if (string.IsNullOrEmpty(url))
        {
            ShowToast("Web 地址不可用");
            return;
        }
        _ = OpenUrlAsync(url);
    }

    /// <summary>托盘图标：绿=已连接，灰=未连接（黑玻璃方块 + 白隧道符）。</summary>
    private static WindowIcon MakeTrayIcon(bool connected)
    {
        const int size = 32;
        var color = connected ? Color.Parse("#17995A") : Color.Parse("#596069");
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(2, 2, 28, 28), 7);
            var white = new SolidColorBrush(Colors.White);
            ctx.FillRectangle(white, new Rect(8, 10, 5, 12), 2);
            ctx.FillRectangle(white, new Rect(19, 10, 5, 12), 2);
            ctx.FillRectangle(white, new Rect(8, 13.5, 16, 5), 2);
        }
        return new WindowIcon(bmp);
    }

    private void SetStatus(bool connected, string text)
    {
        StatusDot.Background = new SolidColorBrush(connected ? Color.Parse("#17995A") : Color.Parse("#9AA1AC"));
        StatusText.Text = text;
        LatencyText.Text = connected && _backend is { IsLocal: false, LatencyMs: > 0 } b ? $"{b.LatencyMs} ms" : "";
        ServerDot.Background = new SolidColorBrush(connected ? Color.Parse("#17995A") : Color.Parse("#9AA1AC"));
        if (!connected) ServerSubText.Text = "未连接";
        UpdateTrayIcon();
    }

    private void ShowToast(string message)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        Toast.Show(top, message, ToastKind.Info, duration: TimeSpan.FromSeconds(3));
    }

    private async Task SetClipboardTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await top.Clipboard!.SetDataAsync(transfer);
    }

    private static async Task OpenUrlAsync(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* 忽略 */ }
        await Task.CompletedTask;
    }

    // ================================================================ 小工具

    private static string FirstChar(string? name)
        => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1];

    private static string FormatAgo(string? time)
    {
        if (string.IsNullOrEmpty(time)) return "-";
        if (!DateTime.TryParse(time, out var t)) return time;
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        return $"{(int)span.TotalDays}d";
    }
}

/// <summary>本机一体机默认配置路径：程序目录 tunnelserver.json。</summary>
internal static class LocalConfigStore
{
    public static string DefaultPath => Path.Combine(AdminConfigStore.DefaultDir, "tunnelserver.json");
}
