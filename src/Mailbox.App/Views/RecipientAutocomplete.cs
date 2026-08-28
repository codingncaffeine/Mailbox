using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mailbox.Core.Compose;

namespace Mailbox.App.Views;

/// <summary>
/// The Auto-Complete List under a recipient line: as an entry is typed, the names it could be
/// drop down beneath the field, and one is taken with the arrow keys and Enter, Tab or a click.
/// </summary>
/// <remarks>
/// A popup rather than a control of its own, attached to the plain text box the address row
/// already has, so the row keeps its rule and its measurements. The arithmetic on the line —
/// which entry the caret is in, how it is swapped — is <see cref="RecipientCompletion"/> in
/// Core, tested without a window; what is here is the part that needs one.
/// <para>
/// The list is offered, never imposed: nothing is inserted until something is chosen, the
/// popup takes no focus, and Escape or typing on past it leaves the line exactly as typed. Each
/// suggestion carries a ✕, because the commonest complaint about this feature anywhere is an
/// old or misspelt address that will not go away.
/// </para>
/// </remarks>
internal sealed class RecipientAutocomplete
{
    private readonly TextBox _box;
    private readonly Func<string, IReadOnlyList<RecipientSuggestion>> _suggest;
    private readonly Action<string> _forget;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _commasSeparate;
    private readonly Popup _popup;
    private readonly ListBox _list;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private RecipientAutocomplete(
        TextBox box,
        Func<string, IReadOnlyList<RecipientSuggestion>> suggest,
        Action<string> forget,
        Func<bool> enabled,
        Func<bool> commasSeparate)
    {
        _box = box;
        _suggest = suggest;
        _forget = forget;
        _enabled = enabled;
        _commasSeparate = commasSeparate;

        _list = new ListBox
        {
            Focusable = false,
            MaxHeight = 8 * 40,
            ItemTemplate = new FuncDataTemplate<RecipientSuggestion>((entry, _) => entry is null ? new Control() : Row(entry)),
        };
        Bind(_list, TemplatedControl.BackgroundProperty, "surface.overlay.brush");

        var frame = new Border
        {
            Child = _list,
            BorderThickness = new Thickness(1),
            MinWidth = 320,
            Padding = new Thickness(0, 2),
        };
        Bind(frame, Border.BackgroundProperty, "surface.overlay.brush");
        Bind(frame, Border.BorderBrushProperty, "border.subtle.brush");

        _popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = frame,
        };
        // As wide as the field, so the list lines up under the writing line it belongs to.
        _popup.Bind(Layoutable.WidthProperty, new Binding("Bounds.Width") { Source = box });

