using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;

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
/// The Address Book: the contacts, searchable, with the three lines to put them on.
/// </summary>
/// <remarks>
/// The reference's own window behind Ctrl+Shift+B and the To… button, which until there were
/// contacts had nothing to show. Opened from the compose window it fills the three lines;
/// opened from the ribbon it is the same window with only the list to look at, which is what
/// the reference does when there is no message to address.
/// <para>
/// A distribution list resolves to its members as it is added, for the reason the Auto-Complete
/// List does it: a plain recipient line has no token to leave unresolved.
/// </para>
/// </remarks>
public sealed class AddressBookDialog : Window
{
    private readonly ContactBook _book;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search", MinWidth = 240 };
    private readonly Dictionary<AddressLine, TextBox> _lines = [];
    private readonly bool _picking;

    private IReadOnlyList<ContactRow> _rows = [];

    public AddressBookDialog(ContactBook book, bool picking = true)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _picking = picking;

        Title = picking ? "Select Names: Contacts" : "Address Book: Contacts";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new DockPanel { Margin = new Thickness(18) };
        body.Children.Add(Buttons());
        body.Children.Add(Body());

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");

        Refresh();
    }

    /// <summary>Who was picked, or null when the window was cancelled.</summary>
    public AddressBookResult? Result { get; private set; }

    private Control Body()
    {
        var stack = new DockPanel();

        var top = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Top,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _search.TextChanged += (_, _) => Refresh();
        top.Children.Add(_search);
        stack.Children.Add(top);

        if (_picking)
        {
            var lines = new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var line in (AddressLine[])[AddressLine.To, AddressLine.Cc, AddressLine.Bcc])
            {
                lines.Children.Add(Line(line));
            }

            stack.Children.Add(lines);
        }

        _list.SelectionMode = SelectionMode.Multiple;
        _list.DoubleTapped += (_, _) => Add(AddressLine.To);
        stack.Children.Add(_list);
        return stack;
    }

    /// <summary>One of the three lines: its button, and the box it fills.</summary>
    private Control Line(AddressLine line)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };

        var button = new Button { Content = line.ToString() + " ->", Width = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => Add(line);
        row.Children.Add(button);

        var box = new TextBox { AcceptsReturn = false };
        _lines[line] = box;
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    private Control Buttons()
    {
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (_picking)
            {
                Result = new AddressBookResult(Split(AddressLine.To), Split(AddressLine.Cc), Split(AddressLine.Bcc));
            }

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
            Children = { ok, cancel },
        };
    }

    /// <summary>The contacts matching the search box, or all of them when it is empty.</summary>
    private void Refresh()
    {
        _rows = _search.Text is { Length: > 0 } typed ? _book.Matching(typed, 200) : _book.Rows();
        _list.ItemsSource = _rows.Select(Describe).ToList();
    }

    private static string Describe(ContactRow row)
    {
        var contact = row.Contact;
        if (contact.IsGroup) return $"{contact.Named()}  —  group";
        return contact.PrimaryEmail is { Length: > 0 } address
            ? $"{contact.Named()}  —  {address}"
            : contact.Named();
    }

    /// <summary>Puts whoever is picked on a line, resolving a group to its members.</summary>
    private void Add(AddressLine line)
    {
        if (!_picking || !_lines.TryGetValue(line, out var box)) return;

        var chosen = _list.SelectedItems is { Count: > 0 } picked
            ? picked.Cast<string>().Select(text => _rows.FirstOrDefault(r => Describe(r) == text)).Where(r => r is not null).Select(r => r!).ToList()
            : [];

        if (chosen.Count == 0) return;

        var additions = new List<string>();
        foreach (var row in chosen)
        {
            foreach (var suggestion in ContactSuggestions.For(_book, row.Named(), limit: 8))
            {
                if (suggestion.DisplayName == row.Named())
                {
                    additions.Add(suggestion.Insert);
                    break;
                }
            }
        }

        var already = box.Text is { Length: > 0 } text ? text.TrimEnd().TrimEnd(';') + "; " : string.Empty;
        box.Text = already + string.Join("; ", additions);
    }

    private IReadOnlyList<string> Split(AddressLine line)
        => _lines.TryGetValue(line, out var box) && box.Text is { Length: > 0 } text
            ? [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Opens it to pick names, and hands back what was chosen.</summary>
    public static async Task<AddressBookResult?> PickAsync(Window owner, ContactBook book)
    {
        var dialog = new AddressBookDialog(book);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }
}
