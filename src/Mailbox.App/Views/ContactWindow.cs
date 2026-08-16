using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Core.Commands;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>What a contact window came back with.</summary>
/// <param name="Deleted">True when the reader deleted it rather than saving it.</param>
public sealed record ContactResult(Contact Contact, long CollectionId, bool Deleted);

/// <summary>
/// The Contact window: one person or one group, in a window of its own.
/// </summary>
/// <remarks>
/// <b>No capture exists for this window</b> — the reference's People module in the one screenshot
/// there is holds nobody, so nothing was open to photograph. The form is authored from the
/// reference's own contact card in the order it puts the fields: the name at the top, then the
/// company and the job, then the three e-mail addresses, the four numbers and the addresses,
/// with the notes underneath. A capture would settle the spacing.
/// <para>
/// A group is the same window with the fields it does not have taken away and a list of members
/// in their place, which is what the reference's Contact Group window is.
/// </para>
/// </remarks>
public sealed class ContactWindow : Window
{
    private readonly Contact _original;
    private readonly IReadOnlyList<Collection> _books;
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly ListBox _members = new();
    private readonly ComboBox _book = new();
    private readonly List<GroupMember> _memberList = [];

    public ContactWindow(CommandCatalog commands, Contact contact, IReadOnlyList<Collection> books, long collectionId)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _original = contact ?? throw new ArgumentNullException(nameof(contact));
        _books = books is { Count: > 0 } ? books : throw new ArgumentException("A contact needs an address book.", nameof(books));
        _memberList.AddRange(contact.Members);
        Chosen = collectionId;

