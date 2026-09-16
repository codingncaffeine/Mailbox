using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// Where a window the reader moved opens next time: the place they left it at, as long as a
/// screen is still there to hold it.
/// </summary>
public class WindowPlaceTests
{
    /// <summary>The progress dialog's caption at 100%: 476 across, 33 down.</summary>
    private static readonly (int Width, int Height) Caption = (476, 33);

    /// <summary>A 1920x1080 screen with a 44px panel along the bottom.</summary>
    private static readonly DesktopArea Primary = new(0, 0, 1920, 1036);

    /// <summary>A 2560x1440 screen to the left of it, and above: the corner is not at the origin.</summary>
    private static readonly DesktopArea Left = new(-2560, -360, 2560, 1440);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1834, 612)]
    [InlineData(-2400, -300)]
    public void APlaceComesBackAsItWasWritten(int x, int y)
        => Assert.Equal((x, y), WindowPlace.Parse(WindowPlace.Format(x, y)));

    [Fact]
    public void APlaceIsWrittenTheSameWhateverTheLanguage()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture whose minus sign is not the ASCII one.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            Assert.Equal("-1720,-40", WindowPlace.Format(-1720, -40));
            Assert.Equal((-1720, -40), WindowPlace.Parse("-1720,-40"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("12")]
    [InlineData("12,")]
    [InlineData("a,b")]
    [InlineData("1,2,3")]
    [InlineData("1.5,2")]
    [InlineData("99999999999,0")]
    public void ASettingThatIsNotAPlaceIsNone(string? text)
        => Assert.Null(WindowPlace.Parse(text));

    [Fact]
    public void APlaceOnAScreenIsUsedAsItIs()
        => Assert.Equal((1400, 20), WindowPlace.Fit((1400, 20), Caption, [Primary]));

    [Fact]
    public void APlaceOnAScreenThatHasGoneIsNotUsed()
        => Assert.Null(WindowPlace.Fit((-2000, 200), Caption, [Primary]));

    [Fact]
    public void APlaceOnTheOtherScreenIsUsedWhileThatScreenIsThere()
        => Assert.Equal((-2000, 200), WindowPlace.Fit((-2000, 200), Caption, [Primary, Left]));

    [Fact]
    public void AWindowHangingOffAnEdgeIsBroughtBackOn()
        => Assert.Equal((1920 - 476, 1036 - 33), WindowPlace.Fit((1700, 1020), Caption, [Primary]));

    [Fact]
    public void ACaptionAboveTheTopOfTheScreenIsBroughtDown()
        => Assert.Equal((300, 0), WindowPlace.Fit((300, -20), Caption, [Primary]));

    [Fact]
    public void TooLittleOfTheCaptionOnAScreenToTakeHoldOfIsNotAPlace()
    {
        // Ninety-nine pixels showing past the right edge's neighbour: not enough to grab.
        Assert.Null(WindowPlace.Fit((1920 - 99, 400), Caption, [Primary]));

        // A hundred is.
        Assert.Equal((1920 - 476, 400), WindowPlace.Fit((1920 - 100, 400), Caption, [Primary]));
    }

    [Fact]
    public void ACaptionOnNoScreenAtAllIsNotAPlace()
    {
        Assert.Null(WindowPlace.Fit((5000, 5000), Caption, [Primary, Left]));
        Assert.Null(WindowPlace.Fit((200, 1036), Caption, [Primary]));
        Assert.Null(WindowPlace.Fit((200, 200), Caption, []));
    }

    [Fact]
    public void AWindowAcrossTwoScreensGoesToTheOneHoldingMoreOfIt()
    {
        // 300 of the 476 on the left screen, 176 on the primary.
        Assert.Equal((-476, 100), WindowPlace.Fit((-300, 100), Caption, [Primary, Left]));

        // 100 on the left screen, 376 on the primary.
        Assert.Equal((0, 100), WindowPlace.Fit((-100, 100), Caption, [Primary, Left]));
    }

    [Fact]
    public void AScreenNarrowerThanTheCaptionHoldsItAtItsLeftEdge()
        => Assert.Equal((0, 10), WindowPlace.Fit((50, 10), Caption, [new DesktopArea(0, 0, 400, 300)]));
}
