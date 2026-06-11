using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Controls;

/// <summary>
/// Date/time picker for a scheduled (server-side) send. Used for both creating a new scheduled
/// message and editing an existing one — <see cref="Initialize"/> decides which. After
/// <c>ShowAsync</c> returns Primary, <see cref="Result"/> and <see cref="MessageText"/> hold
/// validated values.
/// </summary>
public sealed partial class ScheduleSendDialog : ContentDialog
{
    /// <summary>Validated send time; only meaningful when the dialog returned Primary.</summary>
    public DateTimeOffset? Result { get; private set; }

    /// <summary>Message text as (possibly) edited in the dialog.</summary>
    public string MessageText => MessageBox.Text;

    public ScheduleSendDialog()
    {
        InitializeComponent();
    }

    public void Initialize(string messageText, DateTimeOffset? existingTime)
    {
        MessageBox.Text = messageText;
        DatePicker.MinDate = DateTimeOffset.Now.Date;

        if (existingTime is { } existing)
        {
            Title = "Edit scheduled message";
            PrimaryButtonText = "Save";
            DatePicker.Date = existing.Date;
            TimeOfDayPicker.Time = existing.TimeOfDay;
        }
        else
        {
            // Default: an hour from now, rounded up to the next 5 minutes.
            var now = DateTimeOffset.Now.AddHours(1);
            var rounded = now.AddMinutes((5 - now.Minute % 5) % 5).AddSeconds(-now.Second);
            DatePicker.Date = rounded.Date;
            TimeOfDayPicker.Time = rounded.TimeOfDay;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(MessageBox.Text))
        {
            ShowValidation("Message text is required.");
            args.Cancel = true;
            return;
        }

        if (DatePicker.Date is not { } date)
        {
            ShowValidation("Pick a date.");
            args.Cancel = true;
            return;
        }

        // Combine as a *local* wall-clock time so the UTC offset is the one in effect on the
        // chosen date (DST), not necessarily today's.
        var localDt = DateTime.SpecifyKind(date.Date + TimeOfDayPicker.Time, DateTimeKind.Local);
        var sendAt = new DateTimeOffset(localDt);

        // Small buffer so the server's own "must be in the future" check can't race latency.
        if (sendAt < DateTimeOffset.Now.AddMinutes(1))
        {
            ShowValidation("Pick a time at least a minute in the future.");
            args.Cancel = true;
            return;
        }

        ValidationBar.IsOpen = false;
        Result = sendAt;
    }

    private void ShowValidation(string message)
    {
        ValidationBar.Message = message;
        ValidationBar.IsOpen = true;
    }
}
