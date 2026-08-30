using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The People lane's doors: filling the contact form, reading what it would save, and reading
/// back what the module is actually showing.
/// </summary>
/// <remarks>
/// <c>MAILBOX_PEEK=contact</c> could open somebody and <c>MAILBOX_CONTACT_RUN</c> could press the
/// window's own commands, which between them prove that Save writes what was already on the card.
/// Nothing could <i>type</i> — so the fields, the File as box, the photograph and the name parse
/// were only ever readable off the code, which is the thing the rules of evidence forbid. These
/// close that:
/// <list type="bullet">
/// <item><description><c>MAILBOX_CONTACT_SET=name=…;address=…;photo=…</c> — types into the form
/// through the same properties a keystroke sets, one field per <c>;</c>.</description></item>
/// <item><description><c>MAILBOX_CONTACT_PROBE=form|current</c> — what every field says, and what
/// the record the form would save says, which is what a store read-back has to match.</description></item>
/// <item><description><c>MAILBOX_PEOPLE_PROBE=rows|card|favourites|search|press:…</c> — the
/// module's list, the card beside it, and what pressing something on that card does.</description></item>
/// <item><description><c>MAILBOX_FAVOURITES=A. Person,B. Other</c> — the short list, posed, so the
/// peek and the To-Do Bar's People section have something to draw.</description></item>
/// <item><description><c>MAILBOX_PEOPLE_MENU=&lt;entry&gt;</c> — presses an entry of the menu a
/// right-click on somebody opens, which is where Add to Favourites lives.</description></item>
/// </list>
/// <para>
/// The contact half runs at <see cref="DispatcherPriority.Loaded"/>, above the Background where
/// <c>MAILBOX_CONTACT_RUN</c> presses Save: a pose that fills a field and a pose that saves have
/// to happen in that order or the read-back measures the card that was already there.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors onto a contact window the shell has just built.</summary>
    internal static void WirePhase7ADoors(ContactWindow window)
    {
        var set = Environment.GetEnvironmentVariable("MAILBOX_CONTACT_SET");
        var probe = Environment.GetEnvironmentVariable("MAILBOX_CONTACT_PROBE");

        // The Check Full Name dialog is modal, so a pose that presses Full Name… has to have
        // said beforehand what the dialog should do: fields to correct, then ok or cancel.
        if (Environment.GetEnvironmentVariable("MAILBOX_CONTACT_NAMECHECK") is { Length: > 0 } check)
        {
            ContactSurface.CheckFullNameDoor = dialog => dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var verb = "ok";
                foreach (var part in check.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var equals = part.IndexOf('=');
                    if (equals > 0) fields[part[..equals]] = part[(equals + 1)..];
                    else verb = part.ToLowerInvariant();
                }

                if (fields.Count > 0)
                {
                    dialog.Pose(
                        fields.GetValueOrDefault("title", string.Empty),
                        fields.GetValueOrDefault("first", string.Empty),
                        fields.GetValueOrDefault("middle", string.Empty),
                        fields.GetValueOrDefault("last", string.Empty),
                        fields.GetValueOrDefault("suffix", string.Empty));
                }

                Log.Info($"Harness: Check Full Name posed ({check}); pressing {verb}.");
                if (verb == "ok") dialog.PressOk();
                else dialog.Close();
            }, DispatcherPriority.Background);
        }

        if (set is null && probe is null) return;

        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    foreach (var pair in (set ?? string.Empty)
                                 .Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var equals = pair.IndexOf('=');
                        if (equals <= 0)
                        {
                            Log.Info($"Harness: “{pair}” is not field=value.");
                            continue;
                        }

                        var field = pair[..equals];
                        var value = pair[(equals + 1)..];
                        Log.Info($"Harness: contact {field}=“{value}” "
                                 + $"{(window.Surface.PoseField(field, value) ? "typed" : "— no such field")}.");
                    }

                    foreach (var what in (probe ?? string.Empty)
                                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // The labels on this form are buttons — the reference draws them as
                        // buttons, and half of them open something. Whether one is wired is only
                        // answerable by pressing it: an unwired button and a working one are the
                        // same picture.
                        if (what.StartsWith("press:", StringComparison.OrdinalIgnoreCase))
                        {
                            PressOnForm(window, what["press:".Length..]);
                            continue;
                        }

                        switch (what.ToLowerInvariant())
                        {
                            case "form": Log.Info($"Harness: contact form — {window.Surface.DescribeForm()}"); break;
                            case "current": Log.Info($"Harness: contact would save — {window.Surface.DescribeCurrent()}"); break;
                            case "title": Log.Info($"Harness: contact caption “{window.Title}”."); break;
                            case "note": Log.Info($"Harness: contact note — {window.Surface.DescribeNote()}"); break;
                            default: Log.Info($"Harness: no contact probe called “{what}”."); break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Logged rather than dropped: a posted action that throws leaves a run with a
                    // plausible capture, no error and nothing to grep.
                    Log.Warn("Harness: a contact door failed.", ex);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>The lane's shell-side doors, hung off the window opening.</summary>
    private void WirePeopleDoors()
    {
        Opened += (_, _) =>
        {
            if (DataContext is not ShellViewModel shell) return;
            ApplyFavouritesPose(shell);
            ApplyPeopleMenuPose(shell);
            ApplyPeopleProbePose(shell);
        };
    }

    /// <summary>
    /// Puts people into the favourites list before anything that draws it runs.
    /// </summary>
    /// <remarks>
    /// In the handler rather than posted from it, and wired before the peeks are: the People peek
    /// and the To-Do Bar's People section are both built inside their own <c>Opened</c> handler,
    /// so anything posted from this one arrives after they have drawn. Posted, it read "0
    /// favourite(s)" and then added two, which is a pose that proves nothing.
    /// </remarks>
    private void ApplyFavouritesPose(ShellViewModel shell)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_FAVOURITES") is not { Length: > 0 } wanted) return;

        foreach (var name in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (App.Contacts.Rows().FirstOrDefault(
                    r => r.Named().Contains(name, StringComparison.OrdinalIgnoreCase)) is not { } row)
            {
                Log.Info($"Harness: nobody called “{name}” to make a favourite.");
                continue;
            }

            App.ContactFavourites.Add(row.Contact.Uid);
            Log.Info($"Harness: “{row.Named()}” ({row.Contact.Uid}) is a favourite.");
        }

        Log.Info($"Harness: the favourites are [{string.Join(" | ", App.ContactFavourites.All)}].");
        RebuildToDoBar(shell);
    }

    /// <summary>
    /// Presses an entry of the menu a right-click on somebody opens.
    /// </summary>
    /// <remarks>
    /// A context menu is a popup, so no capture shows one and no pose could reach an entry of it:
    /// Add to Favourites lives here and nowhere else, which the reference's own peek says in so
    /// many words. Named by any part of the header, so “Favourites” reaches whichever of the two
    /// headings the selection has earned.
    /// </remarks>
    private void ApplyPeopleMenuPose(ShellViewModel shell)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_PEOPLE_MENU") is not { Length: > 0 } wanted) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                var people = EnsurePeople(shell);
                if (people.Selected is not { } row)
                {
                    Log.Info("Harness: nothing is selected; pose MAILBOX_MODULE=people and MAILBOX_SELECT.");
                    return;
                }

                var menu = ContactMenu(shell, row);
                var entries = menu.Items.OfType<MenuItem>().ToList();
                foreach (var entry in entries)
                {
                    Log.Info($"Harness: the contact menu offers “{entry.Header}”{(entry.IsEnabled ? string.Empty : " (greyed)")}.");
                }

                if (entries.FirstOrDefault(
                        e => e.Header?.ToString()?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true) is not { } chosen)
                {
                    Log.Info($"Harness: no contact-menu entry matching “{wanted}”.");
                    return;
                }

                // Raising Click reaches a greyed entry that a pointer cannot, which is a door that
                // lies about what a reader can do. Refused rather than pressed, and said so.
                if (!chosen.IsEnabled)
                {
                    Log.Info($"Harness: the contact menu's “{chosen.Header}” is greyed and was not pressed.");
                    return;
                }

                Log.Info($"Harness: pressing the contact menu's “{chosen.Header}”.");
                chosen.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Log.Info($"Harness: status “{shell.StatusRight}”, windows: {OtherWindows()}");
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Reads the People module back: its rows, the card beside them, and what a press on the card
    /// does.
    /// </summary>
    /// <remarks>
    /// The list is drawn rather than composed, so nothing in it is a control a capture comparison
    /// can settle — what is filed under which letter, and in what order, is only readable here.
    /// </remarks>
    private void ApplyPeopleProbePose(ShellViewModel shell)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_PEOPLE_PROBE") is not { Length: > 0 } probe) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                var people = EnsurePeople(shell);
                var order = FileAsOrders.FromIndex(App.PeopleOptions.FileAsIndex);

                foreach (var what in probe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (what.StartsWith("press:", StringComparison.OrdinalIgnoreCase))
                    {
                        PressOnCard(shell, people, what["press:".Length..]);
                        continue;
                    }

                    switch (what.ToLowerInvariant())
                    {
                        case "rows":
                            Log.Info($"Harness: People is showing {people.Rows.Count} of {people.Total.Count}, "
                                     + $"{people.Arrangement} arrangement, filed {order}.");
                            foreach (var row in people.Rows)
                            {
                                var contact = row.Contact;
                                Log.Info($"Harness: row {row.Id}\t{contact.IndexLetter(order)}\t"
                                         + $"“{contact.FiledAs(order)}”\tnamed “{row.Named()}”\t"
                                         + $"group={contact.IsGroup} private={contact.IsPrivate} "
                                         + $"flagged={contact.IsFlagged} categories=[{string.Join(" | ", contact.Categories)}] "
                                         + $"favourite={App.ContactFavourites.Contains(contact.Uid)}");
                            }

                            break;

                        case "card":
                            Log.Info($"Harness: the card is showing “{people.Selected?.Named() ?? "nobody"}”.");
                            foreach (var line in people.CardLines()) Log.Info($"Harness: card\t{line}");
                            break;

                        case "favourites":
                            Log.Info($"Harness: {App.ContactFavourites.All.Count} favourite(s): "
                                     + $"[{string.Join(" | ", App.ContactFavourites.All)}]");
                            break;

                        case "search":
                            Log.Info($"Harness: People search “{people.Search}” — {people.Rows.Count} of "
                                     + $"{people.Total.Count}, status “{people.Status}”, "
                                     + $"shell scope {shell.ScopeIndex}, shell box “{shell.SearchText}”.");
                            break;

                        case "books":
                            foreach (var book in App.Contacts.AddressBooks())
                            {
                                Log.Info($"Harness: address book {book.Id} “{book.DisplayName}” visible={book.IsVisible}.");
                            }

                            break;

                        default:
                            Log.Info($"Harness: no People probe called “{what}”.");
                            break;
                    }
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Presses a label on the contact form and reports what, if anything, answered.</summary>
    private static void PressOnForm(ContactWindow window, string words)
    {
        var before = window.Surface.DescribeForm();

        if (window.Surface.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => Content(b).Contains(words, StringComparison.OrdinalIgnoreCase)) is not { } button)
        {
            Log.Info($"Harness: nothing on the contact form says “{words}”.");
            return;
        }

        Log.Info($"Harness: pressing “{Content(button)}” on the contact form "
                 + $"({(button.IsEnabled ? "enabled" : "greyed")}).");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var showing = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows.Select(w => $"“{w.Title}”").ToList() ?? [];

        Log.Info($"Harness: after the press — the form is {(window.Surface.DescribeForm() == before ? "unchanged" : "changed")}, "
                 + $"windows: {string.Join(" | ", showing)}");

        static string Content(Button button) => button.Content switch
        {
            string text => text,
            TextBlock text => text.Text ?? string.Empty,
            StackPanel stack => string.Join(
                " ", stack.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)),
            Grid grid => string.Join(
                " ", grid.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)),
            _ => button.Content?.ToString() ?? string.Empty,
        };
    }

    /// <summary>Presses whatever on the card carries the given words, and says what came of it.</summary>
    private void PressOnCard(ShellViewModel shell, PeopleWorkspace people, string words)
    {
        var before = shell.StatusRight;

        var button = people.CardHost.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => Words(b).Contains(words, StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            Log.Info($"Harness: nothing on the card says “{words}”.");
            return;
        }

        Log.Info($"Harness: pressing “{Words(button)}” on the card.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Log.Info($"Harness: after the press — status “{shell.StatusRight}” "
                 + $"({(shell.StatusRight == before ? "unchanged" : "changed")}), windows: {OtherWindows()}");

        static string Words(Button button) => button.Content switch
        {
            string text => text,
            TextBlock text => text.Text ?? string.Empty,
            StackPanel stack => string.Join(
                " ", stack.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)),
            _ => button.Content?.ToString() ?? string.Empty,
        };
    }
}
