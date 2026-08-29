using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The Categorize menu for anything that is not a message: the set with a tick against what the
/// item already carries, Clear All Categories at the head, and All Categories… under it.
/// </summary>
/// <remarks>
/// One implementation for every module and for the appointment window, which is the point — the
/// shell and a window of its own were about to grow a menu each, and two menus over one set drift
/// the day a category is renamed in only one of them. What the caller supplies is what differs:
/// the item's own name, what it carries, and where the answer is written. What comes back is the
/// whole list the item should carry, so a module writes it through its own save path and nothing
/// here knows about payloads.
/// </remarks>
internal static class ItemCategoryMenu
{
    /// <summary>
    /// Opens the menu at <paramref name="anchor"/>, or — under the harness — presses one of its
    /// entries and returns without drawing anything.
    /// </summary>
    /// <param name="subject">What the item is called, for the log line.</param>
    /// <param name="carried">The categories it carries now.</param>
    /// <param name="apply">Given the whole list it should carry afterwards.</param>
    /// <param name="allCategories">
    /// Opens the Color Categories dialog. Null where the caller has nowhere to open one — the
    /// entry is then still drawn, and says so, rather than quietly disappearing from the menu.
    /// </param>
    public static void Show(
        CategoryBook book,
        Control anchor,
        string subject,
        IReadOnlyList<string> carried,
        Action<IReadOnlyList<string>> apply,
        Action? allCategories)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(apply);

        var categories = book.All();

        // A menu is a surface no capture can show, so the harness presses one of its entries
        // instead: MAILBOX_CATEGORIZE names a category — or "clear" — and what the store holds
        // afterwards is the claim.
        if (Environment.GetEnvironmentVariable("MAILBOX_CATEGORIZE") is { Length: > 0 } posed)
        {
            Pose(posed.Trim(), categories, subject, carried, apply);
            return;
        }

        var flyout = new MenuFlyout();

        var clear = new MenuItem { Header = "Clear All Categories", IsEnabled = carried.Count > 0 };
        clear.Click += (_, _) => apply([]);
        flyout.Items.Add(clear);
        flyout.Items.Add(new Separator());

        if (categories.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No categories are defined", IsEnabled = false });
        }

        foreach (var category in categories)
        {
            var has = carried.Contains(category.Name, StringComparer.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = category.Name,
                Icon = has ? Tick() : Swatch(category.ColourToken),
            };

            var chosen = category;
            item.Click += (_, _) => apply(CategoryBook.Rewrite(
                has ? carried : [.. carried, chosen.Name],
                chosen.Name,
                has ? null : chosen.Name));

            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var all = new MenuItem { Header = "All Categories…", Icon = new RibbonArtwork("categorize", 16) };
        if (allCategories is null) all.IsEnabled = false;
        else all.Click += (_, _) => allCategories();
        flyout.Items.Add(all);

        Log.Info($"Categorize: the item carries {(carried.Count == 0 ? "nothing" : string.Join(", ", carried))}.");
        Log.Debug($"Categorize: the item is “{subject}”.");
        MenuProbe.Show("the item categorize menu", flyout, anchor, atPointer: true);
    }

    private static void Pose(
        string wanted,
        IReadOnlyList<Category> categories,
        string subject,
        IReadOnlyList<string> carried,
        Action<IReadOnlyList<string>> apply)
    {
        if (wanted.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            apply([]);
            Log.Info($"Harness: categories cleared on “{subject}”.");
            return;
        }

        if (categories.FirstOrDefault(c => c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) is not { } pick)
        {
            Log.Info($"Harness: no category matching “{wanted}” is in the set.");
            return;
        }

        var had = carried.Contains(pick.Name, StringComparer.OrdinalIgnoreCase);
        apply(CategoryBook.Rewrite(had ? carried : [.. carried, pick.Name], pick.Name, had ? null : pick.Name));
        Log.Info($"Harness: “{subject}” {(had ? "loses" : "takes")} {pick.Name}.");
    }

    private static Control Swatch(string token)
    {
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2) };
        swatch[!Border.BackgroundProperty] = new DynamicResourceExtension(token + ".brush");
        return swatch;
    }

    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };
}
