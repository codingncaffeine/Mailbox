using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>Which line a picked contact goes on.</summary>
public enum AddressLine
{
    To,
    Cc,
    Bcc,
}

/// <summary>What the Address Book was closed with: who, and on which line.</summary>
public sealed record AddressBookResult(IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc)
{
    public bool IsEmpty => To.Count + Cc.Count + Bcc.Count == 0;
}

/// <summary>
/// The Address Book: File, Edit and Tools over a search row and a report of who is in the book.
/// </summary>
/// <remarks>
/// <b>A system dialog</b>, by the same instruction Account Settings is one under: the reference
/// draws this window with the desktop's own controls, so it is the same light grey in every
/// theme — the capture was taken in Dark Gray and the window in it is light. Built with
/// <see cref="SystemDialogChrome"/> and the <c>systemdialog.*</c> tokens, and transcribed from
/// <c>new contact/file  new entry.png</c> and <c>new entry option.png</c>: a menu bar of three,
/// then <c>Search:</c> with its two radios over a box and its Go and Clear buttons, then
/// <c>Address Book:</c> over the book picker with Advanced Find beside it, then the list.
/// <para>
/// Two windows in one, as the reference has it. Opened from the ribbon it is a place to look
/// people up, with the menus acting on whoever is selected. Opened from a compose window's To…
/// it grows the three recipient lines and an OK, and the menus still work — a reader addressing
/// a message can make a contact without leaving the window they are addressing it from.
/// </para>
/// </remarks>
public sealed class AddressBookDialog : Window
{
    private readonly ContactBook _book;
    private readonly bool _picking;

    private readonly ClassicListView _list = new();
    private readonly TextBox _search = SystemDialogKit.Field();
    private readonly ComboBox _books = new() { MinWidth = 224 };
    private readonly RadioButton _allColumns = new() { Content = "All columns", IsChecked = true, GroupName = "abscope" };
    private readonly RadioButton _nameOnly = new() { Content = "Name only", GroupName = "abscope" };
    private readonly Dictionary<AddressLine, TextBox> _lines = [];

    /// <summary>The picking half's own buttons, kept so a posed run can press the real ones.</summary>
    private readonly Dictionary<AddressLine, Button> _lineButtons = [];
    private Button? _ok;
    private Button? _cancel;

    private readonly MenuItem _addToContacts = Entry("Add to Contacts");
    private readonly MenuItem _delete = Entry("Delete", "Ctrl+D");
    private readonly MenuItem _properties = Entry("Properties");
    private readonly MenuItem _newMessage = Entry("New Message", "Ctrl+N");
    private readonly MenuItem _options = Entry("Options...");

    private IReadOnlyList<Collection> _collections = [];
    private IReadOnlyList<ContactRow> _rows = [];

    /// <summary>An advanced find, or null while the list is showing everything.</summary>
    private AdvancedFind? _find;

    public AddressBookDialog(ContactBook book, bool picking = true)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _picking = picking;

        Title = picking ? "Select Names: Contacts" : "Address Book: Contacts";
        Width = 690;
        Height = picking ? 560 : 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new DockPanel();
        body.Children.Add(MenuBar());
        body.Children.Add(SearchRow());
        if (picking) body.Children.Add(Lines());
        body.Children.Add(ListBox());

        // A window rather than a dialog: the capture shows minimise, maximise and close on it.
        SystemDialogChrome.Apply(this, body, minimizable: true, iconName: "address-book");

