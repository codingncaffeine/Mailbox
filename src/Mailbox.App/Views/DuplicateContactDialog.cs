using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;

namespace Mailbox.App.Views;

/// <summary>What the reader decided about a card that looks like somebody already here.</summary>
public enum DuplicateAnswer
{
    /// <summary>Two cards on purpose — a personal and a work card are sometimes both wanted.</summary>
    AddAnyway,

    /// <summary>One person, one card: the chosen existing card takes the new information.</summary>
    Update,

    /// <summary>Back to the open contact, nothing written.</summary>
    Cancel,
}

public sealed record DuplicateChoice(DuplicateAnswer Answer, ContactRow? Existing);

/// <summary>
/// The prompt on saving a new contact who may already be in the address book.
/// </summary>
/// <remarks>
/// It reports and asks rather than merging: two cards for one person are sometimes deliberate,
/// and a matcher confident enough to merge without asking is confident enough to lose an
/// address. Each match states <em>why</em> it is offered — "they share the address …" is
/// answerable, "this looks like a duplicate" is not — which is the finder's own vocabulary
/// (<see cref="DuplicateMatch.Reason"/>).
/// </remarks>
public static class DuplicateContactDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static async Task<DuplicateChoice> AskAsync(
        Window owner, Contact candidate, IReadOnlyList<DuplicateMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(matches);

        var choice = new DuplicateChoice(DuplicateAnswer.Cancel, null);

        var caption = new TextBlock
        {
            Text = $"“{candidate.Named()}” may already be in your address book.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            FontWeight = FontWeight.SemiBold,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        // The matches, strongest first as the finder hands them over, each with its reason. One
        // is selected because Update has to mean somebody in particular.
        var list = new ListBox
        {
            MaxHeight = 180,
            SelectedIndex = 0,
        };

        foreach (var match in matches)
        {
            var line = new StackPanel { Spacing = 1 };

            var name = new TextBlock { Text = match.Row.Named() };
            Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            line.Children.Add(name);

            var why = new TextBlock
            {
                Text = $"{match.Row.CollectionName} — {match.Reason}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Opacity = 0.75,
            };
            Bind(why, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            Bind(why, TextBlock.FontSizeProperty, "type.ui.size.small.value");
            line.Children.Add(why);

            list.Items.Add(new ListBoxItem { Content = line, Tag = match.Row });
        }

        var add = new RadioButton
        {
            GroupName = "duplicate",
            Content = "Add new contact",
            IsChecked = true,
        };
        Bind(add, RadioButton.ForegroundProperty, "dialog.foreground.brush");

        var update = new RadioButton
        {
            GroupName = "duplicate",
            Content = "Update information of selected contact",
        };
        Bind(update, RadioButton.ForegroundProperty, "dialog.foreground.brush");

        var window = new Window
        {
            Title = "Duplicate Contact Detected",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            var selected = (list.SelectedItem as ListBoxItem)?.Tag as ContactRow ?? matches[0].Row;
            choice = update.IsChecked == true
                ? new DuplicateChoice(DuplicateAnswer.Update, selected)
                : new DuplicateChoice(DuplicateAnswer.AddAnyway, null);
            window.Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                list,
                add,
                update,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        DialogChrome.Apply(window, body);

        // What is on offer, before anything is chosen: a prompt that named the wrong person, or
        // offered a match nothing explains, is a different fault from one whose OK is not wired,
        // and only this line tells them apart in a run that answers it.
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            Mailbox.Core.Diagnostics.Log.Info(
                $"Harness: the duplicate prompt is asking about “{candidate.Named()}” with "
                + $"{matches.Count} match(es): "
                + string.Join(" | ", matches.Select(m => $"{m.Row.Named()} ({m.Strength}, {m.Reason})")));

            if (HarnessDuplicate.Next() is { Length: > 0 } posed)
            {
                window.Opened += (_, _) => Answer(posed);
            }
        }

        await window.ShowDialog(owner);
        return choice;

        // The window's own controls, pressed: what is in doubt is whether the radio and the OK
        // beneath it are wired to anything, and setting the answer directly could not tell that
        // from a pair of controls that are drawn and inert.
        void Answer(string posed)
        {
            var (verb, who) = posed.Split(':', 2) is [var head, var tail] ? (head.Trim(), tail.Trim()) : (posed.Trim(), string.Empty);

            if (who.Length > 0)
            {
                var wanted = list.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => (i.Tag as ContactRow)?.Named()
                        .Contains(who, StringComparison.CurrentCultureIgnoreCase) == true);

                if (wanted is null) Mailbox.Core.Diagnostics.Log.Warn($"Harness: no match on the prompt is called “{who}”.");
                else list.SelectedItem = wanted;
            }

            var press = verb.Equals("cancel", StringComparison.OrdinalIgnoreCase) ? cancel : ok;
            if (verb.Equals("update", StringComparison.OrdinalIgnoreCase)) update.IsChecked = true;
            else if (verb.Equals("add", StringComparison.OrdinalIgnoreCase)) add.IsChecked = true;
            else if (!verb.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                Mailbox.Core.Diagnostics.Log.Warn($"Harness: “{verb}” is not add, update or cancel.");
                return;
            }

            var chosen = (list.SelectedItem as ListBoxItem)?.Tag is ContactRow row ? row.Named() : "nothing";
            Mailbox.Core.Diagnostics.Log.Info(
                $"Harness: pressing the duplicate prompt's {(press == cancel ? "Cancel" : "OK")} "
                + $"with “{chosen}” selected.");

            press.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }
    }
}
