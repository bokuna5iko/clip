using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clip.Converters;

public sealed class KindToImageVisibility : IValueConverter
{
    public static KindToImageVisibility Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string k && k == "image" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class KindToGlyphVisibility : IValueConverter
{
    public static KindToGlyphVisibility Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string k && k == "image" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
