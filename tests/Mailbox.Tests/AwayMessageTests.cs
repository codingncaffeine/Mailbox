using Mailbox.Core.Rules;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The automatic reply as it is kept: per account, in the settings store, and read back exactly
/// as it was written.
/// </summary>
public class AwayMessageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailbox-away-" + Guid.NewGuid().ToString("N")[..8]);

    private SettingsStore Store()
    {
        Directory.CreateDirectory(_root);
        return new SettingsStore(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ItIsWrittenAndReadBackWhole()
    {
        var settings = Store();
        var away = new AwayMessage
        {
            Enabled = true,
            From = new DateOnly(2026, 9, 7),
            Until = new DateOnly(2026, 9, 14),
            Subject = "Away until Monday",
            Body = "Back on the 15th.\nAsk A. Person in the meantime.",
            Days = 3,
            Addresses = ["me@alias.example", "role@example.com"],
        };

        away.Save(settings, "you@example.com");

        // Field by field: a record's own equality compares the address list by reference, which
        // would pass for two different lists and fail for two equal ones.
        var back = AwayMessage.Load(settings, "you@example.com");
        Assert.Equal(away, back with { Addresses = away.Addresses });
        Assert.Equal(away.Addresses, back.Addresses);

        // Per account: another address has its own, and switched off by default.
        Assert.False(AwayMessage.Load(settings, "work@example.net").Enabled);
    }

    /// <summary>
    /// The dates are half-open at neither end: both days count. Somebody away "until the 14th"
    /// means the 14th as well, which is what a date on a form means to the person filling it in.
    /// </summary>
    [Fact]
    public void TheDatesIncludeTheirOwnDays()
    {
        var away = new AwayMessage
        {
            Enabled = true,
            From = new DateOnly(2026, 9, 7),
            Until = new DateOnly(2026, 9, 14),
        };

        Assert.False(away.ActiveOn(new DateOnly(2026, 9, 6)));
        Assert.True(away.ActiveOn(new DateOnly(2026, 9, 7)));
        Assert.True(away.ActiveOn(new DateOnly(2026, 9, 14)));
        Assert.False(away.ActiveOn(new DateOnly(2026, 9, 15)));

        // Switched off is off whatever the dates say.
        Assert.False((away with { Enabled = false }).ActiveOn(new DateOnly(2026, 9, 10)));

        // No dates at all: on until it is switched off.
        var open = new AwayMessage { Enabled = true };
        Assert.True(open.ActiveOn(new DateOnly(2030, 1, 1)));
        Assert.False(open.HasDates);
    }

    [Fact]
    public void ForgettingAnAccountLeavesNothingBehind()
    {
        var settings = Store();
        new AwayMessage { Enabled = true, Subject = "Away", Body = "Back soon." }.Save(settings, "you@example.com");

        AwayMessage.Forget(settings, "you@example.com");

        var back = AwayMessage.Load(settings, "you@example.com");
        Assert.False(back.Enabled);
        Assert.Empty(back.Subject);
        Assert.Empty(back.Body);
    }
}
