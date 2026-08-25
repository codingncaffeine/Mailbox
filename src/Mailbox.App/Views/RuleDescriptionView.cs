using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Rules;

namespace Mailbox.App.Views;

/// <summary>
/// The rule description pane the reference draws under a rule: one clause per line, with the
/// value in each — the people, the words, the folder — underlined and clickable to edit.
/// </summary>
/// <remarks>
/// Built from <see cref="RuleDescription.Describe"/>, which says which words in a clause are
/// the editable value, so the pane never parses a sentence back. A click on a value tells the
/// owner which clause it was — the owner knows what conditions and actions the clauses stand for.
/// </remarks>
public sealed class RuleDescriptionView : Border
{
    private readonly StackPanel _lines = new() { Spacing = 3 };

    /// <summary>Which ink the plain words take — the app's chrome, or a system dialog's.</summary>
    private string _ink = "dialog.surface.text.brush";

    /// <summary>What is on show, so a palette change can redraw it.</summary>
    private MailRule? _rule;

    /// <summary>
    /// Draws the description on a system dialog's page instead of the application's chrome.
    /// </summary>
    /// <remarks>
    /// Rules and Alerts and the wizard are system dialogs — the reference draws both with the
    /// desktop's own controls — and a description keeping the dark chrome's ink inside a light
    /// box would be unreadable in exactly the place a rule is checked.
    /// </remarks>
    public void UseSystemPalette()
    {
        _ink = "systemdialog.foreground.brush";
        Bind(this, BackgroundProperty, "systemdialog.list.background.brush");
        Bind(this, BorderBrushProperty, "systemdialog.field.border.brush");
        Show(_rule);
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public RuleDescriptionView()
    {
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 8);
        MinHeight = 120;
        Child = new ScrollViewer { Content = _lines };
        Bind(this, BackgroundProperty, "dialog.surface.brush");
        Bind(this, BorderBrushProperty, "dialog.border.brush");
        _ink = "dialog.surface.text.brush";
    }

    /// <summary>
    /// Raised when an underlined value is clicked, with the index of the clause among those
    /// <see cref="RuleDescription.Describe"/> returned (the heading is 0).
    /// </summary>
    public event EventHandler<int>? ValueClicked;

    /// <summary>Redraws the pane for a rule, or clears it for none.</summary>
    public void Show(MailRule? rule)
    {
        _rule = rule;
        _lines.Children.Clear();
        if (rule is null) return;

        var clauses = RuleDescription.Describe(rule);
        for (var i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            var prefix = i switch
            {
                0 => string.Empty,
                1 => string.Empty,
                _ when clause.Text.StartsWith("except", StringComparison.Ordinal) || clause.Text.StartsWith("or ", StringComparison.Ordinal) => string.Empty,
                _ => "and ",
            };

            _lines.Children.Add(Line(prefix + clause.Text, clause.Editable, i, indent: i == 0 ? 0 : 12));
        }
    }

    private Control Line(string text, string? editable, int index, double indent)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(indent, 0, 0, 0) };

        var at = editable is { Length: > 0 } ? text.IndexOf(editable, StringComparison.Ordinal) : -1;
        if (at < 0)
        {
            row.Children.Add(Plain(text));
            return row;
        }

        if (at > 0) row.Children.Add(Plain(text[..at]));

        var link = new TextBlock
        {
            Text = editable,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        Bind(link, TextBlock.ForegroundProperty, "text.link.brush");
        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ValueClicked?.Invoke(this, index);
        };
        row.Children.Add(link);

        var rest = text[(at + editable!.Length)..];
        if (rest.Length > 0) row.Children.Add(Plain(rest));

        return row;
    }

    private TextBlock Plain(string text)
    {
        var block = new TextBlock { Text = text };
        Bind(block, TextBlock.ForegroundProperty, _ink);
        return block;
    }
}
