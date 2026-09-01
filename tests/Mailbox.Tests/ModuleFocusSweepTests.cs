namespace Mailbox.Tests;

/// <summary>
/// Every module the shell can switch to hands the keyboard to its own surface.
/// </summary>
/// <remarks>
/// This is a sweep over the source rather than a run of the application, because what it protects
/// is a thing that is easy to leave out and impossible to see: a module whose case in
/// <c>SwitchModule</c> forgets the focus reads exactly like every other module until somebody
/// presses an arrow key in it and nothing moves. Verified in the application by pose — each of
/// the seven says the name of the surface that took the keyboard — and held here so the eighth
/// module cannot ship without it.
/// </remarks>
public class ModuleFocusSweepTests
{
    [Fact]
    public void EveryModuleCaseGivesItsSurfaceTheKeyboard()
    {
        var text = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Mailbox.App", "Views", "MainWindow.Calendar.cs"));

        var start = text.IndexOf("private void SwitchModule(", StringComparison.Ordinal);
        Assert.True(start > 0, "SwitchModule was not found — this sweep is reading the wrong file.");

        var end = text.IndexOf("Log.Info($\"Module: {module}.\")", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of SwitchModule was not found — this sweep is reading nothing.");

        var body = text[start..end];

        // Split on the case labels themselves, so each piece is one module's own arm of the
        // switch and the default — which is Mail, the one module whose surface is not in the host.
        var arms = body.Split("case MailboxModule.", StringSplitOptions.None).Skip(1).ToList();
        Assert.True(arms.Count >= 6, $"only {arms.Count} module cases parsed — the sweep is not reading the switch.");

        var forgotten = arms
            .Where(arm => !arm.Contains("focusSurface =", StringComparison.Ordinal))
            .Select(arm => arm[..arm.IndexOf(':', StringComparison.Ordinal)].Trim())
            .ToList();

        Assert.True(
            forgotten.Count == 0,
            $"switching to {string.Join(", ", forgotten)} leaves the focus where it was — the module's "
            + "arrow keys are then reachable only by tabbing into it, which is how six of the seven "
            + "modules shipped before this was swept.");

        Assert.Contains("default:", body, StringComparison.Ordinal);
        var mail = body[body.IndexOf("default:", StringComparison.Ordinal)..];
        Assert.True(
            mail.Contains("focusSurface =", StringComparison.Ordinal),
            "the default arm is Mail, and it needs the focus like the rest.");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mailbox.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
