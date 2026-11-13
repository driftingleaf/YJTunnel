using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using TKCode.Arc;
using YJTunnel.Admin;
using YJTunnel.Admin.Services;
using YJTunnel.Admin.Views;

internal static class Program
{
    private static bool _startMinimized;

    [STAThread]
    public static void Main(string[] args)
    {
        _startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

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
                .WithTheme(t => t.Accent(Avalonia.Media.Color.Parse("#3B63F3")))
                .UseArcCore()
                .UseArcControls())
            .AfterSetup(_ =>
            {
                if (App.CurrentLifetime is { } lifetime && lifetime.MainWindow is MainWindow main)
                {
                    SingleInstance.ListenShow(() => Avalonia.Threading.Dispatcher.UIThread.Post(main.ShowFromTray));
                }
            })
            .LogToTrace();

    public static bool StartMinimized => _startMinimized;
}
