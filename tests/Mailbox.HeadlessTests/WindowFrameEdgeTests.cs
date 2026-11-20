using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// A window that draws its own frame resizes from its edges whatever is drawn under them.
/// </summary>
/// <remarks>
/// The message window's body runs to the window's edges and takes every press it is given, to
/// hand it to the web engine. The frame listened for presses on their way back up, by which time
/// the body had taken them, so the message window could not be resized from three of its sides.
/// The stand-in here takes presses the same way.
/// </remarks>
public class WindowFrameEdgeTests
{
    private sealed class Greedy : Control
    {
        public int Presses { get; private set; }

        public override void Render(DrawingContext context)
            => context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Presses++;
            e.Handled = true;
        }
    }

    private static int PressesReachingTheBody(Point at, bool resizable = true)
        => HeadlessApp.OnUiThread(() =>
        {
            var body = new Greedy();
            var window = new Window { Width = 400, Height = 300, CanResize = resizable };
            WindowFrame.Apply(window);
            window.Content = body;
            window.Show();

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.Close();
            return body.Presses;
        });

    [Theory]
    [InlineData(398, 150)] // east
    [InlineData(2, 150)] // west
    [InlineData(200, 298)] // south
    [InlineData(398, 298)] // south-east corner
    public void AnEdgePressGoesToTheFrameNotTheBody(double x, double y)
        => Assert.Equal(0, PressesReachingTheBody(new Point(x, y)));

    [Fact]
    public void APressInsideStillReachesTheBody()
        => Assert.Equal(1, PressesReachingTheBody(new Point(200, 150)));

    [Fact]
    public void AWindowThatCannotBeResizedLeavesItsEdgesToTheBody()
        => Assert.Equal(1, PressesReachingTheBody(new Point(398, 150), resizable: false));
}
