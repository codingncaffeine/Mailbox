using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Controls.People;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The People peek: what the rail's People icon opens on a hover.
/// </summary>
/// <remarks>
/// Measured off the reference's own: <b>249 wide inside a 1px black hairline, and grey all
/// through</b> — there is no lighter content panel inside it, which is what separates it from the
/// calendar peek's #F0F0F0 page in a #BDBDBD frame. It holds a Search People box, the pop-out
/// button in its top corner, a <b>Favorites</b> heading, and — with nobody in the list — two
/// centred lines saying how somebody gets there.
/// <para>
/// Those two lines are the reference's own, less the name of a product this application does not
/// mention: it says to right-click a person to add them, and so does this, because that is where
/// the gesture is (<c>MainWindow.ShowContactMenu</c>).
/// </para>
/// <para>
/// It is a desktop popup, so it keeps the desktop's light colours in every theme — the
/// <c>peek.pop.*</c> family, exactly as the calendar peek's floating half does.
/// </para>
/// </remarks>
internal sealed class PeoplePeek : Border
{
    /// <summary>Measured: 249 across, inside the hairline.</summary>
    public const double PeekWidth = 249;

    /// <summary>
    /// Authored: the capture cuts the top off, so the height is the calendar peek's, the two
    /// being the same kind of popup from the same rail.
    /// </summary>
    public const double PeekHeight = 330;

    private readonly ContactListView _list = new() { ShowIndex = false, OnPopup = true };
    private readonly StackPanel _body = new();

    public PeoplePeek(IReadOnlyList<ContactRow> favourites, FileAsOrder order)
    {
        Width = PeekWidth;
        Height = PeekHeight;
        BorderThickness = new Thickness(1);
        this[!BorderBrushProperty] = new DynamicResourceExtension("peek.pop.outline.brush");
        this[!BackgroundProperty] = new DynamicResourceExtension("peek.pop.frame.brush");

        _list.Order = order;
        _list.Rows = favourites;
        _list.ContactActivated += (_, row) => ContactOpened?.Invoke(this, row);

        // The corner button on its own line above the box, which is where the reference draws it.
        _body.Margin = new Thickness(9, 6, 9, 8);
        _body.Spacing = 6;
        _body.Children.Add(Corner());
        _body.Children.Add(SearchBox());
        _body.Children.Add(Heading("Favorites"));
        _body.Children.Add(favourites.Count == 0 ? Empty() : Favourites());

        Child = _body;
    }

    /// <summary>Somebody in the list was opened.</summary>
    public event EventHandler<ContactRow>? ContactOpened;

    /// <summary>The corner button: the section goes into the To-Do Bar, as the calendar's does.</summary>
    public event EventHandler? DockRequested;

    /// <summary>The search box was pressed, which is the module's own Search People.</summary>
    public event EventHandler? SearchRequested;

    /// <summary>What the peek is holding, for a harness pose that cannot photograph a popup.</summary>
    public IReadOnlyList<ContactRow> Rows => _list.Rows;

    private Control Corner()
    {
        var corner = new Button
        {
            Content = Glyph("open-item", 12),
            Classes = { "flat" },
            Padding = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        ToolTip.SetTip(corner, "Show the People section in the To-Do Bar");
        corner.Click += (_, _) => DockRequested?.Invoke(this, EventArgs.Empty);
        return corner;
    }

    /// <summary>
    /// The Search People box, drawn as the reference draws it: a bordered field with the words in
    /// it and a magnifier against its right edge.
    /// </summary>
    private Control SearchBox()
    {
        var words = new TextBlock
        {
            Text = "Search People",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12,
        };
        words[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("peek.pop.text.dim.brush");

        var magnifier = Glyph("search", 12);
        magnifier.HorizontalAlignment = HorizontalAlignment.Right;
        magnifier.Margin = new Thickness(0, 0, 6, 0);

        var box = new Border
        {
            Height = 22,
            BorderThickness = new Thickness(1),
            Child = new Panel { Children = { words, magnifier } },
        };

        box[!BorderBrushProperty] = new DynamicResourceExtension("peek.pop.outline.brush");
        box[!BackgroundProperty] = new DynamicResourceExtension("peek.pop.background.brush");

        var button = new Button
        {
            Content = box,
            Classes = { "flat" },
            Padding = default,
            BorderThickness = default,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        button.Click += (_, _) => SearchRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    private static Control Heading(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 13 };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("peek.pop.title.brush");
        return block;
    }

    /// <summary>The reference's own two lines, centred, for a list with nobody in it.</summary>
    private static Control Empty()
    {
        var text = new TextBlock
        {
            Text = "Right-click a person to add them to your favourites.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(12, 4, 12, 0),
        };

        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("peek.pop.text.brush");
        return text;
    }

    private Control Favourites()
    {
        _list.Height = PeekHeight - 90;
        return _list;
    }

    private static TextBlock Glyph(string name, double size)
    {
        var text = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(name, 16),
            FontFamily = IconFont.Family,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };

        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("peek.pop.text.brush");
        return text;
    }
}
