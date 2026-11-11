using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YJTunnel.Client.Views;

namespace YJTunnel.Client;

public partial class App : Application
{
    /// <summary>当前桌面生命周期（托盘/关闭逻辑用）；退出时置 null。</summary>
    public static IClassicDesktopStyleApplicationLifetime? CurrentLifetime { get; private set; }

    /// <summary>托盘菜单"退出"或程序内请求退出：停隧道后关闭主窗口。</summary>
    public static void RequestShutdown()
    {
        Services.TunnelService.Instance.Dispose();
        if (CurrentLifetime is { } lifetime)
        {
            if (lifetime.MainWindow is Views.MainWindow main)
                main.ForceClose();
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
            desktop.ShutdownRequested += (_, _) => Services.TunnelService.Instance.Dispose();

            if (Program.StartMinimized)
                main.Hide();   // 自启/静默启动：直接进托盘
        }

        base.OnFrameworkInitializationCompleted();
    }
}
