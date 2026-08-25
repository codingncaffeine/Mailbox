using System.Diagnostics.CodeAnalysis;

namespace Mailbox.Core.Calendars;

/// <summary>
/// The rule for turning what somebody typed into the address of an internet calendar.
/// </summary>
/// <remarks>
/// Shared by the two places that ask for one — the calendar module's own subscribe command and
/// Account Settings' Internet Calendars tab — because an address accepted in one of them and
/// refused in the other is the kind of difference nobody finds until a calendar is missing.
/// <para>
/// <c>webcal:</c> is the same URL over HTTPS. Every publisher writes it that way and no client
/// has ever spoken a webcal protocol, so it is rewritten once here rather than carried on as a
/// third scheme every fetch downstream would have to know about.
/// </para>
/// </remarks>
public static class CalendarSubscription
{
    /// <summary>
    /// True when what was typed is an address a calendar could be fetched from, with
    /// <paramref name="address"/> set to the form the sync should keep.
    /// </summary>
    public static bool TryAddress(string? typed, [NotNullWhen(true)] out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(typed)) return false;
        if (!Uri.TryCreate(typed.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https" or "webcal")) return false;

        // Port -1 drops whatever default came with webcal, which is not HTTPS's.
        address = parsed.Scheme == "webcal"
            ? new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri
            : parsed;
        return true;
    }

    /// <summary>
    /// What to call a subscription before its calendar has said what it is called: the host it
    /// came from, which is what the reference lists too until the first download.
    /// </summary>
    public static string SuggestedName(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Host is { Length: > 0 } host ? host : address.ToString();
    }
}
