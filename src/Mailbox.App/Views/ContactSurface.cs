using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Contacts;
using Mailbox.Core.Commands;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Contact window's form: everything about a person, in the reference's own arrangement.
/// </summary>
/// <remarks>
/// <b>Measured off the reference's own Contact window.</b> The form is a column 491 wide on the
/// list's dark panel with a hairline down its right edge, and the notes fill the white rest. Its
/// fields are filled boxes 20 tall rather than the appointment form's underlines — the reference
/// draws the two windows differently and this is the one with boxes. The labels that open
/// something are buttons (Full Name…, Email…, the four numbers, the address kind) and the labels
/// that do not are plain words.
/// <para>
/// Host-neutral, as the compose surface is: the window around it owns the frame, the ribbon and
/// the caption, and everything about the contact is here.
/// </para>
/// <para>
/// A group is the same surface with the fields it has no use for taken away and its members in
/// their place, which is what the reference's Contact Group window is.
/// </para>
/// </remarks>
public sealed class ContactSurface : UserControl
{
    /// <summary>Measured: the form's column, and the hairline that closes it.</summary>
    private const double FormWidth = 491;

    /// <summary>Measured: a field box is 20 tall, and the rows are 23 apart.</summary>
    private const double FieldHeight = 20;
    private const double RowPitch = 23;

    /// <summary>Measured: the form's own inset from the panel's edge.</summary>
    private const double FormInset = 29;

    /// <summary>Measured: the photograph's box at the top right of the form column.</summary>
    private const double PhotoSize = 92;

    private readonly Contact _original;
    private readonly IReadOnlyList<Collection> _books;
    private readonly bool _group;

