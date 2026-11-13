using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YJTunnel.Admin.Views;

/// <summary>踢下线选择：mode = kick | temp | permanent；temp 时 Minutes 有效。</summary>
public sealed record KickChoice(string Mode, int? Minutes);

/// <summary>踢下线三档弹窗：仅踢 / 临时封禁（分钟）/ 永久封禁。</summary>
public partial class KickDialog : Window
{
    public KickDialog()
    {
        InitializeComponent();
        // 分钟输入仅在「临时封禁」选中时可用
        MinutesInput.IsEnabled = false;
        ModeTemp.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty)
                MinutesInput.IsEnabled = ModeTemp.IsChecked == true;
        };
    }

    public KickDialog(string customerName) : this()
        => CustomerText.Text = $"客户：{customerName}";

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (ModePermanent.IsChecked == true)
        {
            Close(new KickChoice("permanent", null));
            return;
        }

        if (ModeTemp.IsChecked == true)
        {
            if (!int.TryParse(MinutesInput.Text?.Trim(), out var minutes) || minutes is < 1 or > 525600)
            {
                MinutesInput.BorderBrush = Avalonia.Media.Brushes.IndianRed;
                MinutesInput.BorderThickness = new Avalonia.Thickness(1.5);
                return;
            }
            Close(new KickChoice("temp", minutes));
            return;
        }

        Close(new KickChoice("kick", null));
    }
}
