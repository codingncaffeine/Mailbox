using System.Text;
using MimeKit.Tnef;

namespace Mailbox.Import;

/// <summary>
/// The body of a message whose only body is compressed RTF ([MS-OXRTFCP]) — the shape mail from
/// older writers arrives in when neither a plain nor an HTML body was kept.
/// </summary>
/// <remarks>
/// Decompression is MimeKit's, already trusted here for everything MIME. What is ours is what
/// happens after: RTF from those writers is very often just HTML in a coat ([MS-OXRTFEX] — the
/// original markup carried in <c>\*\htmltag</c> groups, with the RTF's own rendering fenced
/// behind <c>\htmlrtf</c> toggles), and that HTML is recovered rather than re-derived. RTF that
/// was really written as RTF is stripped to its text: formatting is the part that cannot be
/// carried honestly, and text-with-paragraphs beats an attachment nobody can open.
/// </remarks>
internal static class RtfBody
{
    /// <summary>The HTML or the text inside a compressed RTF body — (null, null) when the bytes will not open.</summary>
    public static (string? Html, string? Text) FromCompressed(byte[] compressed)
    {
        byte[] rtf;
        try
        {
            var converter = new RtfCompressedToRtf();
            var decoded = converter.Flush(compressed, 0, compressed.Length, out var index, out var length);
            rtf = decoded.AsSpan(index, length).ToArray();
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (rtf.Length == 0) return (null, null);

        var text = Encoding.Latin1.GetString(rtf);
        return text.Contains(@"\fromhtml", StringComparison.Ordinal)
            ? (DeEncapsulateHtml(text), null)
            : (null, PlainText(text));
    }

    /// <summary>
    /// Recovers encapsulated HTML: the markup lives in <c>\*\htmltag</c> destinations, the
    /// visible text between them belongs to the document, and everything fenced by
    /// <c>\htmlrtf</c> … <c>\htmlrtf0</c> is the RTF rendering of what the tags already say.
    /// </summary>
    private static string DeEncapsulateHtml(string rtf)
    {
        var html = new StringBuilder();
        var suppress = false;
        Walk(rtf, (text, inHtmlTag) =>
        {
            if (inHtmlTag || !suppress) html.Append(text);
        }, control =>
        {
            switch (control.Word)
            {
                case "htmlrtf":
                    suppress = control.Parameter != 0;
                    break;
                case "par" when !suppress:
                    html.Append("\r\n");
                    break;
                case "tab" when !suppress:
                    html.Append('\t');
                    break;
            }
        });

        return html.ToString();
    }

    /// <summary>Strips real RTF to its text: destinations dropped whole, paragraphs kept as lines.</summary>
    private static string PlainText(string rtf)
    {
        var text = new StringBuilder();
        Walk(rtf, (run, inHtmlTag) =>
        {
            if (!inHtmlTag) text.Append(run);
        }, control =>
        {
            switch (control.Word)
            {
                case "par" or "line":
                    text.Append('\n');
                    break;
                case "tab":
                    text.Append('\t');
                    break;
            }
        });

        return text.ToString().Trim();
    }

    private readonly record struct ControlWord(string Word, int Parameter);

    /// <summary>
    /// One tokenizer for both readings: emits text runs (saying whether they sit inside an
    /// <c>\*\htmltag</c> destination) and control words; skips the header tables and every
    /// unknown starred destination whole, because a colour table read as text is a body that
    /// begins with numbers.
    /// </summary>
    private static void Walk(string rtf, Action<string, bool> emit, Action<ControlWord> onControl)
    {
        var at = 0;
        var htmlTagDepth = -1;
        var skipDepth = -1;
        var depth = 0;
        var ansi = Encoding.Latin1;

        while (at < rtf.Length)
        {
            var c = rtf[at];

            if (c == '{')
            {
                depth++;
                at++;

                // A destination the reader does not know is skipped whole when starred; the
                // known ones — font, colour, stylesheet, info, pictures — are skipped by name.
                if (skipDepth < 0)
                {
                    if (rtf.AsSpan(at).StartsWith(@"\*\htmltag"))
                    {
                        htmlTagDepth = depth;
                        at += 10;
                        while (at < rtf.Length && char.IsAsciiDigit(rtf[at])) at++;
                        if (at < rtf.Length && rtf[at] == ' ') at++;
                        continue;
                    }

                    if (rtf.AsSpan(at).StartsWith(@"\*"))
                    {
                        skipDepth = depth;
                        continue;
                    }

                    foreach (var table in (ReadOnlySpan<string>)[@"\fonttbl", @"\colortbl", @"\stylesheet", @"\info", @"\pict"])
                    {
                        if (rtf.AsSpan(at).StartsWith(table))
                        {
                            skipDepth = depth;
                            break;
                        }
                    }
                }

                continue;
            }

            if (c == '}')
            {
                if (htmlTagDepth == depth) htmlTagDepth = -1;
                if (skipDepth == depth) skipDepth = -1;
                depth--;
                at++;
                continue;
            }

            if (c == '\\')
            {
                if (at + 1 >= rtf.Length) break;
                var next = rtf[at + 1];

                if (next is '{' or '}' or '\\')
                {
                    if (skipDepth < 0) emit(next.ToString(), htmlTagDepth >= 0);
                    at += 2;
                    continue;
                }

                if (next == '\'')
                {
                    if (at + 3 < rtf.Length && skipDepth < 0
                        && byte.TryParse(rtf.AsSpan(at + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
                    {
                        emit(ansi.GetString([value]), htmlTagDepth >= 0);
                    }

                    at += 4;
                    continue;
                }

                // A control word: letters, an optional signed number, an optional space.
                var word = at + 1;
                while (word < rtf.Length && char.IsAsciiLetter(rtf[word])) word++;
                var name = rtf[(at + 1)..word];
                var numberStart = word;
                if (word < rtf.Length && (rtf[word] == '-' || char.IsAsciiDigit(rtf[word]))) word++;
                while (word < rtf.Length && char.IsAsciiDigit(rtf[word])) word++;
                var parameter = numberStart < word && int.TryParse(rtf.AsSpan(numberStart, word - numberStart), out var parsed)
                    ? parsed
                    : 1;
                if (word < rtf.Length && rtf[word] == ' ') word++;

                if (name.Length == 0)
                {
                    at += 2; // \<symbol> — nothing this reading wants
                    continue;
                }

                if (name == "u" && skipDepth < 0)
                {
                    // A Unicode escape carries its own fallback character(s) after it, which
                    // must not be emitted twice; \uc says how many to eat, one by convention.
                    emit(char.ConvertFromUtf32(parameter < 0 ? parameter + 65536 : parameter), htmlTagDepth >= 0);
                    if (word < rtf.Length && rtf[word] == '\\' && word + 1 < rtf.Length && rtf[word + 1] == '\'')
                        word += 4;
                    else if (word < rtf.Length && rtf[word] is not ('\\' or '{' or '}'))
                        word++;
                }
                else if (skipDepth < 0)
                {
                    onControl(new ControlWord(name.ToString(), numberStart < word ? parameter : 1));
                }

                at = word;
                continue;
            }

            var run = at;
            while (run < rtf.Length && rtf[run] is not ('\\' or '{' or '}')) run++;
            var textRun = rtf[at..run].Replace("\r", string.Empty).Replace("\n", string.Empty);
            if (textRun.Length > 0 && skipDepth < 0) emit(textRun, htmlTagDepth >= 0);
            at = run;
        }
    }
}
