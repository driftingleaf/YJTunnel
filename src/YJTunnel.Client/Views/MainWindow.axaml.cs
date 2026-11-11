using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using YJTunnel.Client.Services;
using YJTunnel.Client.ViewModels;

namespace YJTunnel.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private TrayIcon? _tray;
    private bool _forceClose;

    // ---- 状态主题（tone → 主色） ----

    private static readonly Dictionary<string, Color> ToneColors = new()
    {
        ["info"] = Color.Parse("#2F5FEE"),
        ["success"] = Color.Parse("#1E9E57"),
        ["warning"] = Color.Parse("#D9822B"),
        ["danger"] = Color.Parse("#D6484C"),
        ["neutral"] = Color.Parse("#9AA2AC"),
    };

    private static readonly Dictionary<string, Geometry> StateGlyphs = new()
    {
        ["idle"] = ArcIcons.Play,
        ["info"] = ArcIcons.Hourglass,
        ["success"] = ArcIcons.LockOpen,
        ["warning"] = ArcIcons.RefreshCw,
        ["danger"] = ArcIcons.Ban,
        ["neutral"] = ArcIcons.Play,
    };

    private static readonly Dictionary<string, AlertTone> BannerTones = new()
    {
        ["info"] = AlertTone.Info,
        ["success"] = AlertTone.Success,
        ["warning"] = AlertTone.Warning,
        ["danger"] = AlertTone.Danger,
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.ToastRequested += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
        _vm.TrayStateRefreshRequested += UpdateTrayIcon;
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.StateTone):
                case nameof(MainViewModel.StateText):
                case nameof(MainViewModel.StateDetail):
                case nameof(MainViewModel.StateWhen):
                case nameof(MainViewModel.ServerText):
                case nameof(MainViewModel.CompanyText):
                    ApplyState();
                    break;
                case nameof(MainViewModel.StartStopText):
                    StartStopButton.Content = _vm.StartStopText;
                    StartStopButton.Variant = _vm.IsRunning ? ButtonVariant.Secondary : ButtonVariant.Primary;
                    break;
                case nameof(MainViewModel.UptimeMinutes):
                case nameof(MainViewModel.LatencyMs):
                case nameof(MainViewModel.OpenPorts):
                    ApplyMetrics();
                    break;
                case nameof(MainViewModel.MappingCount):
                    ApplyMappingVisibility();
                    break;
            }
        };

        Closing += OnWindowClosing;
        Opened += (_, _) =>
        {
            _vm.Attach();
            UpdateTrayIcon();
            ApplyMetrics();
            ApplyMappingVisibility();
            if (Program.StartMinimized) Hide();
        };
    }

    // ---- Hero 状态渲染 ----

    private void ApplyState()
    {
        var tone = _vm.StateTone;
        var c = ToneColors.GetValueOrDefault(tone, ToneColors["neutral"]);

        // Hero 底色 = 主色 8% 透明（中性状态透明）
        if (tone == "neutral")
            HeroBorder.Background = new SolidColorBrush(Colors.Transparent);
        else
            HeroBorder.Background = new SolidColorBrush(Color.FromArgb(20, c.R, c.G, c.B));

        HeroDot.Background = new SolidColorBrush(c);
        HeroDotGlyph.Data = StateGlyphs.GetValueOrDefault(tone, ArcIcons.Play);

        HeroTitleText.Text = _vm.StateText;
        HeroSubText.Text = _vm.StateDetail;
        if (!string.IsNullOrEmpty(_vm.StateWhen))
        {
            HeroWhenBadge.IsVisible = true;
            HeroWhenText.Text = _vm.StateWhen;
        }
        else
        {
            HeroWhenBadge.IsVisible = false;
        }

        ServerTextBlock.Text = _vm.ServerText;
        CompanyTextBlock.Text = _vm.CompanyText;
        StateShortBlock.Text = _vm.StateText;
        Title = $"YJTunnel 客户端 - {_vm.StateText}";
        UpdateTrayIcon();
    }

    private void ApplyMetrics()
    {
        MetricUptime.Text = _vm.UptimeMinutes >= 60
            ? $"{_vm.UptimeMinutes / 60}h {_vm.UptimeMinutes % 60}m"
            : _vm.UptimeMinutes > 0 ? $"{_vm.UptimeMinutes}m" : "—";
        MetricLatency.Text = _vm.LatencyMs > 0 ? $"{_vm.LatencyMs} ms" : "—";
        MetricPorts.Text = _vm.IsRunning ? $"{_vm.OpenPorts} 个" : "—";
        // 在线且有开放端口时数值着绿色
        if (_vm.StateTone == "success" && _vm.OpenPorts > 0)
            MetricPorts.Foreground = new SolidColorBrush(Color.Parse("#1E9E57"));
        else
            MetricPorts.ClearValue(TextBlock.ForegroundProperty);
    }

    private void ApplyMappingVisibility()
    {
        var hasMappings = _vm.MappingCount > 0;
        MappingCountBadge.Content = $"{_vm.MappingCount} 条";
        MappingCountBadge.Tone = hasMappings ? BadgeTone.Neutral : BadgeTone.Warning;
        MappingsList.IsVisible = hasMappings;
        MappingEmpty.IsVisible = !hasMappings;
    }

    // ---- 事件处理 ----

    private void OnStartStopClick(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning) _vm.StopTunnel();
        else _vm.StartTunnel();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        _vm.ApplyMappingsToConfig();
        _vm.StartTunnel();   // 校验失败弹 Toast；成功即（重）启隧道
    }

    private void OnAddMappingClick(object? sender, RoutedEventArgs e)
    {
        _vm.AddMappingRow();
        ApplyMappingVisibility();
        // 滚到底让新行可见
        if (MappingsList.IsVisible && this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sv)
            sv.ScrollToEnd();
    }

    private void OnEditConfigClick(object? sender, RoutedEventArgs e)
        => _ = new ConfigDialog(_vm).ShowDialog(this);

    private void OnRemoveMappingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MappingItemViewModel row)
        {
            _vm.RemoveMappingRow(row);
            ApplyMappingVisibility();
        }
    }

    private async void OnCopyRowAddressClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not MappingItemViewModel row) return;
        if (row.RemotePort <= 0 || string.IsNullOrEmpty(row.PublicHost))
        {
            ShowToast("当前未在线，没有可复制的公网地址");
            return;
        }
        await SetClipboardTextAsync(row.PublicAddress);
        ShowToast($"已复制 {row.PublicAddress}");
    }

    private async void OnCopyDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SetClipboardTextAsync(_vm.BuildDiagnostics());
            ShowToast("诊断信息已复制");
        }
        catch (Exception ex)
        {
            Log.Error("复制诊断失败", ex);
        }
    }

    private void OnHideToTrayClick(object? sender, RoutedEventArgs e) => HideToTray();

    // ---- 关闭即最小化 ----

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !_vm.MinimizeOnClose) return;   // 真退出走默认
        e.Cancel = true;
        HideToTray();
    }

    /// <summary>托盘菜单"退出"调用：跳过最小化逻辑直接关闭。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        UpdateTrayIcon();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ---- 托盘 ----

    private void EnsureTray()
    {
        if (_tray is not null) return;
        if (App.Current is not { } app) return;

        // Icons 附加属性默认为 null，必须先 SetIcons 初始化（GetIcons 为 null 时直接返回会导致托盘永远不出现）
        var icons = TrayIcon.GetIcons(app);
        if (icons is null)
        {
            icons = new TrayIcons();
            TrayIcon.SetIcons(app, icons);
        }

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowFromTray();
        menu.Add(showItem);

        var startStopItem = new NativeMenuItem("启动 / 停止");
        startStopItem.Click += (_, _) =>
        {
            if (_vm.IsRunning) _vm.StopTunnel(); else _vm.StartTunnel();
        };
        menu.Add(startStopItem);

        var copyAddrItem = new NativeMenuItem("复制公网地址");
        copyAddrItem.Click += async (_, _) =>
        {
            var text = _vm.BuildPublicAddresses();
            if (string.IsNullOrEmpty(text)) { ShowToast("当前未在线，没有可复制的公网地址"); return; }
            await SetClipboardTextAsync(text);
            ShowToast("公网地址已复制");
        };
        menu.Add(copyAddrItem);

        var diagItem = new NativeMenuItem("复制诊断信息");
        diagItem.Click += async (_, _) =>
        {
            await SetClipboardTextAsync(_vm.BuildDiagnostics());
            ShowToast("诊断信息已复制");
        };
        menu.Add(diagItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => App.RequestShutdown();
        menu.Add(exitItem);

        _tray = new TrayIcon
        {
            Icon = MakeTrayIcon("neutral"),
            ToolTipText = "YJTunnel 客户端",
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
        _tray.Icon = MakeTrayIcon(_vm.StateTone);
        _tray.ToolTipText = $"YJTunnel 客户端 - {_vm.StateText}";
    }

    /// <summary>托盘图标：灰=未启动 蓝=连接中 绿=在线 橙=重连 红=失败/封禁。</summary>
    private static WindowIcon MakeTrayIcon(string tone)
    {
        const int size = 32;
        var c = ToneColors.GetValueOrDefault(tone, ToneColors["neutral"]);
        var color = Color.FromRgb(c.R, c.G, c.B);
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(2, 2, 28, 28), 6);
            var white = new SolidColorBrush(Colors.White);
            ctx.FillRectangle(white, new Rect(8, 10, 5, 12), 2);
            ctx.FillRectangle(white, new Rect(19, 10, 5, 12), 2);
            ctx.FillRectangle(white, new Rect(8, 13.5, 16, 5), 2);
        }
        return new WindowIcon(bmp);
    }

    // ---- Toast / 剪贴板 ----

    private void ShowToast(string message)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        Toast.Show(top, message, ToastKind.Info, duration: TimeSpan.FromSeconds(3));
    }

    /// <summary>Avalonia 12 剪贴板走 DataTransfer API。</summary>
    private async Task SetClipboardTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await top.Clipboard!.SetDataAsync(transfer);
    }
}
