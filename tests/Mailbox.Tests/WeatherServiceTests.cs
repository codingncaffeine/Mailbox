using System.Net;
using Mailbox.Core.Weather;
using Mailbox.Protocols;

namespace Mailbox.Tests;

public class NwsTests
{
    [Fact]
    public void APointNamesItsOfficeGridSquareAndRadar()
    {
        var point = Nws.ParsePoint(WeatherFixtures.Read("nws-points-beverly-hills.json"));

        Assert.Equal(new NwsPoint("LOX", 150, 47, "KSOX", "CAZ368", "CAC037", "Beverly Hills", "CA"), point);
    }

    /// <summary>
    /// Outside the United States the service answers with a "no such point" problem, which is an
    /// answer — this place has no Weather Service — not a failure to retry.
    /// </summary>
    [Fact]
    public void OutsideTheCountryIsAnAnswerNotAFailure()
    {
        Assert.True(Nws.IsOutsideCoverage(WeatherFixtures.Read("nws-points-outside-us.json")));
        Assert.False(Nws.IsOutsideCoverage(WeatherFixtures.Read("nws-points-beverly-hills.json")));
        Assert.False(Nws.IsOutsideCoverage("not json"));
    }

    [Fact]
    public void AlertsAreReadWithTheirSeverityAndTheirHours()
    {
        var alerts = Nws.ParseAlerts(WeatherFixtures.Read("nws-alerts-colorado.json"));

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a =>
        {
            Assert.Equal("Flood Watch", a.Event);
            Assert.Equal(AlertSeverity.Severe, a.Severity);
            Assert.Equal(new DateTimeOffset(2026, 9, 28, 6, 0, 0, TimeSpan.FromHours(-6)), a.Onset);
            Assert.Equal(new DateTimeOffset(2026, 9, 30, 6, 0, 0, TimeSpan.FromHours(-6)), a.Ends);
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
        });
        Assert.Equal(["NWS Pueblo CO", "NWS Grand Junction CO"], alerts.Select(a => a.Sender));
    }

    [Fact]
    public void TheMostSeriousComesFirstAndTestsAreLeftOut()
    {
        const string Json = """
            {"features":[
              {"properties":{"id":"1","event":"Wind Advisory","severity":"Moderate","status":"Actual","onset":"2026-09-28T06:00:00-06:00"}},
              {"properties":{"id":"2","event":"Tornado Warning","severity":"Extreme","status":"Actual","onset":"2026-09-28T09:00:00-06:00"}},
              {"properties":{"id":"3","event":"Tornado Warning","severity":"Extreme","status":"Test"}},
              {"properties":{"id":"4","event":"Frost Advisory","severity":"Moderate","status":"Actual","onset":"2026-09-28T02:00:00-06:00"}}
            ]}
            """;

        var alerts = Nws.ParseAlerts(Json);

        Assert.Equal(["2", "4", "1"], alerts.Select(a => a.Id));
    }

    [Fact]
    public void TheDiscussionIsReadIntoItsSections()
    {
        var discussion = Nws.ParseDiscussion(WeatherFixtures.Read("nws-afd.json"));

        Assert.Equal("PUB", discussion.Office);
        Assert.Equal("National Weather Service Pueblo CO", discussion.Issuer);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 23, 33, 0, TimeSpan.Zero), discussion.Issued);
        Assert.Equal(
            [
                "Key Messages",
                "Short Term (Through Monday)",
                "Long Term (Monday Night through Sunday)",
                "Aviation (00Z TAFs through 00Z Tuesday)",
                "Watches/Warnings/Advisories",
            ],
            discussion.Sections.Select(s => s.Title));
    }

    /// <summary>
    /// The text arrives wrapped for a teletype. A bullet and a paragraph are joined back into one
    /// run each, so they reflow to whatever width they are shown at.
    /// </summary>
    [Fact]
    public void BulletsStayBulletsAndParagraphsAreJoinedBackUp()
    {
        var discussion = Nws.ParseDiscussion(WeatherFixtures.Read("nws-afd.json"));

        var key = discussion.Sections[0];
        Assert.Equal(3, key.Blocks.Count);
        Assert.All(key.Blocks, b => Assert.Equal(DiscussionBlockKind.Bullet, b.Kind));
        Assert.StartsWith("Increasing moisture within increasing southwest flow will bring rain, heavy at times,", key.Blocks[0].Text);
        Assert.Equal("Warmer and Drier weather for next weekend.", key.Blocks[2].Text);

        var shortTerm = discussion.Sections[1];
        Assert.Equal("Issued at 248 PM MDT Sun Sep 27 2026", shortTerm.Issued);
        Assert.All(shortTerm.Blocks, b =>
        {
            Assert.Equal(DiscussionBlockKind.Paragraph, b.Kind);
            Assert.DoesNotContain('\n', b.Text);
            Assert.DoesNotContain("  ", b.Text);
        });

        // The forecasters' initials after the end of the product are not a section.
        Assert.DoesNotContain(discussion.Sections, s => s.Title.StartsWith("Short Term...", StringComparison.Ordinal));
    }

    [Fact]
    public void ATableIsKeptExactlyAsWritten()
    {
        const string Table = "Pueblo          47  81  52  74 /  10  40  60  70\nColorado Springs 45  78  50  70 /  10  50  60  70";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            productText = ".PRELIMINARY POINT TEMPS/POPS...\n" + Table + "\n\n&&\n\n$$\n",
            issuingOffice = "KPUB",
        });

        var section = Assert.Single(Nws.ParseDiscussion(json).Sections);

        Assert.Equal("Preliminary Point Temps/Pops", section.Title);
        var block = Assert.Single(section.Blocks);
        Assert.Equal(DiscussionBlockKind.Table, block.Kind);
        Assert.Equal(Table, block.Text);
    }

    [Fact]
    public void TheAddressesAreTheServicesOwn()
    {
        Assert.Equal("https://api.weather.gov/points/34.0736,-118.4004", Nws.PointUrl(34.07362, -118.40036));
        Assert.Equal("https://api.weather.gov/alerts/active?point=34.0736,-118.4004", Nws.AlertsUrl(34.07362, -118.40036));
        Assert.Equal("https://api.weather.gov/products/types/AFD/locations/LOX/latest", Nws.DiscussionUrl("lox"));
        Assert.Equal("https://radar.weather.gov/ridge/standard/KSOX_loop.gif", Nws.RadarLoopUrl("ksox"));
    }
}

