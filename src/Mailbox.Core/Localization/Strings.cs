namespace Mailbox.Core.Localization;

/// <summary>
/// The interface's language, as every surface asks for it.
/// </summary>
/// <remarks>
/// <b>Ambient on purpose, and not a compromise.</b> An application has one interface language at
/// a time, chosen once and changed by restarting; threading a localizer through every view model,
/// dialog and drawn control to express that would add a parameter to several hundred signatures
/// to model a thing that never varies between them. gettext's own <c>_()</c> is a global for the
/// same reason, and so is every toolkit that wraps it.
/// <para>
/// The property that keeps it honest is that absence is harmless: until something sets
/// <see cref="Current"/> it is <see cref="Localizer.Passthrough"/>, which answers every lookup
/// with its own English. A test, a tool and an unadopted surface all behave identically to
/// today's, so adopting a call site can never be what breaks it.
/// </para>
/// </remarks>
public static class Strings
{
    /// <summary>
    /// The catalogue in force. The passthrough until the application loads one.
    /// </summary>
    /// <remarks>
    /// Settable rather than fixed at start-up because the harness and the tests need to pose a
    /// language, and because a language chosen in Options should be able to take effect without
    /// the process being restarted where the surfaces can redraw themselves.
    /// </remarks>
    public static Localizer Current { get; set; } = Localizer.Passthrough;

    /// <summary>The translation of a string, or the English itself.</summary>
    public static string T(string english) => Current.T(english);

    /// <summary>
    /// The translation of a string that means two things, told apart by a context that is never
    /// shown.
    /// </summary>
    public static string T(string context, string english) => Current.T(context, english);

    /// <summary>The translation for a count, in whichever form this language uses for that number.</summary>
    public static string Plural(string english, string englishPlural, long count, string? context = null)
        => Current.Plural(english, englishPlural, count, context);

    /// <summary>
    /// The translation for a count with the number written into it, grouped for this culture.
    /// </summary>
    public static string Counted(string english, string englishPlural, long count, string? context = null)
        => Current.Counted(english, englishPlural, count, context);
}
