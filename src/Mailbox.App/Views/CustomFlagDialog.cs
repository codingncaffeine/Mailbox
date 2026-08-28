using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>What the Custom flag dialog came to.</summary>
/// <param name="Type">What the flag says: "Follow up", "Call", "Review" and the rest.</param>
/// <param name="Start">The start date, or null.</param>
/// <param name="Due">The due date, or null for a flag with no date.</param>
/// <param name="Reminder">When to be reminded, or null for no reminder.</param>
public sealed record CustomFlag(string Type, DateTimeOffset? Start, DateTimeOffset? Due, DateTimeOffset? Reminder);

/// <summary>
/// The reference's Custom flag dialog: Flag to, Start date, Due date, a Reminder with its date
/// and time, and Clear Flag — reached from the flag menu's Custom… and Add Reminder….
/// </summary>
public sealed class CustomFlagDialog : Window
{
    /// <summary>The flag as set, or null when the dialog was cancelled.</summary>
    public CustomFlag? Result { get; private set; }

    /// <summary>True when Clear Flag was pressed: the flag comes off rather than being set.</summary>
    public bool Cleared { get; private set; }

    /// <summary>The reference's own list of what a flag can say.</summary>
    public static readonly string[] FlagTypes =
    [
        "Follow up", "Call", "Do not Forward", "For Your Information", "Forward",
        "No Response Necessary", "Read", "Reply", "Reply to All", "Review",
    ];

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <param name="current">The row the flag is being set on, for its present values.</param>
    /// <param name="reminderOn">Whether the reminder box starts ticked — Add Reminder… ticks it.</param>
    public CustomFlagDialog(MessageSummary? current, bool reminderOn = false)
    {
        Title = "Custom";
        Width = 460;
        Height = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var type = new ComboBox { ItemsSource = FlagTypes.ToList(), MinWidth = 220 };
        type.SelectedIndex = Math.Max(0, Array.IndexOf(FlagTypes, current?.FollowUpType ?? "Follow up"));

        // Today as the application believes it — the real day unless MAILBOX_TODAY pins one.
        // The dialog's date defaults are otherwise a different picture every day it is
        // photographed, and a flag set from here writes a date the run cannot be repeated to.
        var today = Mailbox.Core.PosedClock.Now.LocalDateTime.Date;

        var start = new CalendarDatePicker { SelectedDate = current?.FollowUpStart?.LocalDateTime.Date, MinWidth = 160 };
        var due = new CalendarDatePicker { SelectedDate = current?.FollowUpDue?.LocalDateTime.Date ?? today, MinWidth = 160 };

        var reminderDate = new CalendarDatePicker { MinWidth = 160 };
        var reminderTime = new ComboBox { MinWidth = 110 };
        reminderTime.ItemsSource = Enumerable.Range(0, 48).Select(h => today.AddMinutes(30 * h).ToString("h:mm tt", CultureInfo.CurrentCulture)).ToList();

        var existing = current?.Reminder?.LocalDateTime;
        var reminderOnNow = reminderOn || existing is not null;
        var reminderAt = existing ?? (due.SelectedDate ?? today).Date.AddHours(16);
        reminderDate.SelectedDate = reminderAt.Date;
        reminderTime.SelectedIndex = Math.Clamp((int)(reminderAt.TimeOfDay.TotalMinutes / 30), 0, 47);

        var reminder = new CheckBox { Content = "Reminder:", IsChecked = reminderOnNow, VerticalAlignment = VerticalAlignment.Center };
        Bind(reminder, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        reminderDate.IsEnabled = reminderTime.IsEnabled = reminderOnNow;
        reminder.IsCheckedChanged += (_, _) => reminderDate.IsEnabled = reminderTime.IsEnabled = reminder.IsChecked == true;

        var clear = new Button { Content = "Clear Flag" };
        clear.Click += (_, _) => { Cleared = true; Close(); };

        var ok = new Button { Content = "OK", Width = 74, IsDefault = true };
        ok.Click += (_, _) =>
        {
            DateTimeOffset? At(DateTime? date, TimeSpan time) => date is { } d ? new DateTimeOffset(d.Date.Add(time)) : null;

            var minutes = reminderTime.SelectedIndex < 0 ? 16 * 60 : reminderTime.SelectedIndex * 30;
            Result = new CustomFlag(
                type.SelectedItem as string ?? "Follow up",
                At(start.SelectedDate, TimeSpan.Zero),
                At(due.SelectedDate, TimeSpan.FromHours(17)),
                reminder.IsChecked == true ? At(reminderDate.SelectedDate, TimeSpan.FromMinutes(minutes)) : null);
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var explanation = new TextBlock
        {
            Text = "Flagging creates a to-do item that reminds you to follow up. After you follow up, you can mark the to-do item complete.",
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(explanation, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        void Row(int row, Control label, Control control)
        {
            label.Margin = new Thickness(0, 0, 12, 8);
            control.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(label, row); Grid.SetColumn(label, 0); grid.Children.Add(label);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }

        Row(0, Label("Flag to:"), type);
        Row(1, Label("Start date:"), start);
        Row(2, Label("Due date:"), due);
        Row(3, reminder, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { reminderDate, reminderTime } });

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new Grid
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { clear, Placed(ok, 2), Placed(cancel, 3) },
                },
                new StackPanel { Children = { explanation, grid } },
            },
        };
        ok.Margin = new Thickness(0, 0, 8, 0);

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static Control Placed(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }
}
