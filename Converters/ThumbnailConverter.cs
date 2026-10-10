using System.Globalization;
using System.Windows.Data;
using Clip.ViewModels;

namespace Clip.Converters;

public sealed class ThumbnailConverter : IValueConverter
{
    public static ThumbnailConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string path ? MainViewModel.LoadThumbnail(path) : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
