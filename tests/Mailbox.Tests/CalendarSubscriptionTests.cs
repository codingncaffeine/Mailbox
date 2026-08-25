using Mailbox.Core.Calendars;

namespace Mailbox.Tests;

/// <summary>
/// The one rule for an internet calendar's address, shared by the calendar module's subscribe
/// command and Account Settings' Internet Calendars tab.
/// </summary>
public class CalendarSubscriptionTests
{
    [Theory]
    [InlineData("https://example.com/c.ics", "https://example.com/c.ics")]
    [InlineData("http://example.com/c.ics", "http://example.com/c.ics")]
    [InlineData("   https://example.com/c.ics   ", "https://example.com/c.ics")]
    public void AnAddressThatIsAlreadyOneIsKept(string typed, string expected)
    {
        Assert.True(CalendarSubscription.TryAddress(typed, out var address));
        Assert.Equal(expected, address.ToString());
    }

    /// <summary>
    /// The rewrite that matters: every publisher writes webcal: and nothing here speaks it.
    /// </summary>
    [Theory]
    [InlineData("webcal://example.com/c.ics", "https://example.com/c.ics")]
    [InlineData("WEBCAL://example.com/c.ics", "https://example.com/c.ics")]
    [InlineData("webcal://example.com/a/b/c.ics?token=1", "https://example.com/a/b/c.ics?token=1")]
    public void WebcalBecomesHttpsWithoutCarryingItsOwnPort(string typed, string expected)
    {
        Assert.True(CalendarSubscription.TryAddress(typed, out var address));
        Assert.Equal(expected, address.ToString());
        Assert.Equal(443, address.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("/calendars/c.ics")]
    [InlineData("mailto:a.person@example.com")]
    [InlineData("file:///etc/passwd")]
    public void AnythingThatIsNotAFetchableAddressIsRefused(string? typed)
    {
        Assert.False(CalendarSubscription.TryAddress(typed, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void TheNameOfferedIsTheHostItCameFrom()
    {
        Assert.True(CalendarSubscription.TryAddress("webcal://calendar.example.com/x/y.ics", out var address));
        Assert.Equal("calendar.example.com", CalendarSubscription.SuggestedName(address));
    }
}
