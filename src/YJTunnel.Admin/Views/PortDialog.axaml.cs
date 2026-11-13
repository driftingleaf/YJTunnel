using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YJTunnel.Admin.Views;

/// <summary>改端口弹窗：返回新端口（取消返回 null）。</summary>
public partial class PortDialog : Window
{
    private int _current;

    public PortDialog() => InitializeComponent();

    public PortDialog(string title, int currentPort) : this()
    {
        _current = currentPort;
        TitleText.Text = title;
        CurrentText.Text = $"当前公网端口：{(currentPort > 0 ? currentPort.ToString() : "待分配")}";
        PortInput.Text = currentPort > 0 ? currentPort.ToString() : "";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortInput.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            PortInput.IsInvalid = true;
            PortInput.ErrorMessage = "请输入 1-65535 的端口号";
            return;
        }
        Close(port);
    }
}
