using System.Net;
using Mailbox.Core.Weather;
using Mailbox.Protocols;

namespace Mailbox.Tests;

public sealed class WeatherReceiverTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "mailbox-weather-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 28, 2, 5, 0, TimeSpan.Zero);

    private static readonly WeatherPlace BeverlyHills = new()
    {
        Id = "geonames:5328041", Name = "Beverly Hills", Region = "California", CountryCode = "US",
        Latitude = 34.07362, Longitude = -118.40036,
    };

    private static readonly WeatherPlace Oslo = new()
    {
        Id = "geonames:3143244", Name = "Oslo", Region = "Oslo", CountryCode = "NO",
        Latitude = 59.91273, Longitude = 10.74609,
    };

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    /// <summary>
    /// The services, played back from the recorded responses: every request is noted, and each
    /// kind of address gets the answer the real service gave it — or the status a test sets.
    /// </summary>
    private sealed class Services : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public HttpStatusCode ForecastStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode AirStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode WeatherServiceStatus { get; set; } = HttpStatusCode.OK;
        public bool PointOutsideCoverage { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(Kind(url));

            HttpResponseMessage Answer(string fixture, HttpStatusCode status = HttpStatusCode.OK)
                => new(status) { Content = new StringContent(WeatherFixtures.Read(fixture)) };

            return Task.FromResult(Kind(url) switch
            {
                "forecast" when ForecastStatus != HttpStatusCode.OK => new HttpResponseMessage(ForecastStatus),
                "forecast" => Answer(url.Contains("latitude=59", StringComparison.Ordinal)
                    ? "open-meteo-forecast-oslo.json"
                    : "open-meteo-forecast-beverly-hills.json"),
                "search" when url.Contains("name=85083", StringComparison.Ordinal) => Answer("open-meteo-geocoding-zzqxv.json"),
                "search" when url.Contains("name=75001", StringComparison.Ordinal) => Answer("open-meteo-geocoding-75001.json"),
                "search" => Answer("open-meteo-geocoding-90210.json"),
                "air" when AirStatus != HttpStatusCode.OK => new HttpResponseMessage(AirStatus),
                "air" => Answer(url.Contains("latitude=59", StringComparison.Ordinal)
                    ? "open-meteo-air-oslo.json"
                    : "open-meteo-air-beverly-hills.json"),
                "point" or "alerts" or "discussion" when WeatherServiceStatus != HttpStatusCode.OK => new HttpResponseMessage(WeatherServiceStatus),
                "point" when PointOutsideCoverage => Answer("nws-points-outside-us.json", HttpStatusCode.NotFound),
                "point" => Answer("nws-points-beverly-hills.json"),
                "alerts" => Answer("nws-alerts-colorado.json"),
                "discussion" => Answer("nws-afd.json"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        public static string Kind(string url) => url switch
        {
            _ when url.StartsWith(OpenMeteo.ForecastEndpoint, StringComparison.Ordinal) => "forecast",
            _ when url.StartsWith(OpenMeteo.SearchEndpoint, StringComparison.Ordinal) => "search",
            _ when url.StartsWith(OpenMeteo.AirQualityEndpoint, StringComparison.Ordinal) => "air",
            _ when url.Contains("/points/", StringComparison.Ordinal) => "point",
            _ when url.Contains("/alerts/", StringComparison.Ordinal) => "alerts",
            _ when url.Contains("/products/types/AFD/", StringComparison.Ordinal) => "discussion",
            _ => url,
        };
    }

    private WeatherReceiver Receiver(Services services)
        => new(_cache, "Mailbox/0.0 (+https://github.com/codingncaffeine/Mailbox)", services, () => _now);

    [Fact]
    public async Task AnAmericanPlaceGetsItsForecastAndTheWeatherServicesExtras()
    {
        var services = new Services();
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast", "point", "alerts", "discussion", "air"], services.Asked);
        var weather = receiver.Get(BeverlyHills.Id);
        Assert.Equal(22, weather.Forecast?.Current.Temperature);
        Assert.Equal(_now, weather.Forecast?.Fetched);
        Assert.Equal("LOX", weather.Point?.Office);
        Assert.Equal(2, weather.Alerts.Count);
        Assert.Equal("PUB", weather.Discussion?.Office);
        Assert.Equal(new AirIndex(76, AirPollutant.FineParticles), weather.AirQuality?.UnitedStates);
        Assert.False(weather.Updating);
        Assert.Empty(weather.Error);
    }

    [Fact]
    public async Task APlaceAbroadIsNeverSentToTheWeatherService()
    {
        var services = new Services();
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast", "air"], services.Asked);
        Assert.Equal(14.2, receiver.Get(Oslo.Id).Forecast?.Current.Temperature);
        Assert.Equal(21, receiver.Get(Oslo.Id).AirQuality?.European?.Value);
        Assert.Null(receiver.Get(Oslo.Id).AirQuality?.UnitedStates);
    }

    /// <summary>
    /// Inside the half hour nothing about the forecast is asked again; the warnings, which change
    /// by the minute, are asked for every five, and the discussion and the air quality every hour.
    /// </summary>
    [Fact]
    public async Task EachPartIsAskedForOnItsOwnSchedule()
    {
        var services = new Services();
        using var receiver = Receiver(services);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        services.Asked.Clear();

        _now += TimeSpan.FromMinutes(2);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(services.Asked);

        _now += TimeSpan.FromMinutes(4);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["alerts"], services.Asked);

        services.Asked.Clear();
        _now += TimeSpan.FromMinutes(25);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["forecast", "alerts"], services.Asked);

        services.Asked.Clear();
        _now += TimeSpan.FromMinutes(30);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["forecast", "alerts", "discussion", "air"], services.Asked);
    }

    [Fact]
    public async Task UpdateNowAsksForEverythingButTheOfficesSquare()
    {
        var services = new Services();
        using var receiver = Receiver(services);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        services.Asked.Clear();

        _now += TimeSpan.FromMinutes(1);
        await receiver.RefreshAsync(BeverlyHills, force: true, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast", "alerts", "discussion", "air"], services.Asked);
    }

    /// <summary>
    /// The service's "no such point" is an answer: the place is outside what it covers, which is
    /// remembered — across a restart too — rather than asked again every few minutes.
    /// </summary>
    [Fact]
    public async Task APointOutsideTheServiceIsRememberedAndNotAskedAgain()
    {
        var services = new Services { PointOutsideCoverage = true };
        using (var receiver = Receiver(services))
        {
            await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);

            Assert.Equal(["forecast", "point", "air"], services.Asked);
            Assert.True(receiver.Get(BeverlyHills.Id).OutsideWeatherService);
        }

        services.Asked.Clear();
        _now += TimeSpan.FromHours(2);
        using var restarted = Receiver(services);
        restarted.LoadCache([BeverlyHills]);
        await restarted.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast", "air"], services.Asked);
    }

    /// <summary>A failing service is tried again after one minute, then two, then four — not on every tick.</summary>
    [Fact]
    public async Task AFailureBacksOffAndASuccessClearsIt()
    {
        var services = new Services { ForecastStatus = HttpStatusCode.ServiceUnavailable };
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        var failed = receiver.Get(Oslo.Id);
        Assert.Equal(1, failed.Failures);
        Assert.Equal(_now + TimeSpan.FromMinutes(1), failed.RetryAt);
        Assert.Equal("The weather service is busy.", failed.Error);

        services.Asked.Clear();
        _now += TimeSpan.FromSeconds(30);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(services.Asked);

        _now += TimeSpan.FromSeconds(31);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["forecast"], services.Asked);
        Assert.Equal(2, receiver.Get(Oslo.Id).Failures);
        Assert.Equal(_now + TimeSpan.FromMinutes(2), receiver.Get(Oslo.Id).RetryAt);

        services.ForecastStatus = HttpStatusCode.OK;
        _now += TimeSpan.FromMinutes(3);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        var recovered = receiver.Get(Oslo.Id);
        Assert.Equal(0, recovered.Failures);
        Assert.Null(recovered.RetryAt);
        Assert.Empty(recovered.Error);
        Assert.NotNull(recovered.Forecast);
    }

    /// <summary>
    /// A Weather Service that fails is left alone for a minute, then two, rather than asked on
    /// every tick — the rest of that round's requests to it are not made either — and Update Now
    /// asks it all the same.
    /// </summary>
    [Fact]
    public async Task AFailingWeatherServiceBacksOffOnItsOwn()
    {
        var services = new Services { WeatherServiceStatus = HttpStatusCode.Forbidden };
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        var failed = receiver.Get(BeverlyHills.Id);
        Assert.Equal(["forecast", "point", "air"], services.Asked);
        Assert.NotNull(failed.Forecast);
        Assert.Empty(failed.Error);
        Assert.Equal(1, failed.WeatherServiceFailures);
        Assert.Equal(_now + TimeSpan.FromMinutes(1), failed.WeatherServiceRetryAt);

        services.Asked.Clear();
        _now += TimeSpan.FromSeconds(30);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(services.Asked);

        _now += TimeSpan.FromSeconds(31);
        await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["point"], services.Asked);
        Assert.Equal(_now + TimeSpan.FromMinutes(2), receiver.Get(BeverlyHills.Id).WeatherServiceRetryAt);

        services.WeatherServiceStatus = HttpStatusCode.OK;
        services.Asked.Clear();
        _now += TimeSpan.FromSeconds(10);
        await receiver.RefreshAsync(BeverlyHills, force: true, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["forecast", "point", "alerts", "discussion", "air"], services.Asked);
        var recovered = receiver.Get(BeverlyHills.Id);
        Assert.Equal(0, recovered.WeatherServiceFailures);
        Assert.Null(recovered.WeatherServiceRetryAt);
        Assert.Equal("PUB", recovered.Discussion?.Office);
    }

    /// <summary>
    /// Air quality that cannot be had is left out quietly — the forecast keeps no error for it —
    /// and backs off as the forecast does, rather than being asked for on every tick.
    /// </summary>
    [Fact]
    public async Task AFailingAirQualityServiceBacksOffAndLeavesTheForecastAlone()
    {
        var services = new Services { AirStatus = HttpStatusCode.ServiceUnavailable };
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        var failed = receiver.Get(Oslo.Id);
        Assert.Equal(["forecast", "air"], services.Asked);
        Assert.NotNull(failed.Forecast);
        Assert.Empty(failed.Error);
        Assert.Equal(0, failed.Failures);
        Assert.Null(failed.AirQuality);
        Assert.Equal(1, failed.AirQualityFailures);
        Assert.Equal(_now + TimeSpan.FromMinutes(1), failed.AirQualityRetryAt);

        services.Asked.Clear();
        _now += TimeSpan.FromSeconds(30);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(services.Asked);

        _now += TimeSpan.FromSeconds(31);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["air"], services.Asked);
        Assert.Equal(2, receiver.Get(Oslo.Id).AirQualityFailures);
        Assert.Equal(_now + TimeSpan.FromMinutes(2), receiver.Get(Oslo.Id).AirQualityRetryAt);

        services.AirStatus = HttpStatusCode.OK;
        services.Asked.Clear();
        _now += TimeSpan.FromMinutes(3);
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(["air"], services.Asked);
        var recovered = receiver.Get(Oslo.Id);
        Assert.Equal(0, recovered.AirQualityFailures);
        Assert.Null(recovered.AirQualityRetryAt);
        Assert.Equal(21, recovered.AirQuality?.European?.Value);
    }

    /// <summary>
    /// A restart shows the last forecast at once and, inside the half hour, asks for nothing: the
    /// schedule runs from when the forecast arrived, not from when the application started.
    /// </summary>
    [Fact]
    public async Task TheCacheCarriesTheForecastAndItsAgeAcrossARestart()
    {
        var services = new Services();
        using (var receiver = Receiver(services))
        {
            await receiver.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        }

        var fetched = _now;
        services.Asked.Clear();
        _now += TimeSpan.FromMinutes(3);

        using var restarted = Receiver(services);
        restarted.LoadCache([BeverlyHills]);
        var cached = restarted.Get(BeverlyHills.Id);
        Assert.Equal(22, cached.Forecast?.Current.Temperature);
        Assert.Equal(fetched, cached.Forecast?.Fetched);
        Assert.Equal("LOX", cached.Point?.Office);
        Assert.Equal(2, cached.Alerts.Count);
        Assert.NotNull(cached.Discussion);
        Assert.Equal(76, cached.AirQuality?.UnitedStates?.Value);
        Assert.Equal(fetched, cached.AirQuality?.Fetched);

        await restarted.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(services.Asked);
    }

    [Fact]
    public async Task ForgettingAPlaceDropsItsCache()
    {
        using var receiver = Receiver(new Services());
        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);
        Assert.NotEmpty(Directory.GetDirectories(_cache));

        receiver.Forget(Oslo.Id);

        Assert.Empty(Directory.GetDirectories(_cache));
        Assert.Null(receiver.Get(Oslo.Id).Forecast);
    }

    [Fact]
    public async Task ASearchFindsPlacesAndAFailedOneSaysWhy()
    {
        var services = new Services();
        using var receiver = Receiver(services);

        var (found, error) = await receiver.SearchAsync("Beverly Hills", "en", cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(error);
        Assert.Equal("geonames:5328041", Assert.Single(found).Id);

        using var offline = new WeatherReceiver(_cache, "Mailbox/0.0", new FailingNetwork(), () => _now);
        var (none, why) = await offline.SearchAsync("Beverly Hills", "en", cancellation: TestContext.Current.CancellationToken);
        Assert.Empty(none);
        Assert.Equal("Could not reach the weather service.", why);
    }

    /// <summary>The service has no record of 85083; the application's own list puts it in north Phoenix.</summary>
    [Fact]
    public async Task AZipCodeTheServiceDoesNotKnowIsFoundInTheList()
    {
        using var receiver = Receiver(new Services());

        var (found, error) = await receiver.SearchAsync("85083", "en", "US", TestContext.Current.CancellationToken);

        Assert.Empty(error);
        var place = Assert.Single(found);
        Assert.Equal(("zip:US:85083", "Phoenix", "Arizona", "US", "85083"), (place.Id, place.Name, place.Region, place.CountryCode, place.Postcode));
        Assert.Equal((33.7352, -112.1294), (place.Latitude, place.Longitude));
        Assert.True(place.HasWeatherService);
    }

    /// <summary>The service finds 90210 as Beverly Hills' own record; the code's point replaces it rather than joining it.</summary>
    [Fact]
    public async Task AZipCodeIsPlacedAtItsOwnPointRatherThanItsCitys()
    {
        using var receiver = Receiver(new Services());

        var (found, _) = await receiver.SearchAsync("90210-1234", "en", "US", TestContext.Current.CancellationToken);

        var place = Assert.Single(found);
        Assert.Equal("zip:US:90210", place.Id);
        Assert.Equal((34.0901, -118.4065), (place.Latitude, place.Longitude));
    }

    [Fact]
    public async Task AZipCodeIsFoundWithoutTheNetwork()
    {
        using var offline = new WeatherReceiver(_cache, "Mailbox/0.0", new FailingNetwork(), () => _now);

        var (found, error) = await offline.SearchAsync("85083", "en", "US", TestContext.Current.CancellationToken);

        Assert.Empty(error);
        Assert.Equal("zip:US:85083", Assert.Single(found).Id);
    }

    /// <summary>
    /// 75001 is the first arrondissement of Paris as well as Addison, Texas: a reader in France
    /// has Paris first, a reader in the United States — or one whose country the search cannot
    /// tell — the ZIP code, and either way Addison is found once, at its code's own point.
    /// </summary>
    [Theory]
    [InlineData("FR", new[] { "geonames:2988507", "geonames:6269531", "zip:US:75001" })]
    [InlineData("US", new[] { "zip:US:75001", "geonames:2988507", "geonames:6269531" })]
    [InlineData("", new[] { "zip:US:75001", "geonames:2988507", "geonames:6269531" })]
    public async Task FiveDigitsPutTheReadersOwnCountryFirst(string region, string[] expected)
    {
        using var receiver = Receiver(new Services());

        var (found, _) = await receiver.SearchAsync("75001", "en", region, TestContext.Current.CancellationToken);

        Assert.Equal(expected, found.Select(p => p.Id));
    }

    private sealed class FailingNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Network is unreachable");
    }
}
