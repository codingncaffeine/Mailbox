using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Contacts;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// New Entry: what kind of entry, and which book it goes in.
/// </summary>
/// <remarks>
/// Transcribed from <c>new contact/new entry option.png</c>: the two types in a list with the
/// first one selected, the book under it, and OK and Cancel. A system dialog like the window it
/// opens from — the capture shows both light while the application around them is Dark Gray.
/// <para>
/// Two types rather than the reference's longer list: the others it offers reach a directory
/// service, and there is none here.
/// </para>
/// </remarks>
public sealed class NewEntryDialog : Window
{
    /// <summary>What OK chose: a person or a group, and the book to put them in.</summary>
    public (bool Group, Collection Book)? Result { get; private set; }

    public NewEntryDialog(IReadOnlyList<Collection> books, int selectedBook = 0)
    {
        ArgumentNullException.ThrowIfNull(books);

        Title = "New Entry";
        Width = 500;
        Height = 330;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var types = new ListBox
        {
            ItemsSource = new[] { "New Contact", "New Contact Group" },
            SelectedIndex = 0,
            Height = 150,
        };
        SystemDialogKit.Bind(types, BackgroundProperty, "systemdialog.list.background.brush");
        SystemDialogKit.Bind(types, BorderBrushProperty, "systemdialog.list.border.brush");
        types.BorderThickness = new Thickness(1);

        var book = new ComboBox
        {
            ItemsSource = books
                .Select(c => c.Account is { Length: > 0 } account ? $"{c.DisplayName} - {account}" : c.DisplayName)
                .ToList(),
            SelectedIndex = books.Count == 0 ? -1 : Math.Clamp(selectedBook, 0, books.Count - 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var ok = SystemDialogKit.PushButton("OK", () =>
        {
            if (book.SelectedIndex < 0) return;

            Result = (types.SelectedIndex == 1, books[book.SelectedIndex]);
            Close();
        });
        ok.IsDefault = true;

        var cancel = SystemDialogKit.PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var body = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Top,
            Spacing = 6,
            Children = { SystemDialogKit.Label("Select the entry type:"), types },
        };
        body.Children.Add(top);

        var bottom = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                SystemDialogKit.Label("Put this entry in:"),
                book,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { ok, cancel },
                },
            },
        };
        body.Children.Add(bottom);

        SystemDialogChrome.Apply(this, body);
    }
}

/// <summary>
/// Advanced Find: the fields a reader can narrow the Address Book by.
/// </summary>
/// <remarks>
/// No capture of this one — the link is in the Address Book capture and the window behind it is
/// not — so it asks for the four things this application knows about a contact and stops there,
/// rather than reproducing a directory search whose fields nothing here could answer.
/// </remarks>
public sealed class AdvancedFindDialog : Window
{
    public AdvancedFind? Result { get; private set; }

    public AdvancedFindDialog(AdvancedFind? current = null)
    {
        Title = "Find";
        Width = 420;
        Height = 260;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = SystemDialogKit.Field();
        var company = SystemDialogKit.Field();
        var address = SystemDialogKit.Field();
        var title = SystemDialogKit.Field();

        if (current is { } filled)
        {
            name.Text = filled.Name;
            company.Text = filled.Company;
            address.Text = filled.Address;
            title.Text = filled.JobTitle;
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        void Row(int row, string label, Control field)
        {
            var text = SystemDialogKit.Label(label);
            text.Margin = new Thickness(0, 0, 10, 8);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(text, row);
            grid.Children.Add(text);

            field.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        Row(0, "Name:", name);
        Row(1, "Company:", company);
        Row(2, "E-mail address:", address);
        Row(3, "Job title:", title);

        var ok = SystemDialogKit.PushButton("OK", () =>
        {
            Result = new AdvancedFind(
                (name.Text ?? string.Empty).Trim(),
                (company.Text ?? string.Empty).Trim(),
                (address.Text ?? string.Empty).Trim(),
                (title.Text ?? string.Empty).Trim());
            Close();
        });
        ok.IsDefault = true;

        var clear = SystemDialogKit.PushButton("Clear", () =>
        {
            name.Text = string.Empty;
            company.Text = string.Empty;
            address.Text = string.Empty;
            title.Text = string.Empty;
        });

        var cancel = SystemDialogKit.PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var body = new DockPanel { Margin = new Thickness(14) };
        body.Children.Add(new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { clear, ok, cancel },
        });
        body.Children.Add(grid);

        SystemDialogChrome.Apply(this, body);
    }
}
