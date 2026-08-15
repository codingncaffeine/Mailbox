using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The Mail page's settings as the code reads them.
/// </summary>
/// <remarks>
/// Each default here is a decision — the reference's, where it has one — and each is what a
/// fresh install does before anyone opens Options. So they are asserted, not assumed.
/// </remarks>
public class MailOptionsTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"mailbox-mailopts-{Guid.NewGuid():n}.json");

    private SettingsStore Store() => new(_path);

    private MailOptions Fresh() => new(Store());

    [Fact]
    public void TheDefaultsAreTheReferencesDefaults()
    {
        var options = Fresh();

        Assert.Equal(ComposeFormat.Html, options.ComposeFormat);
        Assert.False(options.CheckSpellingBeforeSend);
        Assert.Equal(3, options.AutosaveMinutes);
        Assert.True(options.SaveCopiesInSent);
        Assert.Equal(0, options.DefaultImportanceIndex);
        Assert.Null(options.DefaultSensitivityHeader);
        Assert.False(options.AlwaysUseDefaultAccount);
        Assert.True(options.CommasSeparateRecipients);
        Assert.True(options.AutomaticNameChecking);
        Assert.True(options.CtrlEnterSends);
        Assert.False(options.RequestDeliveryReceipt);
        Assert.False(options.RequestReadReceipt);
        Assert.False(options.EmptyDeletedItemsOnExit);
        Assert.Equal(">", options.ReplyPrefix);
        Assert.True(options.UseAutoCompleteList);

        // Replies grow inline in the reading pane by default, as the reference has them; the
        // separate-window behaviour is the switch-back, and closing the original is off.
        Assert.False(options.OpenRepliesInNewWindow);
        Assert.False(options.CloseOriginalOnReply);
    }

    /// <summary>The combo persists an index in the reference's own order; this names it.</summary>
    [Theory]
    [InlineData(0, ComposeFormat.Html)]
    [InlineData(1, ComposeFormat.RichText)]
    [InlineData(2, ComposeFormat.PlainText)]
    [InlineData(9, ComposeFormat.Html)]
    public void TheComposeFormatFollowsTheCombo(int index, ComposeFormat expected)
    {
        var store = Store();
        store.Set(MailOptions.ComposeFormatKey, (double)index);

        Assert.Equal(expected, new MailOptions(store).ComposeFormat);
    }

    /// <summary>Normal is the header's absence, which is what every client does.</summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Personal")]
    [InlineData(2, "Private")]
    [InlineData(3, "Company-Confidential")]
    public void SensitivityBecomesTheHeaderOrNothing(int index, string? header)
    {
        var store = Store();
        store.Set(MailOptions.DefaultSensitivityKey, (double)index);

        Assert.Equal(header, new MailOptions(store).DefaultSensitivityHeader);
    }

    /// <summary>What the Options page writes is what the reader reads — the keys agree.</summary>
    [Fact]
    public void ACheckboxWrittenByThePageIsReadHere()
    {
        var store = Store();
        store.Set(MailOptions.CheckSpellingBeforeSendKey, true);
        store.Set(MailOptions.SaveCopiesInSentKey, false);
        store.Set(MailOptions.EmptyDeletedOnExitKey, true);

        var options = new MailOptions(store);
        Assert.True(options.CheckSpellingBeforeSend);
        Assert.False(options.SaveCopiesInSent);
        Assert.True(options.EmptyDeletedItemsOnExit);
    }

    [Fact]
    public void AutosaveIsClampedToSomethingSane()
    {
        var store = Store();
        store.Set(MailOptions.AutosaveMinutesKey, 500.0);
        Assert.Equal(99, new MailOptions(store).AutosaveMinutes);

        store.Set(MailOptions.AutosaveMinutesKey, -4.0);
        Assert.Equal(0, new MailOptions(store).AutosaveMinutes);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception) { }
    }
}
