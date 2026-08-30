using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Platform;
using Mailbox.Rendering;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The row of attachments under a message's header.
/// </summary>
/// <remarks>
/// One chip per attachment, with what it is and how big. Clicking one saves it somewhere the
/// reader chose; the chip's menu carries the rest — Open, hands the file to the desktop after a
/// first-time warning; Save As, the click again by its name; Save All, every file into one
/// chosen directory. Opening goes through a private directory under the runtime dir rather than
/// a world-readable temporary one, and never without the warning having been shown once,
/// because a client that opens whatever a stranger sent without a step in between is doing the
/// attacker's clicking.
/// </remarks>
public sealed class AttachmentStrip : Border
{
    /// <summary>The settings key that remembers the open warning has been shown and accepted.</summary>
    private const string OpenWarnedKey = "mail.attachments.openwarned";

    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private IReadOnlyList<Attachment> _attachments = [];
    private bool _posed;

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

        _attachments = message is null
            ? []
            : MessageAttachments.List(message);

        IsVisible = _attachments.Count > 0;

        foreach (var attachment in _attachments) _chips.Children.Add(Chip(attachment));

        PoseOnce();
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

        button.Click += async (_, _) => await SaveAsAsync(attachment);
        button.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            ShowMenu(button, attachment);
            e.Handled = true;
        });

        return button;
    }

    /// <summary>The chip's menu: what a right-click on an attachment offers.</summary>
    private void ShowMenu(Control at, Attachment attachment)
    {
        var menu = new MenuFlyout();

        var open = new MenuItem { Header = "_Open" };
        open.Click += async (_, _) => await OpenAsync(attachment);
        menu.Items.Add(open);

        var saveAs = new MenuItem { Header = "Save _As…" };
        saveAs.Click += async (_, _) => await SaveAsAsync(attachment);
        menu.Items.Add(saveAs);

        var saveAll = new MenuItem
        {
            Header = "Save A_ll Attachments…",
            IsEnabled = _attachments.Count > 1,
        };
        saveAll.Click += async (_, _) => await SaveAllAsync();
        menu.Items.Add(saveAll);

        MenuProbe.Show("the attachment menu", menu, at, atPointer: true);
    }

    /// <summary>
    /// Opens an attachment with whatever the desktop opens that kind of file with.
    /// </summary>
    /// <remarks>
    /// Two deliberate steps stand between a stranger's file and a double-click. The first time
    /// ever, a warning says what opening means and asks; the answer is remembered, as the
    /// reference remembers its own. And the file is written under the runtime directory —
    /// private to this login, gone with it — never a world-readable temporary path, with the
    /// sanitized name, never the raw one: a file name arrives with the message and is text a
    /// stranger chose.
    /// </remarks>
    private async Task OpenAsync(Attachment attachment)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (!App.Settings.GetBool(OpenWarnedKey))
        {
            var opening = await Confirm.AskAsync(
                owner,
                "Open attachment",
                $"“{attachment.SafeName}” came with a message, and opening it runs whatever "
                + "program the desktop uses for that kind of file. Only open attachments you "
                + "are expecting. This is asked once.",
                "Open",
                destructive: false);

            if (!opening)
            {
                Log.Info("An attachment open was declined at the warning.");
                return;
            }

            App.Settings.Set(OpenWarnedKey, true);
        }

        try
        {
            var path = WriteForOpening(attachment);
            var outcome = DesktopOpen.Open(path);
            Log.Info($"Attachment: {(outcome == DesktopOpenResult.Failed ? "the desktop could not open" : "opened")} "
                     + $"“{attachment.SafeName}” ({attachment.Size} bytes) from the runtime directory.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not write an attachment for opening.", ex);
        }
    }

    /// <summary>
    /// The file, written where the desktop can read it and nobody else can: a 0700 directory
    /// under <c>$XDG_RUNTIME_DIR</c>, which is per-login tmpfs and cleaned up with the session.
    /// </summary>
    private static string WriteForOpening(Attachment attachment)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), "mailbox-opened")
            : Path.Combine(runtime, "mailbox", "opened");

        Directory.CreateDirectory(root);

        // 0700, said explicitly: the runtime dir itself is private, but a fallback under /tmp
        // is not, and a directory of opened attachments is the reader's own mail.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A fresh directory per open, so two attachments with one name cannot overwrite each
        // other while one of them is on screen in another program.
        var dir = Path.Combine(root, Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, attachment.SafeName);
        using (var stream = File.Create(path))
        {
            attachment.SaveTo(stream);
        }

        return path;
    }

    /// <summary>
    /// Saves an attachment where the reader asks.
    /// </summary>
    /// <remarks>
    /// Saving on a plain click is deliberate. An attachment opened straight from a temporary
    /// file is one nobody chose to keep and nobody can find again; Open exists on the menu, with
    /// its warning, for the times a look is genuinely all that is wanted.
    /// <para>
    /// The suggestion is the sanitized name, never the raw one: a file name arrives with the
    /// message and is therefore text a stranger chose — hostile input, and treated as such.
    /// </para>
    /// </remarks>
    private async Task SaveAsAsync(Attachment attachment)
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

    /// <summary>Save All: every attachment into one chosen directory, one dialog for the lot.</summary>
    private async Task SaveAllAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save All Attachments",
            AllowMultiple = false,
        });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } directory) return;

        SaveAllTo(directory);
    }

    /// <summary>The write half of Save All, shared with the harness door — no picker in it.</summary>
    private void SaveAllTo(string directory)
    {
        var written = 0;

        try
        {
            foreach (var attachment in _attachments)
            {
                // Two attachments may carry one name; the second becomes "name (2).ext" rather
                // than winning by arriving later.
                var path = Path.Combine(directory, attachment.SafeName);
                for (var n = 2; File.Exists(path); n++)
                {
                    path = Path.Combine(
                        directory,
                        Path.GetFileNameWithoutExtension(attachment.SafeName)
                        + $" ({n})" + Path.GetExtension(attachment.SafeName));
                }

                using var stream = File.Create(path);
                attachment.SaveTo(stream);
                written++;
            }

            Log.Info($"Saved {written} attachment(s) to {directory}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Save All stopped after {written} attachment(s).", ex);
        }
    }

    /// <summary>
    /// The strip's own door: <c>MAILBOX_ATTACHMENT=menu</c> reads the chip menu back,
    /// <c>open:&lt;name&gt;</c> presses Open on the named chip, and <c>saveall:&lt;dir&gt;</c>
    /// runs the write half of Save All into a directory no pose could pick. Once per run, on
    /// the first strip that shows something — the read-back names what it acted on.
    /// </summary>
    private void PoseOnce()
    {
        if (_posed || _attachments.Count == 0) return;
        if (Environment.GetEnvironmentVariable("MAILBOX_ATTACHMENT") is not { Length: > 0 } spec) return;

        _posed = true;

        Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    Log.Info($"Harness: the strip shows {_attachments.Count} attachment(s): "
                             + string.Join(", ", _attachments.Select(a => $"“{a.SafeName}” ({a.Size} bytes)")));

                    if (spec is "menu")
                    {
                        ShowMenu(_chips.Children.OfType<Button>().First(), _attachments[0]);
                        return;
                    }

                    if (spec.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
                    {
                        var wanted = spec["open:".Length..];
                        var attachment = _attachments.FirstOrDefault(
                            a => a.SafeName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                        if (attachment is null)
                        {
                            Log.Info($"Harness: no attachment matches “{wanted}”.");
                            return;
                        }

                        await OpenAsync(attachment);
                        Log.Info($"Harness: open pressed — warned key {App.Settings.GetBool(OpenWarnedKey)}.");
                        return;
                    }

                    if (spec.StartsWith("saveall:", StringComparison.OrdinalIgnoreCase))
                    {
                        var directory = spec["saveall:".Length..];
                        Directory.CreateDirectory(directory);
                        SaveAllTo(directory);

                        foreach (var file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
                        {
                            Log.Info($"Harness: saved “{Path.GetFileName(file)}” ({new FileInfo(file).Length} bytes).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the attachment pose failed.", ex);
                }
            },
            DispatcherPriority.Background);
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
