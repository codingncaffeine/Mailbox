using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Ribbon;
using Mailbox.Theming.Icons;
using Avalonia.Controls.Primitives;
using static Mailbox.App.Views.ViewDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// The reference's Modify Button: a symbol to draw a toolbar command with, and a name to write
/// beside it.
/// </summary>
/// <remarks>
/// Both halves matter here for the same reason they do there. The name is what "Always show
/// command labels" writes, so a command whose own label is long enough to crowd the bar can be
/// given a short one; the symbol is how two commands that look alike are told apart at a glance.
/// <para>
/// The symbols offered are the application's own icon set (<see cref="IconGlyphs.Names"/>) rather
/// than a fixed grid of the reference's, because these are the glyphs that exist here and a
/// picker showing any others would offer squares. Reset puts both back to whatever the command
/// itself says, which is what the dialog's own OK cannot express by leaving the fields alone.
/// </para>
/// </remarks>
public sealed class ModifyButtonDialog : Window
{
    /// <summary>Measured to sit as the reference's does: a square of symbols over one field.</summary>
    private const int Columns = 14;
    private const double Cell = 30;

    private readonly TextBox _name = new();
    private readonly Border? _chosenCell;
    private string? _icon;

    /// <summary>What was chosen when OK was pressed, or null for Cancel.</summary>
    public QuickAccessOverride? Result { get; private set; }

    /// <summary>True when Reset was pressed: put the command back to its own name and icon.</summary>
    public bool Reset { get; private set; }

    /// <param name="label">The command's own name, which is what the field starts on.</param>
    /// <param name="icon">The command's own icon, or the one Modify… already gave it.</param>
    public ModifyButtonDialog(string label, string? icon)
    {
        Title = "Modify Button";
        Width = 480;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _icon = icon;
        _name.Text = label;
        _name.Width = 300;
        ViewDialogKit.Bind(_name, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        ViewDialogKit.Bind(_name, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        ViewDialogKit.Bind(_name, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");

        var symbols = new WrapPanel { ItemWidth = Cell, ItemHeight = Cell, Width = Columns * Cell };
        var buttons = new List<(Border Ring, string Name)>();

        foreach (var name in IconGlyphs.Names.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (IconGlyphs.GetOrEmpty(name, 16) is not { Length: > 0 } glyph) continue;

            var mark = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont.Family,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ViewDialogKit.Bind(mark, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

            // The ring is a Border inside the button rather than the button's own BorderBrush:
            // a templated control's border is the template's to draw, and a local value set on
            // it does not survive the default style — the trap this project has hit before.
            var ring = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2),
                Child = mark,
            };

            var cell = new Button
            {
                Width = Cell - 2,
                Height = Cell - 2,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = ring,
            };

            ToolTip.SetTip(cell, name);
            var chosen = name;
            cell.Click += (_, _) =>
            {
                _icon = chosen;
                Mark(buttons, chosen);
            };

            buttons.Add((ring, name));
            symbols.Children.Add(cell);
        }

        Mark(buttons, _icon);

        // Opened on the symbol it already has. Sorted by name, the current one is as likely as
        // not to be several screens down, and a picker that opens at "accept" every time makes
        // the reader hunt for what they are looking at on the toolbar behind the dialog.
        _chosenCell = buttons.FirstOrDefault(b => string.Equals(b.Name, _icon, StringComparison.Ordinal)).Ring;

        var ok = Ok(() =>
        {
            Result = new QuickAccessOverride(_name.Text?.Trim(), _icon);
            Close();
        });

        var reset = Ok(
            () =>
            {
                Reset = true;
                Result = new QuickAccessOverride(null, null);
                Close();
            },
            "Reset");

        var cancel = Cancel(this);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Label("Display name:"), _name },
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, ok, cancel },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                Label("Symbol:"),
                Boxed(
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = symbols,
                    },
                    height: 250),
                row,
                footer,
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, TemplatedControl.BackgroundProperty, "dialog.background.brush");
        Opened += (_, _) =>
        {
            _name.Focus();

            // After a layout pass, or the scroll viewer does not yet know where anything is.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _chosenCell?.BringIntoView(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>
    /// Draws the chosen symbol as chosen, and every other as not.
    /// </summary>
    /// <remarks>
    /// The dialog's own selection token, not an accent: this is a picker on a dialog surface and
    /// it has to read the same in all four themes, which is what a hard-coded blue could not do
    /// in the dark ones.
    /// </remarks>
    private static void Mark(List<(Border Ring, string Name)> cells, string? chosen)
    {
        foreach (var (ring, name) in cells)
        {
            if (string.Equals(name, chosen, StringComparison.Ordinal))
            {
                ViewDialogKit.Bind(ring, Border.BorderBrushProperty, "dialog.selection.focused.brush");
                ViewDialogKit.Bind(ring, Border.BackgroundProperty, "dialog.selection.brush");
            }
            else
            {
                ring.ClearValue(Border.BorderBrushProperty);
                ring.ClearValue(Border.BackgroundProperty);
                ring.BorderBrush = Brushes.Transparent;
                ring.Background = Brushes.Transparent;
            }
        }
    }
}