    private readonly TextBox _name = Field();
    private readonly TextBox _company = Field();
    private readonly TextBox _jobTitle = Field();
    private readonly ComboBox _fileAs = new() { Height = FieldHeight + 4 };
    private readonly TextBox _email = Field();
    private readonly TextBox _displayAs = Field();
    private readonly TextBox _webPage = Field();
    private readonly TextBox _im = Field();
    private readonly TextBox[] _phones = [Field(), Field(), Field(), Field()];
    private readonly TextBox _address = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 56,
    };

    private readonly CheckBox _mailing = new()
    {
        Content = new TextBlock { Text = "This is the mailing address", TextWrapping = TextWrapping.Wrap, FontSize = 11 },
    };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _members = new();
    private readonly List<GroupMember> _memberList = [];
    private readonly Border _photo = new();
    private readonly ComboBox _book = new();

    private ContactPhoto? _picture;

    /// <summary>The four numbers the reference's own form offers, in its order.</summary>
    private static readonly (string Label, PhoneKind Kind)[] PhoneRows =
    [
        ("Business…", PhoneKind.Business),
        ("Home…", PhoneKind.Home),
        ("Business Fax…", PhoneKind.BusinessFax),
        ("Mobile…", PhoneKind.Mobile),
    ];

    public ContactSurface(Contact contact, IReadOnlyList<Collection> books, long collectionId)
    {
        _original = contact ?? throw new ArgumentNullException(nameof(contact));
        _books = books is { Count: > 0 } ? books : throw new ArgumentException("A contact needs an address book.", nameof(books));
        _group = contact.IsGroup;
        _picture = contact.Photo;
        Chosen = collectionId;

        Fill(contact);
        Content = BuildRoot();
    }

    /// <summary>Which address book it is being kept in.</summary>
    public long Chosen { get; private set; }

    /// <summary>The window's caption: the name, then what kind of item this is.</summary>
    public string Title => (_name.Text is { Length: > 0 } text ? text : "Untitled")
                           + (_group ? " - Contact Group" : " - Contact");

    public event EventHandler? TitleChanged;

    /// <summary>Save &amp; Close, Delete or Save &amp; New: the window should go.</summary>
    public event EventHandler<ContactResult>? Finished;

    public event EventHandler? Cancelled;

    /// <summary>Save &amp; New: the window closes and another opens behind it.</summary>
    public event EventHandler? AnotherRequested;

    /// <summary>Email, Address Book and the rest, which the window answers with the shell's own.</summary>
    public event EventHandler<CommandId>? ShellCommandRequested;

    /// <summary>What the bar's page buttons chose, so the window can put it under them.</summary>
    public event EventHandler<string>? PageRequested;

    // ---- What the form says ----------------------------------------------------------------------

    private void Fill(Contact contact)
    {
        _name.Text = contact.Named();
        _company.Text = contact.Company;
        _jobTitle.Text = contact.JobTitle;

        _fileAs.ItemsSource = FileAsChoices(contact).ToList();
        _fileAs.SelectedIndex = 0;

        _email.Text = contact.PrimaryEmail;
        _displayAs.Text = contact.Emails.Count > 0 && contact.Emails[0].Name is { Length: > 0 } shown
            ? shown
            : contact.Named();
        _webPage.Text = contact.Urls.FirstOrDefault() ?? string.Empty;
        _im.Text = contact.InstantMessaging.FirstOrDefault() ?? string.Empty;

        for (var i = 0; i < PhoneRows.Length; i++)
        {
            _phones[i].Text = contact.Phones.FirstOrDefault(p => p.Kind == PhoneRows[i].Kind)?.Number ?? string.Empty;
        }

        if (contact.Addresses.FirstOrDefault(a => !a.IsEmpty) is { } address)
        {
            _address.Text = address.OneLine();
        }

        _notes.Text = contact.Notes;
        _memberList.AddRange(contact.Members);
        _members.ItemsSource = _memberList.Select(m => m.Name is { Length: > 0 } n ? $"{n} <{m.Address}>" : m.Address).ToList();

        _name.TextChanged += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The File as choices the reference offers, built from the name as typed.</summary>
    private static IEnumerable<string> FileAsChoices(Contact contact)
    {
        var last = contact.LastName;
        var first = contact.FirstName;
        var company = contact.Company;

        if (last.Length > 0 && first.Length > 0)
        {
            yield return $"{last}, {first}";
            yield return $"{first} {last}";
        }

        if (contact.FiledAs() is { Length: > 0 } filed) yield return filed;
        if (company.Length > 0) yield return company;
        if (contact.Named() is { Length: > 0 } named) yield return named;
    }

    // ---- Layout -------------------------------------------------------------------------------

    private Control BuildRoot()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{FormWidth.ToString(CultureInfo.InvariantCulture)},1,*"),
        };

        var form = new Border();
        form[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");
        form.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _group ? GroupColumn() : PersonColumn(),
        };

        grid.Children.Add(form);

        var rule = new Border();
        rule[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);

        // Built once and placed once: calling the builder twice makes a second host over the same
        // notes box, and a control cannot have two parents.
        var notes = NotesPane();
        Grid.SetColumn(notes, 2);
        grid.Children.Add(notes);
        return grid;
    }

    /// <summary>The right-hand half: the card as it would be sent, and the notes under it.</summary>
    private Control NotesPane()
    {
        var preview = new Border
        {
            Width = 250,
            Height = 130,
            Margin = new Thickness(20, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
        };

        preview[!BackgroundProperty] = new DynamicResourceExtension("people.card.background.brush");
        preview[!Border.BorderBrushProperty] = new DynamicResourceExtension("people.card.rule.brush");
        preview.Child = BusinessCard();

        var label = new TextBlock { Text = "Notes", Margin = new Thickness(20, 10, 0, 4), FontSize = 12 };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");

        _notes.BorderThickness = default;
        _notes[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("compose.body.background.brush");
        _notes[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("compose.body.text.brush");

        var dock = new DockPanel();
        dock.Children.Add(new StackPanel { [DockPanel.DockProperty] = Dock.Top, Children = { preview, label } });
        dock.Children.Add(_notes);
        return dock;
    }

    /// <summary>The card as the reference previews it: the name, the job, and the ways to reach.</summary>
    private Control BusinessCard()
    {
        var lines = new StackPanel { Margin = new Thickness(10, 8, 8, 8), Spacing = 1 };

        void Line(string text, double size, bool strong = false)
        {
            if (text.Length == 0) return;
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal };
            block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
            lines.Children.Add(block);
        }

        Line(_name.Text ?? string.Empty, 14, strong: true);
        Line(_jobTitle.Text ?? string.Empty, 11);
        Line(_company.Text ?? string.Empty, 11);
        Line(_phones[0].Text ?? string.Empty, 11);
        Line(_email.Text ?? string.Empty, 11);
        return lines;
    }

    private Control PersonColumn()
    {
        var stack = new StackPanel { Margin = new Thickness(FormInset, 12, 16, 16), Spacing = 3 };

        // The first block: the name, the company, the job and how it files — with the photograph
        // beside them, as the reference puts it.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var headRows = new StackPanel { Spacing = 3 };
        headRows.Children.Add(Row(ButtonLabel("Full Name…", ContactCommands.CheckNames.Id), _name));
        headRows.Children.Add(Row(WordLabel("Company"), _company));
        headRows.Children.Add(Row(WordLabel("Job title"), _jobTitle));
        headRows.Children.Add(Row(WordLabel("File as"), _fileAs));
        head.Children.Add(headRows);

        Grid.SetColumn(PhotoBox(), 1);
        head.Children.Add(PhotoBox());
        stack.Children.Add(head);

        stack.Children.Add(Section("Internet"));
        stack.Children.Add(Row(ButtonLabel("Email…", ContactCommands.AddressBook.Id), _email));
        stack.Children.Add(Row(WordLabel("Display as"), _displayAs));
        stack.Children.Add(Row(WordLabel("Web page address"), _webPage));
        stack.Children.Add(Row(WordLabel("IM address"), _im));

        stack.Children.Add(Section("Phone numbers"));
        for (var i = 0; i < PhoneRows.Length; i++)
        {
            stack.Children.Add(Row(ButtonLabel(PhoneRows[i].Label, null), _phones[i]));
        }

        stack.Children.Add(Section("Addresses"));
        stack.Children.Add(AddressRow());
        return stack;
    }

    private Control GroupColumn()
    {
        var stack = new StackPanel { Margin = new Thickness(FormInset, 12, 16, 16), Spacing = 6 };
        stack.Children.Add(Row(WordLabel("Name"), _name));
        stack.Children.Add(Section("Members"));

        _members.Height = 240;
        _members[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("list.row.background.brush");
        stack.Children.Add(_members);

        var add = new Button { Content = "Add Members…", Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => ShellCommandRequested?.Invoke(this, ContactCommands.AddressBook.Id);
        stack.Children.Add(add);
        return stack;
    }

    /// <summary>The address block: the kind, the lines, Map It, and the mailing tick.</summary>
    private Control AddressRow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var kind = new StackPanel { Width = 110, Spacing = 4 };
        kind.Children.Add(ButtonLabel("Business…", null));
        _mailing[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
        _mailing.FontSize = 11;
        kind.Children.Add(_mailing);
        grid.Children.Add(kind);

        Box(_address);
        _address.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(_address, 1);
        grid.Children.Add(_address);

        var map = new Button
        {
            Width = 72,
            Height = 56,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty("location", 16),
                        FontFamily = Mailbox.Theming.Icons.IconFont.Family,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock { Text = "Map It", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };

        map.Click += (_, _) => MapRequested?.Invoke(this, _address.Text ?? string.Empty);
        Grid.SetColumn(map, 2);
        grid.Children.Add(map);
        return grid;
    }

    /// <summary>Map It: the address handed to the desktop, which is what a Linux map is.</summary>
    public event EventHandler<string>? MapRequested;

    private Border PhotoBox()
    {
        _photo.Width = PhotoSize;
        _photo.Height = PhotoSize;
        _photo.Margin = new Thickness(10, 0, 0, 0);
        _photo[!BackgroundProperty] = new DynamicResourceExtension("list.row.background.brush");
        RefreshPhoto();
        return _photo;
    }

    /// <summary>The photograph, or the silhouette the reference draws in its place.</summary>
    private void RefreshPhoto()
    {
        if (_picture is { Bytes.Length: > 0 } picture)
        {
            try
            {
                using var stream = new MemoryStream(picture.Bytes);
                _photo.Child = new Image { Source = new Bitmap(stream), Stretch = Stretch.UniformToFill };
                return;
            }
            catch (Exception)
            {
                // A card can carry bytes that are not an image; the silhouette says as much.
            }
        }

        var silhouette = new TextBlock
        {
            Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty("person", 32),
            FontFamily = Mailbox.Theming.Icons.IconFont.Family,
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        silhouette[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("list.row.preview.text.brush");
        _photo.Child = silhouette;
    }

    /// <summary>Sets or clears the photograph, which is what the Picture menu does.</summary>
    public void SetPhoto(ContactPhoto? photo)
    {
        _picture = photo;
        RefreshPhoto();
    }

    public bool HasPhoto => _picture is { Bytes.Length: > 0 };

    // ---- The pieces a row is made of ---------------------------------------------------------

    private static Control Row(Control label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 0, 0, RowPitch - FieldHeight - 3) };
        grid.Children.Add(label);

        if (field is TextBox box) Box(box);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    /// <summary>A label that opens something: the reference draws it as a button.</summary>
    private Control ButtonLabel(string text, CommandId? command)
    {
        var button = new Button
        {
            Content = text,
            Width = 96,
            Height = FieldHeight + 4,
            Padding = new Thickness(4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontSize = 11,
        };

        if (command is { } id) button.Click += (_, _) => ShellCommandRequested?.Invoke(this, id);
        return button;
    }

    private static Control WordLabel(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
        return block;
    }

    private static Control Section(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 8, 0, 4) };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
        return block;
    }

    /// <summary>Measured: a field is a filled box with a hairline round it, 20 tall.</summary>
    private static void Box(TextBox box)
    {
        box.Height = box.AcceptsReturn ? box.Height : FieldHeight;
        box.BorderThickness = new Thickness(1);
        box.Padding = new Thickness(3, 0);
        box.FontSize = 12;
        box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("list.row.background.brush");
        box[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("list.row.read.text.brush");
        box[!TemplatedControl.BorderBrushProperty] = new DynamicResourceExtension("border.strong.brush");
    }

    private static TextBox Field() => new() { VerticalContentAlignment = VerticalAlignment.Center };

    // ---- What the bar presses -------------------------------------------------------------------

    /// <summary>Runs one of the window's own commands. Returns a message when it cannot.</summary>
    public string? Invoke(CommandId id)
    {
        if (id == ContactCommands.SaveAndClose.Id) { Commit(deleted: false); return null; }
        if (id == ContactCommands.Delete.Id) { Commit(deleted: true); return null; }
        if (id == ContactCommands.SaveAndNew.Id)
        {
            Commit(deleted: false);
            AnotherRequested?.Invoke(this, EventArgs.Empty);
            return null;
        }

        if (id == ContactCommands.General.Id || id == ContactCommands.Details.Id
            || id == ContactCommands.Certificates.Id || id == ContactCommands.AllFields.Id)
        {
            PageRequested?.Invoke(this, id.Value);
            return null;
        }

        // Everything else is the shell's: a message to this person, the address book, the vCard.
        ShellCommandRequested?.Invoke(this, id);
        return null;
    }

    public void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>The contact as the form now states it.</summary>
    public Contact Current()
    {
        var typed = (_name.Text ?? string.Empty).Trim();
        var parts = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var emails = new List<ContactEmail>();
        if ((_email.Text ?? string.Empty).Trim() is { Length: > 0 } address)
        {
            emails.Add(new ContactEmail(address, (_displayAs.Text ?? string.Empty).Trim()));
        }

        // The rest of the addresses the card came with are kept: the form shows one and throwing
        // the others away would lose what another client wrote.
        emails.AddRange(_original.Emails.Skip(1));

        var phones = new List<ContactPhone>();
        for (var i = 0; i < PhoneRows.Length; i++)
        {
            if ((_phones[i].Text ?? string.Empty).Trim() is { Length: > 0 } number)
            {
                phones.Add(new ContactPhone(number, PhoneRows[i].Kind));
            }
        }

        return _original with
        {
            DisplayName = typed,
            FirstName = _original.FirstName.Length > 0 || parts.Length < 2 ? _original.FirstName : parts[0],
            LastName = _original.LastName.Length > 0 || parts.Length < 2 ? _original.LastName : parts[^1],
            Company = (_company.Text ?? string.Empty).Trim(),
            JobTitle = (_jobTitle.Text ?? string.Empty).Trim(),
            FileAs = _fileAs.SelectedItem as string ?? _original.FileAs,
            Emails = emails,
            Phones = phones,
            Urls = (_webPage.Text ?? string.Empty).Trim() is { Length: > 0 } url ? [url] : [],
            InstantMessaging = (_im.Text ?? string.Empty).Trim() is { Length: > 0 } im ? [im] : [],
            Notes = _notes.Text ?? string.Empty,
            Photo = _picture,
            Members = _memberList,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    private void Commit(bool deleted)
        => Finished?.Invoke(this, new ContactResult(Current(), Chosen, deleted));
}
