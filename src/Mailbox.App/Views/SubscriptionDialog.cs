using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// The reference's "New Internet Calendar Subscription" and "New RSS Feed": a prompt, a field
/// for the address, an example under it, and Add and Cancel.
/// </summary>
/// <remarks>
/// A system dialog, measured off the capture: 385×125, the prompt 6px in and 10px under the
/// caption, the field 355 wide and 20 tall starting 24px in, the example on the field's left
/// edge, and the two 73px buttons in the bottom right corner with 11px between them. Add is
/// disabled until something has been typed, as the reference's is.
/// </remarks>
public sealed class SubscriptionDialog : Window
{
    private readonly TextBox _location = Field();
    private readonly Button _add;

    /// <summary>What was typed when Add was pressed, or null for Cancel.</summary>
    public string? Location { get; private set; }

    public SubscriptionDialog(string title, string prompt, string example)
    {
        Title = title;
        Width = 385;
        Height = 125;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _add = PushButton("Add", () =>
        {
            Location = _location.Text?.Trim() ?? string.Empty;
            Close();
        });
        _add.IsDefault = true;
        _add.IsEnabled = false;
        _location.TextChanged += (_, _) => _add.IsEnabled = !string.IsNullOrWhiteSpace(_location.Text);

        var cancel = PushButton("Cancel", Close);
        cancel.IsCancel = true;

        _location.Width = 355;
        _location.HorizontalAlignment = HorizontalAlignment.Left;
        _location.VerticalAlignment = VerticalAlignment.Top;
        _location.Margin = new Thickness(24, 23, 0, 0);

        var promptLabel = Label(prompt);
        promptLabel.HorizontalAlignment = HorizontalAlignment.Left;
        promptLabel.VerticalAlignment = VerticalAlignment.Top;
        promptLabel.Margin = new Thickness(6, 5, 0, 0);

        var exampleLabel = Label(example);
        exampleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        exampleLabel.VerticalAlignment = VerticalAlignment.Top;
        exampleLabel.Margin = new Thickness(24, 44, 0, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 7, 8),
            Children = { _add, cancel },
        };

        SystemDialogChrome.Apply(this, new Panel
        {
            Children = { promptLabel, _location, exampleLabel, buttons },
        });

        Opened += (_, _) => _location.Focus();
    }
}
