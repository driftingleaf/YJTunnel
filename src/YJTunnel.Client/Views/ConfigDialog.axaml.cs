using Avalonia.Controls;
using Avalonia.Interactivity;
using YJTunnel.Client.Services;
using YJTunnel.Client.ViewModels;

namespace YJTunnel.Client.Views;

/// <summary>连接配置弹窗：编辑服务端/令牌/公司名/托盘偏好。</summary>
public partial class ConfigDialog : Window
{
    private readonly MainViewModel _vm;

    public ConfigDialog()
    {
        InitializeComponent();
        _vm = new MainViewModel();
    }

    public ConfigDialog(MainViewModel vm) : this()
    {
        _vm = vm;
        Loaded += (_, _) => LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var c = _vm.Config;
        ServerAddrInput.Text = c.ServerAddr;
        ServerPortInput.Value = c.ServerPort <= 0 ? 5003 : c.ServerPort;
        TokenInput.Text = c.Token;
        CompanyNameInput.Text = c.CompanyName;
        MinimizeToTrayCheck.IsChecked = c.MinimizeToTray;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var updated = _vm.Config;
        updated.ServerAddr = ServerAddrInput.Text?.Trim() ?? "";
        updated.ServerPort = (int)ServerPortInput.Value;
        updated.Token = TokenInput.Text?.Trim() ?? "";
        updated.CompanyName = CompanyNameInput.Text?.Trim() ?? "";
        updated.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;

        var wantAutoStart = AutoStartCheck.IsChecked == true;
        var wasRunning = _vm.IsRunning;
        _vm.StopTunnel();
        _vm.SaveConfig(updated, restartIfRunning: wasRunning);

        try
        {
            if (wantAutoStart != AutoStart.IsEnabled())
                AutoStart.Set(wantAutoStart);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置开机自启失败：{ex.Message}");
        }
        Close();
    }
}
