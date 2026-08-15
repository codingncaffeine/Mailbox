using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The row of attachments under a message's header.
/// </summary>
/// <remarks>
/// One chip per attachment, with what it is and how big. Clicking one saves it somewhere the
/// reader chose — rather than opening it from a temporary file nobody can find afterwards,
/// which is how attachments get lost and how they get run by accident.
/// </remarks>
public sealed class AttachmentStrip : Border
{
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };

    public AttachmentStrip()
    {
        Padding = new Thickness(20, 8);
        BorderThickness = new Thickness(0, 0, 0, 1);
        IsVisible = false;

        Bind(this, BorderBrushProperty, "border.subtle.brush");
        Bind(this, BackgroundProperty, "reading.header.background.brush");

        Child = _chips;
    }

    /// <summary>Shows what a message carries, or hides itself when it carries nothing.</summary>
    public void Show(MimeMessage? message)
    {
        _chips.Children.Clear();

        var attachments = message is null
            ? []
            : MessageAttachments.List(message);

        IsVisible = attachments.Count > 0;

        foreach (var attachment in attachments) _chips.Children.Add(Chip(attachment));
    }

    private Control Chip(Attachment attachment)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("attach", 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        row.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = attachment.Name,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        Bind(name, TextBlock.ForegroundProperty, "text.primary.brush");
        row.Children.Add(name);

        var size = new TextBlock
        {
            Text = $"({attachment.Describe()})",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(size, TextBlock.ForegroundProperty, "text.secondary.brush");
        row.Children.Add(size);

        var button = new Button
        {
            Content = row,
            Padding = new Thickness(9, 4),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1),
            [ToolTip.TipProperty] = attachment switch
            {
                { FromTnef: true } => $"{attachment.Name} — carried inside winmail.dat",
                { IsMessage: true } => $"{attachment.Name} — a message, saved as .eml",
                _ => attachment.Name,
            },
        };
        Bind(button, BorderBrushProperty, "border.subtle.brush");
        Bind(button, BackgroundProperty, "surface.raised.brush");

        button.Click += async (_, _) => await SaveAsync(attachment);
        return button;
    }

    /// <summary>
    /// Saves an attachment where the reader asks.
    /// </summary>
    /// <remarks>
    /// Saving rather than opening is deliberate. An attachment opened straight from a temporary
    /// file is one nobody chose to keep and nobody can find again, and a client that opens
    /// whatever a stranger sent without a step in between is doing the attacker's clicking.
    /// <para>
    /// The suggestion is the sanitized name, never the raw one: a file name arrives with the
    /// message and is therefore text a stranger chose. See §19 on hostile input.
    /// </para>
    /// </remarks>
    private async Task SaveAsync(Attachment attachment)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save attachment",
            SuggestedFileName = attachment.SafeName,
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            await using (var stream = File.Create(path))
            {
                attachment.SaveTo(stream);
            }

            Log.Info($"Saved an attachment to {Path.GetDirectoryName(path)}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save an attachment.", ex);
        }
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