        // Tunnelled, so the arrow keys and Enter are taken here before the text box acts on
        // them — but only while the list is showing; with it closed they are the text box's.
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.TextChanged += (_, _) => Refresh();
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty && _popup.IsOpen) Refresh();
        };
        box.LostFocus += (_, _) => Close();
        box.DetachedFromVisualTree += (_, _) => Close();
    }

    /// <summary>
    /// Attaches the list to a recipient field.
    /// </summary>
    /// <param name="suggest">Entries for what has been typed so far.</param>
    /// <param name="forget">Removes an entry — the ✕ on a suggestion.</param>
    /// <param name="enabled">The Options page's switch, read each time so a change applies at once.</param>
    /// <param name="commasSeparate">Whether a comma ends an entry, from the same page.</param>
    public static RecipientAutocomplete Attach(
        TextBox box,
        Func<string, IReadOnlyList<RecipientSuggestion>> suggest,
        Action<string> forget,
        Func<bool> enabled,
        Func<bool> commasSeparate)
    {
        ArgumentNullException.ThrowIfNull(box);
        return new RecipientAutocomplete(box, suggest, forget, enabled, commasSeparate);
    }

    /// <summary>True while suggestions are showing.</summary>
    public bool IsOpen => _popup.IsOpen;

    /// <summary>How many entries the last refresh offered. For the harness, which cannot see a popup.</summary>
    public int Offered { get; private set; }

    /// <summary>
    /// What it is offering, in order, as one line each — the only way to check a popup, which is
    /// a separate surface and never appears in a capture.
    /// </summary>
    public IReadOnlyList<string> Describe()
        => _list.ItemsSource is IEnumerable<RecipientSuggestion> entries
            ? [.. entries.Select(e => $"{e.DisplayName}|{e.Address}|{e.Detail}|{e.Insert}")]
            : [];

    /// <summary>Re-reads the line and shows, updates or hides the list accordingly.</summary>
    public void Refresh()
    {
        if (!_enabled())
        {
            Close();
            return;
        }

        var (_, entry) = RecipientCompletion.CurrentEntry(_box.Text, _box.CaretIndex, _commasSeparate());
        if (!RecipientCompletion.WantsSuggestions(entry))
        {
            Close();
            return;
        }

        var entries = _suggest(entry);
        Offered = entries.Count;
        if (entries.Count == 0)
        {
            Close();
            return;
        }

        _list.ItemsSource = entries;
        _list.SelectedIndex = 0;
        if (!_popup.IsOpen) _popup.IsOpen = true;
    }

    public void Close()
    {
        if (_popup.IsOpen) _popup.IsOpen = false;
    }

    /// <summary>Puts the chosen entry on the line in place of what was being typed.</summary>
    private void Accept(RecipientSuggestion entry)
    {
        var (text, caret) = RecipientCompletion.Replace(
            _box.Text, _box.CaretIndex, entry.Insert, _commasSeparate());

        Close();
        _box.Text = text;
        _box.CaretIndex = caret;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (_list.SelectedItem is RecipientSuggestion chosen)
                {
                    Accept(chosen);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Move(int by)
    {
        var count = _list.ItemCount;
        if (count == 0) return;

        _list.SelectedIndex = ((_list.SelectedIndex + by) % count + count) % count;
        if (_list.SelectedItem is { } item) _list.ScrollIntoView(item);
    }

    /// <summary>
    /// One suggestion: the name in the ink the list uses, the address quieter beside it, and
    /// the ✕ that takes it out of the list for good.
    /// </summary>
    private Control Row(RecipientSuggestion entry)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(6, 2),
            Background = Brushes.Transparent,
        };

        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (entry.DisplayName.Length > 0)
        {
            var name = new TextBlock
            {
                Text = entry.DisplayName,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(name, TextBlock.ForegroundProperty, "text.primary.brush");
            text.Children.Add(name);
        }

        if (entry.Address.Length > 0)
        {
            var address = new TextBlock
            {
                Text = entry.Address,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(address, TextBlock.ForegroundProperty,
                entry.DisplayName.Length > 0 ? "text.secondary.brush" : "text.primary.brush");
            text.Children.Add(address);
        }

        // What kind of entry it is, where that is worth saying: a contact, or how many people a
        // distribution list will put on the line.
        if (entry.Detail.Length > 0)
        {
            var detail = new TextBlock { Text = entry.Detail, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            Bind(detail, TextBlock.ForegroundProperty, "text.secondary.brush");
            text.Children.Add(detail);
        }

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // Only what came from the Auto-Complete List can be taken out of it: the ✕ empties a
        // cache, and it is not how somebody is removed from the address book.
        if (entry.CanForget)
        {
            var remove = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            remove.Classes.Add("plain");
            Bind(remove, TemplatedControl.ForegroundProperty, "text.secondary.brush");
            ToolTip.SetTip(remove, "Remove from the Auto-Complete List");
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                _forget(entry.Address);
                Refresh();
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
        }

        // A click on the row itself is a choice. Handled on release rather than press, so
        // the ✕ — a button, which acts on release — is not pre-empted by the row under it.
        grid.PointerReleased += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
            Accept(entry);
            e.Handled = true;
        };

        return grid;
    }
}
