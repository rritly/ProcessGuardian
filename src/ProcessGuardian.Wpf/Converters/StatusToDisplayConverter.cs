using System.Globalization;
using System.Windows.Data;
using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Wpf.Converters;

[ValueConversion(typeof(GuardianStatus), typeof(string))]
public sealed class StatusToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            GuardianStatus.Waiting => "MONITORING",
            GuardianStatus.Monitoring => "MONITORING",
            GuardianStatus.Restarting => "MONITORING",
            GuardianStatus.Error => "ERROR",
            _ => "STOPPED",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
