using Microsoft.UI.Xaml.Data;

namespace BlueBubbles.Windows.Converters;

/// <summary>
/// Converts Unix millisecond timestamps to relative display strings per spec 10.5:
/// Today → time only, Yesterday → "Yesterday", This week → day name, Older → short date.
/// </summary>
public sealed class DateTimeToRelativeConverter : IValueConverter
{
    public bool Use24HourFormat { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long timestamp = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0
        };

        if (timestamp == 0)
            return string.Empty;

        var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;
        var now = DateTime.Now;
        var today = now.Date;

        if (dateTime.Date == today)
        {
            return dateTime.ToString(Use24HourFormat ? "HH:mm" : "h:mm tt");
        }

        if (dateTime.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        if (dateTime.Date > today.AddDays(-7) && dateTime.Date < today)
        {
            return dateTime.ToString("dddd");
        }

        return dateTime.ToString("M/d/yy");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
