using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Weather;

/// <summary>A place a reader keeps the weather for.</summary>
/// <remarks>
/// The coordinates are what the forecast is asked for; the rest is how the place is named back
/// to the reader. A United States ZIP code is placed at the code's own point, from the list the
/// application carries (see <see cref="ZipCodes"/>); any other postal code is kept only as what
/// was typed, at the point the service chose for the place it belongs to.
/// </remarks>
public sealed record WeatherPlace
{
    /// <summary>Stable across renames at the source: the geocoder's own id where there is one.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }

    /// <summary>The state or province: "California".</summary>
    public string Region { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;

    /// <summary>ISO 3166 alpha-2: "US".</summary>
    public string CountryCode { get; init; } = string.Empty;

    /// <summary>The IANA zone, when the geocoder knew it.</summary>
    public string TimeZone { get; init; } = string.Empty;

    /// <summary>The postal code the reader found it by, when that is how they found it.</summary>
    public string Postcode { get; init; } = string.Empty;

    /// <summary>"Beverly Hills, California" — the name and what makes it unambiguous.</summary>
    public string Label => Region.Length > 0 && !string.Equals(Region, Name, StringComparison.OrdinalIgnoreCase)
        ? $"{Name}, {Region}"
        : Name;

    /// <summary>The whole address, for where there is room: "Beverly Hills, California, United States".</summary>
    public string FullName => Country.Length > 0 ? $"{Label}, {Country}" : Label;

    /// <summary>
    /// Whether the National Weather Service forecasts here: the fifty states and the territories
    /// its offices cover. Everything it adds — warnings, the forecasters' discussion, the radar —
    /// is asked for only where this is true.
    /// </summary>
    public bool HasWeatherService => CountryCode.ToUpperInvariant() is "US" or "PR" or "VI" or "GU" or "AS" or "MP";

    /// <summary>Distance to another point in kilometres, near enough for telling duplicates apart.</summary>
    public double KilometresTo(double latitude, double longitude)
    {
        const double EarthRadiusKm = 6371;
        var meanLatitude = (Latitude + latitude) / 2 * Math.PI / 180;
        var dx = (longitude - Longitude) * Math.PI / 180 * Math.Cos(meanLatitude);
        var dy = (latitude - Latitude) * Math.PI / 180;
        return Math.Sqrt(dx * dx + dy * dy) * EarthRadiusKm;
    }
}

/// <summary>
/// The places a reader keeps, in their order, persisted with the rest of the preferences.
/// </summary>
/// <remarks>
/// Stored as one JSON string under a single key, as the send/receive groups are: the settings
/// file stays readable, and a malformed entry costs a place rather than the module.
/// <para>
/// The first place is home. It is the one the rail shows the weather for and the one the module
/// opens on, which is how every weather application treats the top of its list — so making a
/// place home is moving it to the top, and there is no second notion of it to fall out of step.
/// </para>
/// </remarks>
public sealed class WeatherPlaces
{
    public const string Key = "weather.places";

    /// <summary>Two places closer than this are the same place asked for twice.</summary>
    public const double SamePlaceKm = 1.0;

    private readonly SettingsStore _settings;
    private readonly List<WeatherPlace> _places;

    /// <summary>True while this class is writing the store, so its own write is not read back.</summary>
    private bool _saving;

    public WeatherPlaces(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _places = Load();

        // Watched for the reason the send/receive groups watch it: the Options dialog's Cancel
        // puts the store back, and a list that held on to the cancelled edit would be a Cancel
        // that did not cancel.
        settings.Changed += (_, key) =>
        {
            if (_saving) return;
            if (key.Length > 0 && !string.Equals(key, Key, StringComparison.Ordinal)) return;
            _places.Clear();
            _places.AddRange(Load());
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised after any change, from here or from the store underneath.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<WeatherPlace> All => _places;

    /// <summary>The place the rail shows, or null before any has been added.</summary>
    public WeatherPlace? Home => _places.Count > 0 ? _places[0] : null;

    public WeatherPlace? Find(string id) => _places.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Adds a place at the end, or hands back the one already kept within a kilometre of it —
    /// one point found twice, by name and by a code at its centre or by two codes that share it,
    /// is one place.
    /// </summary>
    public WeatherPlace Add(WeatherPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (_places.FirstOrDefault(p => p.KilometresTo(place.Latitude, place.Longitude) < SamePlaceKm) is { } kept)
        {
            return kept;
        }

        _places.Add(place);
        Save();
        return place;
    }

    public bool Remove(string id)
    {
        var index = _places.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        _places.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>Moves a place to a position in the list, clamped to it.</summary>
    public bool Move(string id, int position)
    {
        var index = _places.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;

        var target = Math.Clamp(position, 0, _places.Count - 1);
        if (target == index) return false;

        var place = _places[index];
        _places.RemoveAt(index);
        _places.Insert(target, place);
        Save();
        return true;
    }

    /// <summary>Makes a place home, which is putting it at the top.</summary>
    public bool MakeHome(string id) => Move(id, 0);

    private List<WeatherPlace> Load()
    {
        if (!_settings.Has(Key)) return [];

        try
        {
            if (JsonNode.Parse(_settings.GetString(Key)) is not JsonArray array) return [];

            var places = new List<WeatherPlace>();
            foreach (var node in array)
            {
                if (Read(node) is { } place) places.Add(place);
                else Log.Warn("A saved weather place could not be read and was skipped.");
            }

            return places;
        }
        catch (JsonException ex)
        {
            Log.Warn("The saved weather places could not be read.", ex);
            return [];
        }
    }

    private static WeatherPlace? Read(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (o["id"]?.GetValue<string>() is not { Length: > 0 } id) return null;
        if (o["name"]?.GetValue<string>() is not { Length: > 0 } name) return null;
        if (o["latitude"] is not JsonValue lat || !lat.TryGetValue<double>(out var latitude)) return null;
        if (o["longitude"] is not JsonValue lon || !lon.TryGetValue<double>(out var longitude)) return null;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return null;

        return new WeatherPlace
        {
            Id = id,
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            Region = o["region"]?.GetValue<string>() ?? string.Empty,
            Country = o["country"]?.GetValue<string>() ?? string.Empty,
            CountryCode = o["countryCode"]?.GetValue<string>() ?? string.Empty,
            TimeZone = o["timeZone"]?.GetValue<string>() ?? string.Empty,
            Postcode = o["postcode"]?.GetValue<string>() ?? string.Empty,
        };
    }

    private void Save()
    {
        var array = new JsonArray();
        foreach (var place in _places)
        {
            array.Add(new JsonObject
            {
                ["id"] = place.Id,
                ["name"] = place.Name,
                ["latitude"] = place.Latitude,
                ["longitude"] = place.Longitude,
                ["region"] = place.Region,
                ["country"] = place.Country,
                ["countryCode"] = place.CountryCode,
                ["timeZone"] = place.TimeZone,
                ["postcode"] = place.Postcode,
            });
        }

        _saving = true;
        try
        {
            _settings.Set(Key, array.ToJsonString());
        }
        finally
        {
            _saving = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
