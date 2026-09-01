using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Contacts;
using Mailbox.Controls.People;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The People module's workspace: the address books down the left, the contact list with its
/// alphabet index beside them, and the card of whoever is picked.
/// </summary>
/// <remarks>
/// The same three-pane shape the mail module has, which is what the reference's People module is:
/// a navigation pane of address books, a list, and a reading pane that shows one person rather
/// than one message. The list is drawn (<see cref="ContactListView"/>); the card is composed,
/// because it is a dozen pieces of text rather than a thousand, and because its addresses have to
/// be selectable text.
/// </remarks>
public sealed class PeopleWorkspace : Border
{
    /// <summary>The list column's width, measured off the People capture: 288 to 594.</summary>
    private const double ListWidth = 306;

    private readonly ContactBook _book;
    private readonly PeopleOptions _options;
    // The reference's own second line, and it is true here: EmptySpaceActivated is wired to
    // NewContactAsync, so the double click the sentence describes really does make a contact.
    private readonly ContactListView _list = new() { EmptyHint = "Double-click here to create a new Contact." };
    private readonly StackPanel _bookList = new();
    private readonly StackPanel _card = new() { Margin = new Thickness(24, 20, 24, 20), Spacing = 2 };
    private readonly ScrollViewer _cardScroll;
    private readonly Border _navPane;
    private Grid _grid = null!;
    private Border _cardPane = null!;
    private Border _divider = null!;

    private IReadOnlyList<ContactRow> _rows = [];

    public PeopleWorkspace(ContactBook book, PeopleOptions options)
    {
        Avalonia.Automation.AutomationProperties.SetName(_list, "Contact list");
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Margin = Resource<Thickness>("workspace.inset.rightmargin") ?? default;
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        _navPane = BuildNavPane();
        _cardScroll = new ScrollViewer
        {
            Content = _card,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        _grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"Auto,{ListWidth.ToString(CultureInfo.InvariantCulture)},*") };
        var grid = _grid;
        grid.Children.Add(_navPane);

