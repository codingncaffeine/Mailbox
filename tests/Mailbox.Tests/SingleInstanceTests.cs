using Mailbox.Core;

namespace Mailbox.Tests;

/// <summary>
/// Single-instance activation: a second launch's command line reaches the primary, and when no
/// primary is listening the caller learns it should become one.
/// </summary>
public class SingleInstanceTests : IDisposable
{
    // A socket path of this run's own, so the test does not collide with a real Mailbox or with
    // another test run. Short, because a Unix socket path has a length limit.
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"mbx-{Guid.NewGuid():N}"[..24] + ".sock");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail(because);
    }

    [Fact]
    public async Task AHandoffReachesTheListeningPrimary()
    {
        using var primary = new SingleInstance(_path);

        IReadOnlyList<string>? received = null;
        primary.Listen(args => received = args);
        await Eventually(() => File.Exists(_path), "the primary should be listening");

        // A second launch hands over and is told to exit.
        using var secondary = new SingleInstance(_path);
        Assert.True(secondary.TryHandOff(["mailto:priya@example.net", "--flag"]));

        await Eventually(() => received is not null, "the primary should receive the command line");
        Assert.Equal(["mailto:priya@example.net", "--flag"], received);
    }

    [Fact]
    public void WithNoPrimaryTheCallerIsToldToBecomeOne()
    {
        using var first = new SingleInstance(_path);

        // Nothing is listening: the handoff fails, and the caller proceeds as the primary.
        Assert.False(first.TryHandOff(["mailto:x@example.com"]));
    }

    [Fact]
    public void AStaleSocketFileIsClearedSoTheNextInstanceCanBind()
    {
        // A file left by a crash — present, but nothing behind it.
        File.WriteAllText(_path, string.Empty);

        using var instance = new SingleInstance(_path);
        Assert.False(instance.TryHandOff(["x"]));

        // The stale file is gone, so a fresh primary can bind where it was.
        Assert.False(File.Exists(_path));
    }
}
