using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using Mailbox.Contacts;
using Mailbox.Core.Commands;
using Mailbox.Editor;
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
    /// <summary>
    /// The note, in the editor the compose window writes messages in.
    /// </summary>
    /// <remarks>
    /// A rich note rather than a box of text, because the reference's is: its Insert and Format
    /// Text tabs act on this field, and a tab of buttons that could not act would be worse than
    /// no tab. What is written is kept twice — the formatting in the card's own extension, the
    /// text in the standard NOTE — so another client still reads the note. See Contact.NotesHtml.
    /// </remarks>
    private readonly ComposeEditor _notes = new()
    {
        [Avalonia.Automation.AutomationProperties.NameProperty] = "Notes",
        AllowRemoteImagesOnPaste = false,
        AllowLocalFileImages = false,
        AutoLinkOnType = true,
    };
    private readonly ListBox _members = new();
    private readonly List<GroupMember> _memberList = [];
    private readonly Border _photo = new();

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
        // The books are checked and not kept. There was a combo here that was built, never
        // filled, never laid out and never read — the form the reference draws has no book
        // picker on it either, and a contact made in the wrong book is moved with Home · Move
        // as it is there. What the argument is for is this check: a window that cannot say
        // where a contact would be kept has nowhere to save it.
        if (books is not { Count: > 0 })
        {
            throw new ArgumentException("A contact needs an address book.", nameof(books));
        }

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

    /// <summary>What a card is called on the form, which is nothing at all when it has no name.</summary>
    private static string Named(Contact contact)
        => contact.Named() is var name && name == Contact.NoName ? string.Empty : name;

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
        // Named() falls back to the words a list row is drawn with when a card says nothing, and a
        // new contact says nothing: the form opened with "(no name)" typed into Full Name and into
        // Display as, and the caption read "(no name) - Contact" where the reference's reads
        // "Untitled - Contact". Empty is what an empty card has to say.
        var named = Named(contact);

        _name.Text = named;
        _company.Text = contact.Company;
        _jobTitle.Text = contact.JobTitle;

        RefreshFileAs();

        _email.Text = contact.PrimaryEmail;
        _displayAs.Text = contact.Emails.Count > 0 && contact.Emails[0].Name is { Length: > 0 } shown
            ? shown
            : named;
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

        // The formatted reading when the card has one, the plain one when it does not — a
        // contact written by another client, or by this one before notes could carry formatting.
        _notes.Clear();
        if (contact.NotesHtml.Length > 0) _notes.LoadHtml(contact.NotesHtml);
        else if (contact.Notes.Length > 0) _notes.InsertText(contact.Notes);
        _memberList.AddRange(contact.Members);
        _members.ItemsSource = _memberList.Select(m => m.Name is { Length: > 0 } n ? $"{n} <{m.Address}>" : m.Address).ToList();

        _name.TextChanged += (_, _) =>
        {
            TitleChanged?.Invoke(this, EventArgs.Empty);
            RefreshFileAs();
        };

        _company.TextChanged += (_, _) => RefreshFileAs();
    }

    /// <summary>
    /// Builds the File as box from the name and the company as they now stand.
    /// </summary>
    /// <remarks>
    /// Every time either changes, not once when the window opens. Built once, the box on a new
    /// contact was built from a card with nothing on it — one offer, the placeholder a nameless
    /// row is drawn with — and every contact this application created was filed under that. They
    /// sorted above the numbers, under the index's own <c>#</c>, whatever they were called.
    /// <para>
    /// Whatever is picked survives the rebuild where it is still on offer, so a decision made and
    /// then a job title corrected is still that decision; where it is not, the card's own filing
    /// wins, and failing that the first offer.
    /// </para>
    /// </remarks>
    private void RefreshFileAs()
    {
        var chosen = _fileAs.SelectedItem as string;
        var choices = FileAsChoices(NameBasis()).ToList();
        _fileAs.ItemsSource = choices;
        if (choices.Count == 0) return;

        var index = chosen is null ? -1 : choices.FindIndex(c => string.Equals(c, chosen, StringComparison.Ordinal));
        if (index < 0) index = choices.FindIndex(c => string.Equals(c, _original.FileAs, StringComparison.Ordinal));
        _fileAs.SelectedIndex = Math.Max(0, index);
    }

    /// <summary>
    /// The File as choices the reference offers, built from the name as typed.
    /// </summary>
    /// <remarks>
    /// Once each: the reference's own list has no repeats, and every card whose stored File As was
    /// already one of the offers — which is most of them — drew that offer twice. The placeholder
    /// a nameless card is drawn with is not an offer at all: it is what a list row says when there
    /// is nothing to say, and filing somebody under it is worse than not filing them.
    /// </remarks>
    private static IEnumerable<string> FileAsChoices(Contact contact)
        => Offers(contact)
            .Where(c => c.Length > 0 && !string.Equals(c, Contact.NoName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> Offers(Contact contact)
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
    /// <summary>
    /// The note's formatting, or nothing at all when there is none worth keeping.
    /// </summary>
    /// <remarks>
    /// An empty note writes an empty property rather than a document's worth of markup around
    /// nothing: a card is a thing other clients read, and every byte in it is one they have to
    /// skip. The same serializer the compose window uses, for the same reason — what comes out
    /// is the narrow HTML §7.3 settled on rather than the editor's own.
    /// </remarks>
    private string NoteHtml()
    {
        if (_notes.GetPlainText().Trim().Length == 0) return string.Empty;

        return EmailHtml.Serialize(_notes.Document ?? new FlowDocument());
    }

    /// <summary>Measured: the card the reference previews, and how far the page stops short.</summary>
    private const double PreviewWidth = 250;
    private const double PreviewHeight = 149;
    private const double PagePad = 13;

    private Control NotesPane()
    {
        // Measured off the reference's own window: the card is 250×149, centred across the pane
        // and 12 down from the top of it — not tucked against the left edge.
        var preview = new Border
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
        };

        preview[!BackgroundProperty] = new DynamicResourceExtension("people.card.background.brush");
        preview[!Border.BorderBrushProperty] = new DynamicResourceExtension("people.card.rule.brush");
        preview.Child = BusinessCard();

        var label = new TextBlock { Text = "Notes", Margin = new Thickness(0, 4, 0, 1), FontSize = 12 };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");

        _editorCommands = new EditorCommands(_notes, Host);

        // The marks on the page, from tokens as the compose body's are.
        _notes[!RichEditor.SelectionBrushProperty] = new DynamicResourceExtension("state.selected.brush");
        _notes[!RichEditor.CaretBrushProperty] = new DynamicResourceExtension("compose.body.text.brush");

        // The page is a border behind the editor rather than the editor's own Background, which is
        // how the compose window does it and why the compose window has a page. Set on the editor,
        // the token never painted: the note sat on the window's own grey in every theme, where the
        // reference draws white from the form's hairline to thirteen short of the frame.
        var page = new Border { Child = _notes };
        page[!BackgroundProperty] = new DynamicResourceExtension("compose.body.background.brush");

        var dock = new DockPanel { Margin = new Thickness(0, 0, PagePad, 0) };
        dock.Children.Add(new StackPanel { [DockPanel.DockProperty] = Dock.Top, Children = { preview, label } });
        dock.Children.Add(page);
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

    /// <summary>Format Text and the document half of Insert, shared with the compose window.</summary>
    private EditorCommands _editorCommands = null!;

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

        // The note is a document, so the two document tabs act on it — the same code the compose
        // window's own Format Text and Insert run.
        if (_editorCommands.Handle(id)) return null;
        if (_editorCommands.HandleInsert(id)) return null;

        if (id == ComposeCommands.Signature.Id) { _ = InsertSignatureAsync(); return null; }

        // Two of the Insert tab's own that a plain document can hold.
        if (id == ComposeCommands.DateAndTime.Id) { _ = InsertDateAsync(); return null; }
        if (id == ComposeCommands.HorizontalLine.Id)
        {
            _notes.InsertHtml("<hr>");
            _notes.Focus();
            return null;
        }

        // The rest of the Insert tab. The reference's contact notes are Rich Text and hold all
        // of it; ours are the card's own, and a card is a thing other clients read: a picture,
        // a shape or an embedded document would have to travel inside it as base64, which is how
        // an address book stops syncing. Each says which, rather than doing nothing.
        // Said rather than returned: this window has no status line, and the caller discards
        // what Invoke hands back. Confirm.TellAsync is what the rest of the application uses to
        // say "this one needs something that is not here".
        if (Absent(id) is { } absent)
        {
            var label = App.Commands.TryGet(id, out var command) ? command.Label : "Insert";
            _ = Confirm.TellAsync(Host(), label, absent);
            return absent;
        }

        // Everything else is the shell's: a message to this person, the address book, the vCard.
        ShellCommandRequested?.Invoke(this, id);
        return null;
    }

    /// <summary>What the Insert tab's other entries would need, in the words the reader needs.</summary>
    private static string? Absent(CommandId id)
    {
        if (id == ComposeCommands.Pictures.Id || id == ComposeCommands.Screenshot.Id)
        {
            return "A picture would have to travel inside the contact card itself, which is what "
                   + "stops an address book syncing. Attach it to a message to this person instead.";
        }

        if (id == ComposeCommands.Shapes.Id || id == ComposeCommands.Icons.Id
            || id == ComposeCommands.Models3D.Id || id == ComposeCommands.WordArt.Id
            || id == ComposeCommands.SmartArt.Id || id == ComposeCommands.Chart.Id)
        {
            return "Drawing tools are not built here — the artwork behind them is somebody else's.";
        }

        if (id == ComposeCommands.TextBox.Id || id == ComposeCommands.DropCap.Id)
        {
            return "The editor lays a note out as text, and has no floating boxes or drop caps.";
        }

        if (id == ComposeCommands.Bookmark.Id)
        {
            return "A bookmark marks a place for a link to jump to, and a note has nothing to "
                   + "link from.";
        }

        if (id == ComposeCommands.InsertBusinessCard.Id)
        {
            return "The card beside the form is this contact's own; inserting somebody else's "
                   + "into the note would put a second card inside this one.";
        }

        if (id == ComposeCommands.AttachItem.Id)
        {
            return "A contact card carries no attachments; a message to this person does.";
        }

        if (id == ComposeCommands.Equation.Id || id == ComposeCommands.Symbol.Id)
        {
            return "Equations and the symbol picker are the compose window's; a note takes "
                   + "whatever the keyboard types.";
        }

        if (id == ComposeCommands.InsertObject.Id)
        {
            return "Embedding a document from another program needs that program; nothing here can.";
        }

        if (id == ComposeCommands.QuickParts.Id)
        {
            return "There are no saved blocks of text yet — Quick Parts arrives with the ones the "
                   + "compose window will keep.";
        }

        if (id == ComposeCommands.AttachFile.Id)
        {
            return "A contact card carries no attachments; a message to this person does.";
        }

        return null;
    }

    /// <summary>
    /// Insert · Signature: the reader's own, dropped into the note as text.
    /// </summary>
    /// <remarks>
    /// The reference offers this on a contact for the same reason it offers it on a message —
    /// the field is a document — and there is nothing here it cannot do: a signature is HTML,
    /// and so is the note.
    /// </remarks>
    private async Task InsertSignatureAsync()
    {
        var choices = App.Signatures.All.Select(sig => new Choice(sig.Name, sig.Name)).ToList();
        if (choices.Count == 0)
        {
            await Confirm.TellAsync(Host(), "Signature",
                "There are no signatures yet. Options · Mail · Signatures makes one.");
            return;
        }

        if (await Chooser.AskAsync(Host(), "Signature", "Insert:", choices) is not { } chosen) return;
        if (App.Signatures.Find(chosen) is not { } signature || signature.IsEmpty) return;

        // The markup where there is any, so a formatted signature stays formatted; the plain
        // reading of the note is produced from the document either way.
        if (signature.Html is { Length: > 0 } html) _notes.InsertHtml(html);
        else _notes.InsertText(signature.Text);

        _notes.Focus();
    }

    /// <summary>Insert · Date &amp; Time: today, or the moment, as the reader chooses.</summary>
    private async Task InsertDateAsync()
    {
        var now = DateTimeOffset.Now;
        var choices = new[]
        {
            new Choice(now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture), "date"),
            new Choice(now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture), "long"),
            new Choice(now.ToString("g", CultureInfo.CurrentCulture), "datetime"),
            new Choice(now.ToString("t", CultureInfo.CurrentCulture), "time"),
        };

        if (await Chooser.AskAsync(Host(), "Date and Time", "Insert:", choices) is not { } chosen) return;

        _notes.InsertText(chosen switch
        {
            "long" => now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture),
            "datetime" => now.ToString("g", CultureInfo.CurrentCulture),
            "time" => now.ToString("t", CultureInfo.CurrentCulture),
            _ => now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
        });

        _notes.Focus();
    }

    /// <summary>The note as text, for a harness run to read back what a command did to it.</summary>
    internal string NoteText => _notes.GetPlainText().Replace("\n", "\u23ce").Trim();

    // ---- What a harness run can pose and read ----------------------------------------------------

    /// <summary>
    /// Types into one of the form's fields, by the name the label gives it.
    /// </summary>
    /// <remarks>
    /// Through the control rather than around it \u2014 the same <c>Text</c> a keystroke sets \u2014 so what
    /// is read back afterwards went through whatever the field does with what is typed. A run that
    /// could not fill the form could only ever prove the fields it was already given.
    /// </remarks>
    /// <returns>False when nothing on the form answers to that name.</returns>
    internal bool PoseField(string field, string value)
    {
        switch (field.Trim().ToLowerInvariant())
        {
            case "name": _name.Text = value; return true;
            case "company": _company.Text = value; return true;
            case "jobtitle": _jobTitle.Text = value; return true;
            case "email": _email.Text = value; return true;
            case "displayas": _displayAs.Text = value; return true;
            case "webpage": _webPage.Text = value; return true;
            case "im": _im.Text = value; return true;
            case "business": _phones[0].Text = value; return true;
            case "home": _phones[1].Text = value; return true;
            case "businessfax": _phones[2].Text = value; return true;
            case "mobile": _phones[3].Text = value; return true;
            case "address": _address.Text = value.Replace("\\n", "\n", StringComparison.Ordinal); return true;
            case "mailing": _mailing.IsChecked = value is "1" or "true"; return true;
            case "note": _notes.Clear(); _notes.InsertText(value); return true;
            case "notehtml": _notes.Clear(); _notes.LoadHtml(value); return true;

            // By the words the box offers rather than by position: the choices are built from the
            // name, so an index means a different thing on every card.
            case "fileas":
                if (_fileAs.ItemsSource?.OfType<string>().ToList() is not { } choices) return false;
                var wanted = choices.FindIndex(c => c.Contains(value, StringComparison.OrdinalIgnoreCase));
                if (wanted < 0) return false;
                _fileAs.SelectedIndex = wanted;
                return true;

            case "photo":
                _picture = value.Length == 0
                    ? null
                    : new ContactPhoto(File.ReadAllBytes(value), MediaTypeOfFile(value));
                RefreshPhoto();
                return true;

            default: return false;
        }
    }

    private static string MediaTypeOfFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>What every field on the form says now, for a run to read back.</summary>
    internal string DescribeForm()
        => $"name=\u201c{_name.Text}\u201d company=\u201c{_company.Text}\u201d jobTitle=\u201c{_jobTitle.Text}\u201d "
           + $"fileAs=\u201c{_fileAs.SelectedItem}\u201d of [{string.Join(" | ", _fileAs.ItemsSource?.OfType<string>() ?? [])}] "
           + $"email=\u201c{_email.Text}\u201d displayAs=\u201c{_displayAs.Text}\u201d webPage=\u201c{_webPage.Text}\u201d im=\u201c{_im.Text}\u201d "
           + $"business=\u201c{_phones[0].Text}\u201d home=\u201c{_phones[1].Text}\u201d businessFax=\u201c{_phones[2].Text}\u201d "
           + $"mobile=\u201c{_phones[3].Text}\u201d address=\u201c{(_address.Text ?? string.Empty).Replace("\n", "\u23ce", StringComparison.Ordinal)}\u201d "
           + $"mailing={_mailing.IsChecked} photo={(_picture is { Bytes.Length: > 0 } p ? $"{p.Bytes!.Length}B {p.MediaType}" : "none")} "
           + $"note=\u201c{NoteText}\u201d";

    /// <summary>
    /// The note as the card would carry it: the plain reading, the document behind it, and the
    /// markup that comes out.
    /// </summary>
    internal string DescribeNote()
        => $"plain=“{NoteText}” document={(_notes.Document is { } doc ? $"{doc.Blocks.Count} block(s)" : "none")} "
           + $"html=“{NoteHtml()}”";

    /// <summary>What the form would save, which is the thing a store read-back has to match.</summary>
    internal string DescribeCurrent()
    {
        var c = Current();
        return $"displayName=\u201c{c.DisplayName}\u201d prefix=\u201c{c.Prefix}\u201d first=\u201c{c.FirstName}\u201d middle=\u201c{c.MiddleName}\u201d "
               + $"last=\u201c{c.LastName}\u201d suffix=\u201c{c.Suffix}\u201d nick=\u201c{c.NickName}\u201d fileAs=\u201c{c.FileAs}\u201d "
               + $"filedAs=\u201c{c.FiledAs()}\u201d company=\u201c{c.Company}\u201d jobTitle=\u201c{c.JobTitle}\u201d "
               + $"emails=[{string.Join(" | ", c.Emails)}] phones=[{string.Join(" | ", c.Phones.Select(p => $"{p.Kind}:{p.Number}"))}] "
               + $"addresses=[{string.Join(" | ", c.Addresses.Select(a => $"{a.Kind}:{a.OneLine()}"))}] "
               + $"urls=[{string.Join(" | ", c.Urls)}] im=[{string.Join(" | ", c.InstantMessaging)}] "
               + $"categories=[{string.Join(" | ", c.Categories)}] birthday={c.Birthday?.ToString("yyyy-MM-dd") ?? "none"} "
               + $"photo={(c.Photo is { Bytes.Length: > 0 } p ? $"{p.Bytes!.Length}B {p.MediaType}" : "none")} "
               + $"private={c.IsPrivate} members={c.Members.Count} note=\u201c{c.Notes.Replace("\n", "\u23ce", StringComparison.Ordinal)}\u201d";
    }

    /// <summary>The window this form is in, for the dialogs its commands open.</summary>
    private Window Host()
        => TopLevel.GetTopLevel(this) as Window
           ?? throw new InvalidOperationException("The contact surface is not hosted in a window.");

    public void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The name and the company as the form states them, split into their parts.
    /// </summary>
    /// <remarks>
    /// What the typed name means is the People page's "Default Full Name order" — the string
    /// itself cannot say whether "Vries Anne" leads with the family name. A card that already
    /// carries its parts keeps them (the form edits the display name, not the analysis); only a
    /// name typed onto a card that has none is parsed.
    /// <para>
    /// Its own method because the File as box needs the same answer on every keystroke, and it
    /// must not pay for serialising the note to get it.
    /// </para>
    /// </remarks>
    private Contact NameBasis()
    {
        var typed = (_name.Text ?? string.Empty).Trim();
        var parsed = Mailbox.Core.People.FullNames.Parse(typed, App.PeopleOptions.FullName);

        return _original with
        {
            DisplayName = typed,
            FirstName = _original.FirstName.Length > 0 || parsed.Last.Length == 0 ? _original.FirstName : parsed.First,
            MiddleName = _original.FirstName.Length > 0 || parsed.Last.Length == 0 ? _original.MiddleName : parsed.Middle,
            LastName = _original.LastName.Length > 0 || parsed.Last.Length == 0 ? _original.LastName : parsed.Last,
            Company = (_company.Text ?? string.Empty).Trim(),
            JobTitle = (_jobTitle.Text ?? string.Empty).Trim(),
        };
    }

    /// <summary>The contact as the form now states it.</summary>
    public Contact Current()
    {
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

        return NameBasis() with
        {
            FileAs = _fileAs.SelectedItem as string ?? _original.FileAs,
            Emails = emails,
            Phones = phones,
            Urls = (_webPage.Text ?? string.Empty).Trim() is { Length: > 0 } url ? [url] : [],
            InstantMessaging = (_im.Text ?? string.Empty).Trim() is { Length: > 0 } im ? [im] : [],
            Notes = _notes.GetPlainText().TrimEnd(),
            NotesHtml = NoteHtml(),
            Photo = _picture,
            Members = _memberList,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    private void Commit(bool deleted)
        => Finished?.Invoke(this, new ContactResult(Current(), Chosen, deleted));
}
