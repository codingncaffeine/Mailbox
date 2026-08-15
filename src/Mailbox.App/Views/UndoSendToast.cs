using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The way back from a message that has just gone.
/// </summary>
/// <remarks>
/// §12's Undo Send, and the reason it is a toast rather than a ribbon button: a button that is
/// only useful for five seconds after an action, and useless the rest of the time, is a button
/// occupying permanent space to be wrong. The offer belongs beside the thing it undoes and
/// leaves when it expires.
/// <para>
/// It counts down, because a grace period with no visible end is one people do not trust — and
/// because the number falling is what tells someone they have to decide now rather than read the
/// sentence first.
/// </para>
/// <para>
/// Owned by the shell rather than the compose window, which closes the instant the message is
/// queued. A message offering to undo something has to outlive the thing that did it.
/// </para>
/// </remarks>
public sealed class UndoSendToast : Border
{
    private readonly TextBlock _message = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undo;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private QueuedMessageEventArgs? _queued;
    private Action<QueuedMessageEventArgs>? _undone;

    public UndoSendToast()
    {
        IsVisible = false;

        Padding = new Thickness(14, 10);
        Margin = new Thickness(0, 0, 20, 20);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;

        // A dialog's tokens rather than the content surface's: this floats over the workspace
        // and is chrome, which in Dark Gray is the difference between readable and not.
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Bind(this, BorderBrushProperty, "dialog.border.brush");
        Bind(_message, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        _undo = new Button
        {
            Content = "Undo",
            Padding = new Thickness(12, 4),
            Margin = new Thickness(14, 0, 0, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(_undo, BorderBrushProperty, "dialog.border.brush");
        Bind(_undo, BackgroundProperty, "dialog.surface.brush");
        _undo.Click += (_, _) => Undo();

        var dismiss = new Button
        {
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("dismiss", 16),
                FontFamily = IconFont.Family,
                FontSize = 11,
            },
            Padding = new Thickness(7, 4),
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = default,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dismiss.Click += (_, _) => Hide();

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Glyph(), _message, _undo, dismiss },
        };

        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The clock, so a test does not have to wait five seconds.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Offers the way back, until the hold runs out.
    /// </summary>
    /// <param name="queued">What went, and when it stops being recallable.</param>
    /// <param name="undone">
    /// What to do if the reader takes the offer. Called on the UI thread, and only while the
    /// message is genuinely still withdrawable — whether it actually comes back is the store's
    /// decision, not this control's.
    /// </param>
    public void Offer(QueuedMessageEventArgs queued, Action<QueuedMessageEventArgs> undone)
    {
        ArgumentNullException.ThrowIfNull(queued);

        _queued = queued;
        _undone = undone;

        IsVisible = true;
        Tick();
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _queued = null;
        _undone = null;
        IsVisible = false;
    }

    private void Undo()
    {
        if (_queued is not { } queued || _undone is not { } undone) return;

        // Stopped first. The callback may put a window up, and a timer that fires behind it and
        // hides this would leave the reader wondering whether the click registered.
        _timer.Stop();
        IsVisible = false;

        undone(queued);

        _queued = null;
        _undone = null;
    }

    private void Tick()
    {
        if (_queued is not { } queued) { Hide(); return; }

        var left = queued.Remaining(Now());

        if (left <= TimeSpan.Zero)
        {
            // Gone for real. The toast says so for a moment rather than vanishing, because a
            // notice that disappears the instant it stops being actionable reads as a glitch.
            _message.Text = Sent(queued);
            _undo.IsVisible = false;
            _timer.Stop();

            DispatcherTimer.RunOnce(Hide, TimeSpan.FromSeconds(2));
            return;
        }

        _undo.IsVisible = true;
        _message.Text = $"{Sent(queued)}  Undo in {Math.Ceiling(left.TotalSeconds):0}s";
    }

    private static string Sent(QueuedMessageEventArgs queued)
        => string.IsNullOrWhiteSpace(queued.Subject)
            ? "Message sent."
            : $"Sent: {Trim(queued.Subject)}";

    private static string Trim(string subject)
        => subject.Length > 46 ? subject[..46] + "…" : subject;

    private Control Glyph()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("send", 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        return glyph;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
