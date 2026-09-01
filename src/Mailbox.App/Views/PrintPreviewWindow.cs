using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;
using Mailbox.Theming;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// A folder as a printable list, with the print and PDF buttons over it.
/// </summary>
/// <remarks>
/// Its own window rather than the reading pane, which is showing a message the reader has not
/// asked to lose. Same engine and same stylesheet as everything else that reaches paper, so
/// what is on screen here is what comes out of the printer.
/// </remarks>
public sealed class PrintPreviewWindow : Window
{
    private readonly NativeWebView? _web;

    public PrintPreviewWindow(ThemeService themes, string folder, IReadOnlyList<TableRow> rows)
        : this(themes, folder, RenderTable(themes, folder, rows))
    {
    }

    /// <summary>The list style's document, built before the window so the one ctor renders both.</summary>
    private static string RenderTable(ThemeService themes, string folder, IReadOnlyList<TableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return TablePrint.Render(folder, rows, Style(themes), DateTimeOffset.Now);
    }

    private static RenderStyle Style(ThemeService themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        return new RenderStyle(
            themes.Tokens.GetString(TokenKeys.Reading.Background),
            themes.Tokens.GetString(TokenKeys.Text.Primary),
            themes.Tokens.GetString(TokenKeys.Text.Link),
            themes.Tokens.GetString(TokenKeys.Text.Secondary),
            themes.Tokens.GetString(TokenKeys.Typography.ContentFamily),
            13);
    }

    /// <summary>
    /// The calendar on paper: the days on show in the style the arrangement asks for.
    /// </summary>
    /// <remarks>
    /// Through <see cref="CalendarPrint"/> and then the same engine everything else printed here
    /// goes through, so a printed week comes off the stylesheet a printed message does.
    /// </remarks>
    public static PrintPreviewWindow ForCalendar(
        ThemeService themes,
        CalendarPrintStyle kind,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<PrintedAppointment> items)
    {
        var title = CalendarPrint.Title(kind, from, to);
        return new PrintPreviewWindow(
            themes, title, CalendarPrint.Render(kind, from, to, items, Style(themes), DateTimeOffset.Now));
    }

    /// <summary>A note or any other small document: the same window over ready-made markup.</summary>
    public static PrintPreviewWindow ForText(ThemeService themes, string title, string text)
    {
        var style = Style(themes);
        var body = System.Net.WebUtility.HtmlEncode(text ?? string.Empty).Replace("\n", "<br>", StringComparison.Ordinal);
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"></head>"
                   + $"<body style=\"background:{style.Background};color:{style.Foreground};"
                   + $"font-family:{style.FontFamily};font-size:13px;margin:24px\">"
                   + $"<h3>{System.Net.WebUtility.HtmlEncode(title)}</h3><p>{body}</p></body></html>";
        return new PrintPreviewWindow(themes, title, html);
    }

    private PrintPreviewWindow(ThemeService themes, string folder, string html)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(html);

        Title = $"Print — {folder}";
        Width = 820;
        Height = 720;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();

        var bar = Toolbar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        try
        {
            _web = new NativeWebView();
            Bind(_web, NativeWebView.BackgroundProperty, "reading.background.brush");

            // A preview that renders nothing looks the same in a capture as one that rendered
            // correctly, since the engine composites offscreen. The log is where it is checked.
            _web.NavigationCompleted += (_, e) => Log.Info(
                e.IsSuccess ? $"Print preview: {html.Length} characters." : "The print preview would not load.");
            root.Children.Add(_web);

            // The same reason the message window releases its own: a preview holds a whole
            // engine, and printing twice should not cost two.
            var engine = _web;
            var host = root;
            Closed += (_, _) =>
            {
                try { engine.Stop(); }
                catch (Exception ex) { Log.Debug($"The preview's engine did not stop cleanly: {ex.Message}"); }

                host.Children.Remove(engine);
            };
        }
        catch (Exception ex)
        {
            Log.Warn("No web engine is available, so the list cannot be previewed.", ex);
            root.Children.Add(new TextBlock
            {
                Text = "This list cannot be printed: no web engine is available.",
                Margin = new Thickness(20),
            });
        }

        DialogChrome.Apply(this, root);

        _web?.NavigateToString(html, new Uri("about:blank"));
    }

    private Control Toolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 10),
        };

        var print = new Button { Content = "Print...", Padding = new Thickness(12, 4) };
        print.Click += (_, _) => _web?.ShowPrintUI();
        row.Children.Add(print);

        var pdf = new Button { Content = "Save as PDF...", Padding = new Thickness(12, 4) };
        pdf.Click += async (_, _) => await SaveAsync();
        row.Children.Add(pdf);

        return row;
    }

    private async Task SaveAsync()
    {
        if (_web is null) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save list as PDF",
            SuggestedFileName = "messages.pdf",
            DefaultExtension = "pdf",
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            await using var pdf = await _web.PrintToPdfStreamAsync();
            await using var destination = File.Create(path);
            await pdf.CopyToAsync(destination);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the list to PDF.", ex);
        }
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