public class WeatherScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhatWasNeverFetchedIsDueAndWhatIsFreshIsNot()
    {
        Assert.True(WeatherSchedule.IsDue(null, WeatherSchedule.Forecast, Now));
        Assert.False(WeatherSchedule.IsDue(Now.AddMinutes(-29), WeatherSchedule.Forecast, Now));
        Assert.True(WeatherSchedule.IsDue(Now.AddMinutes(-30), WeatherSchedule.Forecast, Now));
    }

    [Fact]
    public void ABackOffHoldsEvenWhatIsDue()
    {
        Assert.False(WeatherSchedule.IsDue(null, WeatherSchedule.Forecast, Now, notBefore: Now.AddMinutes(1)));
        Assert.True(WeatherSchedule.IsDue(null, WeatherSchedule.Forecast, Now, notBefore: Now));
    }

    /// <summary>A reader offline for a day costs the services a request an hour, not one a minute.</summary>
    [Fact]
    public void FailuresBackOffByDoublingToAnHour()
    {
        Assert.Equal(Now.AddMinutes(1), WeatherSchedule.RetryAt(1, Now));
        Assert.Equal(Now.AddMinutes(2), WeatherSchedule.RetryAt(2, Now));
        Assert.Equal(Now.AddMinutes(32), WeatherSchedule.RetryAt(6, Now));
        Assert.Equal(Now.AddHours(1), WeatherSchedule.RetryAt(7, Now));
        Assert.Equal(Now.AddHours(1), WeatherSchedule.RetryAt(500, Now));
    }

    [Fact]
    public void TheServicesOwnRetryAfterIsNeverCutShort()
    {
        Assert.Equal(Now.AddMinutes(10), WeatherSchedule.RetryAt(1, Now, TimeSpan.FromMinutes(10)));
        Assert.Equal(Now.AddMinutes(4), WeatherSchedule.RetryAt(3, Now, TimeSpan.FromSeconds(30)));
    }
}

public class WeatherFetchTests
{
    private const string Agent = "Mailbox/0.0 (+https://github.com/codingncaffeine/Mailbox)";

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task ItSaysWhatIsAskingAndHandsBackTheBody()
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":1}") });
        using var fetch = new WeatherFetch(Agent, server);

        var result = await fetch.GetAsync("https://api.open-meteo.com/v1/forecast?x=1", "application/json", TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("{\"ok\":1}", result.Text);
        var request = Assert.Single(server.Seen);
        Assert.Equal(Agent, request.Headers.UserAgent.ToString());
        Assert.Equal("application/json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task ThrottlingCarriesTheServicesRetryAfter()
    {
        var server = new Server(_ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":true,\"reason\":\"Minutely API request limit exceeded.\"}") };
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return busy;
        });
        using var fetch = new WeatherFetch(Agent, server);

        var result = await fetch.GetAsync("https://api.open-meteo.com/v1/forecast", cancellation: TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.True(result.Throttled);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal("Minutely API request limit exceeded.", OpenMeteo.ErrorReason(result.Text));
    }

    [Fact]
    public async Task ANetworkFailureIsAResultNotAnException()
    {
        var server = new Server(_ => throw new HttpRequestException("No route to host"));
        using var fetch = new WeatherFetch(Agent, server);

        var result = await fetch.GetAsync("https://api.weather.gov/points/1,1", cancellation: TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)0, result.Status);
        Assert.Equal("Could not reach the weather service.", result.Error);
    }

    [Theory]
    [InlineData("http://api.weather.gov/points/1,1")]
    [InlineData("not an address")]
    public async Task OnlyASecureAddressIsAsked(string url)
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var fetch = new WeatherFetch(Agent, server);

        var result = await fetch.GetAsync(url, cancellation: TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Empty(server.Seen);
    }
}
