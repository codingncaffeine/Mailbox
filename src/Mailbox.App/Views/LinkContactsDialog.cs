using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// Manages which cards are the same person: the reference's Linked Contacts, reached from the
/// person's right-click menu. Linked cards show as one combined card in the People module.
/// </summary>
/// <remarks>
/// Every change writes immediately — link and unlink are store operations on both cards, queued
/// to their servers like any edit, so closing the window is not a commit and there is nothing to
/// lose. Groups do not link: a distribution list is not a person, however it is named.
/// </remarks>
public static class LinkContactsDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Opens the manager for one person. True when anything changed.</summary>
    public static async Task<bool> ManageAsync(
        Window owner, ContactBook book, Action<PimItem> queue, ContactRow who)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(who);

        var changed = false;

        var caption = new TextBlock
        {
            Text = $"Linked contacts for “{who.Named()}”. Linked cards are shown together as " +
                   "one person, and each keeps its own address book and its own server.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var linked = new StackPanel { Spacing = 4 };

        var search = new TextBox
        {
            Width = 420,
            PlaceholderText = "Find a contact to link",
            Name = "LinkSearch",
        };

        var results = new StackPanel { Spacing = 4 };

        // Both halves write through the book — into both cards — and queue both to their
        // servers, which ContactBook itself does not do: the book is a library and the queue is
        // the application's.
        void QueueBoth(long id, long otherId)
        {
            if (book.Repository.Item(id) is { } mine) queue(mine);
            if (book.Repository.Item(otherId) is { } theirs) queue(theirs);
        }

        void FillLinked()
        {
            linked.Children.Clear();
            var rows = book.Linked(who.Id);

            if (rows.Count == 0)
            {
                var none = new TextBlock { Text = "No linked contacts yet." };
                Bind(none, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
                linked.Children.Add(none);
                return;
            }

            foreach (var row in rows)
            {
                var name = new TextBlock
                {
                    Text = $"{row.Named()} — {row.CollectionName}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 320,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");

                var unlink = new Button { Content = "Unlink" };
                unlink.Click += (_, _) =>
                {
                    if (book.Unlink(who.Id, row.Id))
                    {
                        QueueBoth(who.Id, row.Id);
                        changed = true;
                        Log.Info($"People: “{row.Named()}” unlinked from “{who.Named()}”.");
                    }

                    FillLinked();
                    FillResults();
                };

                linked.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { name, unlink },
                });
            }
        }

        void FillResults()
        {
            results.Children.Clear();
            var term = (search.Text ?? string.Empty).Trim();
            if (term.Length == 0) return;

            var already = book.Linked(who.Id).Select(r => r.Id).ToHashSet();
            var found = book.Matching(term)
                .Where(r => r.Id != who.Id && !already.Contains(r.Id) && !r.Contact.IsGroup)
                .Take(8)
                .ToList();

            if (found.Count == 0)
            {
                var none = new TextBlock { Text = $"Nobody matches “{term}”." };
                Bind(none, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
                results.Children.Add(none);
                return;
            }

            foreach (var row in found)
            {
                var name = new TextBlock
                {
                    Text = $"{row.Named()} — {row.CollectionName}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 320,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");

                var link = new Button { Content = "Link" };
                link.Click += (_, _) =>
                {
                    if (book.Link(who.Id, row.Id))
                    {
                        QueueBoth(who.Id, row.Id);
                        changed = true;
                        Log.Info($"People: “{row.Named()}” linked to “{who.Named()}”.");
                    }

                    FillLinked();
                    FillResults();
                };

                results.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { name, link },
                });
            }
        }

        search.TextChanged += (_, _) => FillResults();

        var window = new Window
        {
            Title = "Linked Contacts",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var done = new Button
        {
            Content = "Done",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        done.Click += (_, _) => window.Close();

        var heading = new TextBlock { Text = "Linked", FontWeight = FontWeight.SemiBold };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children = { caption, heading, linked, search, results, done },
        };

        DialogChrome.Apply(window, body);
        FillLinked();

        window.Opened += (_, _) => search.Focus();

        // This window writes through its own two buttons and nothing else, and neither could be
        // pressed: the find box has to hold something before a Link button exists at all, and a
        // pose that photographs the window proves only that it draws. So the store could be shown
        // to hold a link — that is what the shell's own link pose does — without anything ever
        // showing that the buttons here are what put it there.
        //
        // MAILBOX_LINK_PRESS=find:Other,link:B. Other,unlink:B. Other. Capture runs only.
        if (Mailbox.App.Theming.WindowCapture.IsRequested
            && Environment.GetEnvironmentVariable("MAILBOX_LINK_PRESS") is { Length: > 0 } steps)
        {
            window.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = PressAsync(steps), Avalonia.Threading.DispatcherPriority.Background);
        }

        await window.ShowDialog(owner);
        return changed;

        async Task PressAsync(string spec)
        {
            foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (verb, argument) = step.Split(':', 2) is [var head, var tail]
                    ? (head.Trim().ToLowerInvariant(), tail.Trim())
                    : (step.Trim().ToLowerInvariant(), string.Empty);

                if (verb == "find")
                {
                    // Through the box, so the search runs the way typing runs it — and then a beat,
                    // because the box's own TextChanged is what rebuilds the results and a step
                    // that read them in the same pass read the list as it was before.
                    search.Text = argument;
                    await Task.Delay(150);
                }
                else if (verb is "link" or "unlink")
                {
                    var from = verb == "link" ? results : linked;
                    if (FindButton(from, argument) is not { } button)
                    {
                        Log.Warn($"Harness: no “{argument}” to {verb} — the list holds "
                                 + $"[{string.Join(" | ", Rows(from))}].");
                        continue;
                    }

                    button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150);
                }
                else
                {
                    Log.Warn($"Harness: the Linked Contacts window has no step '{verb}'.");
                    continue;
                }

                Log.Info($"Harness: linked contacts {step} → linked [{string.Join(" | ", Rows(linked))}], "
                         + $"found [{string.Join(" | ", Rows(results))}], "
                         + $"“{who.Named()}” carries [{string.Join(" | ", book.Full(who.Id)?.Links ?? [])}].");
            }
        }

        static IEnumerable<string> Rows(StackPanel panel)
            => panel.Children.OfType<StackPanel>()
                .Select(row => row.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty);

        static Button? FindButton(StackPanel panel, string wanted)
            => panel.Children.OfType<StackPanel>()
                .Where(row => row.Children.OfType<TextBlock>()
                    .Any(t => t.Text?.Contains(wanted, StringComparison.CurrentCultureIgnoreCase) == true))
                .Select(row => row.Children.OfType<Button>().FirstOrDefault())
                .FirstOrDefault(b => b is not null);
    }
}
