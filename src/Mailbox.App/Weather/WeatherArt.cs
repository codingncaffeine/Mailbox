using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Weather;

/// <summary>
/// The weather pictures, decoded once and kept.
/// </summary>
/// <remarks>
/// A picture is asked for by its key — <c>day/partly-cloudy</c>, <c>night/clear-night</c> — which
/// is its file under the weather art. Decoding happens on the pool, never on the interface's
/// thread: the first ask for a key starts the decode and every later ask shares it, so a list of
/// forty days asking for the same sun decodes one sun.
/// <para>
/// A key with no file behind it resolves to null and says so in the log once, rather than
/// throwing into a render; the art tests hold the names in the source against the files, so this
/// is the belt to their braces.
/// </para>
/// </remarks>
internal static class WeatherArt
{
    /// <summary>The picture shown for the weather before there is any weather: a sun behind a cloud.</summary>
    public const string Placeholder = "day/partly-cloudy";

    private static readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> Decoded = new(StringComparer.Ordinal);

    /// <summary>The picture for a key, decoding it on the pool the first time it is asked for.</summary>
    public static Task<Bitmap?> LoadAsync(string key)
        => Decoded.GetOrAdd(key, k => new Lazy<Task<Bitmap?>>(() => Task.Run(() => Decode(k)))).Value;

    /// <summary>The picture if it has already been decoded, for a draw that cannot wait for one.</summary>
    public static Bitmap? Ready(string key)
        => Decoded.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    private static Bitmap? Decode(string key)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://mailbox/Assets/Weather/{key}.png"));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException or InvalidOperationException)
        {
            Log.Warn($"The weather picture “{key}” could not be loaded.", ex);
            return null;
        }
    }
}
