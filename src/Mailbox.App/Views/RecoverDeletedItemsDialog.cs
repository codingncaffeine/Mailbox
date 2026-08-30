using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Recover Deleted Items: what was permanently deleted within the retention window, with
/// Restore Selected Items and Purge Selected Items — the reference's dialog, over the holding
/// area in the account's own store rather than a server's.
/// </summary>
public sealed class RecoverDeletedItemsDialog : Window
{
    private readonly ListBox _list = new()
    {
        Height = 300,
        SelectionMode = SelectionMode.Multiple,
        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Disabled,
    };
    private readonly ComboBox _account = new() { MinWidth = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private OpenAccount? _current;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public RecoverDeletedItemsDialog(string? address = null)
    {
        Title = "Recover Deleted Items";
        Width = 700;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var accounts = App.Accounts.All;
        _current = (address is { Length: > 0 } ? App.Accounts.Find(address) : null) ?? App.Accounts.Default;

        _account.ItemsSource = accounts.Select(a => a.Account.Address).ToList();
        _account.SelectedIndex = _current is null ? -1 : accounts.ToList().FindIndex(a => a.Account.Address == _current.Account.Address);
        _account.SelectionChanged += (_, _) =>
        {
            if (_account.SelectedIndex >= 0 && _account.SelectedIndex < accounts.Count)
            {
                _current = accounts[_account.SelectedIndex];
                Reload();
            }
        };

        _list.ItemTemplate = new FuncDataTemplate<RecoverableMessage>((item, _) => item is null ? new Control() : Row(item));
        Bind(_list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(_list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        Bind(_status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        DialogChrome.Apply(this, Layout());
        Reload();
    }

    private static Control Row(RecoverableMessage item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,*,120,140"),
            Margin = new Thickness(6, 3),
        };

        TextBlock Cell(string text, int column, bool subtle = false)
        {
            var block = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            // Inside the box the ink is the box's, whether or not the cell is played down —
            // the ground's subtle ink is invisible on a light box in Dark Gray.
            Bind(block, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            if (subtle) block.Opacity = 0.7;
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
            return block;
        }

        Cell(item.DisplayFrom, 0);
        Cell(item.Subject.Length > 0 ? item.Subject : "(no subject)", 1);
        Cell(item.OriginalFolderName, 2, subtle: true);
        Cell(item.Deleted.ToLocalTime().ToString("g"), 3, subtle: true);
        return grid;
    }

    private Control Layout()
    {
        var heading = new TextBlock
        {
            Text = "Items that were permanently deleted can be recovered until the retention window closes.",
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var forAccount = new TextBlock { Text = "Account:", VerticalAlignment = VerticalAlignment.Center };
        Bind(forAccount, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,120,140"), Margin = new Thickness(6, 0) };
        foreach (var (title, column) in new[] { ("From", 0), ("Subject", 1), ("Original Folder", 2), ("Deleted On", 3) })
        {
            var block = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold };
            Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            Grid.SetColumn(block, column);
            header.Children.Add(block);
        }

        var selectAll = new Button { Content = "Select All" };
        selectAll.Click += (_, _) => _list.SelectAll();

        var restore = new Button { Content = "Restore Selected Items" };
        restore.Click += (_, _) => Restore();

        var purge = new Button { Content = "Purge Selected Items" };
        purge.Click += async (_, _) => await PurgeAsync();

        var close = new Button { Content = "Close", Width = 74, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { selectAll, restore, purge },
        };

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { close },
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        heading,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { forAccount, _account } },
                        header,
                        _list,
                        buttons,
                        _status,
                    },
                },
            },
        };
    }

    private void Reload()
    {
        var items = _current?.Mail.Recoverable() ?? [];
        _list.ItemsSource = items;
        _status.Text = items.Count switch
        {
            0 => $"Nothing to recover. Permanently deleted items are kept for {App.MailOptions.RecoverDays} days.",
            1 => $"1 item can be recovered for up to {App.MailOptions.RecoverDays} days after it was deleted.",
            _ => $"{items.Count} items can be recovered for up to {App.MailOptions.RecoverDays} days after they were deleted.",
        };
    }

    private IReadOnlyList<long> SelectedIds =>
        [.. (_list.SelectedItems ?? new List<object>()).OfType<RecoverableMessage>().Select(r => r.Id)];

    private void Restore()
    {
        if (_current is null) return;
        var ids = SelectedIds;
        if (ids.Count == 0) { _status.Text = "Select the items to restore."; return; }

        var fallback = _current.Mail.FolderWithRole(_current.Account.Id, FolderRole.Deleted)
                       ?? _current.Mail.FolderWithRole(_current.Account.Id, FolderRole.Inbox);
        if (fallback is null) return;

        var restored = _current.Mail.Restore(ids, fallback.Id);
        Reload();
        _status.Text = $"{restored} item{(restored == 1 ? "" : "s")} restored to where {(restored == 1 ? "it was" : "they were")}.";
    }

    private async Task PurgeAsync()
    {
        if (_current is null) return;
        var ids = SelectedIds;
        if (ids.Count == 0) { _status.Text = "Select the items to purge."; return; }

        var go = await Confirm.AskAsync(this, "Purge Selected Items",
            $"Permanently delete {ids.Count} item{(ids.Count == 1 ? "" : "s")}? Once purged, {(ids.Count == 1 ? "it" : "they")} cannot be recovered.",
            "Purge");
        if (!go) return;

        var purged = _current.Mail.Purge(ids);
        Reload();
        _status.Text = $"{purged} item{(purged == 1 ? "" : "s")} purged.";
    }
}
