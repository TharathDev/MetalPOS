using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PosApp.Converters;

/// <summary>
/// Returns <see cref="TrueBrush"/> when the bound value's string form equals the
/// ConverterParameter, otherwise <see cref="FalseBrush"/>. Used to style the
/// active tab and the selected dimension row without per-state XAML classes.
/// </summary>
public class EqualityToBrushConverter : IValueConverter
{
    public IBrush? TrueBrush { get; set; }
    public IBrush? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isMatch = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return isMatch ? TrueBrush : FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
