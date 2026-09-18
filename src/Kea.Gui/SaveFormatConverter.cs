using System.Globalization;
using Avalonia.Data.Converters;
using Kea.Core;

namespace Kea.Gui;

/// <summary>Shows a <see cref="SaveFormat"/> using the same wording as the original drop-down.</summary>
public sealed class SaveFormatConverter : IValueConverter
{
    public static SaveFormatConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SaveFormat format ? format.DisplayName() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && SaveFormats.TryParse(text, out SaveFormat format) ? format : null;
}
