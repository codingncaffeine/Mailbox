using System.Globalization;
using System.Text;
using MimeKit;

namespace Mailbox.Rendering;

/// <summary>What is being done to the original.</summary>
public enum ReplyKind
{
    Reply,
    ReplyAll,
    Forward,
}

/// <summary>
/// What a reply does with the original, in the Options page's own order.
/// </summary>
public enum QuoteStyle
{
    /// <summary>The header block and the original below it, as the reference does by default.</summary>
    Include = 0,

    /// <summary>Nothing of the original.</summary>
    None = 1,

    /// <summary>The original as a <c>message/rfc822</c> attachment.</summary>
    Attach = 2,

    /// <summary>The header block and the original, indented.</summary>
    IncludeIndented = 3,

    /// <summary>Each line of the original prefixed — the plain-text convention.</summary>
    Prefix = 4,
}

/// <summary>What the caller can vary about a reply.</summary>
public sealed record ReplyOptions
{
    /// <summary>
    /// The reader's own addresses, so a Reply All does not send them a copy of their own reply.
    /// </summary>
    public IReadOnlyCollection<string> OwnAddresses { get; init; } = [];

    public QuoteStyle Style { get; init; } = QuoteStyle.Include;

    /// <summary>The prefix on each line when the style asks for one.</summary>
    public string Prefix { get; init; } = ">";

    /// <summary>Whether the quoted half is wanted as HTML or as text.</summary>
    public bool PlainText { get; init; }
}

/// <summary>
/// One attachment carried into a forward or an attached original.
/// </summary>
public sealed record CarriedPart(string Name, string MimeType, MimeEntity Entity);

/// <summary>
/// Everything a compose window needs to open on: who it goes to, what it says at the top, and
/// what it quotes.
/// </summary>
public sealed record ReplyDraft
{
    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public string Subject { get; init; } = string.Empty;

    /// <summary>The original's Message-ID, for <c>In-Reply-To</c>. Null on a forward.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>The original's References with its Message-ID appended, for <c>References</c>.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The quoted original as the compose window's editor can load it, or empty.</summary>
    public string QuotedHtml { get; init; } = string.Empty;

    /// <summary>The same, as text with the prefix, for a plain-text reply.</summary>
    public string QuotedText { get; init; } = string.Empty;

    /// <summary>What travels with the message: a forward's attachments, or the attached original.</summary>
    public IReadOnlyList<CarriedPart> Attachments { get; init; } = [];

    /// <summary>
    /// How the original is quoted, for the compose window to finish the job: the editor's
    /// parser keeps a quotation only as a property of its own paragraphs, so indenting and
    /// prefixing are done on the loaded document rather than in this markup.
    /// </summary>
    public QuoteStyle Style { get; init; } = QuoteStyle.Include;
}

/// <summary>
/// Turns a received message into the start of a reply or a forward.
/// </summary>
/// <remarks>
/// The rules are RFC 5322 §3.6.4's and every client's since: <c>Reply-To</c> outranks
/// <c>From</c>; a reply to all keeps everyone but the reader; the subject gains one <c>RE:</c>
/// or <c>FW:</c> and never two; <c>In-Reply-To</c> and <c>References</c> are what let the
/// recipient's client thread it. The quoted original is the reference's shape — a rule, then
/// From/Sent/To/Cc/Subject, then the message — because that is what everyone the reader writes
/// to is used to seeing.
/// <para>
/// The quoted body goes through the same sanitizer the reading pane uses, and for the same
/// reason: it is a stranger's markup, and it is about to be loaded into an editor and sent on
/// to someone else. Nothing that could act on it survives, remote images become placeholders,
/// and <c>cid:</c> parts become <c>data:</c> so the editor can show them.
/// </para>
/// </remarks>
public static class Reply
{
    public static ReplyDraft Build(MimeMessage original, ReplyKind kind, ReplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        options ??= new ReplyOptions();

        var (to, cc) = Recipients(original, kind, options.OwnAddresses);

        var draft = new ReplyDraft
        {
            To = to,
            Cc = cc,
            Subject = Prefixed(original.Subject ?? string.Empty, kind == ReplyKind.Forward ? "FW" : "RE"),
            InReplyTo = kind == ReplyKind.Forward ? null : original.MessageId,
            References = kind == ReplyKind.Forward ? [] : References(original),
        };

        // A forward carries the original's attachments; a reply does not, because the person
        // being replied to already has them. Attaching the original itself is a style choice
        // that carries the whole message instead of quoting it.
        var attachments = new List<CarriedPart>();

        if (options.Style == QuoteStyle.Attach)
        {
            attachments.Add(new CarriedPart(
                (string.IsNullOrWhiteSpace(original.Subject) ? "message" : original.Subject.Trim()) + ".eml",
                "message/rfc822",
                new MessagePart { Message = original }));

            return draft with { Attachments = attachments };
        }

        if (kind == ReplyKind.Forward)
        {
            attachments.AddRange(MessageAttachments.List(original)
                .Select(a => new CarriedPart(a.Name, a.MimeType, a.Part)));
        }

        if (options.Style == QuoteStyle.None) return draft with { Attachments = attachments };

        return draft with
        {
            Attachments = attachments,
            QuotedHtml = options.PlainText ? string.Empty : QuoteHtml(original),
            QuotedText = QuoteText(original, options.Style, options.Prefix),
            Style = options.Style,
        };
    }

