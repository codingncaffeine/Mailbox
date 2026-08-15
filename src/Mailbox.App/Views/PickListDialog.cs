using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Mailbox.App.Views;

/// <summary>
/// Ticks some entries out of a long list, themed like the rest of the application.
/// </summary>
/// <remarks>
/// The fourth of the small dialogs, beside <see cref="Confirm"/>, <see cref="Prompt"/> and
/// <see cref="Chooser"/>: a checkbox per row, Select All and Clear All, OK and Cancel. What the
/// reference's Blocked Top-Level Domain List and Blocked Encodings List are, and what a rules
/// wizard's "specific words" list is not — that one is add-and-remove rather than tick.
/// </remarks>
public static class PickListDialog
{
    /// <summary>One row: what it says, and what ticking it stands for.</summary>
    public sealed record Item(string Label, string Value);

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The values ticked when OK was pressed, or null when the dialog was dismissed.</summary>
    public static async Task<IReadOnlyList<string>?> PickAsync(
        Window owner, string title, string label, IReadOnlyList<Item> items, IReadOnlyCollection<string> ticked)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ticked);

        IReadOnlyList<string>? answer = null;
        var state = items.ToDictionary(i => i.Value, i => ticked.Contains(i.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var boxes = new List<CheckBox>(items.Count);

        var caption = new TextBlock { Text = label };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };
        foreach (var item in items)
        {
            var box = new CheckBox { Content = item.Label, IsChecked = state[item.Value], Tag = item.Value };
            Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
            box.IsCheckedChanged += (_, _) => state[item.Value] = box.IsChecked == true;
            boxes.Add(box);
            rows.Children.Add(box);
        }

        var list = new Border
        {
            Width = 360,
            Height = 360,
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = rows },
        };
        Bind(list, Border.BackgroundProperty, "dialog.surface.brush");
        Bind(list, Border.BorderBrushProperty, "dialog.border.brush");

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var selectAll = new Button { Content = "Select All" };
        selectAll.Click += (_, _) => { foreach (var box in boxes) box.IsChecked = true; };
        var clearAll = new Button { Content = "Clear All" };
        clearAll.Click += (_, _) => { foreach (var box in boxes) box.IsChecked = false; };

        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 74 };
        cancel.Click += (_, _) => window.Close();
        var ok = new Button { Content = "OK", IsDefault = true, Width = 74 };
        ok.Click += (_, _) =>
        {
            answer = [.. items.Where(i => state[i.Value]).Select(i => i.Value)];
            window.Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        list,
                        new StackPanel { Spacing = 6, Width = 100, Children = { selectAll, clearAll } },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };

        DialogChrome.Apply(window, body);
        await window.ShowDialog(owner);
        return answer;
    }
}
