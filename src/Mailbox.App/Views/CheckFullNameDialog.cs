using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Core.People;

namespace Mailbox.App.Views;

/// <summary>
/// Check Full Name: the reference's dialog behind the contact form's Full Name… button — the
/// typed line split into Title, First, Middle, Last and Suffix, each in a box of its own so the
/// split can be corrected before it is stored.
/// </summary>
/// <remarks>
/// The titles and suffixes are the reference's own short lists, offered rather than enforced:
/// both boxes take typing, because the world holds more honorifics than any list.
/// </remarks>
public sealed class CheckFullNameDialog : Window
{
    private readonly ComboBox _title = new() { MinWidth = 110 };
    private readonly TextBox _first = new();
    private readonly TextBox _middle = new();
    private readonly TextBox _last = new();
    private readonly ComboBox _suffix = new() { MinWidth = 110 };
    private readonly Button _ok = new() { Content = "OK", Width = 84, IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", Width = 84, IsCancel = true };

    public CheckFullNameDialog(FullNames.NameParts parts)
    {
        Title = "Check Full Name";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _title.ItemsSource = Offer(FullNames.Prefixes, parts.Prefix);
        _title.SelectedItem = parts.Prefix.Length > 0 ? parts.Prefix : null;
        _first.Text = parts.First;
        _middle.Text = parts.Middle;
        _last.Text = parts.Last;
        _suffix.ItemsSource = Offer(FullNames.Suffixes, parts.Suffix);
        _suffix.SelectedItem = parts.Suffix.Length > 0 ? parts.Suffix : null;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(18, 14, 18, 0),
        };

        Place(grid, 0, "Title:", _title);
        Place(grid, 1, "First:", _first);
        Place(grid, 2, "Middle:", _middle);
        Place(grid, 3, "Last:", _last);
        Place(grid, 4, "Suffix:", _suffix);

        _ok.Click += (_, _) =>
        {
            Result = new FullNames.NameParts(
                _title.SelectedItem as string ?? string.Empty,
                (_first.Text ?? string.Empty).Trim(),
                (_middle.Text ?? string.Empty).Trim(),
                (_last.Text ?? string.Empty).Trim(),
                _suffix.SelectedItem as string ?? string.Empty);
            Close();
        };

        _cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(18, 14, 18, 14),
            Children = { _ok, _cancel },
        };

        DialogChrome.Apply(this, new StackPanel { Children = { grid, buttons } });
    }

    /// <summary>The parts as corrected, or null when the dialog was dismissed.</summary>
    public FullNames.NameParts? Result { get; private set; }

    /// <summary>The list with whatever is already on the card kept, wherever it came from.</summary>
    private static IReadOnlyList<string> Offer(IReadOnlyList<string> standard, string carried)
        => carried.Length > 0 && !standard.Contains(carried, StringComparer.OrdinalIgnoreCase)
            ? [.. standard, carried]
            : standard;

    /// <summary>Fills the five boxes, for a harness pose that cannot type into a modal.</summary>
    internal void Pose(string title, string first, string middle, string last, string suffix)
    {
        _title.ItemsSource = Offer(FullNames.Prefixes, title);
        _title.SelectedItem = title.Length > 0 ? title : null;
        _first.Text = first;
        _middle.Text = middle;
        _last.Text = last;
        _suffix.ItemsSource = Offer(FullNames.Suffixes, suffix);
        _suffix.SelectedItem = suffix.Length > 0 ? suffix : null;
    }

    /// <summary>Presses OK, for the harness.</summary>
    internal void PressOk() => _ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private void Place(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
