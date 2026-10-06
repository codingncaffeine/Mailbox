using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>The main window opens as it was left, on the screens there are now.</summary>
public class ShellWindowStateTests
{
    private static readonly (DesktopArea, double)[] OneScreen = [(new DesktopArea(0, 0, 2560, 1400), 1.0)];

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        var state = new ShellWindowState(1500, 900, (120, 80), Maximized: true);

        Assert.Equal("1500,900,120,80,1", state.Format());
        Assert.Equal(state, ShellWindowState.Parse(state.Format()));
    }

    /// <summary>Native Wayland gives no place: the size and the maximised state still come back.</summary>
    [Fact]
    public void AStateWithNoPlaceReadsBackWithout()
    {
        var state = new ShellWindowState(1500, 900, null, Maximized: false);

        Assert.Equal("1500,900,,,0", state.Format());
        Assert.Equal(state, ShellWindowState.Parse("1500,900,,,0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1500,900")]
    [InlineData("wide,900,0,0,0")]
    [InlineData("0,900,0,0,0")]
    [InlineData("NaN,900,0,0,0")]
    public void SomethingElseIsNothing(string? text) => Assert.Null(ShellWindowState.Parse(text));

    [Fact]
    public void AStateThatFitsIsKeptAsItIs()
    {
        var state = new ShellWindowState(1500, 900, (120, 80), false);

        Assert.Equal(state, state.Fit(OneScreen, 350, 480));
    }

    /// <summary>A monitor unplugged since: the place is let go and the size is kept.</summary>
    [Fact]
    public void APlaceOnAScreenThatIsGoneIsLetGo()
    {
        var state = new ShellWindowState(1500, 900, (3000, 100), false);

        var fit = state.Fit(OneScreen, 350, 480);

        Assert.Null(fit.Place);
        Assert.Equal((1500d, 900d), (fit.Width, fit.Height));
    }

    /// <summary>Left on a larger screen than there is now: no bigger than the working area.</summary>
    [Fact]
    public void ASizeLargerThanTheScreenIsBroughtDownToIt()
    {
        var state = new ShellWindowState(3800, 2100, (0, 0), false);

        var fit = state.Fit(OneScreen, 350, 480);

        Assert.Equal((2560d, 1400d), (fit.Width, fit.Height));
        Assert.Equal((0, 0), fit.Place);
    }

    /// <summary>Half off the right edge with its caption still in reach: brought fully in.</summary>
    [Fact]
    public void APlaceHangingOffAnEdgeIsBroughtIn()
    {
        var state = new ShellWindowState(1500, 900, (2300, 1000), false);

        var fit = state.Fit(OneScreen, 350, 480);

        Assert.Equal((2560 - 1500, 1400 - 900), fit.Place);
    }

    /// <summary>Sizes are device-independent, screens are pixels: a 2× screen holds half as much.</summary>
    [Fact]
    public void AScaledScreenIsMeasuredInItsOwnPixels()
    {
        (DesktopArea, double)[] hiDpi = [(new DesktopArea(0, 0, 2560, 1400), 2.0)];

        var fit = new ShellWindowState(1500, 900, (0, 0), false).Fit(hiDpi, 350, 480);

        Assert.Equal((1280d, 700d), (fit.Width, fit.Height));
    }

    [Fact]
    public void NeverSmallerThanTheWindowAllows()
    {
        var fit = new ShellWindowState(100, 100, null, false).Fit(OneScreen, 350, 480);

        Assert.Equal((350d, 480d), (fit.Width, fit.Height));
    }

    /// <summary>The second of two screens, where the window was left.</summary>
    [Fact]
    public void APlaceOnTheSecondScreenStaysThere()
    {
        (DesktopArea, double)[] two = [(new DesktopArea(0, 0, 1920, 1040), 1.0), (new DesktopArea(1920, 0, 2560, 1400), 1.0)];

        var fit = new ShellWindowState(2400, 1300, (2000, 50), false).Fit(two, 350, 480);

        Assert.Equal((2000, 50), fit.Place);
        Assert.Equal((2400d, 1300d), (fit.Width, fit.Height));
    }

    [Fact]
    public void MaximisedStaysMaximised()
        => Assert.True(new ShellWindowState(1500, 900, null, true).Fit(OneScreen, 350, 480).Maximized);
}
