using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>Which part of a series an edit or a delete applies to.</summary>
public enum EditScope
{
    /// <summary>The one occurrence, kept as a sibling item with its own RECURRENCE-ID.</summary>
    Occurrence,

    /// <summary>The whole series, master and every override with it.</summary>
    Series,

    /// <summary>Nothing: the prompt was cancelled.</summary>
    None,
}

/// <summary>
/// "Open this occurrence" or "Open the entire series" — the question the reference asks before
/// it lets anyone touch a repeating appointment.
/// </summary>
/// <remarks>
/// The two answers are very different data operations: one occurrence becomes an override
/// stored beside its master, or an EXDATE taken out of it; the series is the master itself. Asking
/// is not a courtesy — without it, editing one week's meeting would silently move every week's.
/// </remarks>
public sealed class EditScopePrompt : Window
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public EditScope Scope { get; private set; } = EditScope.None;

    /// <param name="deleting">Changes the wording to Delete, as the reference's own does.</param>
    public EditScopePrompt(string summary, bool deleting)
    {
        Title = deleting ? "Confirm Delete" : "Open Recurring Item";
        Width = 430;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var verb = deleting ? "delete" : "open";
        var question = new TextBlock
        {
            Text = $"“{summary}” is a recurring appointment. Do you want to {verb} only this occurrence "
                   + $"or the series?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(question, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var occurrence = new RadioButton { GroupName = "scope", Content = deleting ? "Delete this occurrence" : "Open this occurrence", IsChecked = true };
        var series = new RadioButton { GroupName = "scope", Content = deleting ? "Delete the series" : "Open the series" };
        Bind(occurrence, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        Bind(series, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            Scope = series.IsChecked == true ? EditScope.Series : EditScope.Occurrence;
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel },
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                buttons,
                new StackPanel { Spacing = 6, Children = { question, occurrence, series } },
            },
        };

        DialogChrome.Apply(this, body);
    }

    /// <summary>Asks, and returns what was chosen. A one-off appointment is never asked about.</summary>
    /// <remarks>
    /// <c>MAILBOX_EDITSCOPE=occurrence|series|cancel</c> answers it without showing it, on a
    /// capture run only. Everything past this prompt — opening one week of a series, deleting one,
    /// overriding one — is on the far side of a modal window the harness cannot click, so without
    /// this the whole occurrence-versus-series half of the calendar has no route to a read-back
    /// at all. The wording it would have shown is logged, since that is the other half of the
    /// claim and the capture of a dialog that never opened cannot carry it.
    /// </remarks>
    public static async Task<EditScope> AskAsync(Window owner, string summary, bool deleting)
    {
        if (Theming.WindowCapture.IsRequested
            && Environment.GetEnvironmentVariable("MAILBOX_EDITSCOPE") is { Length: > 0 } posed)
        {
            var scope = posed.Trim().ToLowerInvariant() switch
            {
                "occurrence" or "this" => EditScope.Occurrence,
                "series" or "all" => EditScope.Series,
                _ => EditScope.None,
            };

            Log.Info($"Harness: “{(deleting ? "Confirm Delete" : "Open Recurring Item")}” asked about "
                     + $"“{summary}” and was answered {scope}.");
            return scope;
        }

        var prompt = new EditScopePrompt(summary, deleting);
        await prompt.ShowDialog(owner);
        return prompt.Scope;
    }
}
