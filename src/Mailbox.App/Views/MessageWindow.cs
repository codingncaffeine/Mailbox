using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;
using Mailbox.Theming;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// One message in a window of its own, as double-clicking a row opens it.
/// </summary>
/// <remarks>
/// The same body the reading pane uses, so a message renders identically whichever it is read
/// in — bars, blocking and all. What it does not have yet is the reference's read ribbon: that
/// is a third ribbon host after the shell's and the compose window's, and the commands on it
/// are the ones Phase 6 and Phase 8 are still building.
/// </remarks>
public sealed class MessageWindow : Window
{
    private readonly ReadingPaneBody _body;
    private readonly AttachmentStrip _attachments = new();

    public MessageWindow(
        ThemeService themes, Func<MailRepository?> mail, MimeMessage message, byte[]? raw)
    {
        ArgumentNullException.ThrowIfNull(message);

        Title = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;
        Width = 900;
        Height = 640;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _body = new ReadingPaneBody(themes, mail);

        var root = new DockPanel();

        var header = Header(message, raw);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_attachments, Dock.Top);
        root.Children.Add(_attachments);

        root.Children.Add(_body);

        DialogChrome.Apply(this, root);

        _attachments.Show(message);
        _body.Show(message, message.TextBody ?? string.Empty);
        _ = _body.ApplySenderPolicyAsync();
    }

    private Control Header(MimeMessage message, byte[]? raw)
    {
        var stack = new StackPanel { Spacing = 3 };

        var subject = new TextBlock
        {
            Text = message.Subject ?? string.Empty,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Bind(subject, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(subject);

        var from = new TextBlock
        {
            Text = message.From.ToString(),
            FontWeight = FontWeight.SemiBold,
        };
        Bind(from, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(from);

        var to = new TextBlock { Text = "To: " + message.To };
        Bind(to, TextBlock.ForegroundProperty, "text.secondary.brush");
        stack.Children.Add(to);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        if (raw is { Length: > 0 })
        {
            var source = new Button
            {
                Content = "View Source",
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(1),
            };
            Bind(source, BorderBrushProperty, "border.subtle.brush");
            Bind(source, BackgroundProperty, "surface.raised.brush");

            source.Click += (_, _) =>
                new MessageSourceWindow(message.Subject ?? string.Empty, raw).Show(this);

            Grid.SetColumn(source, 1);
            grid.Children.Add(source);
        }

        var header = new Border
        {
            Child = grid,
            Padding = new Thickness(20, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(header, BorderBrushProperty, "border.subtle.brush");
        Bind(header, BackgroundProperty, "reading.header.background.brush");

        return header;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
