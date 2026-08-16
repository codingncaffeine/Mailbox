using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Store;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// Settings for one account file, from the Data Files tab: its name, where it is, how big it
/// is, and Compact Now.
/// </summary>
/// <remarks>
/// The reference's data file dialog names the file, shows its path and format, and offers
/// Compact Now and a password. The name here is the account's display name — the file is the
/// account, and its file name follows the address — the format is what it is, and there is no
/// password: the store is not encrypted, and saying so beats a button that pretends. Compact
/// Now runs the store's own compaction and shows what it saved.
/// </remarks>
public sealed class DataFileSettingsDialog : Window
{
    private readonly OpenAccount _account;
    private readonly TextBox _name = Field();
    private readonly TextBlock _size = Label(string.Empty);

    /// <summary>True when the name changed or the file was compacted.</summary>
    public bool Changed { get; private set; }

    public DataFileSettingsDialog(OpenAccount account)
    {
        _account = account;
        Title = "Mailbox Data File";
        Width = 440;
        Height = 236;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var messages = account.Mail.Folders(account.Account.Id).Sum(f => f.Total);
        _name.Text = account.Account.DisplayName.Length > 0 ? account.Account.DisplayName : account.Account.Address;
        _name.Width = 300;
        ShowSize();

        var compact = PushButton("Compact Now", CompactAsync, width: 100);

        var grid = new Grid
        {
            Margin = new Thickness(12, 10, 12, 0),
            ColumnDefinitions = new ColumnDefinitions("80,*"),
            RowDefinitions = new RowDefinitions("26,22,22,22,22,30"),
        };

        void Row(int row, string label, Control value)
        {
            var name = Label(label);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            value.VerticalAlignment = VerticalAlignment.Center;
            value.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        var path = Label(account.Path);
        path.MaxWidth = 330;
        path.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(path, account.Path);

        Row(0, "Name:", _name);
        Row(1, "Filename:", path);
        Row(2, "Format:", Label("Mailbox account file (SQLite, one account)"));
        Row(3, "Messages:", Label($"{messages:N0}"));
        Row(4, "Size:", _size);
        Row(5, string.Empty, compact);

        var ok = PushButton("OK", Save);
        ok.IsDefault = true;
        var cancel = PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 7, 8),
            Children = { ok, cancel },
        };

        SystemDialogChrome.Apply(this, new Panel { Children = { grid, buttons } });
    }

    private void ShowSize() => _size.Text = MailboxCleanupDialog.Size(_account.Bytes);

    private async Task CompactAsync()
    {
        var before = _account.Bytes;
        var after = await Task.Run(() => _account.Store.Compact());
        Changed = true;
        ShowSize();

        var saved = Math.Max(0, before - after);
        await Confirm.TellAsync(this, "Compact Now",
            saved > 0
                ? $"Compacted {Path.GetFileName(_account.Path)}: {MailboxCleanupDialog.Size(saved)} recovered."
                : $"{Path.GetFileName(_account.Path)} was already compact.");
    }

    private void Save()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length > 0 && name != _account.Account.DisplayName)
        {
            _account.Mail.RenameAccount(_account.Account.Id, name);
            Changed = true;
        }
        Close();
    }
}