        Grid.SetColumn(_list, 1);
        grid.Children.Add(_list);

        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left };
        divider[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");
        Grid.SetColumn(divider, 2);
        grid.Children.Add(divider);

        // The card's own panel, which is lighter than the mail module's reading pane — measured
        // #F0F0F0 against its #D4D4D4 in Dark Gray, and its own token family since.
        _cardPane = new Border { Margin = new Thickness(1, 0, 0, 0) };
        _cardPane[!BackgroundProperty] = new DynamicResourceExtension("people.card.background.brush");
        _cardPane.Child = _cardScroll;
        Grid.SetColumn(_cardPane, 2);
        grid.Children.Add(_cardPane);

        _divider = divider;

        Child = grid;

        _list.ContactSelected += (_, row) => Show(row);
        _list.ContactActivated += (_, row) => ContactOpened?.Invoke(this, row);
        _list.ContactMenuRequested += (_, row) => ContactMenuRequested?.Invoke(this, row);
        _list.EmptySpaceActivated += (_, _) => NewRequested?.Invoke(this, EventArgs.Empty);

        Reload();
    }

    /// <summary>
    /// Puts the keyboard on the contact list, so the arrow keys reach it without a Tab or a click
    /// first — the rail button that switched to the module keeps the focus otherwise.
    /// </summary>
    public bool FocusSurface() => _list.Focus();

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => Search.Length == 0
        ? $"Items: {_rows.Count}"
        : $"Items: {_list.Rows.Count} of {_rows.Count}";

    /// <summary>
    /// What the Search People box is looking for, or empty for everybody.
    /// </summary>
    /// <remarks>
    /// It matches a name, a company or an address — the three things somebody is looked up by —
    /// and the list is what shows the answer, as the reference's own box narrows its list rather
    /// than opening a second one.
    /// </remarks>
    public string Search
    {
        get;
        set
        {
            var wanted = value?.Trim() ?? string.Empty;
            if (field == wanted) return;
            field = wanted;
            _list.Rows = Filtered();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    } = string.Empty;

    private IReadOnlyList<ContactRow> Filtered()
    {
        if (Search.Length == 0) return _rows;

        return
        [
            .. _rows.Where(r =>
                Has(r.Contact.Named()) || Has(r.Contact.FiledAs(FileAsOrders.FromIndex(_options.FileAsIndex)))
                || Has(r.Contact.Company) || r.Contact.Emails.Any(e => Has(e.Address))
                || r.Contact.Phones.Any(p => Has(p.Number))
                // A collapsed person answers to every member's name, or linking two cards would
                // make one of them unfindable.
                || r.AlsoNamed.Any(Has)),
        ];

        bool Has(string? text) => text is { Length: > 0 } && text.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which of the Current View group's five arrangements the module is showing.
    /// </summary>
    /// <remarks>
    /// The list draws all five: People is the one with a capture, and the other four are what the
    /// reference's own group offers — two grids of cards and two tables.
    /// </remarks>
    public ContactArrangement Arrangement
    {
        get => _list.Arrangement;
        set
        {
            _list.Arrangement = value;

            // The People view is a list beside a card; the other four are the whole window, which
            // is what the reference gives them — a table of numbers in a 306px column would be a
            // table of one column.
            var people = value == ContactArrangement.People;
            _cardPane.IsVisible = people;
            _divider.IsVisible = people;
            // Both columns, not just the list's: a hidden pane in a star column still takes half
            // the window, and the table came out the width it had before.
            _grid.ColumnDefinitions[1].Width = people ? new GridLength(ListWidth) : new GridLength(1, GridUnitType.Star);
            _grid.ColumnDefinitions[2].Width = people ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    public ContactRow? Selected => _list.Selected;

    /// <summary>The rows on show — what the search left, when one is running.</summary>
    public IReadOnlyList<ContactRow> Rows => _list.Rows;

    /// <summary>Everybody in the shown address books, search or no search.</summary>
    public IReadOnlyList<ContactRow> Total => _rows;

    public event EventHandler? Changed;

    public event EventHandler<ContactRow>? ContactOpened;

    /// <summary>A right-click on somebody, which the shell answers with the reference's menu.</summary>
    public event EventHandler<ContactRow>? ContactMenuRequested;

    /// <summary>A double click on the empty list, which the reference invites in so many words.</summary>
    public event EventHandler? NewRequested;

    // ---- The left-hand pane ---------------------------------------------------------------------

    private Border BuildNavPane()
    {
        var pane = new Border { Width = Resource<double>("nav.width.value") ?? 235 };
        pane[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");

        var stack = new StackPanel();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Height = 24,
            Margin = new Thickness(9, 12, 0, 0),
        };

        var chevron = new TextBlock
        {
            Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = Mailbox.Theming.Icons.IconFont.Family,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(chevron);

        var text = new TextBlock { Text = "My Contacts", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(text);
        stack.Children.Add(header);

        _bookList.Margin = new Thickness(5, 4, 4, 0);
        stack.Children.Add(_bookList);

        pane.Child = stack;
        return pane;
    }

    private void RefreshBooks()
    {
        _bookList.Children.Clear();
        var books = _book.AddressBooks();

        foreach (var book in books)
        {
            var row = new Border { Height = 24, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            if (book.IsVisible) row[!BackgroundProperty] = new DynamicResourceExtension("nav.item.selected.brush");

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (books.Count > 1)
            {
                line.Children.Add(new CheckBox
                {
                    IsChecked = book.IsVisible,
                    Margin = new Thickness(22, 0, 0, 0),
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                });
            }

            var name = new TextBlock
            {
                Text = book.DisplayName,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(books.Count > 1 ? 0 : 43, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            line.Children.Add(name);
            row.Child = line;

            var id = book.Id;
            var visible = book.IsVisible;
            row.PointerPressed += (_, _) => ToggleBook(id, !visible);
            _bookList.Children.Add(row);
        }
    }

    private void ToggleBook(long id, bool visible)
    {
        // The last shown address book cannot be hidden — that would leave nothing to look at.
        if (!visible && _book.AddressBooks().Count(b => b.IsVisible) <= 1) return;
        _book.Repository.SetCollectionVisible(id, visible);
        Reload();
    }

    // ---- Reading the store ----------------------------------------------------------------------

    /// <summary>Reads the address books and hands the list what it draws.</summary>
    public void Reload()
    {
        try
        {
            // The reference starts with one address book called Contacts, so a first run has a
            // folder to show rather than an empty navigation pane.
            _book.Default();
            // People() rather than Rows(): linked cards collapse into one person here, and only
            // here — the Address Book and Select Names keep every card, a picker being a picker.
            _rows = _book.People();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The address book could not be read.", ex);
            _rows = [];
        }

        _list.Order = FileAsOrders.FromIndex(_options.FileAsIndex);
        _list.ShowIndex = _options.ShowIndex;
        _list.Rows = Filtered();

        RefreshBooks();

        // Keep the card on whoever was showing, by identity rather than by position: the list
        // has been rebuilt and the row in hand is the one that was thrown away.
        var chosen = _list.Selected is { } previous ? _rows.FirstOrDefault(r => r.Id == previous.Id) : null;
        _list.Selected = chosen;
        Show(chosen);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Picks somebody by the row they are on, which is what a command acts through.</summary>
    public void Select(long id)
    {
        var row = _rows.FirstOrDefault(r => r.Id == id);
        _list.Selected = row;
        Show(row);
    }

    // ---- The card -------------------------------------------------------------------------------

    /// <summary>
    /// The contact card: who somebody is at the top, then every way of reaching them, labelled as
    /// the reference labels them.
    /// </summary>
    private void Show(ContactRow? row)
    {
        _card.Children.Clear();
        if (row is null) return;

        // The whole card rather than the list's own row: the addresses, the notes and the
        // photograph are not in the columns.
        var contact = _book.Full(row.Id) ?? row.Contact;

        // A linked person's card is the person, not the file: the other cards' ways of reaching
        // them are appended, de-duplicated, and the link itself is named further down. Each
        // linked card is loaded whole for the same reason this one is.
        var linkedRows = _book.Linked(row.Id);
        if (linkedRows.Count > 0)
        {
            contact = ContactMerge.Display(
                contact, [.. linkedRows.Select(l => _book.Full(l.Id) ?? l.Contact)]);
        }

        _card.Children.Add(Heading(contact));

        if (contact.JobTitle.Length > 0 || contact.Company.Length > 0)
        {
            var where = string.Join(", ", new[] { contact.JobTitle, contact.Company }.Where(p => p.Length > 0));
            _card.Children.Add(Line(where, subtle: true, size: 14));
        }

        _card.Children.Add(TabStrip());

        if (contact.IsGroup)
        {
            _card.Children.Add(Section("Members"));
            foreach (var member in contact.Members)
            {
                var name = member.Name.Length > 0 ? member.Name : member.Address;
                _card.Children.Add(Field(string.Empty, name.Length > 0 ? name : member.Uid));
            }

            if (contact.Members.Count == 0) _card.Children.Add(Line("Nobody yet.", subtle: true));
            return;
        }

        for (var i = 0; i < contact.Emails.Count; i++)
        {
            // The reference numbers the second and third and leaves the first bare.
            var label = i == 0 ? "E-mail" : $"E-mail {(i + 1).ToString(CultureInfo.CurrentCulture)}";
            _card.Children.Add(Field(label, contact.Emails[i].Address));
        }

        foreach (var phone in contact.Phones)
        {
            _card.Children.Add(Field(PhoneLabel(phone.Kind), phone.Number));
        }

        foreach (var address in contact.Addresses.Where(a => !a.IsEmpty))
        {
            _card.Children.Add(Field(AddressLabel(address.Kind), address.OneLine()));
        }

        foreach (var url in contact.Urls) _card.Children.Add(Field("Web page", url));
        foreach (var im in contact.InstantMessaging) _card.Children.Add(Field("IM", im));

        if (contact.Birthday is { } birthday)
        {
            _card.Children.Add(Field("Birthday", birthday.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)));
        }

        if (contact.Categories.Count > 0) _card.Children.Add(Field("Categories", string.Join(", ", contact.Categories)));

        if (linkedRows.Count > 0)
        {
            _card.Children.Add(Field(
                "Linked contacts", string.Join(", ", linkedRows.Select(l => l.Named()))));
        }

        // The reference's card always has a Notes section, and invites one where there is none:
        // "Add your own notes here" under a pencil, over a rule.
        _card.Children.Add(Gap());
        _card.Children.Add(Section("Notes"));

        if (contact.Notes.Length > 0)
        {
            _card.Children.Add(Line(contact.Notes, subtle: false));
        }
        else
        {
            var invite = new Button
            {
                Classes = { "flat" },
                Padding = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        Glyph("edit"),
                        new TextBlock { Text = "Add your own notes here", VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };

            invite[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
            invite.Click += (_, _) => ContactOpened?.Invoke(this, row);
            _card.Children.Add(invite);
        }

        var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0) };
        rule[!BackgroundProperty] = new DynamicResourceExtension("people.card.rule.brush");
        _card.Children.Add(rule);
    }

    /// <summary>One of the icon font's glyphs, for the card's own small marks.</summary>
    private static Control Glyph(string name)
    {
        var text = new TextBlock
        {
            Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty(name, 16),
            FontFamily = Mailbox.Theming.Icons.IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.subtle.brush");
        return text;
    }

    /// <summary>
    /// The head of the card: the photograph, or the initials in its place, and the name.
    /// </summary>
    /// <remarks>
    /// Measured off the reference's own card: a 72px disc 20 in from the panel's edge and 24 down,
    /// the name at 22px beside it, and the ellipsis under the name that its own card draws. The
    /// disc is the People family's blue with white initials, not the account disc's darker one.
    /// </remarks>
    private Control Heading(Contact contact)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 21, Margin = new Thickness(0, 0, 0, 10) };

        var badge = new Border
        {
            Width = DiscSize,
            Height = DiscSize,
            CornerRadius = new CornerRadius(DiscSize / 2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        badge[!BackgroundProperty] = new DynamicResourceExtension("people.avatar.brush");

        if (_options.ShowPhotographs && Photograph(contact) is { } photo)
        {
            badge.Child = new Image { Source = photo, Stretch = Stretch.UniformToFill, Width = DiscSize, Height = DiscSize };
        }
        else
        {
            var initials = new TextBlock
            {
                Text = ContactInitials(contact),
                FontSize = 26,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            initials[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.avatar.text.brush");
            badge.Child = initials;
        }

        row.Children.Add(badge);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        var title = new TextBlock { Text = contact.Named(), FontSize = 22, TextWrapping = TextWrapping.Wrap };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
        names.Children.Add(title);

        if (contact.NickName.Length > 0) names.Children.Add(Line($"“{contact.NickName}”", subtle: true));

        // The reference draws an ellipsis under the name: everything the card can do that is not
        // one of the buttons above it.
        var more = new Button
        {
            Content = "⋯",
            Classes = { "flat" },
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        more[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
        more.Click += (_, _) => MoreRequested?.Invoke(this, EventArgs.Empty);
        names.Children.Add(more);

        row.Children.Add(names);

        return row;
    }

    /// <summary>Measured: the card's own disc is 72 across.</summary>
    private const double DiscSize = 72;

    /// <summary>The ellipsis under the name, which the shell answers with the card's own menu.</summary>
    public event EventHandler? MoreRequested;

    /// <summary>
    /// The strip under the head: the reference's one tab, its accent line, and the rule across.
    /// </summary>
    /// <remarks>
    /// Measured: the open tab carries a 2px line in the accent and the strip is closed by a 1px
    /// rule the width of the card. One tab, because the reference draws one — the others it shows
    /// for a linked contact wait on linked-contact cards, which are unbuilt.
    /// </remarks>
    private Control TabStrip()
    {
        var label = new TextBlock { Text = "Contact", FontSize = 14, Margin = new Thickness(0, 0, 0, 6) };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");

        var underline = new Border { Height = 2 };
        underline[!BackgroundProperty] = new DynamicResourceExtension("people.card.tab.brush");

        var tab = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Children = { label, underline } };

        var rule = new Border { Height = 1, Margin = new Thickness(0, -1, 0, 10) };
        rule[!BackgroundProperty] = new DynamicResourceExtension("people.card.rule.brush");

        return new StackPanel { Margin = new Thickness(0, 4, 0, 0), Children = { tab, rule } };
    }

    /// <summary>A contact's photograph as a bitmap, or null when there is none to draw.</summary>
    private static Bitmap? Photograph(Contact contact)
    {
        if (contact.Photo?.Bytes is not { Length: > 0 } bytes) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (ArgumentException ex)
        {
            // A card can carry anything in its PHOTO; a picture that will not decode is not a
            // reason to refuse to show the person.
            Log.Warn("A contact's photograph could not be read.", ex);
            return null;
        }
    }

    private static string ContactInitials(Contact contact) => ContactListView.InitialsOf(contact);

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

    private static string AddressLabel(AddressKind kind) => kind switch
    {
        AddressKind.Home => "Home address",
        AddressKind.Other => "Other address",
        _ => "Business address",
    };

    private Control Section(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 6, 0, 4) };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.subtle.brush");
        return block;
    }

    /// <summary>One labelled line of the card: what it is on the left, what it says on the right.</summary>
    private Control Field(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 3, 0, 3) };

        var name = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
        name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.subtle.brush");
        grid.Children.Add(name);

        var text = new SelectableTextBlock { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private Control Line(string text, bool subtle, double size = 14)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(subtle ? "people.card.subtle.brush" : "people.card.text.brush");
        return block;
    }

    private static Control Gap() => new Border { Height = 8 };

    // ---- What a harness run can read -------------------------------------------------------------

    /// <summary>
    /// The card as it is drawn: one line per row, the label and the value it carries.
    /// </summary>
    /// <remarks>
    /// Read off the built controls rather than off the contact, so what comes back is what a
    /// reader is looking at — a field the card silently drops does not appear here either.
    /// </remarks>
    internal IReadOnlyList<string> CardLines()
    {
        var lines = new List<string>();
        foreach (var child in _card.Children) lines.Add(Describe(child));
        return lines;

        static string Describe(Control control) => control switch
        {
            TextBlock text => text.Text ?? string.Empty,
            Grid grid => string.Join(
                "\t",
                grid.Children.Select(c => c switch
                {
                    SelectableTextBlock s => s.Text ?? string.Empty,
                    TextBlock t => t.Text ?? string.Empty,
                    _ => c.GetType().Name,
                })),
            Button button => $"[button] {Flatten(button.Content)}",
            Border { Height: 1 } => "————",
            StackPanel stack => string.Join(" · ", stack.Children.Select(Describe)),
            _ => control.GetType().Name,
        };

        static string Flatten(object? content) => content switch
        {
            string text => text,
            TextBlock text => text.Text ?? string.Empty,
            StackPanel stack => string.Join(
                " ", stack.Children.Select(c => c is TextBlock t ? t.Text ?? string.Empty : string.Empty))
                .Trim(),
            _ => content?.ToString() ?? string.Empty,
        };
    }

    /// <summary>The card's own panel, so a run can press what is drawn on it.</summary>
    internal Control CardHost => _card;

    private static T? Resource<T>(string key) where T : struct
        => Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed ? typed : null;
}