        FillBooks();
        Refresh();
        UpdateMenus();
    }

    /// <summary>Who was picked, or null when the window was cancelled.</summary>
    public AddressBookResult? Result { get; private set; }

    /// <summary>
    /// Asks the shell for a new message — the window cannot compose on its own.
    /// </summary>
    /// <remarks>
    /// A blank one, from whichever account the reader sends as by default, and not addressed to
    /// whoever happens to be selected: that is what the reference's File · New Message does, and
    /// it is why the capture shows it black while Delete and Properties beside it are greyed.
    /// </remarks>
    public event EventHandler? NewMessageRequested;

    /// <summary>Asks the shell to open Account Settings on its Address Books tab (Tools · Options).</summary>
    public event EventHandler? OptionsRequested;

    // ---- The menu bar --------------------------------------------------------------------

    private Control MenuBar()
    {
        var file = new MenuItem { Header = "File", Classes = { "sysmenu" } };
        var newEntry = Entry("New Entry...");
        newEntry.Click += async (_, _) => await NewEntryAsync();
        _newMessage.Click += (_, _) => WriteTo();
        _addToContacts.Click += (_, _) => { };
        _delete.Click += async (_, _) => await DeleteAsync();
        _properties.Click += async (_, _) => await OpenAsync();

        var close = Entry("Close", "Alt+F4");
        close.Click += (_, _) => Close();

        file.Items.Add(newEntry);
        file.Items.Add(_newMessage);
        file.Items.Add(new Separator());
        file.Items.Add(_addToContacts);
        file.Items.Add(new Separator());
        file.Items.Add(_delete);
        file.Items.Add(_properties);
        file.Items.Add(new Separator());
        file.Items.Add(close);

        // Add to Contacts copies a directory's entry into a contacts folder, and every entry
        // this window can show is already in one. Greyed for the same reason the capture's is,
        // and it says why rather than looking broken.
        _addToContacts.IsEnabled = false;
        ToolTip.SetTip(_addToContacts, "Everything here is already a contact; this copies an entry from a directory into a contacts folder.");

        // Edit: no capture of this menu, so it carries what means anything over a list of
        // addresses and nothing invented beyond that.
        var edit = new MenuItem { Header = "Edit", Classes = { "sysmenu" } };
        var copy = Entry("Copy Address", "Ctrl+C");
        copy.Click += async (_, _) => await CopyAddressAsync();
        var selectAll = Entry("Select All", "Ctrl+A");
        selectAll.Click += (_, _) => _list.FocusList();
        edit.Items.Add(copy);
        edit.Items.Add(selectAll);

        var tools = new MenuItem { Header = "Tools", Classes = { "sysmenu" } };
        var find = Entry("Find...", "Ctrl+F");
        find.Click += (_, _) => _search.Focus();
        var advanced = Entry("Advanced Find...");
        advanced.Click += async (_, _) => await AdvancedFindAsync();
        _options.Click += (_, _) => { OptionsRequested?.Invoke(this, EventArgs.Empty); Close(); };
        tools.Items.Add(find);
        tools.Items.Add(advanced);
        tools.Items.Add(new Separator());
        tools.Items.Add(_options);

        var menu = new Menu
        {
            [DockPanel.DockProperty] = Dock.Top,
            Height = 22,
            Margin = new Thickness(4, 1, 0, 0),
            Items = { file, edit, tools },
        };

        return menu;
    }

    private static MenuItem Entry(string header, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Classes = { "sysmenu" } };
        if (gesture is { Length: > 0 }) item.InputGesture = KeyGesture.Parse(gesture);
        return item;
    }

    /// <summary>Greys what cannot act on nothing, as the capture's File menu is greyed.</summary>
    private void UpdateMenus()
    {
        // New Message is not one of them: it opens a blank message from the default account,
        // so it means the same thing with nothing selected — which is how the capture draws it.
        // Properties is not either: the reference draws it black over an empty list, and
        // pressing it with nothing chosen simply answers nothing, which greying also said.
        var chosen = Selected() is not null;
        _delete.IsEnabled = chosen;
    }

    // ---- The search row ------------------------------------------------------------------

    private Control SearchRow()
    {
        var grid = new Grid
        {
            [DockPanel.DockProperty] = Dock.Top,
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(10, 8, 10, 8),
        };

        void At(Control control, int column, int row, Thickness? margin = null)
        {
            Grid.SetColumn(control, column);
            Grid.SetRow(control, row);
            if (margin is { } m) control.Margin = m;
            grid.Children.Add(control);
        }

        At(SystemDialogKit.Label("Search:", bold: true), 0, 0, new Thickness(0, 0, 8, 4));
        At(Ink(_allColumns), 1, 0, new Thickness(0, 0, 14, 4));
        At(Ink(_nameOnly), 2, 0, new Thickness(0, 0, 0, 4));
        At(SystemDialogKit.Label("Address Book:", bold: true), 4, 0, new Thickness(18, 0, 0, 4));

        _allColumns.IsCheckedChanged += (_, _) => Refresh();
        _nameOnly.IsCheckedChanged += (_, _) => Refresh();

        // The box and its two buttons: Go runs the search the box holds, and the cross empties
        // it — which is what the reference's pair beside the box do.
        _search.Width = 250;
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) Refresh(); };

        var go = GlyphButton("▶", "Search", Refresh);
        var clear = GlyphButton("✕", "Clear the search", () =>
        {
            _search.Text = string.Empty;
            _find = null;
            Refresh();
        });

        var typed = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { _search, go, clear },
        };

        Grid.SetColumnSpan(typed, 3);
        At(typed, 0, 1);

        _books.SelectionChanged += (_, _) => Refresh();
        At(_books, 4, 1, new Thickness(18, 0, 0, 0));

        var advanced = new Button
        {
            Content = "Advanced Find",
            Classes = { "syslink" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        advanced.Click += async (_, _) => await AdvancedFindAsync();
        At(advanced, 5, 1, new Thickness(12, 0, 0, 0));

        return grid;
    }

    private Button GlyphButton(string glyph, string tip, Action click)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SystemDialogKit.Bind(text, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var button = new Button
        {
            Content = text,
            Classes = { "systool" },
            Width = 24,
            Height = 23,
            Padding = default,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static T Ink<T>(T control) where T : Avalonia.Controls.Primitives.TemplatedControl
    {
        SystemDialogKit.Bind(control, ForegroundProperty, "systemdialog.foreground.brush");
        return control;
    }

    // ---- The list ------------------------------------------------------------------------

    private Control ListBox()
    {
        _list.Columns =
        [
            new ClassicColumn("Name", 220),
            new ClassicColumn("Display Name", 220),
            new ClassicColumn("E-mail Address", 220),
        ];

        _list.SelectionChanged += (_, _) => UpdateMenus();
        _list.ItemActivated += async (_, _) =>
        {
            if (_picking) Add(AddressLine.To);
            else await OpenAsync();
        };

        _list.Margin = new Thickness(10, 0, 10, 10);
        return _list;
    }

    /// <summary>The books to choose between, newest state each time the window opens.</summary>
    private void FillBooks()
    {
        _collections = _book.AddressBooks();

        // Named as the reference names them: the book, then the account it belongs to.
        _books.ItemsSource = _collections
            .Select(c => c.Account is { Length: > 0 } account ? $"{c.DisplayName} - {account}" : c.DisplayName)
            .ToList();

        if (_collections.Count > 0) _books.SelectedIndex = 0;
    }

    /// <summary>The book on show, or null when the account has none.</summary>
    private Collection? Book()
        => _books.SelectedIndex >= 0 && _books.SelectedIndex < _collections.Count
            ? _collections[_books.SelectedIndex]
            : null;

    private ContactRow? Selected()
        => _list.SelectedRow?.Tag is long id ? _rows.FirstOrDefault(r => r.Id == id) : null;

    /// <summary>Re-reads the book, filtered by whatever the search row is asking for.</summary>
    private void Refresh()
    {
        var book = Book();
        var rows = book is null ? _book.Rows() : _book.Rows([book.Id]);

        if (_find is { } find) rows = [.. rows.Where(find.Matches)];
        else if (_search.Text is { Length: > 0 } typed) rows = [.. rows.Where(row => Matches(row, typed))];

        _rows = rows;
        _list.SetRows([.. rows.Select(row => new ClassicRow(
            [row.Named(), DisplayName(row), row.Contact.PrimaryEmail ?? string.Empty], Tag: row.Id))]);

        UpdateMenus();
    }

    /// <summary>
    /// Whether a row answers what was typed: every column, or the name alone.
    /// </summary>
    /// <remarks>
    /// The two radios are the reference's own, and they mean what they say — "Name only" leaves
    /// out the address, which is the difference between finding somebody by who they are and
    /// finding them by where their mail goes.
    /// </remarks>
    private bool Matches(ContactRow row, string typed)
    {
        if (row.Named().Contains(typed, StringComparison.CurrentCultureIgnoreCase)) return true;
        if (_nameOnly.IsChecked == true) return false;

        return DisplayName(row).Contains(typed, StringComparison.CurrentCultureIgnoreCase)
               || (row.Contact.PrimaryEmail ?? string.Empty).Contains(typed, StringComparison.OrdinalIgnoreCase)
               || (row.Contact.Company ?? string.Empty).Contains(typed, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string DisplayName(ContactRow row)
        => row.Contact.IsGroup
            ? row.Named()
            : row.Contact.PrimaryEmail is { Length: > 0 } address ? $"{row.Named()} ({address})" : row.Named();

    // ---- What the menus do ----------------------------------------------------------------

    /// <summary>File · New Entry…: the type, the book, and then the window for it.</summary>
    private async Task NewEntryAsync()
    {
        if (_collections.Count == 0) return;

        var dialog = new NewEntryDialog(_collections, Math.Max(0, _books.SelectedIndex));
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;

        var draft = new Contact { Uid = Contact.NewUid(), IsGroup = chosen.Group };
        var window = new ContactWindow(App.Commands, draft, _collections, chosen.Book.Id);
        await window.ShowDialog(this);

        if (window.Result is not { Deleted: false } result) return;

        var written = _book.Save(result.Contact, result.CollectionId);
        App.PimSync.QueuePut(written);
        Log.Info($"Address Book: contact {written.Id} added.");
        Log.Debug($"Address Book: contact {written.Id} is {result.Contact.Named()}.");

        FillBooks();
        Refresh();
    }

    /// <summary>File · Properties, and a double-click: the card, open for editing.</summary>
    private async Task OpenAsync()
    {
        if (Selected() is not { } row) return;
        if (_book.Repository.Item(row.Id) is not { } stored) { Refresh(); return; }

        var contact = _book.Full(row.Id) ?? row.Contact;
        var window = new ContactWindow(App.Commands, contact, _collections, row.CollectionId);
        await window.ShowDialog(this);

        if (window.Result is not { } result) return;

        if (result.Deleted)
        {
            App.PimSync.Remove(stored);
            Log.Info($"Address Book: contact {row.Id} deleted from its own window.");
        }
        else
        {
            var written = _book.Save(result.Contact, result.CollectionId, stored);
            App.PimSync.QueuePut(written);
        }

        Refresh();
    }

    /// <summary>File · Delete, and Ctrl+D: with a question first, because it is not undoable here.</summary>
    private async Task DeleteAsync()
    {
        if (Selected() is not { } row) return;
        if (_book.Repository.Item(row.Id) is not { } stored) { Refresh(); return; }

        var go = await Confirm.AskAsync(this, "Delete",
            $"Delete “{row.Named()}” from {row.CollectionName}?", "Delete");
        if (!go) return;

        App.PimSync.Remove(stored);
        Log.Info($"Address Book: contact {row.Id} deleted.");
        Refresh();
    }

    /// <summary>File · New Message: the shell writes it, and this window steps out of the way.</summary>
    private void WriteTo()
    {
        NewMessageRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>Edit · Copy Address: the selected entry's address, on the clipboard.</summary>
    private async Task CopyAddressAsync()
    {
        if (Selected() is not { } row) return;
        if (row.Contact.PrimaryEmail is not { Length: > 0 } address) return;
        if (Clipboard is not { } clipboard) return;

        await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(
            clipboard, Avalonia.Input.DataFormat.Text, address);
    }

    /// <summary>Advanced Find: the fields the reference's own asks for, and the list answers.</summary>
    private async Task AdvancedFindAsync()
    {
        var dialog = new AdvancedFindDialog(_find);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } found) return;

        _find = found.IsEmpty ? null : found;
        if (_find is not null) _search.Text = string.Empty;
        Refresh();
    }

    // ---- Picking (the compose window's To…) -------------------------------------------------

    private Control Lines()
    {
        var panel = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Spacing = 6,
            Margin = new Thickness(10, 4, 10, 10),
        };

        foreach (var line in (AddressLine[])[AddressLine.To, AddressLine.Cc, AddressLine.Bcc])
        {
            panel.Children.Add(Line(line));
        }

        var ok = SystemDialogKit.PushButton("OK", () =>
        {
            Result = new AddressBookResult(Split(AddressLine.To), Split(AddressLine.Cc), Split(AddressLine.Bcc));
            Close();
        });
        ok.IsDefault = true;
        _ok = ok;

        var cancel = SystemDialogKit.PushButton("Cancel", Close);
        cancel.IsCancel = true;
        _cancel = cancel;

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { ok, cancel },
        });

        return panel;
    }

    private Control Line(AddressLine line)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("94,*") };

        var button = SystemDialogKit.PushButton($"{line} ->", () => Add(line), 88);
        _lineButtons[line] = button;
        row.Children.Add(button);

        var box = SystemDialogKit.Field();
        box.AcceptsReturn = false;
        _lines[line] = box;
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    /// <summary>Puts whoever is selected on a line, a group resolved to its members.</summary>
    private void Add(AddressLine line)
    {
        if (!_picking || !_lines.TryGetValue(line, out var box)) return;
        if (Selected() is not { } row) return;

        var additions = new List<string>();
        foreach (var suggestion in ContactSuggestions.For(_book, row.Named(), limit: 8))
        {
            if (suggestion.DisplayName != row.Named()) continue;

            additions.Add(suggestion.Insert);
            break;
        }

        if (additions.Count == 0 && row.Contact.PrimaryEmail is { Length: > 0 } address) additions.Add(address);
        if (additions.Count == 0) return;

        var already = box.Text is { Length: > 0 } text ? text.TrimEnd().TrimEnd(';') + "; " : string.Empty;
        box.Text = already + string.Join("; ", additions);
    }

    private IReadOnlyList<string> Split(AddressLine line)
        => _lines.TryGetValue(line, out var box) && box.Text is { Length: > 0 } text
            ? [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    // ---- The harness ----------------------------------------------------------------------

    /// <summary>
    /// Presses this window's own controls, for a run that has to prove they act.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_ADDRESSBOOK=select:1,properties</c> and the like. A menu cannot be clicked by
    /// a capture and a modal inside a modal cannot be photographed, so what each entry did is
    /// read out of the log instead — the same answer the row menu's harness gives.
    /// </remarks>
    internal async Task HarnessAsync(string actions)
    {
        foreach (var raw in actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (action, argument) = raw.Split(':', 2) is [var a, var b] ? (a, b) : (raw, string.Empty);

            switch (action.ToLowerInvariant())
            {
                case "select":
                    _list.SelectedIndex = int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "book":
                    _books.SelectedIndex = int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "search":
                    _search.Text = argument;
                    Refresh();
                    break;
                case "nameonly": _nameOnly.IsChecked = true; break;
                case "allcolumns": _allColumns.IsChecked = true; break;
                case "clear":
                    _search.Text = string.Empty;
                    _find = null;
                    Refresh();
                    break;
                // The four that open a window of their own are started rather than awaited: the
                // window they open is modal over this one, so awaiting would park the harness
                // until a person closed it. Started, looked at, and closed again.
                case "newentry": await OpensAsync(NewEntryAsync); break;
                case "properties": await OpensAsync(OpenAsync); break;
                case "delete": await OpensAsync(DeleteAsync); break;
                case "advanced": await OpensAsync(AdvancedFindAsync); break;
                // Through the entries themselves: each does two things — raise the event the shell
                // is listening for, and close this window so the shell can act on it — and calling
                // only the first left the window up, so the shell's own half never ran at all.
                case "newmessage": _newMessage.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); break;
                case "copy": await CopyAddressAsync(); break;
                case "options": _options.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); break;
                case "close": Close(); break;

                // The picking half. Pressed rather than called: what is in doubt is whether the
                // three "To ->" buttons and the OK beneath them are wired to anything, and calling
                // the handler they are supposed to be wired to cannot answer that.
                case "to": Push(_lineButtons.GetValueOrDefault(AddressLine.To)); break;
                case "cc": Push(_lineButtons.GetValueOrDefault(AddressLine.Cc)); break;
                case "bcc": Push(_lineButtons.GetValueOrDefault(AddressLine.Bcc)); break;
                case "ok": Push(_ok); break;
                case "cancel": Push(_cancel); break;
                default: Log.Warn($"Harness: the Address Book has no action '{action}'."); continue;
            }

            Log.Info($"Harness: address book {raw} → {_rows.Count} row(s), "
                     + $"selected \u201c{Selected()?.Named() ?? "nothing"}\u201d, "
                     + $"book \u201c{Book()?.DisplayName ?? "none"}\u201d, "
                     + $"menus delete={_delete.IsEnabled} properties={_properties.IsEnabled} write={_newMessage.IsEnabled}.");

            // What the three lines now hold, which is the whole claim of the picking half: a name
            // chosen here has to arrive on the message this window was opened from, and only the
            // lines themselves tell "it put nothing there" from "it put the wrong thing there".
            if (_picking)
            {
                Log.Info($"Harness: address book lines — To “{_lines[AddressLine.To].Text}”, "
                         + $"Cc “{_lines[AddressLine.Cc].Text}”, "
                         + $"Bcc “{_lines[AddressLine.Bcc].Text}”.");
            }
        }
    }

    /// <summary>Presses one of this window's own buttons, as a pointer would.</summary>
    private static void Push(Button? button)
    {
        if (button is null)
        {
            Log.Warn("Harness: the Address Book is not picking names, so that button is not drawn.");
            return;
        }

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Runs something that opens a window, says which window it was, and closes it again.
    /// </summary>
    private static async Task OpensAsync(Func<Task> action)
    {
        // Held while the window is up, so a capture run photographs what opened rather than
        // whatever was on screen when the press landed — the row menu's harness does the same.
        using var hold = Mailbox.App.Theming.WindowCapture.Hold();

        _ = action();
        await Task.Delay(700);

        var opened = (Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows.Where(w => w.IsVisible).Select(w => w.Title ?? w.GetType().Name).ToList() ?? [];

        Log.Info($"Harness: address book opened \u2192 {string.Join(", ", opened.Select(t => $"\u201c{t}\u201d"))}");

        // A capture run keeps it open: the picture is of the window that opened, and closing it
        // first would photograph whatever was underneath.
        if (Mailbox.App.Theming.WindowCapture.IsRequested) return;

        // Newest first, so a contact window over a New Entry dialog closes in the order a person
        // would close it.
        foreach (var window in (Application.Current?.ApplicationLifetime
                     as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                 ?.Windows.Reverse().Where(w => w is NewEntryDialog or AdvancedFindDialog or ContactWindow).ToList() ?? [])
        {
            window.Close();
            await Task.Delay(120);
        }
    }

    /// <summary>Opens it to pick names, and hands back what was chosen.</summary>
    /// <remarks>
    /// The same door the ribbon's own Address Book has. Without it this window could be opened by
    /// a pose and photographed but never answered: a compose window's To… put a modal on screen
    /// that nothing could pick a name in, so the message it was opened from stayed empty and
    /// "picking names addresses the message" was a claim no run had ever tested.
    /// </remarks>
    public static async Task<AddressBookResult?> PickAsync(Window owner, ContactBook book)
    {
        var dialog = new AddressBookDialog(book);

        if (Environment.GetEnvironmentVariable("MAILBOX_ADDRESSBOOK") is { Length: > 0 } actions)
        {
            dialog.Opened += (_, _) => _ = dialog.HarnessAsync(actions);
        }

        await dialog.ShowDialog(owner);
        return dialog.Result;
    }
}
