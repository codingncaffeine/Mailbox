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
                "search" => Answer("open-meteo-geocoding-90210.json"),
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

        Assert.Equal(["forecast", "point", "alerts", "discussion"], services.Asked);
        var weather = receiver.Get(BeverlyHills.Id);
        Assert.Equal(22, weather.Forecast?.Current.Temperature);
        Assert.Equal(_now, weather.Forecast?.Fetched);
        Assert.Equal("LOX", weather.Point?.Office);
        Assert.Equal(2, weather.Alerts.Count);
        Assert.Equal("PUB", weather.Discussion?.Office);
        Assert.False(weather.Updating);
        Assert.Empty(weather.Error);
    }

    [Fact]
    public async Task APlaceAbroadIsNeverSentToTheWeatherService()
    {
        var services = new Services();
        using var receiver = Receiver(services);

        await receiver.RefreshAsync(Oslo, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast"], services.Asked);
        Assert.Equal(14.2, receiver.Get(Oslo.Id).Forecast?.Current.Temperature);
    }

    /// <summary>
    /// Inside the half hour nothing about the forecast is asked again; the warnings, which change
    /// by the minute, are asked for every five.
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

        Assert.Equal(["forecast", "alerts", "discussion"], services.Asked);
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

            Assert.Equal(["forecast", "point"], services.Asked);
            Assert.True(receiver.Get(BeverlyHills.Id).OutsideWeatherService);
        }

        services.Asked.Clear();
        _now += TimeSpan.FromHours(2);
        using var restarted = Receiver(services);
        restarted.LoadCache([BeverlyHills]);
        await restarted.RefreshAsync(BeverlyHills, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["forecast"], services.Asked);
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

        var (found, error) = await receiver.SearchAsync("90210", "en", TestContext.Current.CancellationToken);
        Assert.Empty(error);
        Assert.Equal("Beverly Hills", Assert.Single(found).Name);

        using var offline = new WeatherReceiver(_cache, "Mailbox/0.0", new FailingNetwork(), () => _now);
        var (none, why) = await offline.SearchAsync("90210", "en", TestContext.Current.CancellationToken);
        Assert.Empty(none);
        Assert.Equal("Could not reach the weather service.", why);
    }

    private sealed class FailingNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Network is unreachable");
    }
}
