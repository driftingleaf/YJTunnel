using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using TKCode.Arc;
using YJTunnel.Client;
using YJTunnel.Client.Services;
using YJTunnel.Client.Views;

internal static class Program
{
    /// <summary>--minimized：启动即隐藏到托盘（开机自启场景）。</summary>
    private static bool _startMinimized;

    [STAThread]
    public static void Main(string[] args)
    {
        _startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // 单实例：第二个实例通知已有实例弹主窗口后退出
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalShow();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseArc(arc => arc
                .WithTheme(t => t.Accent(Avalonia.Media.Color.Parse("#2F5FEE")))   // 品牌蓝种子（默认 Neutral 黑白灰）
                .UseArcCore()
                .UseArcControls())
            .AfterSetup(_ =>
            {
                // 唯一实例监听"弹主窗口"信号
                if (App.CurrentLifetime is { } lifetime && lifetime.MainWindow is MainWindow main)
                {
                    SingleInstance.ListenShow(() => Avalonia.Threading.Dispatcher.UIThread.Post(main.ShowFromTray));
                }
            })
            .LogToTrace();

    /// <summary>窗口创建后按需最小化（由 App.OnFrameworkInitializationCompleted 调用）。</summary>
    public static bool StartMinimized => _startMinimized;
}
