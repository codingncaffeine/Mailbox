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

    private readonly List<string> _made = [];

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
        Assert.True(options.PlayArrivalSound);
        Assert.Equal(string.Empty, options.ArrivalSoundFile);
        Assert.True(options.PlayReminderSound);
        Assert.Equal(string.Empty, options.ReminderSoundFile);
        Assert.False(options.RequestDeliveryReceipt);
        Assert.False(options.RequestReadReceipt);
        Assert.False(options.EmptyDeletedItemsOnExit);
        Assert.Equal(">", options.ReplyPrefix);
        Assert.True(options.UseAutoCompleteList);

        // Replies grow inline in the reading pane by default, as the reference has them; the
        // separate-window behaviour is the switch-back, and closing the original is off.
        Assert.False(options.OpenRepliesInNewWindow);
        Assert.False(options.CloseOriginalOnReply);

        // The junk filter defaults to Low — the reference's own default, a high bar.
        Assert.Equal(1, options.JunkLevelIndex);

        // "After moving or deleting an open item": back to the folder, as the capture shows.
        Assert.Equal(AfterOpenItem.ReturnToFolder, options.AfterOpenItem);
    }

    /// <summary>The after-open-item combo, in its own order, out-of-range falling to the default.</summary>
    [Theory]
    [InlineData(0, AfterOpenItem.PreviousItem)]
    [InlineData(1, AfterOpenItem.NextItem)]
    [InlineData(2, AfterOpenItem.ReturnToFolder)]
    [InlineData(9, AfterOpenItem.ReturnToFolder)]
    public void AfterOpenItemFollowsTheCombosOrder(int stored, AfterOpenItem meant)
    {
        var store = Store();
        store.Set(MailOptions.AfterOpenItemKey, stored);

        Assert.Equal(meant, new MailOptions(store).AfterOpenItem);
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
        foreach (var made in _made) { try { File.Delete(made); } catch (Exception) { } }
    }

    [Fact]
    public void TheChosenSoundWinsOverTheOneTheBuildShips()
    {
        var chosen = Touch("chosen.ogg");
        var bundled = Touch("bundled.ogg");

        Assert.Equal(chosen, MailOptions.SoundFor(chosen, bundled));
    }

    [Fact]
    public void ASoundThatHasGoneMissingFallsBackRatherThanGoingSilent()
    {
        // Somebody moved the file, or it lives on a disk that is not mounted this morning. The
        // sound they picked is gone either way, and silence they cannot explain is the worst of
        // the three answers — so the shipped one takes over, and the Options page says so.
        var bundled = Touch("bundled.ogg");

        Assert.Equal(bundled, MailOptions.SoundFor("/nowhere/gone.ogg", bundled));
        Assert.Equal(bundled, MailOptions.SoundFor(string.Empty, bundled));
        Assert.Equal(bundled, MailOptions.SoundFor(null, bundled));
    }

    [Fact]
    public void ABuildThatShipsNoSoundAsksTheDesktopForOne()
    {
        // Null is not a failure: it is the caller's cue to ask the freedesktop sound theme for
        // message-new-email, which is the right last word on a desktop that has one.
        Assert.Null(MailOptions.SoundFor(null, "/nowhere/bundled.ogg"));
        Assert.Null(MailOptions.SoundFor("/nowhere/chosen.ogg", null));
    }

    [Fact]
    public void EitherSoundIsRememberedAndCanBePutBack()
    {
        // Clearing the field is the reset — there is no Reset button, the reference drawing
        // none — so empty has to mean "the one the build ships" rather than "no sound".
        var options = new MailOptions(Store())
        {
            ArrivalSoundFile = "/sounds/mail.ogg",
            ReminderSoundFile = "/sounds/bell.ogg",
        };

        Assert.Equal("/sounds/mail.ogg", new MailOptions(Store()).ArrivalSoundFile);
        Assert.Equal("/sounds/bell.ogg", new MailOptions(Store()).ReminderSoundFile);

        options.ArrivalSoundFile = string.Empty;
        options.ReminderSoundFile = string.Empty;

        Assert.Equal(string.Empty, new MailOptions(Store()).ArrivalSoundFile);
        Assert.Equal(string.Empty, new MailOptions(Store()).ReminderSoundFile);
    }

    [Fact]
    public void TheTwoSoundsAreKeptApart()
    {
        // One rule picks both, and one key for the pair would make choosing a reminder chime
        // change what mail sounds like.
        var options = new MailOptions(Store()) { ArrivalSoundFile = "/sounds/mail.ogg" };

        Assert.Equal(string.Empty, options.ReminderSoundFile);
        Assert.NotEqual(MailOptions.ArrivalSoundFileKey, MailOptions.ReminderSoundFileKey);
    }

    /// <summary>An empty file that really exists, for the rule that asks whether one does.</summary>
    private string Touch(string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + "-" + name);
        File.WriteAllBytes(path, []);
        _made.Add(path);
        return path;
    }
}
