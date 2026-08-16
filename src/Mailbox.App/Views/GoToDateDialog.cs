using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Mailbox.App.Views;

/// <summary>
/// The reference's Go to Date prompt: a date, and which arrangement to show it in.
/// </summary>
/// <remarks>
/// Reached from the Go To group's corner arrow and from <c>Ctrl+G</c>. Small on purpose — it is
/// two controls and two buttons in the reference too.
/// </remarks>
public sealed class GoToDateDialog : Window
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The date picked, or null when the prompt was cancelled.</summary>
    public DateOnly? Chosen { get; private set; }

    /// <summary>The arrangement to show it in.</summary>
    public CalendarViewKind View { get; private set; }

    public GoToDateDialog(DateOnly current, CalendarViewKind view)
    {
        Title = "Go To Date";
        Width = 360;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = view;

        var date = new CalendarDatePicker { SelectedDate = current.ToDateTime(TimeOnly.MinValue), MinWidth = 180 };

        CalendarViewKind[] kinds = [CalendarViewKind.Day, CalendarViewKind.WorkWeek, CalendarViewKind.Week, CalendarViewKind.Month, CalendarViewKind.Schedule];
        var picker = new ComboBox
        {
            ItemsSource = kinds.Select(CalendarWorkspace.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(kinds, view)),
            MinWidth = 180,
        };

        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            Chosen = DateOnly.FromDateTime(date.SelectedDate?.Date ?? current.ToDateTime(TimeOnly.MinValue));
            View = kinds[Math.Clamp(picker.SelectedIndex, 0, kinds.Length - 1)];
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Place(grid, 0, "Date:", date);
        Place(grid, 1, "Show in:", picker);

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { ok, cancel },
                },
                grid,
            },
        };

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static void Place(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }
}
