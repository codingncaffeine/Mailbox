using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
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

    /// <summary>The air this hour, on the place's own index.</summary>
    public AirQuality? AirQuality { get; init; }

    /// <summary>Air quality failures in a row, and no request before <see cref="AirQualityRetryAt"/>, as for the forecast.</summary>
    public int AirQualityFailures { get; init; }

    public DateTimeOffset? AirQualityRetryAt { get; init; }

    public DateTimeOffset? PointFetched { get; init; }
    public DateTimeOffset? AlertsFetched { get; init; }
    public DateTimeOffset? DiscussionFetched { get; init; }

    /// <summary>Weather Service failures in a row, and nothing asked of it before <see cref="WeatherServiceRetryAt"/>.</summary>
    public int WeatherServiceFailures { get; init; }

    public DateTimeOffset? WeatherServiceRetryAt { get; init; }

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
/// error and back-off are unaffected. The service backs off on its own clock: a failure stops
/// that round's requests to it and holds the next ones off, one minute, two, four, as the
/// forecast does — a service that is down, or one that has begun to want a key, is not asked
/// every minute for every place.
/// </para>
/// <para>
/// Air quality is an extra too, asked for everywhere, on the index of the place's own region. It
/// fails quietly — the page leaves its card out rather than showing an error — but it backs off
/// the way the forecast does, so a service that is down is not asked every minute.
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
    /// <param name="region">
    /// The reader's own country, ISO 3166 alpha-2, which decides the order when five digits are a
    /// postcode in their country as well as a ZIP code in the United States.
    /// </param>
    /// <returns>The places found, or an empty list and the reason the search could not be made.</returns>
    /// <remarks>
    /// A United States ZIP code is also looked up in the list the application carries, which puts
    /// it at its own point: the service knows most codes only as the city they belong to, and a
    /// few not at all. The service is still asked, for the other countries whose codes are five
    /// digits, but its record of the code's own city is dropped as the same place found less
    /// exactly — and a ZIP code found in the list needs nothing from the service to be offered.
    /// </remarks>
    public async Task<(IReadOnlyList<WeatherPlace> Places, string Error)> SearchAsync(
        string query, string language, string region = "", CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return ([], string.Empty);

        // Read on the pool: the list is half a megabyte to unpack, and a search starts on the
        // interface's thread.
        var code = ZipCodes.Parse(query);
        var zip = code is not null
            ? await Task.Run(() => FindZip(code), cancellation).ConfigureAwait(false)
            : null;

        // A ZIP+4 is asked for as its five digits, which are all the service knows of it.
        var asked = code ?? query;
        var result = await _fetch.GetAsync(OpenMeteo.SearchUrl(asked, language), "application/json", cancellation).ConfigureAwait(false);
        IReadOnlyList<WeatherPlace> found = [];
        var error = string.Empty;
        if (!result.Ok)
        {
            error = OpenMeteo.ErrorReason(result.Text) ?? result.Error;
        }
        else
        {
            try
            {
                found = OpenMeteo.ParsePlaces(result.Text, asked);
            }
            catch (FormatException ex)
            {
                Log.Warn("A place search answer could not be read.", ex);
                error = "The answer from the weather service could not be read.";
            }
        }

        if (zip is null) return (found, error);

        var others = found.Where(p => !(p.CountryCode == "US" && p.Postcode == zip.Postcode)).ToList();
        List<WeatherPlace> theirs = region.Length == 2 && !region.Equals("US", StringComparison.OrdinalIgnoreCase)
            ? [.. others.Where(p => p.CountryCode.Equals(region, StringComparison.OrdinalIgnoreCase))]
            : [];
        return ([.. theirs, zip, .. others.Except(theirs)], string.Empty);
    }

    private const string ZipList = "Mailbox.Protocols.us-zip.tsv.gz";

    /// <summary>A code from the ZIP code list the build carries, unpacked as it is read.</summary>
    private static WeatherPlace? FindZip(string zip)
    {
        using var stream = typeof(WeatherReceiver).Assembly.GetManifestResourceStream(ZipList);
        if (stream is null)
        {
            Log.Warn("The ZIP code list is missing from this build; ZIP codes are searched with the weather service alone.");
            return null;
        }

        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
        return ZipCodes.Find(reader, zip);
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
                    AirQuality = Read("air.json") is { } air && When("air") is { } airFetched
                        ? OpenMeteo.ParseAirQuality(air, airFetched)
                        : null,
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

            var air = Get(place.Id);
            if (force || WeatherSchedule.IsDue(air.AirQuality?.Fetched, WeatherSchedule.AirQuality, _clock(), air.AirQualityRetryAt))
            {
                await RefreshAirQualityAsync(place, cancellation).ConfigureAwait(false);
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

    private async Task RefreshAirQualityAsync(WeatherPlace place, CancellationToken cancellation)
    {
        var scale = AirQualityIndex.For(place.CountryCode);
        var result = await _fetch.GetAsync(OpenMeteo.AirQualityUrl(place.Latitude, place.Longitude, scale), "application/json", cancellation)
            .ConfigureAwait(false);
        var now = _clock();

        if (result.Ok && TryParse(() => OpenMeteo.ParseAirQuality(result.Text, now), "air quality", place) is { } air)
        {
            Update(place.Id, w => w with { AirQuality = air, AirQualityFailures = 0, AirQualityRetryAt = null });
            Save(place.Id, "air.json", result.Text, "air", now);
            return;
        }

        var failures = Get(place.Id).AirQualityFailures + 1;
        var retry = WeatherSchedule.RetryAt(failures, now, result.RetryAfter);
        Log.Info($"Air quality for {place.Label} not updated ({OpenMeteo.ErrorReason(result.Text) ?? result.Error}); next try {retry:HH:mm:ss}.");
        Update(place.Id, w => w with { AirQualityFailures = failures, AirQualityRetryAt = retry });
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

        // Update Now asks anyway, as it does of the forecast.
        if (!force && known.WeatherServiceRetryAt is { } hold && now < hold) return;

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

            if (result.Ok && TryParse(() => Nws.ParsePoint(result.Text), "Weather Service's point", place) is { } point)
            {
                Update(place.Id, w => w with { Point = point, PointFetched = now, WeatherServiceFailures = 0, WeatherServiceRetryAt = null });
                Save(place.Id, "point.json", result.Text, "point", now);
            }
            else
            {
                WeatherServiceFailed(place, now, result);
                return;
            }
        }

        if (force || WeatherSchedule.IsDue(Get(place.Id).AlertsFetched, WeatherSchedule.Alerts, now))
        {
            var result = await _fetch.GetAsync(Nws.AlertsUrl(place.Latitude, place.Longitude), "application/geo+json", cancellation)
                .ConfigureAwait(false);
            if (result.Ok && TryParse(() => Nws.ParseAlerts(result.Text), "Weather Service's alerts", place) is { } alerts)
            {
                Update(place.Id, w => w with { Alerts = alerts, AlertsFetched = now, WeatherServiceFailures = 0, WeatherServiceRetryAt = null });
                Save(place.Id, "alerts.json", result.Text, "alerts", now);
            }
            else
            {
                WeatherServiceFailed(place, now, result);
                return;
            }
        }

        if (Get(place.Id).Point is { } office
            && (force || WeatherSchedule.IsDue(Get(place.Id).DiscussionFetched, WeatherSchedule.Discussion, now)))
        {
            var result = await _fetch.GetAsync(Nws.DiscussionUrl(office.Office), "application/ld+json", cancellation)
                .ConfigureAwait(false);
            if (result.Ok && TryParse(() => Nws.ParseDiscussion(result.Text), "Weather Service's discussion", place) is { } discussion)
            {
                Update(place.Id, w => w with { Discussion = discussion, DiscussionFetched = now, WeatherServiceFailures = 0, WeatherServiceRetryAt = null });
                Save(place.Id, "discussion.json", result.Text, "discussion", now);
            }
            else
            {
                WeatherServiceFailed(place, now, result);
            }
        }
    }

    /// <summary>
    /// The Weather Service did not answer, or sent what could not be read: nothing more is asked of
    /// it for the place until its back-off has passed.
    /// </summary>
    private void WeatherServiceFailed(WeatherPlace place, DateTimeOffset now, WeatherFetchResult result)
    {
        var failures = Get(place.Id).WeatherServiceFailures + 1;
        var retry = WeatherSchedule.RetryAt(failures, now, result.RetryAfter);
        var reason = result.Ok ? "an answer that could not be read" : result.Error;
        Log.Info($"The Weather Service did not answer for {place.Label} ({reason}); next try {retry:HH:mm:ss}.");
        Update(place.Id, w => w with { WeatherServiceFailures = failures, WeatherServiceRetryAt = retry });
    }

    private static T? TryParse<T>(Func<T> parse, string what, WeatherPlace place) where T : class
    {
        try
        {
            return parse();
        }
        catch (FormatException ex)
        {
            Log.Warn($"The {what} for {place.Label} could not be read.", ex);
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
