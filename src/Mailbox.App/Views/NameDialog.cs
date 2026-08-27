using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// Asks for one line of text: a name for a feed, a heading, a board.
/// </summary>
/// <remarks>
/// The smallest dialog in the application and the one most often wanted. Renaming the thing you
/// are pointing at is a gesture every list has, and building a window per place that needs one is
/// how a rename ends up being reachable only from a settings page with four group boxes in it.
/// <para>
/// Themed rather than a system dialog: this is opened from the shell's own surfaces — a menu on
/// a pane row — and belongs to the window it came from, unlike the Account Settings family which
/// the reference draws with the desktop's own controls.
/// </para>
/// </remarks>
public sealed class NameDialog : Window
{
    private readonly TextBox _text = new();
    private readonly Button _ok;

    /// <summary>What was typed, or null when the reader cancelled.</summary>
    public string? Result { get; private set; }

    private NameDialog(string title, string question, string existing)
    {
        Title = title;
        Width = 420;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _ok = Push("OK", Commit);
        _ok.IsDefault = true;
        _ok.IsEnabled = existing.Trim().Length > 0;

        var cancel = Push("Cancel", Close);
        cancel.IsCancel = true;

        DialogChrome.Apply(this, Layout(title, question, cancel));

        Opened += (_, _) =>
        {
            _text.Text = existing;

            // Selected, not merely present: a rename starts by typing over what is there, and a
            // caret at the end means the reader has to clear it first.
            _text.SelectAll();
            _text.Focus();
        };
    }

    /// <summary>Asks, and hands back what was typed — or null when nothing was.</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string question, string existing = "")
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new NameDialog(title, question, existing);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private Control Layout(string title, string question, Button cancel)
    {
        var heading = Label(title, bold: true, size: 15);

        var explain = Label(question);
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 12);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _text.MaxLength = 120;
        _text.TextChanged += (_, _) => _ok.IsEnabled = (_text.Text ?? string.Empty).Trim().Length > 0;
        _text.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Enter || !_ok.IsEnabled) return;
            e.Handled = true;
            Commit();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { _ok, cancel },
        };

        return new StackPanel
        {
            Margin = new Thickness(18),
            Children = { heading, explain, _text, buttons },
        };
    }

    private void Commit()
    {
        if ((_text.Text ?? string.Empty).Trim() is not { Length: > 0 } typed) return;

        Result = typed;
        Close();
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80 };
        button.Click += (_, _) => onClick();
        return button;
    }
}
