using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TKCode.Arc;
using YJTunnel.Client.Services;
using YJTunnel.Client.ViewModels;

namespace YJTunnel.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

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
                case nameof(MainViewModel.ServerText):
                case nameof(MainViewModel.CompanyText):
                    ApplyState();
                    break;
                case nameof(MainViewModel.StartStopText):
                    StartStopButton.Content = _vm.StartStopText;
                    StartStopButton.Variant = _vm.IsRunning ? ButtonVariant.Secondary : ButtonVariant.Primary;
                    break;
            }
        };

        Closing += OnWindowClosing;
        Opened += (_, _) => { _vm.Attach(); UpdateTrayIcon(); };
    }

    // ---- 状态横幅（VM 字符串驱动，Arc 类型留在 View） ----

    private static readonly Dictionary<string, AlertTone> Tones = new()
    {
        ["info"] = AlertTone.Info,
        ["success"] = AlertTone.Success,
        ["warning"] = AlertTone.Warning,
        ["danger"] = AlertTone.Danger,
    };

    private void ApplyState()
    {
        StateBanner.Tone = Tones.GetValueOrDefault(_vm.StateTone, AlertTone.Info);
        StateTextBlock.Text = _vm.StateText;
        StateDetailBlock.Text = _vm.StateDetail;
        ServerTextBlock.Text = _vm.ServerText;
        CompanyTextBlock.Text = _vm.CompanyText;
        StateShortBlock.Text = _vm.StateText;
        Title = $"YJTunnel 客户端 - {_vm.StateText}";
        UpdateTrayIcon();
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

    private void OnAddMappingClick(object? sender, RoutedEventArgs e) => _vm.AddMappingRow();

    private void OnEditConfigClick(object? sender, RoutedEventArgs e)
    {
        // 首版：连接配置（服务端/令牌/公司名）在弹窗中编辑
        _ = ShowConfigDialogAsync();
    }

    /// <summary>连接配置弹窗：编辑服务端地址/端口/令牌/公司名，保存后按需重启。</summary>
    private async Task ShowConfigDialogAsync()
    {
        var dialog = new ConfigDialog(_vm);
        await dialog.ShowDialog(this);
    }

    private void OnRemoveMappingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MappingItemViewModel row) _vm.RemoveMappingRow(row);
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

    private bool _forceClose;

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !_vm.MinimizeOnClose) return;   // 真退出走默认
        e.Cancel = true;
        HideToTray();
    }
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

    private TrayIcon? _tray;

    private void EnsureTray()
    {
        if (_tray is not null) return;
        if (App.Current is not { } app) return;
        if (TrayIcon.GetIcons(app) is not { } icons) return;

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
        var color = tone switch
        {
            "success" => Colors.SeaGreen,
            "info" => Colors.RoyalBlue,
            "warning" => Colors.DarkOrange,
            "danger" => Colors.IndianRed,
            _ => Colors.Gray,
        };
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