        Title = contact.IsGroup ? "Contact Group" : "Contact";
        Width = 700;
        Height = contact.IsGroup ? 520 : 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new DockPanel { Margin = new Thickness(18) };
        body.Children.Add(Buttons());
        body.Children.Add(new ScrollViewer
        {
            Content = contact.IsGroup ? GroupForm() : PersonForm(),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    /// <summary>What the window came back with, or null when it was cancelled.</summary>
    public ContactResult? Result { get; private set; }

    // ---- The form -------------------------------------------------------------------------------

    private Control PersonForm()
    {
        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(Section("Name"));
        stack.Children.Add(Row(
            ("First", "first", _original.FirstName, 150),
            ("Middle", "middle", _original.MiddleName, 110),
            ("Last", "last", _original.LastName, 180)));
        stack.Children.Add(Field("Display as", "display", _original.DisplayName));
        stack.Children.Add(Field("File as", "fileas", _original.FileAs, "Last, First"));

        stack.Children.Add(Section("Organization"));
        stack.Children.Add(Field("Company", "company", _original.Company));
        stack.Children.Add(Field("Department", "department", _original.Department));
        stack.Children.Add(Field("Job title", "jobtitle", _original.JobTitle));

        stack.Children.Add(Section("Internet"));
        for (var i = 0; i < 3; i++)
        {
            var label = i == 0 ? "E-mail" : $"E-mail {(i + 1).ToString(CultureInfo.CurrentCulture)}";
            stack.Children.Add(Field(label, "email" + i.ToString(CultureInfo.InvariantCulture), i < _original.Emails.Count ? _original.Emails[i].Address : string.Empty));
        }

        stack.Children.Add(Field("Web page", "url", _original.Urls.FirstOrDefault() ?? string.Empty));
        stack.Children.Add(Field("IM address", "im", _original.InstantMessaging.FirstOrDefault() ?? string.Empty));

        stack.Children.Add(Section("Phone numbers"));
        foreach (var kind in (PhoneKind[])[PhoneKind.Business, PhoneKind.Home, PhoneKind.Mobile, PhoneKind.BusinessFax])
        {
            stack.Children.Add(Field(
                PhoneLabel(kind),
                "phone" + kind.ToString(),
                _original.Phones.FirstOrDefault(p => p.Kind == kind)?.Number ?? string.Empty));
        }

        stack.Children.Add(Section("Addresses"));
        var business = _original.Addresses.FirstOrDefault(a => a.Kind == AddressKind.Business) ?? new ContactAddress();
        stack.Children.Add(Field("Street", "street", business.Street));
        stack.Children.Add(Row(
            ("City", "city", business.City, 180),
            ("State", "state", business.State, 110),
            ("Postal code", "postal", business.PostalCode, 120)));
        stack.Children.Add(Field("Country", "country", business.Country));

        stack.Children.Add(Section("Notes"));
        var notes = new TextBox
        {
            Text = _original.Notes,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
            Margin = new Thickness(0, 2, 0, 8),
        };
        _fields["notes"] = notes;
        stack.Children.Add(notes);

        stack.Children.Add(BookRow());
        return stack;
    }

    private Control GroupForm()
    {
        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(Section("Name"));
        stack.Children.Add(Field("Group name", "display", _original.DisplayName));

        stack.Children.Add(Section("Members"));
        _members.Height = 220;
        _members.Margin = new Thickness(0, 2, 0, 6);
        RefreshMembers();
        stack.Children.Add(_members);

        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var entry = new TextBox { Width = 320, PlaceholderText = "Name <someone@example.com>" };
        _fields["member"] = entry;
        add.Children.Add(entry);

        var addButton = new Button { Content = "Add", Width = 84 };
        addButton.Click += (_, _) =>
        {
            var member = GroupMembers.Parse(entry.Text);
            if (member.IsEmpty) return;
            _memberList.Add(member);
            entry.Text = string.Empty;
            RefreshMembers();
        };
        add.Children.Add(addButton);

        var removeButton = new Button { Content = "Remove", Width = 84 };
        removeButton.Click += (_, _) =>
        {
            if (_members.SelectedIndex < 0 || _members.SelectedIndex >= _memberList.Count) return;
            _memberList.RemoveAt(_members.SelectedIndex);
            RefreshMembers();
        };
        add.Children.Add(removeButton);
        stack.Children.Add(add);

        stack.Children.Add(BookRow());
        return stack;
    }

    private void RefreshMembers()
        => _members.ItemsSource = _memberList
            .Select(m => m.Name is { Length: > 0 } name && m.Address is { Length: > 0 }
                ? $"{name} <{m.Address}>"
                : m.Address is { Length: > 0 } address ? address : m.Uid)
            .ToList();

    /// <summary>Which address book it goes in — the reference's own "Folder" line.</summary>
    private Control BookRow()
    {
        _book.ItemsSource = _books.Select(b => b.DisplayName).ToList();
        _book.SelectedIndex = Math.Max(0, _books.ToList().FindIndex(b => b.Id == Chosen));
        _book.MinWidth = 220;
        return Labelled("Address book", _book);
    }

    /// <summary>The address book it was opened in, which the picker starts on.</summary>
    private long Chosen { get; init; }

    // ---- The buttons ----------------------------------------------------------------------------

    private Control Buttons()
    {
        var save = new Button { Content = "Save & Close", Width = 110, IsDefault = true };
        save.Click += (_, _) =>
        {
            Result = new ContactResult(Read(), BookId(), Deleted: false);
            Close();
        };

        var delete = new Button { Content = "Delete", Width = 84 };
        delete.Click += (_, _) =>
        {
            Result = new ContactResult(_original, BookId(), Deleted: true);
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        return new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { save, delete, cancel },
        };
    }

    private long BookId()
        => _book.SelectedIndex >= 0 && _book.SelectedIndex < _books.Count ? _books[_book.SelectedIndex].Id : _books[0].Id;

    /// <summary>
    /// What the form says, as a contact. Everything the window does not show is carried over from
    /// the one that was opened — a card holds more than any form shows, and editing a phone number
    /// must not throw away the rest of somebody's vCard.
    /// </summary>
    internal Contact Read()
    {
        var emails = new List<ContactEmail>();
        for (var i = 0; i < 3; i++)
        {
            if (Text("email" + i.ToString(CultureInfo.InvariantCulture)) is { Length: > 0 } typed) emails.Add(new ContactEmail(typed));
        }

        var phones = new List<ContactPhone>();
        foreach (var kind in (PhoneKind[])[PhoneKind.Business, PhoneKind.Home, PhoneKind.Mobile, PhoneKind.BusinessFax])
        {
            if (Text("phone" + kind.ToString()) is { Length: > 0 } number) phones.Add(new ContactPhone(number, kind));
        }

        var address = new ContactAddress
        {
            Kind = AddressKind.Business,
            Street = Text("street"),
            City = Text("city"),
            State = Text("state"),
            PostalCode = Text("postal"),
            Country = Text("country"),
        };

        // The addresses the form does not show are kept as they were.
        var addresses = new List<ContactAddress>();
        if (!address.IsEmpty) addresses.Add(address);
        addresses.AddRange(_original.Addresses.Where(a => a.Kind != AddressKind.Business && !a.IsEmpty));

        var urls = Text("url") is { Length: > 0 } url ? new List<string> { url } : [.. _original.Urls.Skip(1)];
        var ims = Text("im") is { Length: > 0 } im ? new List<string> { im } : [.. _original.InstantMessaging.Skip(1)];

        return _original with
        {
            DisplayName = Text("display"),
            FirstName = Text("first"),
            MiddleName = Text("middle"),
            LastName = Text("last"),
            FileAs = Text("fileas"),
            Company = Text("company"),
            Department = Text("department"),
            JobTitle = Text("jobtitle"),
            Emails = _original.IsGroup ? _original.Emails : emails,
            Phones = _original.IsGroup ? _original.Phones : phones,
            Addresses = _original.IsGroup ? _original.Addresses : addresses,
            Urls = urls,
            InstantMessaging = ims,
            Notes = Text("notes"),
            Members = _original.IsGroup ? [.. _memberList] : _original.Members,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    private string Text(string key) => _fields.TryGetValue(key, out var box) ? box.Text?.Trim() ?? string.Empty : string.Empty;

    // ---- Furniture ------------------------------------------------------------------------------

    private static string PhoneLabel(PhoneKind kind) => kind switch
    {
        PhoneKind.Home => "Home",
        PhoneKind.Mobile => "Mobile",
        PhoneKind.BusinessFax => "Business fax",
        PhoneKind.HomeFax => "Home fax",
        PhoneKind.Pager => "Pager",
        PhoneKind.Other => "Other",
        _ => "Business",
    };

    private Control Section(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 12, 0, 4),
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private Control Field(string label, string key, string value, string? watermark = null)
    {
        var box = new TextBox { Text = value, PlaceholderText = watermark, MinWidth = 320 };
        _fields[key] = box;
        return Labelled(label, box);
    }

    private Control Row(params (string Label, string Key, string Value, double Width)[] cells)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (label, key, value, width) in cells)
        {
            var stack = new StackPanel { Spacing = 2 };
            var caption = new TextBlock { Text = label, FontSize = 12 };
            Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            stack.Children.Add(caption);

            var box = new TextBox { Text = value, Width = width };
            _fields[key] = box;
            stack.Children.Add(box);
            row.Children.Add(stack);
        }

        return row;
    }

    private Control Labelled(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), Margin = new Thickness(0, 2, 0, 2) };
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        grid.Children.Add(caption);

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
