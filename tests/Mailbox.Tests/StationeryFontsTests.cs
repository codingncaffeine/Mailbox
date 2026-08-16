using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class StationeryFontsTests
{
    [Fact]
    public void TheDefaultsAreTheReferencesCalibriElevenAndCourierForPlainText()
    {
        var fonts = new StationeryFonts(SettingsStore.Transient());
        Assert.Equal(new MessageFont("Calibri", 11), fonts.Get(StationeryUse.NewMessages));
        Assert.Equal(new MessageFont("Calibri", 11), fonts.Get(StationeryUse.Replies));
        Assert.Equal("Courier New", fonts.Get(StationeryUse.PlainText).Family);
        Assert.Equal("Calibri 11", fonts.Get(StationeryUse.NewMessages).Summary);
    }

    [Fact]
    public void AFontRoundTripsThroughTheSettingsFileAsReadableJson()
    {
        var settings = SettingsStore.Transient();
        var fonts = new StationeryFonts(settings);
        var changes = 0;
        fonts.Changed += (_, _) => changes++;

        fonts.Set(StationeryUse.Replies, new MessageFont("Georgia", 12, Bold: true, Italic: false, Colour: "#1F3864"));

        var back = new StationeryFonts(settings).Get(StationeryUse.Replies);
        Assert.Equal(new MessageFont("Georgia", 12, true, false, "#1F3864"), back);
        Assert.Equal("Georgia 12 Bold", back.Summary);
        Assert.Contains("\"family\":\"Georgia\"", settings.GetString(StationeryFonts.ReplyKey));
        Assert.Equal(1, changes);
        // The others are untouched.
        Assert.Equal(MessageFont.Default, new StationeryFonts(settings).Get(StationeryUse.NewMessages));
    }

    [Fact]
    public void ResetGoesBackToTheDefaultForThatUse()
    {
        var fonts = new StationeryFonts(SettingsStore.Transient());
        fonts.Set(StationeryUse.PlainText, new MessageFont("Consolas", 9));
        fonts.Reset(StationeryUse.PlainText);
        Assert.Equal(MessageFont.PlainDefault, fonts.Get(StationeryUse.PlainText));
    }

    [Fact]
    public void AHandEditThatIsNotAFontReadsAsTheDefault()
    {
        var settings = SettingsStore.Transient();
        settings.Set(StationeryFonts.NewKey, "Calibri");
        Assert.Equal(MessageFont.Default, new StationeryFonts(settings).Get(StationeryUse.NewMessages));

        settings.Set(StationeryFonts.NewKey, """{"family":"","points":11}""");
        Assert.Equal(MessageFont.Default, new StationeryFonts(settings).Get(StationeryUse.NewMessages));

        settings.Set(StationeryFonts.NewKey, """{"family":"Arial","points":900}""");
        Assert.Equal(MessageFont.Default, new StationeryFonts(settings).Get(StationeryUse.NewMessages));

        settings.Set(StationeryFonts.NewKey, """{"family":"Arial","points":10.5}""");
        Assert.Equal(new MessageFont("Arial", 10.5), new StationeryFonts(settings).Get(StationeryUse.NewMessages));
    }

    [Fact]
    public void TheCommentSwitchesAreKept()
    {
        var settings = SettingsStore.Transient();
        var fonts = new StationeryFonts(settings);
        Assert.False(fonts.MarkComments);
        Assert.Equal("A. Person", fonts.MarkCommentsWith("A. Person"));

        fonts.MarkComments = true;
        fonts.SetMarkCommentsWith("AP");
        fonts.PickColourOnReply = true;

        var back = new StationeryFonts(settings);
        Assert.True(back.MarkComments);
        Assert.Equal("AP", back.MarkCommentsWith("A. Person"));
        Assert.True(back.PickColourOnReply);
    }
}
