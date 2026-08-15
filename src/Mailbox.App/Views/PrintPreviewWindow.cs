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
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(rows);

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
                e.IsSuccess ? $"Print preview: {rows.Count} rows." : "The print preview would not load.");
            root.Children.Add(_web);
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

        var style = new RenderStyle(
            themes.Tokens.GetString(TokenKeys.Reading.Background),
            themes.Tokens.GetString(TokenKeys.Text.Primary),
            themes.Tokens.GetString(TokenKeys.Text.Link),
            themes.Tokens.GetString(TokenKeys.Text.Secondary),
            themes.Tokens.GetString(TokenKeys.Typography.ContentFamily),
            13);

        _web?.NavigateToString(
            TablePrint.Render(folder, rows, style, DateTimeOffset.Now), new Uri("about:blank"));
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