    // ---- Who ------------------------------------------------------------------------------

    private static (IReadOnlyList<string> To, IReadOnlyList<string> Cc) Recipients(
        MimeMessage original, ReplyKind kind, IReadOnlyCollection<string> own)
    {
        if (kind == ReplyKind.Forward) return ([], []);

        bool IsOwn(MailboxAddress m) => own.Contains(m.Address, StringComparer.OrdinalIgnoreCase);

        // Reply-To outranks From, which is what the header is for.
        var author = original.ReplyTo.Mailboxes.Any()
            ? original.ReplyTo.Mailboxes.ToList()
            : original.From.Mailboxes.ToList();

        // Replying to one's own sent message replies to the people it went to, not to oneself.
        if (author.All(IsOwn) && original.To.Mailboxes.Any())
        {
            author = original.To.Mailboxes.ToList();
        }

        var to = author.Select(Format).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (kind == ReplyKind.Reply) return (to, []);

        // Everyone else who was on it, less the reader, less anyone already in To.
        var seen = new HashSet<string>(to.Select(AddressOf), StringComparer.OrdinalIgnoreCase);

        var alsoTo = original.To.Mailboxes.Where(m => !IsOwn(m) && seen.Add(m.Address)).Select(Format).ToList();
        var cc = original.Cc.Mailboxes.Where(m => !IsOwn(m) && seen.Add(m.Address)).Select(Format).ToList();

        return ([.. to, .. alsoTo], cc);
    }

    /// <summary>A name and address as the field shows it, or the address alone.</summary>
    private static string Format(MailboxAddress mailbox)
        => string.IsNullOrWhiteSpace(mailbox.Name)
            ? mailbox.Address
            : $"{mailbox.Name} <{mailbox.Address}>";

    private static string AddressOf(string formatted)
    {
        var open = formatted.LastIndexOf('<');
        return open >= 0 && formatted.EndsWith('>') ? formatted[(open + 1)..^1] : formatted;
    }

    // ---- What it is called -----------------------------------------------------------------

    /// <summary>
    /// The subject with one prefix. <c>RE: RE: RE:</c> is the failure this exists to avoid, and
    /// the other languages' prefixes count as already having one.
    /// </summary>
    internal static string Prefixed(string subject, string prefix)
    {
        var trimmed = subject.Trim();

        foreach (var existing in (string[])["RE:", "FW:", "FWD:", "AW:", "WG:", "SV:", "VS:", "TR:", "RIF:"])
        {
            if (trimmed.StartsWith(existing, StringComparison.OrdinalIgnoreCase))
            {
                // Already a reply or forward. Replying to a forward is still a reply, so the
                // prefix changes to say what this message is; replying to a reply keeps it.
                var rest = trimmed[existing.Length..].TrimStart();
                return string.Equals(existing.TrimEnd(':'), prefix, StringComparison.OrdinalIgnoreCase)
                       || (prefix == "RE" && existing is "AW:" or "SV:" or "VS:" or "RIF:")
                       || (prefix == "FW" && existing is "FWD:" or "WG:" or "TR:")
                    ? trimmed
                    : $"{prefix}: {rest}";
            }
        }

        return $"{prefix}: {trimmed}";
    }

    private static IReadOnlyList<string> References(MimeMessage original)
    {
        var references = new List<string>(original.References);
        if (original.MessageId is { Length: > 0 } id) references.Add(id);
        return references;
    }

    // ---- What it quotes --------------------------------------------------------------------

