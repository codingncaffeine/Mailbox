using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Theming.Fonts;

namespace Mailbox.App.Views;

/// <summary>
/// The Font dialog behind each Font… button on Personal Stationery: face, style, size and colour,
/// with the sample drawn in the choice.
/// </summary>
/// <remarks>
/// The families offered are the wire names mail is written in — Calibri, Arial, Times New Roman
/// and the rest of the substitution table's requests — beside whatever this machine has
/// installed. A face that is not here is drawn by its metric-compatible substitute (§6) and goes
/// on the wire under its own name, so a message set in Calibri on this machine reads as Calibri
/// on the reader's; the sample says which face is drawing it when that is the case.
/// </remarks>
public sealed class FontDialog : Window
{
    /// <summary>What OK left, or null.</summary>
    public MessageFont? Result { get; private set; }

    private static readonly IReadOnlyList<double> Sizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

    /// <summary>The colours the reference's Font color dropdown starts with, under Automatic.</summary>
    private static readonly IReadOnlyList<(string Name, string? Hex)> Colours =
    [
        ("Automatic", null),
        ("Black", "#000000"), ("Dark Blue", "#1F3864"), ("Blue", "#0563C1"), ("Dark Red", "#C00000"),
        ("Red", "#FF0000"), ("Green", "#00B050"), ("Dark Green", "#375623"), ("Purple", "#7030A0"),
        ("Orange", "#ED7D31"), ("Gray", "#7F7F7F"), ("Dark Gray", "#404040"),
    ];

    public FontDialog(string title, MessageFont current, IReadOnlyCollection<string> installedFamilies)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(installedFamilies);

        Title = title;
        Width = 480;
        Height = 530;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var families = FontSubstitution.NonRedistributable
            .Concat(FontSubstitution.Table.Select(t => t.Original))
            .Concat(installedFamilies)
            .Concat([current.Family])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var family = ViewDialogKit.SurfaceList(200, 200);
        family.ItemTemplate = new FuncDataTemplate<object>((item, _) => ViewDialogKit.SurfaceText(item?.ToString() ?? string.Empty));
        family.ItemsSource = families;
        family.SelectedIndex = Math.Max(0, families.FindIndex(f => string.Equals(f, current.Family, StringComparison.OrdinalIgnoreCase)));

        var styles = new[] { "Regular", "Italic", "Bold", "Bold Italic" };
        var style = ViewDialogKit.SurfaceList(110, 200);
        style.ItemTemplate = new FuncDataTemplate<object>((item, _) => ViewDialogKit.SurfaceText(item?.ToString() ?? string.Empty));
        style.ItemsSource = styles;
        style.SelectedIndex = Math.Max(0, Array.IndexOf(styles, current.Style));

        var size = ViewDialogKit.SurfaceList(70, 200);
        size.ItemTemplate = new FuncDataTemplate<object>((item, _) => ViewDialogKit.SurfaceText(item is double d ? d.ToString("0.#") : string.Empty));
        size.ItemsSource = Sizes.Cast<object>().ToList();
        size.SelectedIndex = Math.Max(0, Sizes.ToList().IndexOf(current.Points));

        var colour = new ComboBox
        {
            ItemsSource = Colours.Select(c => c.Name).ToList(),
            SelectedIndex = Math.Max(0, Colours.ToList().FindIndex(c => string.Equals(c.Hex, current.Colour, StringComparison.OrdinalIgnoreCase))),
            MinWidth = 160,
        };

        // The sample: drawn in the face that will draw the message here, sized as the message
        // will be, in the colour chosen. A face this machine does not have is drawn by its
        // substitute, and the line under the sample says so.
        var sample = new TextBlock
        {
            Text = "The quick brown fox jumps over the lazy dog",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sampleBox = ViewDialogKit.Boxed(sample, height: 76);
        sampleBox.Padding = new Thickness(8);
        var drawnBy = ViewDialogKit.Label(string.Empty, subtle: true);

        MessageFont Chosen()
        {
            var face = family.SelectedItem as string ?? current.Family;
            var chosenStyle = style.SelectedItem as string ?? "Regular";
            var points = size.SelectedItem is double d ? d : current.Points;
            var hex = colour.SelectedIndex >= 0 && colour.SelectedIndex < Colours.Count ? Colours[colour.SelectedIndex].Hex : null;
            return new MessageFont(face, points, chosenStyle.Contains("Bold"), chosenStyle.Contains("Italic"), hex);
        }

        void Preview()
        {
            var chosen = Chosen();
            var resolved = App.Fonts.Resolve(chosen.Family);
            sample.FontFamily = BundledFonts.FamilyFor(resolved.Rendered);
            sample.FontSize = chosen.Points / 0.75;
            sample.FontWeight = chosen.Bold ? FontWeight.Bold : FontWeight.Normal;
            sample.FontStyle = chosen.Italic ? FontStyle.Italic : FontStyle.Normal;
            if (chosen.Colour is { } hex && Color.TryParse(hex, out var c)) sample.Foreground = new SolidColorBrush(c);
            else ViewDialogKit.Bind(sample, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            drawnBy.Text = string.Equals(resolved.Rendered, chosen.Family, StringComparison.OrdinalIgnoreCase)
                ? $"{chosen.Family} is installed."
                : $"{chosen.Family} is not installed here; drawn by {resolved.Rendered} at the same metrics. Mail is sent naming {chosen.Family}.";
        }

        family.SelectionChanged += (_, _) => Preview();
        style.SelectionChanged += (_, _) => Preview();
        size.SelectionChanged += (_, _) => Preview();
        colour.SelectionChanged += (_, _) => Preview();
        Preview();

        Control Column(string label, Control list)
            => new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label(label), list } };

        var lists = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { Column("Font:", family), Column("Font style:", style), Column("Size:", size) },
        };

        var colourRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { ViewDialogKit.Label("Font color:"), colour },
        };

        var ok = ViewDialogKit.Ok(() => { Result = Chosen(); Close(); });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                lists,
                colourRow,
                ViewDialogKit.Label("Preview", bold: true),
                sampleBox,
                drawnBy,
                ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}
