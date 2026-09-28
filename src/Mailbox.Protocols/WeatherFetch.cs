using System.Net;
using System.Net.Http.Headers;

namespace Mailbox.Protocols;

/// <summary>What one request for weather came back with.</summary>
/// <param name="Status">The HTTP status, or 0 when the request never got that far.</param>
/// <param name="Body">The body, empty for a failure.</param>
/// <param name="Error">What went wrong, or empty.</param>
public sealed record WeatherFetchResult(HttpStatusCode Status, byte[] Body, string Error = "")
{
    public bool Ok => (int)Status is >= 200 and < 300;

    /// <summary>Whether the service is asking to be left alone for a while, rather than refusing this request.</summary>
    public bool Throttled => Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>How long the service asked to be left alone, from Retry-After.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Asking the weather services for something, from the reader's own connection.
/// </summary>
/// <remarks>
/// No key and no account: both services count requests by where they come from, so each reader
/// has their own allowance, and what keeps it from ever being reached is the schedule, not this.
/// This is the polite part — compression asked for, a User-Agent that says what is asking and
/// where to find it (the Weather Service asks every client for one), a ceiling on what is read,
/// and Retry-After passed back up so the schedule can honour it.
/// <para>
/// The handler is injectable, as the feed fetcher's is, so the whole of this is testable against
/// a fake server rather than the real services.
/// </para>
/// </remarks>
public sealed class WeatherFetch : IDisposable
{
    /// <summary>
    /// The most that will be read. A forecast is forty kilobytes and a station's radar loop under
    /// a megabyte; this is generous for both and finite, which is the point.
    /// </summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    private readonly HttpClient _client;

    public WeatherFetch(string userAgent, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        var owns = handler is null;
        handler ??= new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };

        _client = new HttpClient(handler, disposeHandler: owns)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = MaximumBytes,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>Asks for a document. Never throws for the network: a failure is a result.</summary>
    public async Task<WeatherFetchResult> GetAsync(string url, string? accept = null, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return new WeatherFetchResult(0, [], "That is not a secure web address.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (accept is { Length: > 0 }) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);

            return new WeatherFetchResult(response.StatusCode, body, response.IsSuccessStatusCode ? string.Empty : Describe(response.StatusCode))
            {
                RetryAfter = RetryAfter(response),
            };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new WeatherFetchResult(0, [], "The weather service did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new WeatherFetchResult(ex.StatusCode ?? 0, [], ex.StatusCode is null ? "Could not reach the weather service." : ex.Message);
        }
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "The weather service asked for fewer requests for a while.",
        HttpStatusCode.ServiceUnavailable => "The weather service is busy.",
        HttpStatusCode.NotFound => "The weather service has nothing for this place.",
        _ => $"The weather service answered {(int)status}.",
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        return null;
    }

    public void Dispose() => _client.Dispose();
}
