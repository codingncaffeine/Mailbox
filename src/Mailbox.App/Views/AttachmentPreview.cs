using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;

namespace Mailbox.App.Views;

/// <summary>
/// A file from the attachment strip, shown before any save: pictures and plain text drawn in
/// place, and an honest sentence for everything else.
/// </summary>
/// <remarks>
/// The reference previews attachments through the programs installed beside it, which is an
/// ecosystem this platform does not have — so what previews here is what this application can
/// draw itself, and the rest says so rather than showing a broken pane. The control sits over
/// the message body in whichever host the strip lives in, and Back hands the body back.
/// </remarks>
public sealed class AttachmentPreview : Border
{
    /// <summary>Text preview stops here, so a log the size of a mailbox cannot eat the window.</summary>
    private const int TextCap = 256 * 1024;

    /// <summary>Nothing bigger is read at all: a preview is a look, not a load test.</summary>
    private const int BytesCap = 16 * 1024 * 1024;

    private readonly TextBlock _title = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _meta = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _content = new();
    private AttachmentStrip? _source;
    private Attachment? _showing;

    public AttachmentPreview()
    {
        IsVisible = false;
        Bind(this, BackgroundProperty, "reading.background.brush");

        var open = new Button { Content = "Open" };
        open.Click += async (_, _) =>
        {
            if (_source is { } strip && _showing is { } attachment) await strip.OpenAsync(attachment);
        };

        var save = new Button { Content = "Save As…" };
        save.Click += async (_, _) =>
        {
            if (_source is { } strip && _showing is { } attachment) await strip.SaveAsAsync(attachment);
        };

        var back = new Button { Content = "Back to message" };
        back.Click += (_, _) => Hide();

        Bind(_title, TextBlock.ForegroundProperty, "text.primary.brush");
        Bind(_meta, TextBlock.ForegroundProperty, "text.secondary.brush");

        var bar = new Border
        {
            Padding = new Thickness(20, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(back, Dock.Right),
                    Docked(save, Dock.Right),
                    Docked(open, Dock.Right),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _title, _meta },
                    },
                },
            },
        };
        Bind(bar, BorderBrushProperty, "border.subtle.brush");
        Bind(bar, BackgroundProperty, "reading.header.background.brush");

        Child = new DockPanel
        {
            Children =
            {
                Docked(bar, Dock.Top),
                _content,
            },
        };

        static Control Docked(Control control, Dock dock)
        {
            control.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(control, dock);
            return control;
        }
    }

    /// <summary>Shows one attachment, deciding what it is from its type and then its name.</summary>
    public void Show(Attachment attachment, AttachmentStrip source)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _source = source;
        _showing = attachment;
        _title.Text = attachment.SafeName;
        _meta.Text = $"({attachment.Describe()})";

        _content.Content = Build(attachment);
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
        _content.Content = null;
        _showing = null;
    }

    private Control Build(Attachment attachment)
    {
        if (attachment.Size > BytesCap)
        {
            Log.Info($"Preview: “{attachment.SafeName}” is {attachment.Describe()} — too big to preview.");
            return Sentence($"“{attachment.SafeName}” is {attachment.Describe()} — too big to "
                            + "preview. Open it or save it instead.");
        }

        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            attachment.SaveTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview: “{attachment.SafeName}” could not be read.", ex);
            return Sentence($"“{attachment.SafeName}” could not be read out of the message.");
        }

        if (IsPicture(attachment))
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = Bitmap.DecodeToWidth(stream, 1600);
                Log.Info($"Preview: “{attachment.SafeName}” as a picture — "
                         + $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}, {bytes.Length} byte(s).");

                return new ScrollViewer
                {
                    Content = new Image
                    {
                        Source = bitmap,
                        Stretch = Avalonia.Media.Stretch.Uniform,
                        StretchDirection = Avalonia.Media.StretchDirection.DownOnly,
                        Margin = new Thickness(20),
                    },
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Preview: “{attachment.SafeName}” did not decode as a picture.", ex);
                return Sentence($"“{attachment.SafeName}” says it is a picture and could not be "
                                + "read as one. Open it or save it instead.");
            }
        }

        if (IsText(attachment))
        {
            var capped = bytes.Length > TextCap;
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, TextCap));
            Log.Info($"Preview: “{attachment.SafeName}” as text — {text.Length} character(s)"
                     + (capped ? " (capped)" : string.Empty) + ".");

            var block = new SelectableTextBlock
            {
                Text = capped
                    ? text + $"{Environment.NewLine}— showing the first {TextCap / 1024} KB —"
                    : text,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(20),
            };
            Bind(block, TextBlock.ForegroundProperty, "text.primary.brush");

            return new ScrollViewer { Content = block };
        }

        Log.Info($"Preview: “{attachment.SafeName}” ({attachment.MimeType}) has no preview here.");
        return Sentence($"No preview for this kind of file ({attachment.MimeType}). Open it with "
                        + "the desktop's own program, or save it.");
    }

    private Control Sentence(string words)
    {
        var block = new TextBlock
        {
            Text = words,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(block, TextBlock.ForegroundProperty, "text.secondary.brush");
        return block;
    }

    private static bool IsPicture(Attachment attachment)
        => attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
           || Path.GetExtension(attachment.SafeName).ToLowerInvariant()
               is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private static bool IsText(Attachment attachment)
        => attachment.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           || Path.GetExtension(attachment.SafeName).ToLowerInvariant()
               is ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv";

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
