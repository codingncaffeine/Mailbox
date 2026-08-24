namespace Mailbox.Pst;

/// <summary>
/// The one exception this reader throws for a file that cannot be read as it claims to be.
/// </summary>
/// <remarks>
/// Every message is a sentence naming what disagreed with what, because the person who sees it
/// is mid-import with a file somebody else's software wrote: "damaged at 0x..." is actionable
/// where an index-out-of-range five layers down is not. A malformed file must always surface as
/// this and never as a runtime fault — the parsers bounds-check before every read for that
/// reason.
/// </remarks>
public sealed class PstException : Exception
{
    public PstException(string message) : base(message)
    {
    }

    public PstException(string message, Exception inner) : base(message, inner)
    {
    }
}
