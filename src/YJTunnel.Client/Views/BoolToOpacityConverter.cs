using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace YJTunnel.Client.Views;

/// <summary>行启用状态 → 整行透明度（停用置灰）。</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1.0;
    public double FalseValue { get; set; } = 0.45;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueValue : FalseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