    /// <summary>
    /// The header block the reference puts above a quoted message, and the message under it.
    /// </summary>
    private static string QuoteHtml(MimeMessage original)
    {
        // A text-only original as paragraphs of its own lines. The renderer's plain-text form
        // relies on the reading pane's stylesheet to keep the line breaks, and a fragment has
        // no stylesheet — so the breaks are made structural here.
        //
        // No blockquote here, whatever the style asks: the editor's parser keeps a blockquote
        // only when its content is inline, so one wrapped around paragraphs loaded as ordinary
        // text at indent zero — which made three of the five styles byte-identical on the
        // wire. The style travels on the draft instead, and the compose window marks the
        // loaded paragraphs themselves.
        var body = original.HtmlBody is { Length: > 0 }
            ? MessageRenderer.Render(original, new RenderOptions { Fragment = true }).Html
            : TextAsHtml(original.TextBody ?? string.Empty);

        var quoted = new StringBuilder();
        quoted.Append("<p>&nbsp;</p>");
        quoted.Append("<hr />");
        quoted.Append(HeaderBlock(original));
        quoted.Append(body);

        return quoted.ToString();
    }

    private static string HeaderBlock(MimeMessage original)
    {
        var block = new StringBuilder("<p>");

        void Line(string label, string value)
        {
            if (value.Length == 0) return;
            block.Append("<b>").Append(label).Append(":</b> ").Append(Escape(value)).Append("<br />");
        }

        Line("From", original.From.ToString());
        Line("Sent", original.Date.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm", CultureInfo.CurrentCulture));
        Line("To", original.To.ToString());
        Line("Cc", original.Cc.ToString());
        Line("Subject", original.Subject ?? string.Empty);

        return block.Append("</p>").ToString();
    }

    private static string QuoteText(MimeMessage original, QuoteStyle style, string prefix)
    {
        var text = original.TextBody ?? StripToText(original.HtmlBody ?? string.Empty);

        var quoted = new StringBuilder();
        quoted.AppendLine();
        quoted.AppendLine();

        if (style == QuoteStyle.Prefix)
        {
            // The plain-text convention: every line of the original marked, and the header
            // lines above it marked too, so a reader's client folds it as a quotation.
            var mark = prefix.Length == 0 ? ">" : prefix;
            quoted.AppendLine($"{mark} From: {original.From}");
            quoted.AppendLine($"{mark} Sent: {original.Date.ToLocalTime():dddd, d MMMM yyyy HH:mm}");
            if (original.To.Count > 0) quoted.AppendLine($"{mark} To: {original.To}");
            if (original.Cc.Count > 0) quoted.AppendLine($"{mark} Cc: {original.Cc}");
            quoted.AppendLine($"{mark} Subject: {original.Subject}");
            quoted.AppendLine(mark);

            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                quoted.AppendLine(line.Length == 0 ? mark : $"{mark} {line}");
            }

            return quoted.ToString();
        }

        // Include-and-indent's plain-text form: every line of the original — headers too, as
        // the reference does it — moved in by one tab. Include leaves the lines where they are.
        var lead = style == QuoteStyle.IncludeIndented ? "\t" : string.Empty;

        quoted.AppendLine($"{lead}-----Original Message-----");
        quoted.AppendLine($"{lead}From: {original.From}");
        quoted.AppendLine($"{lead}Sent: {original.Date.ToLocalTime():dddd, d MMMM yyyy HH:mm}");
        if (original.To.Count > 0) quoted.AppendLine($"{lead}To: {original.To}");
        if (original.Cc.Count > 0) quoted.AppendLine($"{lead}Cc: {original.Cc}");
        quoted.AppendLine($"{lead}Subject: {original.Subject}");
        if (lead.Length == 0)
        {
            quoted.AppendLine();
            quoted.Append(text);
            return quoted.ToString();
        }

        quoted.AppendLine(lead);

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            quoted.AppendLine(line.Length == 0 ? lead : $"{lead}{line}");
        }

        return quoted.ToString();
    }

    /// <summary>Lines of text as paragraphs, so a quoted plain-text message keeps its shape.</summary>
    private static string TextAsHtml(string text)
    {
        var html = new StringBuilder();

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            html.Append("<p>").Append(line.Length == 0 ? "&nbsp;" : Escape(line)).Append("</p>");
        }

        return html.ToString();
    }

    /// <summary>The words of an HTML body, for a plain-text quote of a message that had no text half.</summary>
    private static string StripToText(string html)
    {
        var text = new StringBuilder(html.Length);
        var inTag = false;

        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) text.Append(c);
        }

        return System.Net.WebUtility.HtmlDecode(text.ToString());
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal);
}
