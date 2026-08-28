using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The menu behind the chevron at the end of the Quick Access Toolbar.
/// </summary>
/// <remarks>
/// A short curated list with a tick against what is already placed, then the two structural
/// choices the reference offers: which side of the ribbon the bar sits on, and whether it is
/// there at all. Everything beyond the list is reached through Customize Ribbon, which is the
/// point of keeping this menu short.
/// <para>
/// Rebuilt each time it opens rather than once, because every item in it changes a tick.
/// </para>
/// </remarks>
internal static class QuickAccessFlyout
{
    /// <summary>
    /// How to fill each menu this class has built, so the harness can fill one without showing it.
    /// </summary>
    /// <remarks>
    /// A <see cref="MenuFlyout"/> never appears in a capture, and this one is filled from its own
    /// <c>Opening</c> event — so a run that only photographs the window measures an empty menu and
    /// concludes the toolbar has no editor. Keyed weakly so a flyout that goes away takes its
    /// entry with it. This is the same list the chevron opens, not a second one written for the
    /// harness, which would agree with the menu right up until somebody edited one of them.
    /// </remarks>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFlyout, Action>
        Fillers = new();

    internal static MenuFlyout Build(
        CommandCatalog catalog,
        QuickAccessLayout layout,
        Action changed,
        Action moreCommands)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        void Fill() => Populate(flyout, catalog, layout, changed, moreCommands);

        // Filled now as well as on every open. The presenter is built from these entries when
        // the popup is created, and creation happens before Opening is raised: a menu that had
        // nothing in it at that moment stays empty on screen however many entries the event
        // then adds. The chevron opened a popup with no rows in it, which is not a menu a reader
        // can tell from a menu that failed to open.
        Fill();
        flyout.Opening += (_, _) => Fill();
        Fillers.AddOrUpdate(flyout, Fill);
        return flyout;
    }

    /// <summary>Harness only: fills the menu as opening it would, without showing it.</summary>
    internal static void Fill(MenuFlyout flyout)
    {
        if (Fillers.TryGetValue(flyout, out var fill)) fill();
    }

    private static void Populate(
        MenuFlyout flyout,
        CommandCatalog catalog,
        QuickAccessLayout layout,
        Action changed,
        Action moreCommands)
    {
        flyout.Items.Clear();
        flyout.Items.Add(Header("Customize Quick Access Toolbar"));

        foreach (var id in DefaultRibbonLayouts.QuickAccessCandidates)
        {
            if (!catalog.TryGet(id, out var command)) continue;

            var item = new MenuItem
            {
                Header = command.Label,
                Icon = layout.Contains(id) ? Tick() : null,
            };

            item.Click += (_, _) =>
            {
                layout.Toggle(id);
                changed();
            };

            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var more = new MenuItem { Header = "More Commands…" };
        more.Click += (_, _) => moreCommands();
        flyout.Items.Add(more);

        var below = layout.Placement == QuickAccessPlacement.BelowRibbon;
        var placement = new MenuItem
        {
            Header = below ? "Show Above the Ribbon" : "Show Below the Ribbon",
        };
        placement.Click += (_, _) =>
        {
            layout.Placement = below
                ? QuickAccessPlacement.AboveRibbon
                : QuickAccessPlacement.BelowRibbon;
            changed();
        };
        flyout.Items.Add(placement);

        flyout.Items.Add(new Separator());

        var hide = new MenuItem { Header = "Hide Quick Access Toolbar" };
        hide.Click += (_, _) =>
        {
            layout.IsVisible = false;
            changed();
        };
        flyout.Items.Add(hide);

        var reset = new MenuItem { Header = "Reset Quick Access Toolbar" };
        reset.Click += (_, _) =>
        {
            layout.Reset();
            changed();
        };
        flyout.Items.Add(reset);
    }

    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };

    private static Control Header(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 6, 10, 3),
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");

        // A heading rather than a command: not clickable, not highlighted on hover.
        return new MenuItem { Header = label, IsEnabled = false };
    }
}
