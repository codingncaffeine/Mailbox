using System.Text;

namespace Mailbox.Pst;

/// <summary>
/// Windows code pages, resolved once and cached. String8 values in these formats decode by the
/// message's own code page (PidTagMessageCodepage) — a Cyrillic or CJK file read as Latin-1 is
/// the classic mojibake — and the platform's tables cover what Windows wrote.
/// </summary>
public static class PstCodePage
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    /// <summary>The encoding for a stated code page, or null for none, UTF-16, or one the platform does not know.</summary>
    public static Encoding? Resolve(int? codePage)
    {
        // 1200 and 1201 are UTF-16, which is what PtypString already is — a String8 claiming
        // them is confused, and Latin-1 is the safer reading than a two-byte decode.
        if (codePage is not { } page || page <= 0 || page is 1200 or 1201) return null;

        _ = Registered.Value;
        try
        {
            return Encoding.GetEncoding(page);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
