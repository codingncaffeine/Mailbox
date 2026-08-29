using System.Text;

namespace Mailbox.Protocols;

/// <summary>
/// The code pages mail still arrives in, made resolvable.
/// </summary>
/// <remarks>
/// .NET ships ASCII, the UTF family and Latin-1, and nothing else. Every other code page lives in
/// <see cref="CodePagesEncodingProvider"/> and has to be registered before
/// <c>Encoding.GetEncoding</c> will answer for it — and when it will not, MimeKit falls back to
/// Latin-1, which decodes every byte to *something*. So the failure is silent and complete: a
/// Japanese message becomes a page of accented Latin, a Russian one likewise, and nothing anywhere
/// reports a problem.
/// <para>
/// The tree already knew this twice over — <c>FeedFetch</c> registers the provider because "a
/// great many feeds are still written in them", and <c>PstCodePage</c> because "a Cyrillic or CJK
/// file read as Latin-1 is the classic mojibake". Mail itself, which carries more of it than
/// either, did not. The audit found it with a message in Shift_JIS whose *subject* came out right
/// — RFC 2047 encoded-words are decoded by MimeKit's own table — while its body did not, which is
/// as clear a signature as this fault has.
/// </para>
/// <para>
/// Registration is process-wide and idempotent, and this is called from the places a message is
/// first turned into text rather than from application start alone, so a caller that reaches the
/// mapper without going through the shell — a test, a tool, an import — gets it too.
/// </para>
/// </remarks>
public static class LegacyCodePages
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    /// <summary>Makes the legacy code pages resolvable. Safe to call as often as you like.</summary>
    public static void Register() => _ = Registered.Value;
}
