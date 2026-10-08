using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Copying out of a message: Ctrl+C and the pane's own Copy / Select All menu.
/// </summary>
/// <remarks>
/// Chromium draws off screen with no display connection of its own, so it has no way to reach the
/// desktop's clipboard: a reader selecting a sign-in code and pressing Ctrl+C would get nothing to
/// paste. Chromium reports every change of selection, and the pane puts that text on the
/// clipboard itself.
/// <para>
/// The right button is taken before the engine sees it. The engine marks every pointer event it
/// is handed as handled, so a menu attached the ordinary way never opens over the message; and
/// the engine's own menu, where it has one, belongs to an off-screen window with nowhere to draw.
/// </para>
/// </remarks>
public sealed partial class ReadingPaneBody
{
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
    /// Ctrl+C copies, Ctrl+A selects the message.
    /// </summary>
    private void OnSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || !ReferenceEquals(_surface.Content, _web)) return;

        if (e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            _ = SelectAllAsync();
            e.Handled = true;
        }
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
    /// <returns>The text copied; empty when nothing was selected or there is no engine.</returns>
    internal async Task<string> CopySelectionAsync()
    {
        var text = _web is { } web && ReferenceEquals(_surface.Content, web) ? web.SelectedText : string.Empty;

        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(clipboard, DataFormat.Text, text);
        }

        if (Mailbox.App.Theming.WindowCapture.IsRequested)
            Log.Info($"Harness: reading copy — {text.Length} character(s) put on the clipboard.");

        return text;
    }

    /// <summary>Selects the whole message, so the next copy takes all of it.</summary>
    internal Task SelectAllAsync()
    {
        if (_web is { } web && ReferenceEquals(_surface.Content, web)) web.SelectAll();
        return Task.CompletedTask;
    }
}
