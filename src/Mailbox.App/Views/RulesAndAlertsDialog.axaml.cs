using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// Rules and Alerts.
/// </summary>
/// <remarks>
/// The shape only. Rules need somewhere to run and something to run against — the filter engine
/// and ManageSieve are Phase 8 — so the dialog stands here with its buttons disabled and says
/// what it is waiting for, rather than offering a New Rule that opens onto nothing.
/// <para>
/// Manage Alerts is deliberately absent. It subscribes to SharePoint alert sources, which this
/// application has no way to reach and no plan to.
/// </para>
/// </remarks>
public sealed class RulesAndAlertsDialog : Window
{
    public RulesAndAlertsDialog()
    {
        Title = "Rules and Alerts";
        Width = 600;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                Disabled("New Rule…"), Disabled("Change Rule"), Disabled("Copy…"),
                Disabled("Delete"), Disabled("Run Rules Now…"),
            },
        };

        var empty = new TextBlock
        {
            Text = "No rules yet.",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0),
        };
        Bind(empty, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var list = new Border
        {
            Height = 190,
            BorderThickness = new Thickness(1),
            Child = empty,
        };
        Bind(list, Border.BorderBrushProperty, "border.subtle.brush");
        Bind(list, Border.BackgroundProperty, "surface.raised.brush");

        var note = new TextBlock
        {
            Text = "Rules need the filter engine, which arrives with junk filtering and search. "
                   + "Server-side rules will run over ManageSieve where the server offers it, so "
                   + "they keep working while Mailbox is closed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Bind(note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var heading = new TextBlock { Text = "Email Rules", FontWeight = FontWeight.SemiBold };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { close },
                },
                new StackPanel { Children = { heading, toolbar, list, note } },
            },
        };

        DialogChrome.Apply(this, body);

        Bind(this, BackgroundProperty, "surface.ground.brush");
    }

    private static Button Disabled(string label)
        => new() { Content = label, IsEnabled = false, Padding = new Thickness(9, 4) };

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
