using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The reading pane only builds an engine that can paint into it, and renders the message as
/// text when there is none.
/// </summary>
/// <remarks>
/// The case this exists for cannot be reproduced on a machine that has WPE: with it hidden, the
/// library builds a WebKitGTK view instead, reports the document loaded, answers with the words
/// it parsed — and paints nothing. The pane looked healthy in every read-back and the body was
/// blank. So the rule is asked here, where no engine is needed to ask it.
/// </remarks>
public class ReadingPaneEngineTests
{
    private static ReadingPaneEngines.Candidate Wpe(bool installed = true, bool offscreen = true)
        => new("WPE WebKit", installed, true, offscreen, installed ? null : "not installed here");

    /// <summary>WebKitGTK as the library actually reports it: present, and native-window only.</summary>
    private static ReadingPaneEngines.Candidate Gtk(bool offscreen = false)
        => new("WebKitGTK", true, true, offscreen, null);

    [Fact]
    public void TheOffscreenEngineRendersWhenItIsThere()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(), Gtk(), gtkAsked: false);

        Assert.True(choice.UseWebView);
        Assert.False(choice.PreferWebKitGtk);
        Assert.Contains("WPE WebKit", choice.Reason);
    }

    [Fact]
    public void NoOffscreenEngineMeansText()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(installed: false), Gtk(), gtkAsked: false);

        Assert.False(choice.UseWebView);

        // Both refusals travel, because on a machine with neither the reader deserves to know
        // which library is missing rather than that "something" is.
        Assert.Contains("WPE WebKit is not installed", choice.Reason);
        Assert.Contains("WebKitGTK is installed but cannot draw off screen", choice.Reason);
        Assert.Contains("rendered as text", choice.Reason);
    }

    [Fact]
    public void ThePlatformsOwnReasonIsCarried()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(installed: false), Gtk(), gtkAsked: false);

        Assert.Contains("not installed here", choice.Reason);
    }

    /// <summary>
    /// The debugging escape reorders the two; it does not force an engine that would draw
    /// nothing, because honouring it literally puts a blank body in front of whoever asked.
    /// </summary>
    [Fact]
    public void AskingForTheOneThatCannotDrawFallsThroughToTheOneThatCan()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(), Gtk(), gtkAsked: true);

        Assert.True(choice.UseWebView);
        Assert.False(choice.PreferWebKitGtk);
        Assert.Contains("cannot draw off screen, so WPE WebKit renders", choice.Reason);
    }

    /// <summary>
    /// And the day the library learns to draw WebKitGTK off screen, asking for it works — the
    /// rule is about the embedding, not about the name of the engine.
    /// </summary>
    [Fact]
    public void AskingForOneThatCanDrawIsHonoured()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(), Gtk(offscreen: true), gtkAsked: true);

        Assert.True(choice.UseWebView);
        Assert.True(choice.PreferWebKitGtk);
    }

    /// <summary>
    /// A machine with no WPE and a library that had grown offscreen GTK would use it, unasked.
    /// </summary>
    [Fact]
    public void TheSecondChoiceIsTakenWhenTheFirstIsMissing()
    {
        var choice = ReadingPaneEngines.Choose(Wpe(installed: false), Gtk(offscreen: true), gtkAsked: false);

        Assert.True(choice.UseWebView);
        Assert.True(choice.PreferWebKitGtk);
        Assert.Contains("WebKitGTK renders the message", choice.Reason);
    }

    /// <summary>
    /// Installed and offscreen-capable is not enough on its own: a build of the library that
    /// cannot drive the adapter at all says so separately, and that refusal reads differently.
    /// </summary>
    [Fact]
    public void AnAdapterThisBuildCannotDriveIsRefusedInItsOwnWords()
    {
        var unsupported = new ReadingPaneEngines.Candidate("WPE WebKit", true, false, true, null);
        var choice = ReadingPaneEngines.Choose(unsupported, Gtk(), gtkAsked: false);

        Assert.False(choice.UseWebView);
        Assert.Contains("not supported by this build", choice.Reason);
    }
}
