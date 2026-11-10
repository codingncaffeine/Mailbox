using System.Net;
using System.Net.Sockets;
using Mailbox.App.Views;
using Mailbox.Rendering;

namespace Mailbox.HeadlessTests;

/// <summary>
/// Pictures fetched for a message are kept for the session, so reading it again — or coming back
/// to it while its pictures are still on their way — does not fetch them a second time.
/// </summary>
public class PictureMemoryTests
{
    [Fact]
    public void TheLeastRecentlyUsedPictureGoesFirst()
    {
        var memory = new PictureMemory(budgetBytes: 100);

        memory.Add("a", Picture(20));
        memory.Add("b", Picture(20));
        memory.Add("c", Picture(20));
        memory.Add("d", Picture(20));
        memory.Add("e", Picture(20));
        Assert.Equal(100, memory.Used);

        // Reading "a" makes "b" the oldest, so "b" is the one the next picture pushes out.
        Assert.NotNull(memory.TryGet("a"));
        memory.Add("f", Picture(20));

        Assert.Null(memory.TryGet("b"));
        Assert.NotNull(memory.TryGet("a"));
        Assert.NotNull(memory.TryGet("f"));
        Assert.Equal(100, memory.Used);
    }

    [Fact]
    public void APictureTooLargeToShareTheBudgetIsNotKept()
    {
        var memory = new PictureMemory(budgetBytes: 100);
        memory.Add("small", Picture(10));
        memory.Add("huge", Picture(26));

        Assert.Null(memory.TryGet("huge"));
        Assert.NotNull(memory.TryGet("small"));
    }

    [Fact]
    public void AReplacedPictureIsCountedOnce()
    {
        var memory = new PictureMemory(budgetBytes: 100);
        memory.Add("a", Picture(20));
        memory.Add("a", Picture(10));

        Assert.Equal(1, memory.Count);
        Assert.Equal(10, memory.Used);
    }

    /// <summary>
    /// Against a real server that counts its requests: read a message, read it again, and read
    /// one whose picture is still arriving from an earlier visit. Each address is asked for once.
    /// </summary>
    [Fact]
    public async Task APictureIsFetchedOnceHoweverOftenItsMessageIsRead()
    {
        using var server = new CountingServer();
        var quick = server.Url("quick.png");
        var slow = server.Url("slow.png");

        var first = await RemoteImages.FetchAsync([Blocked(quick)], TestContext.Current.CancellationToken);
        var again = await RemoteImages.FetchAsync([Blocked(quick)], TestContext.Current.CancellationToken);
        Assert.Equal("image/png", first[quick].Type);
        Assert.Same(first[quick], again[quick]);
        Assert.Equal(1, server.Requests("quick.png"));

        // The reader leaves while the slow one is arriving and comes back before it lands: the
        // second visit joins the fetch under way.
        var leaving = RemoteImages.FetchAsync([Blocked(slow)], TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var back = await RemoteImages.FetchAsync([Blocked(slow)], TestContext.Current.CancellationToken);
        await leaving;

        Assert.Equal("image/png", back[slow].Type);
        Assert.Equal(1, server.Requests("slow.png"));
    }

    private static BlockedResource Blocked(string url) => new(url, "127.0.0.1", BlockedResourceKind.Image);

    private static FetchedPicture Picture(int bytes) => new(new byte[bytes], "image/png");

    /// <summary>A local server that answers every path with a tiny PNG and counts what it was asked.</summary>
    private sealed class CountingServer : IDisposable
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly int _port;
        private readonly string _run = Guid.NewGuid().ToString("N");

        public CountingServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        /// <summary>An address unique to this run, so the session-wide memory starts empty for it.</summary>
        public string Url(string name) => $"http://127.0.0.1:{_port}/{_run}/{name}";

        public int Requests(string name)
        {
            lock (_counts) return _counts.GetValueOrDefault(name);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var name = context.Request.Url!.Segments[^1];
                lock (_counts) _counts[name] = _counts.GetValueOrDefault(name) + 1;

                _ = Task.Run(async () =>
                {
                    if (name.StartsWith("slow", StringComparison.Ordinal)) await Task.Delay(1000);
                    context.Response.ContentType = "image/png";
                    context.Response.ContentLength64 = Png.Length;
                    await context.Response.OutputStream.WriteAsync(Png);
                    context.Response.Close();
                });
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
