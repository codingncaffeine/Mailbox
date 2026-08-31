using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The Icon column's envelope, drawn in the message's state: closed while unread, open once
/// read — which is what the reference's column says, and what a constant glyph could not.
/// </summary>
/// <remarks>
/// Drawn rather than typed because no text face carries an open envelope. In the list's own
/// secondary ink, so the state reads as a shape and the row's weight stays the unread signal
/// it already is. What the column still cannot say — replied, forwarded — is not recorded on
/// the message at all yet, and the absence is queued where absences go.
/// </remarks>
public sealed class MessageStateIcon : Control
{
    public static readonly StyledProperty<bool> IsUnreadProperty =
        AvaloniaProperty.Register<MessageStateIcon, bool>(nameof(IsUnread));

    public static readonly StyledProperty<bool> IsAnsweredProperty =
        AvaloniaProperty.Register<MessageStateIcon, bool>(nameof(IsAnswered));

    public static readonly StyledProperty<bool> IsForwardedProperty =
        AvaloniaProperty.Register<MessageStateIcon, bool>(nameof(IsForwarded));

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<MessageStateIcon, IBrush?>(nameof(Ink));

    public static readonly StyledProperty<IBrush?> ReplyInkProperty =
        AvaloniaProperty.Register<MessageStateIcon, IBrush?>(nameof(ReplyInk));

    public static readonly StyledProperty<IBrush?> ForwardInkProperty =
        AvaloniaProperty.Register<MessageStateIcon, IBrush?>(nameof(ForwardInk));

    static MessageStateIcon()
    {
        AffectsRender<MessageStateIcon>(IsUnreadProperty, IsAnsweredProperty, IsForwardedProperty,
            InkProperty, ReplyInkProperty, ForwardInkProperty);
    }

    public MessageStateIcon()
    {
        Width = 16;
        Height = 16;
        this[!InkProperty] = new DynamicResourceExtension("text.secondary.brush");

        // The arrows take the ribbon's own reply and forward colours — the same two the Reply
        // and Forward buttons are drawn in, because they are the same two meanings.
        this[!ReplyInkProperty] = new DynamicResourceExtension("ribbon.icon.magenta.brush");
        this[!ForwardInkProperty] = new DynamicResourceExtension("ribbon.icon.blue.brush");
    }

    public bool IsUnread
    {
        get => GetValue(IsUnreadProperty);
        set => SetValue(IsUnreadProperty, value);
    }

    public bool IsAnswered
    {
        get => GetValue(IsAnsweredProperty);
        set => SetValue(IsAnsweredProperty, value);
    }

    public bool IsForwarded
    {
        get => GetValue(IsForwardedProperty);
        set => SetValue(IsForwardedProperty, value);
    }

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public IBrush? ReplyInk
    {
        get => GetValue(ReplyInkProperty);
        set => SetValue(ReplyInkProperty, value);
    }

    public IBrush? ForwardInk
    {
        get => GetValue(ForwardInkProperty);
        set => SetValue(ForwardInkProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Ink is not { } ink) return;
        var pen = new Pen(ink, 1);

        if (IsUnread)
        {
            // Closed: the envelope face-up, its flap folded to the middle.
            context.DrawRectangle(null, pen, new Rect(1.5, 3.5, 13, 9));
            context.DrawGeometry(null, pen, Lines((1.5, 3.5), (8, 8.5), (14.5, 3.5)));
        }
        else
        {
            // Open: the flap standing above an emptied pocket, the letter's edge showing.
            context.DrawGeometry(null, pen, Lines((2.5, 7.5), (8, 2.5), (13.5, 7.5)));
            context.DrawGeometry(null, pen, Lines((4.5, 7.5), (4.5, 5.5), (11.5, 5.5), (11.5, 7.5)));
            context.DrawRectangle(null, pen, new Rect(2.5, 7.5, 11, 6));
            context.DrawGeometry(null, pen, Lines((2.5, 7.5), (8, 11.5), (13.5, 7.5)));
        }

        // The reply or forward arrow over the bottom-left corner, as the reference overlays
        // it — on either envelope, because marking a message unread does not unanswer it.
        // Replied wins when both are true: two booleans cannot say which came last, and
        // "did I answer this?" is the question the column is scanned for.
        if (IsAnswered && ReplyInk is { } reply)
        {
            context.DrawGeometry(reply, null, Filled(
                (0.5, 11.5), (4.5, 8), (4.5, 10), (9.5, 10), (9.5, 13), (4.5, 13), (4.5, 15)));
        }
        else if (IsForwarded && ForwardInk is { } forward)
        {
            context.DrawGeometry(forward, null, Filled(
                (9.5, 11.5), (5.5, 8), (5.5, 10), (0.5, 10), (0.5, 13), (5.5, 13), (5.5, 15)));
        }
    }

    private static StreamGeometry Lines(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var open = geometry.Open();
        open.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);
        for (var i = 1; i < points.Length; i++) open.LineTo(new Point(points[i].X, points[i].Y));
        open.EndFigure(false);
        return geometry;
    }

    private static StreamGeometry Filled(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var open = geometry.Open();
        open.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true);
        for (var i = 1; i < points.Length; i++) open.LineTo(new Point(points[i].X, points[i].Y));
        open.EndFigure(true);
        return geometry;
    }
}
