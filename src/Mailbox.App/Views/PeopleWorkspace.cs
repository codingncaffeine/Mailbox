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
    private readonly ContactListView _list = new();
    private readonly StackPanel _bookList = new();
    private readonly StackPanel _card = new() { Margin = new Thickness(24, 20, 24, 20), Spacing = 2 };
    private readonly ScrollViewer _cardScroll;
    private readonly Border _navPane;

    private IReadOnlyList<ContactRow> _rows = [];

    public PeopleWorkspace(ContactBook book, PeopleOptions options)
    {
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

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"Auto,{ListWidth.ToString(CultureInfo.InvariantCulture)},*") };
        grid.Children.Add(_navPane);

        Grid.SetColumn(_list, 1);
        grid.Children.Add(_list);

        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left };
        divider[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");
        Grid.SetColumn(divider, 2);
        grid.Children.Add(divider);

        var pane = new Border { Margin = new Thickness(1, 0, 0, 0) };
        pane[!BackgroundProperty] = new DynamicResourceExtension("reading.background.brush");
        pane.Child = _cardScroll;
        Grid.SetColumn(pane, 2);
        grid.Children.Add(pane);

        Child = grid;

        _list.ContactSelected += (_, row) => Show(row);
        _list.ContactActivated += (_, row) => ContactOpened?.Invoke(this, row);
        _list.EmptySpaceActivated += (_, _) => NewRequested?.Invoke(this, EventArgs.Empty);

        Reload();
    }

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => $"Items: {_rows.Count}";

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    public ContactRow? Selected => _list.Selected;

    /// <summary>The rows on show, for a harness pose that needs to name one.</summary>
    public IReadOnlyList<ContactRow> Rows => _rows;

    public event EventHandler? Changed;

    public event EventHandler<ContactRow>? ContactOpened;

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
            _rows = _book.Rows();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The address book could not be read.", ex);
            _rows = [];
        }

        _list.Order = FileAsOrders.FromIndex(_options.FileAsIndex);
        _list.ShowIndex = _options.ShowIndex;
        _list.Rows = _rows;

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

        _card.Children.Add(Heading(contact));

        if (contact.JobTitle.Length > 0 || contact.Company.Length > 0)
        {
            var where = string.Join(", ", new[] { contact.JobTitle, contact.Company }.Where(p => p.Length > 0));
            _card.Children.Add(Line(where, subtle: true, size: 14));
        }

        _card.Children.Add(Gap());

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

        if (contact.Notes.Length > 0)
        {
            _card.Children.Add(Gap());
            _card.Children.Add(Section("Notes"));
            _card.Children.Add(Line(contact.Notes, subtle: false));
        }
    }

    /// <summary>The head of the card: the photograph, or the initials in its place, and the name.</summary>
    private Control Heading(Contact contact)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 8) };

        var badge = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        badge[!BackgroundProperty] = new DynamicResourceExtension("accent.subtle.brush");

        if (Photograph(contact) is { } photo)
        {
            badge.Child = new Image { Source = photo, Stretch = Stretch.UniformToFill, Width = 56, Height = 56 };
        }
        else
        {
            var initials = new TextBlock
            {
                Text = ContactInitials(contact),
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            initials[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
            badge.Child = initials;
        }

        row.Children.Add(badge);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new TextBlock { Text = contact.Named(), FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        names.Children.Add(title);

        if (contact.NickName.Length > 0) names.Children.Add(Line($"“{contact.NickName}”", subtle: true));
        row.Children.Add(names);

        return row;
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
        var block = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        return block;
    }

    /// <summary>One labelled line of the card: what it is on the left, what it says on the right.</summary>
    private Control Field(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 3, 0, 3) };

        var name = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
        name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        grid.Children.Add(name);

        var text = new SelectableTextBlock { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private Control Line(string text, bool subtle, double size = 14)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(subtle ? "text.secondary.brush" : "text.primary.brush");
        return block;
    }

    private static Control Gap() => new Border { Height = 8 };

    private static T? Resource<T>(string key) where T : struct
        => Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed ? typed : null;
}
