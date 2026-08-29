using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// What is taking up room, and the ways to reduce it: the reference's Mailbox Cleanup — the
/// sizes, Find items older than / larger than, AutoArchive now, and Empty Deleted Items.
/// </summary>
/// <remarks>
/// The reference leads with a mailbox quota, which only means anything on Exchange. Here the
/// number that matters is the size of the store on disk, so that is what this leads with, with
/// the per-folder breakdown under it. The two Find buttons hand a search to the shell —
/// <c>received:&lt;date</c>, <c>size:&gt;Nkb</c> — across every mailbox, and close.
/// </remarks>
public sealed class MailboxCleanupDialog : Window
{
    private readonly StackPanel _body = new() { Spacing = 6 };
    private readonly NumericUpDown _olderDays = new() { Width = 90, Minimum = 1, Maximum = 3650, Value = 90 };
    private readonly NumericUpDown _largerKb = new() { Width = 90, Minimum = 1, Maximum = 1_000_000, Value = 250 };

    /// <summary>A search the shell should run once the dialog closes — Find items older than / larger than.</summary>
    public string? SearchRequested { get; private set; }

    /// <summary>What a button did, for the status line.</summary>
    public string? Report { get; private set; }

    public MailboxCleanupDialog()
    {
        Title = "Mailbox Cleanup";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var heading = new TextBlock
        {
            Text = "Mailbox size",
            FontWeight = FontWeight.SemiBold,
        };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                heading,
                _body,
                Actions(),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0),
                    Children = { close },
                },
            },
        };

        DialogChrome.Apply(this, body);

        Bind(this, BackgroundProperty, "surface.ground.brush");
        Populate();
    }

    /// <summary>The reference's three rows of actions under the sizes.</summary>
    private Control Actions()
    {
        var older = new Button { Content = "Find…", Width = 80 };
        older.Click += (_, _) =>
        {
            var days = (int)(_olderDays.Value ?? 90);
            SearchRequested = $"received:<{DateTimeOffset.Now.AddDays(-days):yyyy-MM-dd}";
            Close();
        };

        var larger = new Button { Content = "Find…", Width = 80 };
        larger.Click += (_, _) =>
        {
            SearchRequested = $"size:>{(long)(_largerKb.Value ?? 250)}kb";
            Close();
        };

        var archive = new Button { Content = "AutoArchive", Width = 110 };
        archive.Click += async (_, _) =>
        {
            var go = await Confirm.AskAsync(this, "Mailbox Cleanup",
                "AutoArchive will move old items to each account's Archive folder — or delete them, where the settings say so — now.", "AutoArchive", destructive: false);
            if (!go) return;
            var outcome = Archiver.RunAll(App.Accounts.All, App.AutoArchive, DateTimeOffset.Now);
            App.AutoArchive.LastRun = DateTimeOffset.Now;
            Report = "AutoArchive: " + outcome.Summary;
            _body.Children.Clear();
            Populate();
        };

        var empty = new Button { Content = "Empty", Width = 110 };
        empty.Click += async (_, _) =>
        {
            var deleted = App.Accounts.All.Select(a => a.Mail.FolderWithRole(a.Account.Id, FolderRole.Deleted)).Sum(f => f?.Total ?? 0);
            if (deleted == 0) { Report = "Deleted Items is already empty."; return; }
            var go = await Confirm.AskBeforePermanentDeleteAsync(this, "Empty Deleted Items",
                $"Permanently delete {deleted:N0} item{(deleted == 1 ? "" : "s")} from Deleted Items?");
            if (!go) return;
            var count = 0;
            foreach (var account in App.Accounts.All)
            {
                if (account.Mail.FolderWithRole(account.Account.Id, FolderRole.Deleted) is not { } folder) continue;
                var ids = account.Mail.Messages(folder.Id, int.MaxValue).Select(m => m.Id).ToList();
                if (ids.Count > 0) count += account.Mail.DeleteMessages(ids);
            }

            Report = $"Deleted Items emptied: {count:N0} item{(count == 1 ? "" : "s")}.";
            _body.Children.Clear();
            Populate();
        };

        Control Row(string text, params Control[] controls)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            row.Children.Add(label);
            foreach (var control in controls) row.Children.Add(control);
            return row;
        }

        var heading = new TextBlock { Text = "Reduce the size of the mailbox", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                heading,
                Row("Find items older than", _olderDays, Caption("days"), older),
                Row("Find items larger than", _largerKb, Caption("kilobytes"), larger),
                Row("Move old items to the Archive folder by the AutoArchive settings:", archive),
                Row("Permanently delete everything in Deleted Items:", empty),
            },
        };
    }

    private TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private void Populate()
    {
        var accounts = App.Accounts.All;

        if (accounts.Count == 0)
        {
            _body.Children.Add(Line("No accounts yet", string.Empty));
            return;
        }

        _body.Children.Add(Line(
            "All accounts", Size(accounts.Sum(a => a.Bytes)), emphasise: true));

        foreach (var account in accounts)
        {
            _body.Children.Add(Line(
                account.Account.Address, Size(account.Bytes), emphasise: true));

            foreach (var folder in account.Mail.Folders(account.Account.Id)
                         .Where(f => f.Total > 0))
            {
                var bytes = account.Mail.Messages(folder.Id, int.MaxValue).Sum(m => m.SizeBytes);
                _body.Children.Add(Line(
                    $"    {folder.Name}", $"{folder.Total:N0} items, {Size(bytes)}"));
            }
        }
    }

    /// <summary>Bytes as something readable. Mail sizes span kilobytes to gigabytes.</summary>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:N1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:N2} GB",
    };

    private Control Line(string label, string value, bool emphasise = false)
    {
        var name = new TextBlock
        {
            Text = label,
            Width = 300,
            FontWeight = emphasise ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var amount = new TextBlock { Text = value };
        Bind(amount, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { name, amount },
        };
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
