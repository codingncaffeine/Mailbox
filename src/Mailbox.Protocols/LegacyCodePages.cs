using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Registers before anything in this assembly runs, because registering late is the same as
    /// not registering at all.
    /// </summary>
    /// <remarks>
    /// MimeKit resolves a charset name through a static cache of its own, and a name it failed to
    /// resolve stays failed for the life of the process — so a single MIME parse that happens
    /// before the provider is registered poisons every later one. Calling <see cref="Register"/>
    /// from the composition root is enough for the application, whose first parse comes long
    /// afterwards, and was not enough for the test assembly, where the order tests run in decides
    /// it: the same four code pages passed here and failed on CI, on the same commit.
    /// <para>
    /// A module initializer runs before any code in the assembly does, which makes the ordering
    /// question go away rather than answering it.
    /// </para>
    /// </remarks>
#pragma warning disable CA2255 // Running before anything else in the assembly is the whole point.
    [ModuleInitializer]
    internal static void RegisterEarly() => Register();
#pragma warning restore CA2255

    /// <summary>Makes the legacy code pages resolvable. Safe to call as often as you like.</summary>
    public static void Register() => _ = Registered.Value;
}
