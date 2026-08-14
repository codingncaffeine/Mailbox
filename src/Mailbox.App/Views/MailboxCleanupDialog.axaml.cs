using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// What is taking up room, and the ways to reduce it.
/// </summary>
/// <remarks>
/// The reference leads with a mailbox quota, which only means anything on Exchange. Here the
/// number that matters is the size of the store on disk, so that is what this leads with, with
/// the per-folder breakdown under it.
/// </remarks>
public sealed class MailboxCleanupDialog : Window
{
    private readonly StackPanel _body = new() { Spacing = 6 };

    public MailboxCleanupDialog()
    {
        Title = "Mailbox Cleanup";
        Width = 520;
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
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                heading,
                _body,
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

        Bind(this, BackgroundProperty, "surface.ground.brush");
        Populate();
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
        Bind(name, TextBlock.ForegroundProperty, "text.primary.brush");

        var amount = new TextBlock { Text = value };
        Bind(amount, TextBlock.ForegroundProperty, "text.secondary.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { name, amount },
        };
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
