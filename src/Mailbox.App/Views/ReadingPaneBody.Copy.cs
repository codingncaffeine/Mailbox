using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Copying out of a message: Ctrl+C and the pane's own Copy / Select All menu.
/// </summary>
/// <remarks>
/// The engine draws off screen, so its clipboard is not necessarily the desktop's: WPE's goes to
/// a pasteboard of its own that no other application can read, and a reader selecting a sign-in
/// code and pressing Ctrl+C got nothing to paste. So the pane asks the engine what is selected and
/// puts it on the clipboard itself — the same answer whichever engine is drawing.
/// <para>
/// The right button is taken before the engine sees it. The engine marks every pointer event it
/// is handed as handled, so a menu attached the ordinary way never opens over the message; and
/// the engine's own menu, where it has one, belongs to an off-screen window with nowhere to draw.
/// </para>
/// </remarks>
public sealed partial class ReadingPaneBody
{
    private const string SelectionScript = "window.getSelection().toString()";

    private const string SelectAllScript =
        "(function(){ var s = window.getSelection(); s.removeAllRanges();"
        + " var r = document.createRange(); r.selectNodeContents(document.body); s.addRange(r);"
        + " return 'ok'; })()";

    private ContextMenu? _copyMenu;

    /// <summary>Wires the shortcut and the menu onto the surface. Called once, from the constructor.</summary>
    private void WireCopy()
    {
        _surface.AddHandler(KeyDownEvent, OnSurfaceKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _surface.AddHandler(PointerPressedEvent, OnSurfacePointerPressed, RoutingStrategies.Tunnel);
        _surface.AddHandler(PointerReleasedEvent, OnSurfacePointerReleased, RoutingStrategies.Tunnel);

        var copy = new MenuItem { Header = "_Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += async (_, _) => await CopySelectionAsync();

        var selectAll = new MenuItem { Header = "Select _All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += async (_, _) => await SelectAllAsync();

        _copyMenu = new ContextMenu { ItemsSource = new[] { copy, selectAll } };
    }

    /// <summary>
    /// Ctrl+C copies, Ctrl+A selects the message. Left unhandled so the engine still sees both:
    /// where its clipboard is the desktop's, it puts the same text there.
    /// </summary>
    private void OnSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || !ReferenceEquals(_surface.Content, _web)) return;

        if (e.Key == Key.C) _ = CopySelectionAsync();
        else if (e.Key == Key.A) _ = SelectAllAsync();
    }

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(_surface.Content, _web)) return;
        if (e.GetCurrentPoint(_surface).Properties.IsRightButtonPressed) e.Handled = true;
    }

    private void OnSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_surface.Content, _web) || e.InitialPressMouseButton != MouseButton.Right) return;

        e.Handled = true;
        _copyMenu?.Open(_surface);
    }

    /// <summary>
    /// Puts what the reader selected in the message on the clipboard, and says what it put there.
    /// </summary>
    /// <returns>The text copied; empty when nothing was selected or there is no engine to ask.</returns>
    internal async Task<string> CopySelectionAsync()
    {
        var text = await AskEngineAsync(SelectionScript);

        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(clipboard, DataFormat.Text, text);
        }

        if (Mailbox.App.Theming.WindowCapture.IsRequested)
            Log.Info($"Harness: reading copy — {text.Length} character(s) put on the clipboard.");

        return text;
    }

    /// <summary>Selects the whole message, so the next copy takes all of it.</summary>
    internal async Task SelectAllAsync() => await AskEngineAsync(SelectAllScript);

    /// <summary>One script against the engine on show, answered as text; empty when there is none.</summary>
    private async Task<string> AskEngineAsync(string script)
    {
        if (_web is not { } web || !ReferenceEquals(_surface.Content, web)) return string.Empty;

        try
        {
            var answer = await Dispatcher.UIThread.InvokeAsync(async () => await web.InvokeScript(script));
            return answer?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn("The reading pane could not read the selection from its engine.", ex);
            return string.Empty;
        }
    }
}
