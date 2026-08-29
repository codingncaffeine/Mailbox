using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Mailbox.App.Views;

/// <summary>
/// The Office dialog kit's shapes, re-inked with the <c>systemdialog.*</c> family. For dialogs
/// the reference draws with the operating system's own controls — the editor's family and the
/// rules family alike — which stay the desktop's light grey in every theme, the way Account
/// Settings does. Their shapes and measures were right already; only the palette changes.
/// </summary>
internal static class SystemInkKit
{
    internal static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => ViewDialogKit.Bind(target, property, key);

    internal static TextBlock Label(string text, bool bold = false, bool subtle = false)
    {
        var block = ViewDialogKit.Label(text, bold, subtle);
        Bind(block, TextBlock.ForegroundProperty,
            subtle ? "systemdialog.foreground.subtle.brush" : "systemdialog.foreground.brush");
        return block;
    }

    /// <summary>A checkbox or radio in the dialog's ink.</summary>
    internal static T Ink<T>(T control) where T : TemplatedControl
    {
        Bind(control, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
        return control;
    }

    internal static Border Boxed(Control content, double? width = null, double? height = null)
    {
        var box = ViewDialogKit.Boxed(content, width, height);
        Bind(box, Border.BackgroundProperty, "systemdialog.list.background.brush");
        Bind(box, Border.BorderBrushProperty, "systemdialog.list.border.brush");
        return box;
    }

    /// <summary>A list box on the dialog's white ground, its rows in the dialog's ink.</summary>
    internal static ListBox SurfaceList(double width, double height)
    {
        var list = ViewDialogKit.SurfaceList(width, height);
        Bind(list, TemplatedControl.BackgroundProperty, "systemdialog.list.background.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "systemdialog.list.border.brush");
        return list;
    }

    internal static TextBlock SurfaceText(string text)
    {
        var block = ViewDialogKit.SurfaceText(text);
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    internal static StackPanel Buttons(params Control[] buttons) => ViewDialogKit.Buttons(buttons);

    internal static Button Ok(Action click, string label = "OK") => ViewDialogKit.Ok(click, label);

    internal static Button Cancel(Window window, string label = "Cancel") => ViewDialogKit.Cancel(window, label);
}
