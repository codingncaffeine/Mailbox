using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Mailbox.App.Views;

/// <summary>
/// Advanced Find for mail: the reference's fields — words and where they look, from, sent to, a
/// time span, and the narrowing ticks — composed into the search grammar and run through the
/// same search the box runs.
/// </summary>
/// <remarks>
/// For people who do not type <c>from:</c> syntax. The dialog owns no search of its own: OK
/// composes one query string (quoting values that carry spaces) and the caller hands it to the
/// shell's search box, so scope, highlighting, and the results list are exactly the search the
/// reader already knows. The address book's own Advanced Find is a different dialog over
/// contact fields; this one is mail's.
/// </remarks>
public static class MailAdvancedFindDialog
{
    /// <summary>Asks, and answers with the composed query — or null for Cancel.</summary>
    public static async Task<string?> AskAsync(Window owner)
    {
        string? composed = null;

        var words = SystemInkKit.Ink(new TextBox { Width = 260 });
        var wordsIn = SystemInkKit.Ink(new ComboBox
        {
            ItemsSource = new[] { "subject field and message body", "subject field only", "message body only" },
            SelectedIndex = 0,
            Width = 260,
        });

        var from = SystemInkKit.Ink(new TextBox { Width = 260 });
        var sentTo = SystemInkKit.Ink(new TextBox { Width = 260 });

        var timeField = SystemInkKit.Ink(new ComboBox
        {
            ItemsSource = new[] { "received", "sent" },
            SelectedIndex = 0,
            Width = 110,
        });
        var timeSpan = SystemInkKit.Ink(new ComboBox
        {
            ItemsSource = new[] { "anytime", "today", "yesterday", "in the last 7 days", "this week", "last week", "this month", "last month" },
            SelectedIndex = 0,
            Width = 146,
        });

        var attachments = SystemInkKit.Ink(new CheckBox { Content = "Only items with attachments" });
        var unread = SystemInkKit.Ink(new CheckBox { Content = "Only unread items" });
        var flagged = SystemInkKit.Ink(new CheckBox { Content = "Only flagged items" });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
            RowDefinitions = new RowDefinitions("Auto,6,Auto,10,Auto,6,Auto,10,Auto"),
        };

        void Row(int row, string label, Control control)
        {
            var text = SystemInkKit.Label(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            text[Grid.RowProperty] = row;
            control[Grid.RowProperty] = row;
            control[Grid.ColumnProperty] = 2;
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        Row(0, "Search for the word(s):", words);
        Row(2, "In:", wordsIn);
        Row(4, "From:", from);
        Row(6, "Sent to:", sentTo);

        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { timeField, timeSpan },
        };
        Row(8, "Time:", timeRow);

        var narrowing = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { attachments, unread, flagged },
        };

        var stack = new StackPanel { Spacing = 0, Children = { grid, narrowing } };

        var window = new Window
        {
            Title = "Advanced Find",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var find = SystemInkKit.Ok(() =>
        {
            composed = Compose(
                words.Text, wordsIn.SelectedIndex, from.Text, sentTo.Text,
                timeField.SelectedIndex, timeSpan.SelectedIndex,
                attachments.IsChecked == true, unread.IsChecked == true, flagged.IsChecked == true);
            window.Close();
        }, "Find Now");

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                SystemInkKit.Buttons(find, SystemInkKit.Cancel(window)),
                stack,
            },
        };
        body.Children[0][DockPanel.DockProperty] = Dock.Bottom;

        SystemDialogChrome.Apply(window, body);

        await window.ShowDialog(owner);
        return composed;
    }

    /// <summary>The span words the search grammar itself parses, by combo position.</summary>
    private static readonly string?[] Spans =
        [null, "today", "yesterday", "last7days", "thisweek", "lastweek", "thismonth", "lastmonth"];

    /// <summary>
    /// One query string in the box's own grammar. Values carrying spaces are quoted; the words
    /// field passes through as typed, so a reader's own quotes and operators keep working.
    /// </summary>
    internal static string Compose(
        string? words, int wordsIn, string? from, string? sentTo,
        int timeField, int timeSpan, bool attachments, bool unread, bool flagged)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(words))
        {
            var typed = words.Trim();
            switch (wordsIn)
            {
                case 1: parts.AddRange(Tokens(typed).Select(t => $"subject:{Quote(t)}")); break;
                case 2: parts.AddRange(Tokens(typed).Select(t => $"body:{Quote(t)}")); break;
                default: parts.Add(typed); break;
            }
        }

        if (!string.IsNullOrWhiteSpace(from)) parts.Add($"from:{Quote(from.Trim())}");
        if (!string.IsNullOrWhiteSpace(sentTo)) parts.Add($"to:{Quote(sentTo.Trim())}");

        if (Spans[Math.Clamp(timeSpan, 0, Spans.Length - 1)] is { } span)
        {
            parts.Add($"{(timeField == 1 ? "sent" : "received")}:{span}");
        }

        if (attachments) parts.Add("hasattachment:yes");
        if (unread) parts.Add("read:no");
        if (flagged) parts.Add("flagged:yes");

        return string.Join(' ', parts);
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value.Trim('"')}\"" : value;

    /// <summary>Whitespace tokens, with a reader's own quoted phrases kept whole.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var inQuotes = false;
        var current = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
