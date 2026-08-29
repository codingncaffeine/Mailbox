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

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<MessageStateIcon, IBrush?>(nameof(Ink));

    static MessageStateIcon()
    {
        AffectsRender<MessageStateIcon>(IsUnreadProperty, InkProperty);
    }

    public MessageStateIcon()
    {
        Width = 16;
        Height = 16;
        this[!InkProperty] = new DynamicResourceExtension("text.secondary.brush");
    }

    public bool IsUnread
    {
        get => GetValue(IsUnreadProperty);
        set => SetValue(IsUnreadProperty, value);
    }

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
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
            return;
        }

        // Open: the flap standing above an emptied pocket, the letter's edge showing.
        context.DrawGeometry(null, pen, Lines((2.5, 7.5), (8, 2.5), (13.5, 7.5)));
        context.DrawGeometry(null, pen, Lines((4.5, 7.5), (4.5, 5.5), (11.5, 5.5), (11.5, 7.5)));
        context.DrawRectangle(null, pen, new Rect(2.5, 7.5, 11, 6));
        context.DrawGeometry(null, pen, Lines((2.5, 7.5), (8, 11.5), (13.5, 7.5)));
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
}
