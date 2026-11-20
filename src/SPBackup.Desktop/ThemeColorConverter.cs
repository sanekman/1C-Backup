using System.Globalization;
using System.Windows;
using System.Windows.Data;
namespace SPBackup.Desktop;
public sealed class ThemeColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Application.Current.Resources[(value as string) switch
    {
        "#15803D" or "#166534" => "SuccessBrush",
        "#B91C1C" => "ErrorBrush",
        "#B45309" or "#92400E" => "WarningBrush",
        "#1D4ED8" => "LinkBrush",
        _ => "MutedBrush"
    }];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ObservationLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SPBackup.Core.ExternalBackupObservation observation) return "";
        var time = Equals(parameter, "Start") ? observation.StartedAt : observation.FinishedAt;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(observation.TimeZoneId);
        time = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        // Old source evidence retains its original zone; only presentation changes.
        if (zone.IsInvalidTime(time)) return "Время источника некорректно";
        return TimeZoneInfo.ConvertTime(time, zone, TimeZoneInfo.Local).ToString("dd.MM.yyyy HH:mm:ss", culture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
