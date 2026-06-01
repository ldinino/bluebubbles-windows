namespace BlueBubbles.Windows.ViewModels;

public class DateSeparatorViewModel
{
    public string Label { get; }

    public DateSeparatorViewModel(DateTimeOffset date)
    {
        var today = DateTime.Now.Date;
        var msgDate = date.LocalDateTime.Date;

        Label = msgDate == today ? "Today"
            : msgDate == today.AddDays(-1) ? "Yesterday"
            : msgDate > today.AddDays(-7) ? date.LocalDateTime.ToString("dddd")
            : date.LocalDateTime.ToString("MMMM d, yyyy");
    }
}
