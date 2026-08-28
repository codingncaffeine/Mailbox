using Avalonia.Controls;
using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The caption buttons, built and asked what they are — the first thing in this tree that could
/// not be tested before.
/// </summary>
/// <remarks>
/// Phase 2 found that <c>ForceHover</c> resolved a button by a class two of them share and
/// stopped at the first match, so posing a hover on maximize lit minimize instead. A door that
/// lies is worse than no door: every hover claim built on it would have been wrong, and the only
/// reason it was caught is that nothing had used it yet.
/// <para>
/// That bug is a property of a built control — three buttons, each identifiable — and neither a
/// source scan nor a photograph can ask about it directly. Here it is one assertion.
/// </para>
/// </remarks>
public class CaptionButtonsTests
{
    [Fact]
    public void TheShellsCaptionCarriesThreeButtons()
    {
        var count = HeadlessApp.OnUiThread(() =>
            new CaptionButtons(new Window()).Children.OfType<Button>().Count());

        Assert.Equal(3, count);
    }

    /// <summary>
    /// Hovering a named caption button lights that one and no other.
    /// </summary>
    /// <remarks>
    /// The exact regression. Minimize and maximize deliberately share the <c>caption</c> class —
    /// they are styled identically, and that is right — so a lookup that resolved by class and
    /// stopped at the first match found minimize whichever of the two was asked for. Naming the
    /// buttons apart in the markup would have been the wrong repair; not identifying them by
    /// their styling is the right one. This asks the question the bug got wrong: hover each in
    /// turn, and require that exactly the named one is lit.
    /// </remarks>
    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("close")]
    public void HoveringACaptionButtonLightsThatOneAlone(string which)
    {
        var lit = HeadlessApp.OnUiThread(() =>
        {
            var caption = new CaptionButtons(new Window());
            Assert.True(caption.ForceHover(which), $"the caption pose did not recognise \"{which}\"");

            return caption.Children
                .OfType<Button>()
                .Count(b => b.Classes.Contains(":pointerover"));
        });

        Assert.Equal(1, lit);
    }

    /// <summary>
    /// A dialog's caption carries only the close button, as the reference's dialogs do — there is
    /// nothing useful about minimizing a modal.
    /// </summary>
    [Fact]
    public void ADialogsCaptionCarriesOnlyItsCloseButton()
    {
        var count = HeadlessApp.OnUiThread(() =>
        {
            var window = new Window();
            var caption = new CaptionButtons(window, dialog: true);
            return caption.Children.OfType<Button>().Count();
        });

        Assert.Equal(1, count);
    }
}
