using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The panel behind the account button in the title bar.
/// </summary>
/// <remarks>
/// Two stacked sections on one panel: the signed-in account, then a row to add another. Every
/// number here is measured off a capture — the panel is 338 wide, the initial circle 88 across
/// and centred on the block of text beside it rather than on the section, and the account
/// section carries a deep run of blank space above the circle that is simply how the reference
/// lays it out.
/// </remarks>
public static class AccountFlyout
{
    private const double PanelWidth = 338;
    private const double CircleSize = 88;
    private const double SidePadding = 16;

    /// <summary>Blank run above the circle. Measured; there is nothing drawn in it.</summary>
    private const double AccountTopPadding = 52;

    private const double AccountBottomPadding = 21;
    private const double AddAccountHeight = 68;

    public static Flyout Build(string address, string initial, Action onViewAccount,
        Action onAddAccount)
    {
        var panel = new StackPanel { Width = PanelWidth };
        panel.Children.Add(AccountSection(address, initial, onViewAccount));
        panel.Children.Add(Separator());
        panel.Children.Add(AddAccountRow(onAddAccount));

        var flyout = new Flyout
        {
            Content = panel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Standard,
        };
        flyout.FlyoutPresenterClasses.Add("account");
        return flyout;
    }

    private static Control AccountSection(string address, string initial, Action onViewAccount)
    {
        var circle = new Border
        {
            Width = CircleSize,
            Height = CircleSize,
            CornerRadius = new CornerRadius(CircleSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(circle, Border.BackgroundProperty, "avatar.background.brush");

        var letter = new TextBlock
        {
            Text = initial,
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(letter, TextBlock.ForegroundProperty, "avatar.foreground.brush");
        circle.Child = letter;

        // The name line is the address when the account has no display name, which is what the
        // reference shows — the same string twice, the upper one bold and clipped.
        var name = new TextBlock
        {
            Text = address,
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Bind(name, TextBlock.ForegroundProperty, "text.primary.brush");

        var email = new TextBlock { Text = address, Margin = new Thickness(0, 4, 0, 0) };
        Bind(email, TextBlock.ForegroundProperty, "text.secondary.brush");

        var view = new Button
        {
            Content = "View account",
            Padding = default,
            Margin = new Thickness(0, 8, 0, 0),
            Background = null,
            BorderThickness = default,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        Bind(view, TemplatedControl.ForegroundProperty, "text.link.brush");
        view.Click += (_, _) => onViewAccount();

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(SidePadding, 0, SidePadding, 0),
            Children = { name, email, view },
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(circle, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(circle);
        row.Children.Add(text);

        var host = new Border
        {
            Padding = new Thickness(
                SidePadding, AccountTopPadding, SidePadding, AccountBottomPadding),
            Child = row,
        };
        Bind(host, Border.BackgroundProperty, "surface.overlay.brush");
        return host;
    }

    private static Control Separator()
    {
        var rule = new Border { Height = 1 };
        Bind(rule, Border.BackgroundProperty, "border.subtle.brush");
        return rule;
    }

    private static Control AddAccountRow(Action onAddAccount)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("person-add", 20),
            FontFamily = IconFont.Family,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Bind(glyph, TextBlock.ForegroundProperty, "text.primary.brush");

        var label = new TextBlock
        {
            Text = "Add an account",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");

        var button = new Button
        {
            Height = AddAccountHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(SidePadding + 12, 0),
            BorderThickness = default,
            CornerRadius = default,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glyph, label },
            },
        };
        Bind(button, TemplatedControl.BackgroundProperty, "surface.sunken.brush");
        button.Click += (_, _) => onAddAccount();
        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
