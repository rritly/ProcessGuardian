using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProcessGuardian.Wpf.Converters;

// Collapses an element when the bound string is null or empty, so the persistent
// config-state indicators (reset notice / refusal guidance) only occupy layout space
// when a message is present.
public sealed class StringIsEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
