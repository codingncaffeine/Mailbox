using Avalonia.Controls;
using Mailbox.App.Views;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The context menus a capture cannot see hold what they should the moment they are shown.
/// </summary>
/// <remarks>
/// The rule these hold came out of walking all fifty-two menus in the tree: a menu is built
/// full and then shown, so its presenter measures with its entries in it. The menus tested here
/// are the ones a bare store can build — the rest are walked in a posed run, where each one
/// logs itself through <c>MenuProbe</c> as it opens.
/// </remarks>
public class MenuContentsTests
{
    private static IReadOnlyList<string> Headers(MenuFlyout menu)
        => [.. menu.Items.Select(i => i switch
        {
            MenuItem { Header: string header } => header,
            Separator => "—",
            _ => i?.GetType().Name ?? "null",
        })];

    /// <summary>
    /// The boards menu over a store with no boards: the honest empty row, then New and Manage.
    /// </summary>
    [Fact]
    public void TheBoardsMenuHoldsItsEntriesTheMomentItIsShown()
    {
        var headers = HeadlessApp.OnUiThread(() =>
        {
            using var store = MailStore.Transient();
            var mail = new MailRepository(store);

            var window = new Window();
            var anchor = new Button();
            window.Content = anchor;
            window.Show();

            try
            {
                BoardMenu.Show(
                    mail, anchor, "an article", [1L], DateTimeOffset.UnixEpoch,
                    changed: () => { }, newBoard: () => { }, manage: () => { });

                return Headers(MenuProbe.Last!.Value.Menu);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(["No boards yet", "—", "New Board…", "Manage Boards…"], headers);
    }

    /// <summary>
    /// The categorize menu over a fresh store: Clear All greyed context aside, the six shipped
    /// colours between the two rules, and All Categories… closing it.
    /// </summary>
    [Fact]
    public void TheCategorizeMenuHoldsItsEntriesTheMomentItIsShown()
    {
        var headers = HeadlessApp.OnUiThread(() =>
        {
            using var mailStore = MailStore.Transient();
            var mail = new MailRepository(mailStore);
            var book = new CategoryBook(new PimRepository(PimStore.Transient()), () => [mail]);

            var window = new Window();
            var anchor = new Button();
            window.Content = anchor;
            window.Show();

            try
            {
                ItemCategoryMenu.Show(
                    book, anchor, "an item", carried: [], apply: _ => { }, allCategories: () => { });

                return Headers(MenuProbe.Last!.Value.Menu);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Clear All Categories", headers[0]);
        Assert.Equal("—", headers[1]);
        Assert.Equal("All Categories…", headers[^1]);
        Assert.True(headers.Count >= 4, "the menu lists no categories at all");
    }

    /// <summary>
    /// The toolbar's customize menu is the one deliberate exception to built-then-shown — it
    /// refills on every open — and the reason it is safe is that Build fills it immediately too.
    /// This is the assertion that keeps that reason true.
    /// </summary>
    [Fact]
    public void TheToolbarMenuIsFilledAtBuildTimeNotOnlyOnOpening()
    {
        var count = HeadlessApp.OnUiThread(() =>
        {
            var layout = new QuickAccessLayout(
                SettingsStore.Transient(),
                [new CommandId("mail.sendreceive.all"), new CommandId("mail.undo")]);

            var flyout = QuickAccessFlyout.Build(
                new CommandCatalog(), layout, changed: () => { }, moreCommands: () => { });

            return flyout.Items.Count;
        });

        Assert.True(count > 0, "the menu was empty at the moment its presenter would measure it");
    }
}
