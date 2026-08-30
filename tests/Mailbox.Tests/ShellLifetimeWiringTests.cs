using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// The shell window's close ends the application, whatever hidden machinery is pooled behind it.
/// </summary>
/// <remarks>
/// Found live on 30 August 2026, hours after the warm message window shipped. The lifetime had
/// been left on its default, which shuts down when the <em>last</em> window closes, and the two
/// readings of "last" were the same until the shell grew a hidden pooled window: with the pool
/// warmed, closing the shell no longer closed the last window, so nothing shut down — the
/// process stayed up with no interface, the notification-area icon stayed visible, and every
/// activation of it failed re-showing a closed window, which the framework refuses. Proved from
/// outside by calling the icon's own <c>Activate</c> over the bus and reading the error back.
/// The repair pins the lifetime to the shell wherever the shell becomes the main window; these
/// hold the pin, because the default is the kind of thing a tidy-up quietly restores.
/// </remarks>
public class ShellLifetimeWiringTests
{
    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot(), "src", .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "The repository root was not found above the test binary.");
    }

    /// <summary>Comment lines dropped, so prose about a mode does not count as a use of it.</summary>
    private static string Code(string source)
        => string.Join(
            '\n',
            source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// Wherever the shell becomes the main window, the very next statement pins the lifetime
    /// to it.
    /// </summary>
    /// <remarks>
    /// Adjacency is the assertion on purpose: the pin is only trustworthy while it is decided in
    /// the same breath as the adoption, rather than somewhere later that a new code path around
    /// the adoption could miss. There are two adoptions today — the ordinary launch, and the
    /// minimised start adopting the window the first time it is shown — and the pair below must
    /// come out equal however many there are tomorrow.
    /// </remarks>
    [Fact]
    public void WhereverTheShellBecomesTheMainWindowTheLifetimeIsPinnedToIt()
    {
        var code = Code(Source("Mailbox.App", "App.axaml.cs"));

        var adoptions = Regex.Matches(code, Regex.Escape("desktop.MainWindow = window;")).Count;
        Assert.True(adoptions > 0, "No adoption of the shell as the main window was found at all — "
            + "the lifetime wiring has moved, and this sweep is reading the wrong place.");

        var pinned = Regex.Matches(
            code,
            Regex.Escape("desktop.MainWindow = window;") + @"\s*"
            + Regex.Escape("desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;")).Count;

        Assert.True(
            pinned == adoptions,
            $"The shell is adopted as the main window {adoptions} time(s) and the lifetime is pinned to "
            + $"ShutdownMode.OnMainWindowClose beside {pinned} of them. An adoption without the pin leaves "
            + "the lifetime on its default, which waits for the last window — and the warm message window "
            + "is a real, hidden window, so under the default the shell's close ends nothing: the process "
            + "outlives its interface behind an icon that fails to re-show a closed window.");
    }

    /// <summary>The warm pool forgets a window that dies for real.</summary>
    /// <remarks>
    /// The application's shutdown closes a pooled window without its hold-back, and the field
    /// keeping it must not go on pointing at the corpse: the next open would take it and re-show
    /// a closed window, which is the same refusal the icon hit, one double-click later.
    /// </remarks>
    [Fact]
    public void TheWarmPoolForgetsAWindowThatDiesForReal()
    {
        var code = Code(Source("Mailbox.App", "Views", "MainWindow.axaml.cs"));

        Assert.Contains(
            "if (ReferenceEquals(_warmMessageWindow, window)) _warmMessageWindow = null;",
            code.Replace("\r", string.Empty), StringComparison.Ordinal);
    }
}
