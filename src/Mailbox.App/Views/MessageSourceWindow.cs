using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The message exactly as it arrived: headers, boundaries, encodings and all.
/// </summary>
/// <remarks>
/// Not in the reference's default ribbon, which is why the command that opens it is one of the
/// additions in §12. It is the thing that settles an argument about what a sender actually sent
/// — a header the reading pane summarises, an encoding that came out wrong, a signature that
/// did not verify — and it costs nothing, because the bytes are already in the store.
/// <para>
/// Read-only and monospaced. Wrapping is off: a header folded by the sender is folded here,
/// because re-wrapping it would be showing something other than what arrived.
/// </para>
/// </remarks>
public sealed class MessageSourceWindow : Window
{
    public MessageSourceWindow(string subject, byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        Title = string.IsNullOrWhiteSpace(subject) ? "Message Source" : $"Source — {subject}";
        Width = 900;
        Height = 700;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Text = Decode(raw),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = default,
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // The theme's monospaced family rather than a name in this file: a family asked for by its
        // bare name skips the metric-compatible substitution, and a bundled one is never found.
        Bind(text, TemplatedControl.FontFamilyProperty, "mono.fontfamily");
        Bind(text, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(text, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");

        var box = new Border
        {
            Child = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12),
        };
        Bind(box, BorderBrushProperty, "dialog.border.brush");
        Bind(box, BackgroundProperty, "dialog.surface.brush");

        DialogChrome.Apply(this, box);
    }

    /// <summary>
    /// Turns the bytes into text without deciding anything about them.
    /// </summary>
    /// <remarks>
    /// A message is bytes, and a message worth looking at the source of is often one whose
    /// encoding is the problem. Decoded as UTF-8 where it is valid and as Latin-1 where it is
    /// not, so a mis-encoded header shows as the mojibake it is rather than as replacement
    /// characters that hide which byte was wrong.
    /// </remarks>
    private static string Decode(byte[] raw)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
