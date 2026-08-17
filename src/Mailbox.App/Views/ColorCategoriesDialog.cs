using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// Puts a renamed or removed category right on the items that carried it.
/// </summary>
/// <remarks>
/// A parameter rather than something the dialog does itself: an item's categories live in its own
/// iCalendar or vCard text and only the codecs can put them back, so the shell — which has all
/// four of them — supplies the doing of it.
/// </remarks>
/// <param name="items">The items that carried the old name.</param>
/// <param name="from">The name they carry now.</param>
/// <param name="to">What to call it instead, or null when the category has gone.</param>
public delegate void CategoryRewrite(IReadOnlyList<PimItem> items, string from, string? to);

/// <summary>
/// Manage Colour Categories: the account's categories, with the way to create, rename, recolour,
/// delete and shortcut them.
/// </summary>
/// <remarks>
/// The categories are one set across every module (§9), so this manages that set rather than an
/// account's own: the master list lives in the PIM store and every mail account keeps a mirror of
/// it, which <see cref="CategoryBook"/> holds in step. The colour of a category is a theme token,
/// not a value, which is what keeps it legible when the theme changes — so the palette a category
/// is coloured from is the six the theme defines, and recolouring picks another of them rather
/// than a free colour that a dark theme would swallow.
/// <para>
/// Renaming and deleting reach the items as well as the list: both hand back what carried the
/// name and the shell's <see cref="CategoryRewrite"/> writes each one again through the same path
/// an edit takes, so the change queues for the server like any other.
/// </para>
/// </remarks>
public sealed class ColorCategoriesDialog : Window
{
    private readonly CategoryBook _categories;
    private readonly CategoryRewrite? _rewrite;
    private readonly ListBox _list = new() { MinHeight = 220 };

    /// <summary>The six the theme defines, by the names the reader knows them by.</summary>
    private static readonly (string Name, string Token)[] Palette =
    [
        ("Red", "category.red"), ("Orange", "category.orange"), ("Yellow", "category.yellow"),
        ("Green", "category.green"), ("Blue", "category.blue"), ("Purple", "category.purple"),
    ];

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public ColorCategoriesDialog(CategoryBook categories, CategoryRewrite? rewrite = null)
    {
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _rewrite = rewrite;

        Title = "Colour Categories";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        DialogChrome.Apply(this, Layout());
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Reload();
    }

    private Control Layout()
    {
        _list.ItemTemplate = new FuncDataTemplate<Category>((category, _) => Row(category));

        var newButton = Action("New…", NewCategory);
        var rename = Action("Rename…", RenameSelected);
        var colour = Action("Colour…", RecolourSelected);
        var shortcut = Action("Shortcut…", ShortcutSelected);
        var delete = Action("Delete", DeleteSelected);

        var buttons = new StackPanel
        {
            Spacing = 8,
            Width = 110,
            Children = { newButton, rename, colour, shortcut, delete },
        };

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                Heading("Colour categories let you group items across mail, calendar and contacts."),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { new Border { Child = _list, Width = 280 }, buttons },
                },
                close,
            },
        };

        return body;
    }

    private Control Row(Category category)
    {
        var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3) };
        Bind(swatch, Border.BackgroundProperty, category.ColourToken + ".brush");

        var name = new TextBlock { Text = category.Name, VerticalAlignment = VerticalAlignment.Center };
        Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var shortcut = new TextBlock
        {
            Text = category.Shortcut ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Bind(shortcut, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(6, 4),
        };
        Grid.SetColumn(swatch, 0);
        swatch.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(swatch);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(shortcut, 2);
        grid.Children.Add(shortcut);
        return grid;
    }

    private static Button Action(string label, Func<Task> run)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += async (_, _) => await run();
        return button;
    }

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private void Reload()
    {
        var categories = _categories.All();
        _list.ItemsSource = categories;
        if (categories.Count > 0) _list.SelectedIndex = 0;
    }

    private Category? Selected => _list.SelectedItem as Category;

    private async Task NewCategory()
    {
        if (await Prompt.AskAsync(this, "New Category", "Name:") is not { Length: > 0 } name) return;
        if (await PickColour("Colour for " + name) is not { } token) return;

        _categories.Add(name.Trim(), token);
        Reload();
    }

    private async Task RenameSelected()
    {
        if (Selected is not { } category) return;
        if (await Prompt.AskAsync(this, "Rename Category", "Name:", category.Name) is not { Length: > 0 } name) return;

        var carried = _categories.Rename(category.Id, name.Trim());
        _rewrite?.Invoke(carried, category.Name, name.Trim());
        Reload();
    }

    private async Task RecolourSelected()
    {
        if (Selected is not { } category) return;
        if (await PickColour("Colour for " + category.Name) is not { } token) return;

        _categories.Recolour(category.Id, token);
        Reload();
    }

    private async Task ShortcutSelected()
    {
        if (Selected is not { } category) return;

        var choices = new List<Choice> { new("None", string.Empty, "no shortcut") };
        for (var n = 1; n <= 9; n++) choices.Add(new Choice($"Ctrl+F{n}", $"Ctrl+F{n}"));

        if (await Chooser.AskAsync(this, "Category Shortcut", "Shortcut:", choices, category.Shortcut ?? "None")
            is not { } chosen)
        {
            return;
        }

        _categories.SetShortcut(category.Id, chosen.Length == 0 ? null : chosen);
        Reload();
    }

    private async Task DeleteSelected()
    {
        if (Selected is not { } category) return;

        var go = await Confirm.AskAsync(this, "Delete Category",
            $"Delete the “{category.Name}” category? It will be removed from every item that has it.",
            "Delete", destructive: true);
        if (!go) return;

        var carried = _categories.Delete(category.Id);
        _rewrite?.Invoke(carried, category.Name, null);
        Reload();
    }

    private async Task<string?> PickColour(string title)
    {
        var choices = Palette.Select(p => new Choice(p.Name, p.Token)).ToList();
        return await Chooser.AskAsync(this, title, "Colour:", choices);
    }
}
