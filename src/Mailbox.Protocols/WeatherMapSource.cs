using System.Collections.Concurrent;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Weather;

namespace Mailbox.Protocols;

/// <summary>
/// Asks the map services what times a layer has and for the pictures of it — from the reader's
/// own connection, with no key, like everything else the weather does.
/// </summary>
/// <remarks>
/// A layer's times are kept for five minutes: radar is rebuilt every few minutes and a model's
/// run every few hours, so asking more often would only ask again for what is already known. The
/// pictures are not kept here — the map holds the frames it is playing, and a picture for a view
/// the reader has left is not worth keeping.
/// </remarks>
public sealed class WeatherMapSource : IDisposable
{
    private static readonly TimeSpan TimesLast = TimeSpan.FromMinutes(5);

    private readonly WeatherFetch _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, IReadOnlyList<DateTimeOffset> Times)> _times = new(StringComparer.Ordinal);

    public WeatherMapSource(string userAgent, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _fetch = new WeatherFetch(userAgent, handler, TimeSpan.FromSeconds(30));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The layer's times, oldest first; empty for a layer that has none or could not be asked.</summary>
    public async Task<IReadOnlyList<DateTimeOffset>> TimesAsync(MapLayer layer, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Capabilities.Length == 0) return [];
        if (_times.TryGetValue(layer.Capabilities, out var known) && _clock() - known.Fetched < TimesLast) return known.Times;

        var result = await _fetch.GetAsync(layer.Capabilities, "text/xml", cancellation).ConfigureAwait(false);
        if (!result.Ok)
        {
            Log.Info($"Weather map: the times of {layer.Layer} could not be read ({result.Error}).");
            return known.Times ?? [];
        }

        var times = MapLayers.Times(result.Text, layer.Layer);
        _times[layer.Capabilities] = (_clock(), times);
        return times;
    }

    /// <summary>A picture's bytes, or null when the service did not send one.</summary>
    public async Task<byte[]?> ImageAsync(string url, CancellationToken cancellation = default)
    {
        var result = await _fetch.GetAsync(url, "image/png", cancellation).ConfigureAwait(false);
        if (result.Ok && result.Body.Length > 8 && result.Body[0] == 0x89 && result.Body[1] == (byte)'P') return result.Body;
        if (!result.Ok) Log.Info($"Weather map: a picture was refused ({result.Error}).");
        return null;
    }

    public void Dispose() => _fetch.Dispose();
}
