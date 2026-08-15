namespace Mailbox.Core.Settings;

/// <summary>
/// How long a sent message waits before it can actually go.
/// </summary>
/// <remarks>
/// §12's addition, and one of the few that is on by default. The argument for that is the same
/// one every client that has it makes: the cost of the hold is a few seconds of latency nobody
/// notices, because mail is not instant messaging and a message five seconds late is a message
/// on time. The cost of not having it is the one everybody has paid at least once.
/// <para>
/// Shaped the way the reference would have shaped it: not a ribbon button — a toast is not
/// ribbon real-estate — but a setting beside delayed delivery, which is the same mechanism with
/// a different number in it. The outbox already knew how to hold something back.
/// </para>
/// </remarks>
public sealed class UndoSend(SettingsStore settings)
{
    public const string EnabledKey = "mail.undosend.enabled";
    public const string SecondsKey = "mail.undosend.seconds";

    /// <summary>Long enough to notice the mistake, short enough not to be a delay.</summary>
    public const int DefaultSeconds = 5;

    /// <summary>
    /// The most it will hold for.
    /// </summary>
    /// <remarks>
    /// Past about half a minute this stops being an undo and becomes delayed delivery, which
    /// already exists and is per-message rather than global. A cap keeps the two features from
    /// becoming the same badly-named one.
    /// </remarks>
    public const int MaximumSeconds = 30;

    private readonly SettingsStore _settings =
        settings ?? throw new ArgumentNullException(nameof(settings));

    public bool IsEnabled
    {
        get => _settings.GetBool(EnabledKey, fallback: true);
        set => _settings.Set(EnabledKey, value);
    }

    /// <summary>How long the hold lasts, clamped to something that is still an undo.</summary>
    public int Seconds
    {
        get => Clamp((int)_settings.GetNumber(SecondsKey, DefaultSeconds));
        set => _settings.Set(SecondsKey, Clamp(value));
    }

    /// <summary>
    /// When a message queued now may be sent, or null when it may go at once.
    /// </summary>
    /// <param name="now">The clock, so a test does not have to wait five seconds.</param>
    public DateTimeOffset? HoldUntil(DateTimeOffset now)
        => IsEnabled && Seconds > 0 ? now.AddSeconds(Seconds) : null;

    private static int Clamp(int seconds) => Math.Clamp(seconds, 0, MaximumSeconds);
}
