using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Security;
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

    /// <summary>
    /// The two lines the pane can correct, held so it can.
    /// </summary>
    /// <remarks>
    /// A message that carried its own header fields inside its cryptography says <c>[...]</c> where
    /// its subject should be on the outside, and the pane is the only thing that has opened it — so
    /// the header here is drawn from the envelope and then told better. Without this the window
    /// disagreed with the body inside it (RFC 9788 §4).
    /// </remarks>
    private readonly TextBlock _subject;
    private readonly TextBlock _from;

    public MessageWindow(
        ThemeService themes, Func<MailRepository?> mail, MimeMessage message, byte[]? raw,
        DkimResult? verified = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        Title = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;
        Width = 900;
        Height = 640;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _body = new ReadingPaneBody(themes, mail);
        _subject = Line(18, "text.primary.brush");
        _from = Line(null, "text.primary.brush");
        _from.FontWeight = FontWeight.SemiBold;

        var root = new DockPanel();

        var header = Header(message, raw);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_attachments, Dock.Top);
        root.Children.Add(_attachments);

        root.Children.Add(_body);

        DialogChrome.Apply(this, root);

        // Wired before the message is shown, because the pane answers about the header inside Show.
        _body.HeaderChanged += (_, _) => Correct();

        _attachments.Show(message);
        _body.Show(message, message.TextBody ?? string.Empty, verified);
        _ = _body.ApplySenderPolicyAsync();
    }

    /// <summary>Takes the pane's word for the subject and the sender, when it has one.</summary>
    private void Correct()
    {
        if (_body.HeaderSubject is { } subject)
        {
            _subject.Text = subject;
            Title = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject;
        }

        if (_body.HeaderFrom is { } from) _from.Text = from;
    }

    private static TextBlock Line(double? size, string ink)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (size is { } points) line.FontSize = points;

        Bind(line, TextBlock.ForegroundProperty, ink);
        return line;
    }

    private Control Header(MimeMessage message, byte[]? raw)
    {
        var stack = new StackPanel { Spacing = 3 };

        _subject.Text = message.Subject ?? string.Empty;
        _subject.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(_subject);

        _from.Text = message.From.ToString();
        stack.Children.Add(_from);

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
