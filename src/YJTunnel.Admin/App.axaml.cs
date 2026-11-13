using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YJTunnel.Admin.Views;

namespace YJTunnel.Admin;

public partial class App : Application
{
    public static IClassicDesktopStyleApplicationLifetime? CurrentLifetime { get; private set; }

    public static void RequestShutdown()
    {
        if (CurrentLifetime is { } lifetime)
        {
            if (lifetime.MainWindow is MainWindow main) main.ForceClose();
            lifetime.Shutdown();
        }
    }

    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CurrentLifetime = desktop;
            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 关窗≠退出（托盘常驻）

            if (Program.StartMinimized) main.Hide();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
