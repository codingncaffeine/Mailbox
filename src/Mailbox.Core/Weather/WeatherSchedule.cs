namespace Mailbox.Core.Weather;

/// <summary>
/// How often each kind of weather is asked for again, and how long to stay away after a failure.
/// </summary>
/// <remarks>
/// The intervals follow how often the thing itself changes, not how often a reader might look:
/// the models behind a forecast are rerun hourly at best, so half an hour catches every run
/// without asking twice for the same one; warnings change by the minute when they change at all;
/// a discussion is written a few times a day; and an office's grid square almost never moves,
/// though the service asks that it be looked up again now and then rather than kept for ever.
/// <para>
/// A failure backs off by doubling — one minute, two, four — to an hour at most, and never sooner
/// than the service's own Retry-After when it sent one. A reader offline for a day costs the
/// services a request an hour, not one a minute.
/// </para>
/// </remarks>
public static class WeatherSchedule
{
    public static readonly TimeSpan Forecast = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Alerts = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Discussion = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan Point = TimeSpan.FromDays(7);

    /// <summary>A station's loop is rebuilt every few minutes; asked for only while it is on screen.</summary>
    public static readonly TimeSpan Radar = TimeSpan.FromMinutes(5);

    /// <summary>The longest a failure keeps the next attempt away.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether something last fetched at <paramref name="last"/> is due again: never fetched, or
    /// older than <paramref name="every"/> — unless a back-off still holds it off.
    /// </summary>
    public static bool IsDue(DateTimeOffset? last, TimeSpan every, DateTimeOffset now, DateTimeOffset? notBefore = null)
    {
        if (notBefore is { } hold && now < hold) return false;
        return last is not { } fetched || now - fetched >= every;
    }

    /// <summary>
    /// When to try again after <paramref name="failures"/> failures in a row, the first being 1.
    /// </summary>
    public static DateTimeOffset RetryAt(int failures, DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        var doubling = TimeSpan.FromMinutes(Math.Pow(2, Math.Clamp(failures - 1, 0, 10)));
        var wait = doubling < LongestBackoff ? doubling : LongestBackoff;
        if (retryAfter is { } asked && asked > wait) wait = asked;
        return now + wait;
    }
}
