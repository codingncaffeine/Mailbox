using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Dav;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The wizard's calendar-and-contacts lane: a CalDAV or CardDAV account, added in the same
/// window an email account is.
/// </summary>
/// <remarks>
/// The engine for these accounts is built and runs on every send/receive; what was missing was
/// any way to make an account for it to talk to — every collection the application could create
/// was local or a read-only subscription. This page is that way in: a server address, a user
/// name and a password, the discovery that already exists, and the chosen collections written
/// with their addresses so the engine picks them up. The credential goes to the keyring under
/// the same purpose the sync loop loads it from; nothing here writes a password anywhere else.
/// <para>
/// One password is filed per user name, because that is how the sync loop asks for it — so
/// adding a second server under the same name replaces the first one's sign-in, and the page
/// says so before it happens rather than after the first server starts refusing.
/// </para>
/// </remarks>
public sealed partial class AccountWizard
{
    private readonly TextBox _davServer = new()
    {
        Classes = { "sysfield" }, PlaceholderText = "https://cloud.example.net", Width = 320,
    };

    private readonly TextBox _davUser = new() { Classes = { "sysfield" }, Width = 320 };
    private readonly TextBox _davPassword = new() { Classes = { "sysfield" }, PasswordChar = '•', Width = 320 };
    private readonly Button _davFind = new() { Content = "Find Collections", IsEnabled = false, Classes = { "sysbutton" } };
    private readonly Button _davAdd = new() { Content = "Add", IsEnabled = false, Classes = { "sysbutton" } };
    private readonly TextBlock _davStatus = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private readonly StackPanel _davChoices = new() { Spacing = 6, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
    private readonly List<(CheckBox Box, DavCollection Collection)> _davFound = [];

    private TextBlock _heading = null!;
    private TextBlock _subheading = null!;
    private Control _mailFields = null!;
    private Control _mailButtons = null!;
    private Control _davSwapRow = null!;
    private Control _davPane = null!;
    private bool _davReplaceArmed;

    /// <summary>How many collections the calendar-and-contacts lane added, for the caller's refresh.</summary>
    public int DavCollectionsAdded { get; private set; }

    /// <summary>The link under the email fields that swaps the page to the other kind of account.</summary>
    private Control DavSwapRow()
    {
        var link = new Button
        {
            Classes = { "syslink" },
            Content = "Add a calendar and contacts account instead (CalDAV or CardDAV)…",
        };
        link.Click += (_, _) => SwapLane(dav: true);

        _davSwapRow = new StackPanel
        {
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Children = { link },
        };
        return _davSwapRow;
    }

    /// <summary>The lane itself, hidden until the link opens it.</summary>
    private Control DavPane()
    {
        _davFind.Click += async (_, _) => await DavFindAsync();
        _davAdd.Click += async (_, _) => await DavAddAsync();
        _davServer.TextChanged += (_, _) => UpdateDavButtons();
        _davUser.TextChanged += (_, _) => UpdateDavButtons();

        var back = new Button { Classes = { "syslink" }, Content = "Add an email account instead…" };
        back.Click += (_, _) => SwapLane(dav: false);

        var cancel = new Button { Content = "Cancel", Classes = { "sysbutton" } };
        cancel.Click += (_, _) => Close();

        Bind(_davStatus, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        _davPane = new StackPanel
        {
            IsVisible = false,
            Spacing = 10,
            Children =
            {
                Labelled("Server address", _davServer),
                Labelled("User name", _davUser),
                Labelled("Password", _davPassword),
                // 110 of caption and 8 of spacing: the button starts where the boxes above start.
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Avalonia.Thickness(118, 0, 0, 0),
                    Children = { _davFind },
                },
                _davStatus,
                _davChoices,
                new StackPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0), Children = { back } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 20, 0, 0),
                    Children = { cancel, _davAdd },
                },
            },
        };
        return _davPane;
    }

    /// <summary>Swaps the page between its two kinds of account, heading and all.</summary>
    private void SwapLane(bool dav)
    {
        _heading.Text = dav ? "Add a calendar and contacts account" : "Add an email account";
        _subheading.Text = dav
            ? "Mailbox will ask the server which calendars and address books it offers."
            : "Mailbox will work out the server settings from your address.";

        _mailFields.IsVisible = !dav;
        _mailButtons.IsVisible = !dav;
        _davSwapRow.IsVisible = !dav;
        _davPane.IsVisible = dav;

        if (dav) _davServer.Focus();
        else _address.Focus();
    }

    private void UpdateDavButtons()
    {
        _davFind.IsEnabled = !string.IsNullOrWhiteSpace(_davServer.Text)
                             && !string.IsNullOrWhiteSpace(_davUser.Text);
        UpdateDavAdd();
    }

    private void UpdateDavAdd()
        => _davAdd.IsEnabled = DavCollectionsAdded > 0 || _davFound.Any(f => f.Box.IsChecked == true);

    /// <summary>A typed address as a URL: people type the host their browser shows them.</summary>
    private static Uri? DavAddress(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var address)
               && address.Scheme is "http" or "https"
            ? address
            : null;
    }

    private async Task DavFindAsync()
    {
        if (DavAddress(_davServer.Text ?? string.Empty) is not { } address)
        {
            _davStatus.Text = "That is not a server address.";
            return;
        }

        var user = (_davUser.Text ?? string.Empty).Trim();
        var password = _davPassword.Text ?? string.Empty;

        _davFind.IsEnabled = false;
        _davStatus.Text = $"Asking {address.Host}…";
        _davChoices.Children.Clear();
        _davFound.Clear();
        _davReplaceArmed = false;
        UpdateDavAdd();

        try
        {
            using var client = new DavClient(new DavCredentials(user, password), PosedDavServer.FromEnvironment());

            // Discovery is allowed to fail every step into the next, so a wrong password would
            // come out of it as "nothing found" — this one request tells the two apart.
            if (await DavDiscovery.RefusesSignInAsync(client, address))
            {
                _davStatus.Text = $"{address.Host} refused that sign-in. Check the user name and password.";
                return;
            }

            var found = await DavDiscovery.FindAsync(client, address);
            if (found.Count == 0)
            {
                _davStatus.Text = $"No calendars or address books were found at {address.Host}.";
                return;
            }

            foreach (var collection in found)
            {
                var label = new TextBlock { Text = Described(collection) };
                Bind(label, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

                var box = new CheckBox { IsChecked = true, Content = label };
                box.IsCheckedChanged += (_, _) => UpdateDavAdd();

                _davChoices.Children.Add(box);
                _davFound.Add((box, collection));
                Log.Info($"Wizard: DAV found {Described(collection)} at {collection.Url}.");
            }

            _davStatus.Text = $"{found.Count} found at {address.Host}. Untick anything you do not want.";
        }
        catch (HttpRequestException ex)
        {
            _davStatus.Text = $"{address.Host} could not be reached: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            _davStatus.Text = $"{address.Host} did not answer.";
        }
        finally
        {
            UpdateDavButtons();
        }
    }

    private async Task DavAddAsync()
    {
        // Once the collections exist the button's job changes, exactly as the email lane's does:
        // pressing it again would file the same calendars twice.
        if (DavCollectionsAdded > 0)
        {
            Close();
            return;
        }

        var chosen = _davFound.Where(f => f.Box.IsChecked == true).Select(f => f.Collection).ToList();
        if (chosen.Count == 0)
        {
            _davStatus.Text = "Nothing is ticked.";
            return;
        }

        var user = (_davUser.Text ?? string.Empty).Trim();
        var password = _davPassword.Text ?? string.Empty;

        // One password per user name is how the sync loop loads credentials, so a second server
        // under the same name replaces the first one's sign-in — said before it happens, and
        // gone through only on a second press.
        if (!_davReplaceArmed && ElsewhereUnder(user, chosen) is { Length: > 0 } elsewhere)
        {
            _davReplaceArmed = true;
            _davStatus.Text = $"“{user}” already signs in to {elsewhere}, and one password is kept "
                              + "per user name — adding these replaces it. Press Add again to go on.";
            return;
        }

        if (password.Length > 0
            && !await App.Secrets.SaveAsync(user, PimSyncService.Purpose, password))
        {
            _davStatus.Text = $"The password could not be filed in the {App.Secrets.Description}, "
                              + "so nothing was added.";
            return;
        }

        var outcome = DavAccountSetup.Add(App.Pim, user, chosen);
        DavCollectionsAdded = outcome.Added.Count;

        foreach (var added in outcome.Added)
        {
            Log.Info($"Wizard: DAV added “{added.DisplayName}” ({added.Kind}) for {user} — {added.DavUrl}.");
        }

        _davStatus.Text = outcome.Said();
        if (DavCollectionsAdded > 0) _davAdd.Content = "Close";
    }

    /// <summary>A host this user name already signs in to that is not the one being added, or empty.</summary>
    private static string ElsewhereUnder(string user, IReadOnlyList<DavCollection> chosen)
    {
        var host = chosen[0].Url.Host;

        return App.Pim.Collections()
            .Where(c => string.Equals(c.Account, user, StringComparison.OrdinalIgnoreCase)
                        && c.DavUrl is { Length: > 0 }
                        && Uri.TryCreate(c.DavUrl, UriKind.Absolute, out var there)
                        && !string.Equals(there.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Uri(c.DavUrl!).Host)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string Described(DavCollection collection)
    {
        var kind = collection.Kind switch
        {
            CollectionKind.Contacts => "Address Book",
            CollectionKind.Tasks => "Task List",
            CollectionKind.Journal => "Journal",
            _ => "Calendar",
        };

        return $"“{collection.DisplayName}” — {kind}{(collection.IsReadOnly ? ", read-only" : string.Empty)}";
    }

    /// <summary>The lane's harness verbs, called from the wizard's one action door.</summary>
    private async Task HarnessDavAsync(string action, string argument)
    {
        switch (action)
        {
            case "dav":
                SwapLane(dav: true);
                Log.Info($"Harness: DAV lane open — heading “{_heading.Text}”.");
                break;

            case "server":
                _davServer.Text = argument;
                break;

            case "user":
                _davUser.Text = argument;
                break;

            case "davpassword":
                _davPassword.Text = argument;
                break;

            case "find":
                await DavFindAsync();
                Log.Info($"Harness: DAV find settled — status “{_davStatus.Text}”, "
                         + $"{_davFound.Count} row(s), add {(_davAdd.IsEnabled ? "on" : "off")}.");
                foreach (var (box, collection) in _davFound)
                {
                    Log.Info($"Harness:   offers {Described(collection)} ticked={box.IsChecked == true}.");
                }

                break;

            case "untick":
                foreach (var (box, collection) in _davFound
                             .Where(f => f.Collection.DisplayName.Contains(argument, StringComparison.OrdinalIgnoreCase)))
                {
                    box.IsChecked = false;
                    Log.Info($"Harness: unticked “{collection.DisplayName}”.");
                }

                break;

            case "davadd":
                await DavAddAsync();
                Log.Info($"Harness: DAV add settled — status “{_davStatus.Text}”, "
                         + $"added {DavCollectionsAdded}.");

                // The store read back, which is the claim: rows with addresses, filed under the
                // account the sync loop will group them by.
                foreach (var collection in App.Pim.Collections().Where(c => c.DavUrl is { Length: > 0 }))
                {
                    Log.Info($"Harness:   store holds “{collection.DisplayName}” ({collection.Kind}) "
                             + $"account “{collection.Account}” readonly={collection.IsReadOnly} — {collection.DavUrl}");
                }

                var user = (_davUser.Text ?? string.Empty).Trim();
                var secret = user.Length > 0
                    ? await App.Secrets.LoadAsync(user, PimSyncService.Purpose)
                    : null;
                Log.Info($"Harness: keyring holds {(secret is null ? "nothing" : "a password")} "
                         + $"for “{user}” under “{PimSyncService.Purpose}”.");
                break;

            default:
                Log.Warn($"Harness: the DAV lane has no action named {action}.");
                break;
        }
    }
}
