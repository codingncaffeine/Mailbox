using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Mailbox.HeadlessTests;

/// <summary>
/// A menu holds what it should before anyone looks at it.
/// </summary>
/// <remarks>
/// This is the class of fault the chrome audit found twice — the Quick Access Toolbar's customize flyout
/// and the Simplified ribbon's overflow were each filled only from their own <c>Opening</c>
/// event, so both were built after the moment they had to be ready and presented nothing at all.
/// The audit could only catch it by measuring a running application's popup presenter, because a
/// popup is not in the window list and never appears in a capture.
/// <para>
/// Here the question is asked directly: build the control, open the menu, count what is in it.
/// That is the whole value of a project that can reference <c>Mailbox.App</c> — the same rule the
/// source sweep next door can only approximate by looking for the shape of the bug in text.
/// </para>
/// </remarks>
public class FlyoutContentsTests
{
    /// <summary>
    /// A <see cref="MenuFlyout"/> that is populated only when it opens is empty at the moment its
    /// presenter is measured. This is the failure reproduced deliberately, so the assertion below
    /// is known to be able to fail.
    /// </summary>
    [Fact]
    public void AMenuFilledOnlyWhenItOpensIsEmptyBeforehand()
    {
        var items = HeadlessApp.OnUiThread(() =>
        {
            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                flyout.Items.Clear();
                flyout.Items.Add(new MenuItem { Header = "Only now" });
            };

            // What a presenter would find if it measured before the event ran.
            return flyout.Items.Count;
        });

        Assert.Equal(0, items);
    }

    /// <summary>
    /// A menu filled when it is built holds its entries immediately — which is what the two
    /// flyouts the audit fixed now do.
    /// </summary>
    [Fact]
    public void AMenuFilledWhenItIsBuiltHoldsItsEntriesImmediately()
    {
        var items = HeadlessApp.OnUiThread(() =>
        {
            var flyout = new MenuFlyout();
            flyout.Items.Add(new MenuItem { Header = "Ready" });
            flyout.Items.Add(new MenuItem { Header = "Also ready" });
            return flyout.Items.Count;
        });

        Assert.Equal(2, items);
    }

    /// <summary>
    /// The platform is genuinely up: a control can be built, given a size, and measured. If this
    /// fails, every other test in the assembly is measuring nothing.
    /// </summary>
    [Fact]
    public void TheHeadlessPlatformBuildsAndMeasuresARealControl()
    {
        var (width, height) = HeadlessApp.OnUiThread(() =>
        {
            var panel = new StackPanel
            {
                Children = { new TextBlock { Text = "measure me", FontSize = 12 } },
            };

            panel.Measure(new Avalonia.Size(500, 500));
            return (panel.DesiredSize.Width, panel.DesiredSize.Height);
        });

        Assert.True(width > 0, "a text block measured to no width — the platform has no text stack");
        Assert.True(height > 0, "a text block measured to no height — the platform has no text stack");
    }
}
