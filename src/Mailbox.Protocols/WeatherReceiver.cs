using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Weather;

namespace Mailbox.Protocols;

/// <summary>Everything known about one place's weather: what arrived, when, and what went wrong.</summary>
public sealed record PlaceWeather
{
    public required string PlaceId { get; init; }
    public Forecast? Forecast { get; init; }

    /// <summary>What the Weather Service knows the place by, once it has been asked.</summary>
    public NwsPoint? Point { get; init; }

    /// <summary>The Weather Service answered that it does not cover the place.</summary>
    public bool OutsideWeatherService { get; init; }

    public IReadOnlyList<WeatherAlert> Alerts { get; init; } = [];
    public ForecastDiscussion? Discussion { get; init; }

    public DateTimeOffset? PointFetched { get; init; }
    public DateTimeOffset? AlertsFetched { get; init; }
    public DateTimeOffset? DiscussionFetched { get; init; }

    /// <summary>Why the last forecast request failed, or empty. Cleared by the next success.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Forecast failures in a row, which is what the back-off doubles on.</summary>
    public int Failures { get; init; }

    /// <summary>No forecast request before this, after a failure.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>A request for this place is under way.</summary>
    public bool Updating { get; init; }
}

/// <summary>
/// Fetches, keeps and schedules the weather for the reader's places.
/// </summary>
/// <remarks>
/// Nothing here runs on the interface's thread or waits on it: requests are awaited on the pool,
/// parsing happens where the response arrives, and <see cref="Changed"/> is raised from there,
/// for the interface to marshal what it wants to draw.
/// <para>
/// What arrives is written to the cache exactly as the service sent it and read back through the
/// same parsers at the next start, so a reader opening the application sees the last forecast at
/// once, and the schedule — measured from when each part was fetched, not from when the
/// application started — decides whether anything needs asking for again. A restart never costs a
/// request that a running copy would not have made.
/// </para>
/// <para>
/// The Weather Service is asked only for places it covers. Its answers are extras over the
/// forecast: when one fails, the place keeps what it had and carries on, and the forecast's own
/// error and back-off are unaffected.
/// </para>
/// </remarks>
public sealed class WeatherReceiver : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly WeatherFetch _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, PlaceWeather> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public WeatherReceiver(string cacheDirectory, string userAgent, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _fetch = new WeatherFetch(userAgent, handler);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A place's weather changed. The argument is its id. Raised off the interface's thread.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>What is known about a place now — an empty record before anything has arrived.</summary>
    public PlaceWeather Get(string placeId) => _state.TryGetValue(placeId, out var known) ? known : new PlaceWeather { PlaceId = placeId };

    /// <summary>Searches for places by name or postal code.</summary>
    /// <returns>The places found, or an empty list and the reason the search could not be made.</returns>
    public async Task<(IReadOnlyList<WeatherPlace> Places, string Error)> SearchAsync(string query, string language, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return ([], string.Empty);

        var result = await _fetch.GetAsync(OpenMeteo.SearchUrl(query, language), "application/json", cancellation).ConfigureAwait(false);
        if (!result.Ok) return ([], OpenMeteo.ErrorReason(result.Text) ?? result.Error);

        try
        {
            return (OpenMeteo.ParsePlaces(result.Text, query), string.Empty);
        }
        catch (FormatException ex)
        {
            Log.Warn("A place search answer could not be read.", ex);
            return ([], "The answer from the weather service could not be read.");
        }
    }

    /// <summary>
    /// Reads what the cache holds for the places, so they have weather to show before the first
    /// request. Anything unreadable is skipped: the next refresh replaces it.
    /// </summary>
    public void LoadCache(IEnumerable<WeatherPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        foreach (var place in places)
        {
            var folder = Folder(place.Id);
            if (!Directory.Exists(folder)) continue;

            try
            {
                var meta = File.Exists(Path.Combine(folder, "meta.json"))
                    ? JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json"))) as JsonObject
                    : null;
                DateTimeOffset? When(string key)
                    => meta?[key]?.GetValue<string>() is { } text
                       && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        ? at
                        : null;
                string? Read(string name) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllText(Path.Combine(folder, name)) : null;

                var weather = new PlaceWeather
                {
                    PlaceId = place.Id,
                    Forecast = Read("forecast.json") is { } forecast && When("forecast") is { } fetched
                        ? OpenMeteo.ParseForecast(forecast, fetched)
                        : null,
                    Point = Read("point.json") is { } point ? Nws.ParsePoint(point) : null,
                    PointFetched = When("point"),
                    OutsideWeatherService = meta?["outside"]?.GetValue<bool>() ?? false,
                    Alerts = Read("alerts.json") is { } alerts ? Nws.ParseAlerts(alerts) : [],
                    AlertsFetched = When("alerts"),
                    Discussion = Read("discussion.json") is { } discussion ? Nws.ParseDiscussion(discussion) : null,
                    DiscussionFetched = When("discussion"),
                };

                _state[place.Id] = weather;
                Changed?.Invoke(this, place.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            {
                Log.Warn($"The cached weather for {place.Label} could not be read and will be fetched again.", ex);
            }
        }
    }

    /// <summary>
    /// Fetches whatever is due for a place — or everything, when <paramref name="force"/> is set,
    /// which is Update Now. One refresh per place at a time: a second call waits for the first and
    /// then finds nothing due.
    /// </summary>
    public async Task RefreshAsync(WeatherPlace place, bool force = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(place);
        var gate = _gates.GetOrAdd(place.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            var now = _clock();
            var known = Get(place.Id);
            // Update Now asks whatever the schedule says: the back-off is there to keep automatic
            // retries off a failing service, not to refuse a reader who asked.
            var wantForecast = force
                || WeatherSchedule.IsDue(known.Forecast?.Fetched, WeatherSchedule.Forecast, now, known.RetryAt);

            if (wantForecast)
            {
                Update(place.Id, w => w with { Updating = true });
                await RefreshForecastAsync(place, cancellation).ConfigureAwait(false);
            }

            if (place.HasWeatherService && !Get(place.Id).OutsideWeatherService)
            {
                await RefreshWeatherServiceAsync(place, force, cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Get(place.Id).Updating) Update(place.Id, w => w with { Updating = false });
            gate.Release();
        }
    }

    /// <summary>Drops a place's weather and its cache, for a place the reader removed.</summary>
    public void Forget(string placeId)
    {
        _state.TryRemove(placeId, out _);
        try
        {
            var folder = Folder(placeId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("A removed place's cached weather could not be deleted.", ex);
        }
    }

    private async Task RefreshForecastAsync(WeatherPlace place, CancellationToken cancellation)
    {
        var result = await _fetch.GetAsync(OpenMeteo.ForecastUrl(place.Latitude, place.Longitude), "application/json", cancellation)
            .ConfigureAwait(false);
        var now = _clock();

        if (result.Ok)
        {
            try
            {
                var text = result.Text;
                var forecast = OpenMeteo.ParseForecast(text, now);
                Update(place.Id, w => w with { Forecast = forecast, Error = string.Empty, Failures = 0, RetryAt = null });
                Save(place.Id, "forecast.json", text, "forecast", now);
                return;
            }
            catch (FormatException ex)
            {
                Log.Warn($"The forecast for {place.Label} could not be read.", ex);
                Fail(place, now, "The forecast could not be read.", null);
                return;
            }
        }

        Fail(place, now, OpenMeteo.ErrorReason(result.Text) ?? result.Error, result.RetryAfter);
    }

    private void Fail(WeatherPlace place, DateTimeOffset now, string error, TimeSpan? retryAfter)
    {
        var failures = Get(place.Id).Failures + 1;
        var retry = WeatherSchedule.RetryAt(failures, now, retryAfter);
        Log.Info($"Weather for {place.Label} not updated ({error}); next try {retry:HH:mm:ss}.");
        Update(place.Id, w => w with { Error = error, Failures = failures, RetryAt = retry });
    }

    private async Task RefreshWeatherServiceAsync(WeatherPlace place, bool force, CancellationToken cancellation)
    {
        var known = Get(place.Id);
        var now = _clock();

        if (known.Point is null || WeatherSchedule.IsDue(known.PointFetched, WeatherSchedule.Point, now))
        {
            var result = await _fetch.GetAsync(Nws.PointUrl(place.Latitude, place.Longitude), "application/geo+json", cancellation)
                .ConfigureAwait(false);

            if (result.Status == HttpStatusCode.NotFound && Nws.IsOutsideCoverage(result.Text))
            {
                // An answer, not a failure: this place has no Weather Service, and asking again
                // will not change that. Kept, so a restart does not ask either.
                Update(place.Id, w => w with { OutsideWeatherService = true, PointFetched = now });
                SaveMeta(place.Id);
                return;
            }

            if (result.Ok && TryParse(() => Nws.ParsePoint(result.Text), "point", place) is { } point)
            {
                Update(place.Id, w => w with { Point = point, PointFetched = now });
                Save(place.Id, "point.json", result.Text, "point", now);
            }
        }

        if (force || WeatherSchedule.IsDue(Get(place.Id).AlertsFetched, WeatherSchedule.Alerts, now))
        {
            var result = await _fetch.GetAsync(Nws.AlertsUrl(place.Latitude, place.Longitude), "application/geo+json", cancellation)
                .ConfigureAwait(false);
            if (result.Ok && TryParse(() => Nws.ParseAlerts(result.Text), "alerts", place) is { } alerts)
            {
                Update(place.Id, w => w with { Alerts = alerts, AlertsFetched = now });
                Save(place.Id, "alerts.json", result.Text, "alerts", now);
            }
        }

        if (Get(place.Id).Point is { } office
            && (force || WeatherSchedule.IsDue(Get(place.Id).DiscussionFetched, WeatherSchedule.Discussion, now)))
        {
            var result = await _fetch.GetAsync(Nws.DiscussionUrl(office.Office), "application/ld+json", cancellation)
                .ConfigureAwait(false);
            if (result.Ok && TryParse(() => Nws.ParseDiscussion(result.Text), "discussion", place) is { } discussion)
            {
                Update(place.Id, w => w with { Discussion = discussion, DiscussionFetched = now });
                Save(place.Id, "discussion.json", result.Text, "discussion", now);
            }
        }
    }

    private static T? TryParse<T>(Func<T> parse, string what, WeatherPlace place) where T : class
    {
        try
        {
            return parse();
        }
        catch (FormatException ex)
        {
            Log.Warn($"The Weather Service's {what} for {place.Label} could not be read.", ex);
            return null;
        }
    }

    private void Update(string placeId, Func<PlaceWeather, PlaceWeather> change)
    {
        _state.AddOrUpdate(placeId, id => change(new PlaceWeather { PlaceId = id }), (_, old) => change(old));
        Changed?.Invoke(this, placeId);
    }

    private string Folder(string placeId)
    {
        var safe = new string([.. placeId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-')]);
        return Path.Combine(_cacheDirectory, safe);
    }

    /// <summary>
    /// Writes a response to the cache beside a note of when it arrived: into a temporary file
    /// first and then renamed over the old one, so a crash mid-write leaves the old copy rather
    /// than half of a new one.
    /// </summary>
    private void Save(string placeId, string name, string text, string key, DateTimeOffset fetched)
    {
        try
        {
            var folder = Folder(placeId);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, target, overwrite: true);
            SaveMeta(placeId, (key, fetched));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Weather could not be written to the cache; it will be fetched again at the next start.", ex);
        }
    }

    private void SaveMeta(string placeId, (string Key, DateTimeOffset At)? stamp = null)
    {
        try
        {
            var folder = Folder(placeId);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "meta.json");
            var meta = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : new JsonObject();
            if (stamp is { } s) meta[s.Key] = s.At.ToString("O", CultureInfo.InvariantCulture);
            meta["outside"] = Get(placeId).OutsideWeatherService;
            File.WriteAllText(path + ".tmp", meta.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("The weather cache's notes could not be written.", ex);
        }
    }

    public void Dispose()
    {
        _fetch.Dispose();
        foreach (var gate in _gates.Values) gate.Dispose();
    }
}
