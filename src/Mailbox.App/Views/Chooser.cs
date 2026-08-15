using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Mailbox.App.Views;

/// <summary>One thing that can be picked: what is shown, and what it stands for.</summary>
/// <param name="Label">What the reader sees.</param>
/// <param name="Value">What the caller gets back.</param>
/// <param name="Note">A second line, for saying what the choice costs or means.</param>
public sealed record Choice(string Label, string Value, string? Note = null);

/// <summary>
/// Picks one from a list, themed like the rest of the application.
/// </summary>
/// <remarks>
/// The third of the small dialogs, beside <see cref="Confirm"/> and <see cref="Prompt"/>, and
/// there for the same reason: Avalonia has no list picker, and a dependency's would be the one
/// window in a themed application that ignores the theme.
/// <para>
/// A ribbon gallery is the reference's answer to most of these, and this is not that — a gallery
/// previews live and this does not. What it is for is the choices a command has to make before
/// it can act, on a ribbon whose controls report only which command was pressed and never a
/// value with it. Replacing these with real galleries is ribbon work, not compose work.
/// </para>
/// </remarks>
public static class Chooser
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The value chosen, or null if the dialog was dismissed.</summary>
    public static async Task<string?> AskAsync(
        Window owner, string title, string label, IReadOnlyList<Choice> choices,
        string? current = null)
    {
        ArgumentNullException.ThrowIfNull(choices);

        string? answer = null;

        var caption = new TextBlock { Text = label };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var list = new ListBox
        {
            ItemsSource = choices,
            Height = Math.Min(360, Math.Max(120, choices.Count * 34)),
            Width = 320,
            ItemTemplate = new FuncDataTemplate<Choice>((choice, _) =>
            {
                var stack = new StackPanel { Margin = new Thickness(2) };

                var name = new TextBlock { Text = choice.Label };
                Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
                stack.Children.Add(name);

                if (choice.Note is { Length: > 0 } note)
                {
                    var second = new TextBlock { Text = note, FontSize = 11, Opacity = 0.75 };
                    Bind(second, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
                    stack.Children.Add(second);
                }

                return stack;
            }),
        };

        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        list.SelectedIndex = current is null
            ? 0
            : Math.Max(0, choices.ToList().FindIndex(
                c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase)));

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            answer = (list.SelectedItem as Choice)?.Value;
            window.Close();
        };

        // Double-clicking a row is how a list like this is used, and having to travel to OK
        // afterwards is the kind of thing that makes a dialog feel like a form.
        list.DoubleTapped += (_, _) =>
        {
            answer = (list.SelectedItem as Choice)?.Value;
            window.Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        DialogChrome.Apply(window, body);

        window.Opened += (_, _) => list.Focus();

        await window.ShowDialog(owner);
        return answer;
    }
}
