using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailbox.Editor;

/// <summary>
/// The reference's AutoCorrect switches, under the names its dialog gives them.
/// </summary>
/// <remarks>
/// Two tabs' worth: the AutoCorrect tab, which is about words, and AutoFormat As You Type,
/// which is about marks and paragraphs. They are one record because they are one decision to
/// the code that applies them — every one of them fires on the same two events, a character
/// and a word ending — and because a caller that had to assemble two records would sooner or
/// later assemble half of one.
/// <para>
/// The defaults are the reference's own: everything on but the two that would surprise
/// somebody who has never opened this dialog — <see cref="Hyperlinks"/> is the editor's own
/// switch and is decided by the compose surface, and <see cref="MathReplacements"/> is off in
/// the reference too, where it applies inside equations only.
/// </para>
/// </remarks>
public sealed record AutocorrectOptions
{
    /// <summary>The master switch, the reference's "Replace text as you type".</summary>
    public bool ReplaceAsYouType { get; init; } = true;

    /// <summary>"Correct TWo INitial CApitals" — a capital held a moment too long.</summary>
    public bool TwoInitialCapitals { get; init; } = true;

    /// <summary>"Capitalize first letter of sentences".</summary>
    public bool CapitalizeSentences { get; init; } = true;

    /// <summary>"Capitalize first letter of table cells".</summary>
    public bool CapitalizeTableCells { get; init; } = true;

    /// <summary>"Capitalize names of days" — in this machine's language as well as in English.</summary>
    public bool CapitalizeDays { get; init; } = true;

    /// <summary>"Correct accidental usage of cAPS LOCK key".</summary>
    public bool CapsLock { get; init; } = true;

    /// <summary>"Automatically use suggestions from the spelling checker".</summary>
    public bool UseSpellingSuggestions { get; init; } = true;

    /// <summary>Math AutoCorrect's table — <c>\alpha</c>, <c>\times</c> — outside an equation.</summary>
    /// <remarks>Off, as the reference has it. There are no equations here, so this switch is all of it.</remarks>
    public bool MathReplacements { get; init; }

    // ---- AutoFormat As You Type ---------------------------------------------------------

    /// <summary>"Straight quotes" with "smart quotes".</summary>
    public bool SmartQuotes { get; init; } = true;

    /// <summary>Fractions (1/2) with fraction character.</summary>
    public bool Fractions { get; init; } = true;

    /// <summary>Hyphens (--) with dash (—).</summary>
    public bool Dashes { get; init; } = true;

    /// <summary>*Bold* and _italic_ with real formatting.</summary>
    public bool BoldAndItalic { get; init; } = true;

    /// <summary>Internet and network paths with hyperlinks — the editor's own, passed through.</summary>
    public bool Hyperlinks { get; init; } = true;

    /// <summary>Automatic bulleted lists.</summary>
    public bool BulletedLists { get; init; } = true;

    /// <summary>Automatic numbered lists.</summary>
    public bool NumberedLists { get; init; } = true;

    /// <summary>Border lines — a rule typed as three or more hyphens on a line of its own.</summary>
    public bool BorderLines { get; init; } = true;

    public static AutocorrectOptions Default { get; } = new();

    /// <summary>Everything off, for the reader who wants none of it and for tests of one rule.</summary>
    public static AutocorrectOptions Off { get; } = new()
    {
        ReplaceAsYouType = false,
        TwoInitialCapitals = false,
        CapitalizeSentences = false,
        CapitalizeTableCells = false,
        CapitalizeDays = false,
        CapsLock = false,
        UseSpellingSuggestions = false,
        MathReplacements = false,
        SmartQuotes = false,
        Fractions = false,
        Dashes = false,
        BoldAndItalic = false,
        Hyperlinks = false,
        BulletedLists = false,
        NumberedLists = false,
        BorderLines = false,
    };
}

/// <summary>One row of the reference's Replace/With table.</summary>
/// <param name="Replace">What is typed. Matched whole, and without regard to case.</param>
/// <param name="With">What it becomes, in the case the typing asked for.</param>
public sealed record AutocorrectEntry(string Replace, string With);

/// <summary>What the editor should do about what was just typed.</summary>
/// <param name="Remove">How many characters immediately before the caret to take away.</param>
/// <param name="Insert">What to type in their place. Empty for the rules that only format.</param>
public sealed record AutocorrectAction(int Remove, string Insert)
{
    /// <summary>
    /// Whether <see cref="Insert"/> stands in for the character that triggered this, rather
    /// than going in before it.
    /// </summary>
    /// <remarks>
    /// A quotation mark becomes a curly one — the straight one never reaches the document. A
    /// word correction does not work that way: "teh" becomes "the" and the space that ended it
    /// is still typed afterwards.
    /// </remarks>
    public bool ReplacesInput { get; init; }

    /// <summary>The formatting to apply along with the text, if any.</summary>
    public AutocorrectFormat Format { get; init; }
}

/// <summary>Formatting an autocorrection carries, beyond the letters.</summary>
public enum AutocorrectFormat
{
    /// <summary>Text only.</summary>
    None,

    /// <summary>Type the text in bold — <c>*like this*</c>.</summary>
    Bold,

    /// <summary>Type the text in italic — <c>_like this_</c>.</summary>
    Italic,

    /// <summary>Make this paragraph a bulleted list item.</summary>
    Bullet,

    /// <summary>Make this paragraph a numbered list item.</summary>
    Numbering,

    /// <summary>Draw a rule across the page in place of this paragraph.</summary>
    Divider,
}

/// <summary>
/// The reference's Exceptions dialog: the words that stop a rule firing.
/// </summary>
/// <remarks>
/// Both lists are short and both are English. They are not the reference's own lists — those
/// are its data, not ours (§7, rule 4) — but the shape of the problem is the language's rather
/// than anybody's: an abbreviation ends in a full stop without ending a sentence, and a plural
/// of an acronym carries two capitals and a lower-case s on purpose.
/// </remarks>
public sealed class AutocorrectExceptions
{
    /// <summary>Abbreviations after which the next word is not the start of a sentence.</summary>
    public static IReadOnlyList<string> DefaultFirstLetter { get; } =
    [
        "a.m.", "abbr.", "addr.", "adj.", "approx.", "apt.", "ave.", "blvd.", "co.", "corp.",
        "dept.", "dr.", "e.g.", "est.", "etc.", "fig.", "hr.", "i.e.", "inc.", "jr.", "ltd.",
        "min.", "misc.", "mr.", "mrs.", "ms.", "no.", "p.m.", "pp.", "prof.", "rd.", "ref.",
        "rev.", "sec.", "sr.", "st.", "vol.", "vs.",
    ];

    /// <summary>Words that begin with two capitals and mean to.</summary>
    public static IReadOnlyList<string> DefaultInitialCaps { get; } =
    [
        "CDs", "DVDs", "GBs", "IDs", "IPs", "ISBNs", "KBs", "MBs", "MPs", "OSs", "PCs", "PDFs",
        "PhD", "PhDs", "TVs", "URLs", "USBs",
    ];

    private readonly HashSet<string> _firstLetter;
    private readonly HashSet<string> _initialCaps;

    public AutocorrectExceptions(
        IEnumerable<string>? firstLetter = null, IEnumerable<string>? initialCaps = null)
    {
        _firstLetter = new HashSet<string>(
            firstLetter ?? DefaultFirstLetter, StringComparer.OrdinalIgnoreCase);
        _initialCaps = new HashSet<string>(
            initialCaps ?? DefaultInitialCaps, StringComparer.Ordinal);
    }

    /// <summary>The abbreviations, in the order the dialog lists them.</summary>
    public IReadOnlyList<string> FirstLetter => [.. _firstLetter.OrderBy(w => w, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The two-capital words that are left alone.</summary>
    public IReadOnlyList<string> InitialCaps => [.. _initialCaps.OrderBy(w => w, StringComparer.Ordinal)];

    /// <summary>Whether a full stop after this word ends a sentence.</summary>
    public bool EndsSentence(string wordWithStop) => !_firstLetter.Contains(wordWithStop);

    /// <summary>Whether the two capitals this word starts with are meant.</summary>
    public bool KeepsInitialCaps(string word) => _initialCaps.Contains(word);

    /// <summary>
    /// Both lists, in full.
    /// </summary>
    /// <remarks>
    /// In full rather than as the difference from this build's own, which is how the
    /// Replace/With table is stored: that table is two hundred rows nobody reads and a later
    /// build's additions to it are welcome, where these two lists are short and hand-kept, and
    /// merging a later build's abbreviations into a list somebody has pruned would put back
    /// exactly what they took out.
    /// </remarks>
    public string ToJson() => JsonSerializer.Serialize(
        new Lists([.. FirstLetter], [.. InitialCaps]), AutocorrectJson.Options);

    /// <summary>Reads back what <see cref="ToJson"/> wrote, or this build's own lists.</summary>
    public static AutocorrectExceptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutocorrectExceptions();

        try
        {
            var lists = JsonSerializer.Deserialize<Lists>(json, AutocorrectJson.Options);
            return lists is null
                ? new AutocorrectExceptions()
                : new AutocorrectExceptions(lists.FirstLetter, lists.InitialCaps);
        }
        catch (JsonException)
        {
            return new AutocorrectExceptions();
        }
    }

    private sealed record Lists(
        [property: JsonPropertyName("firstLetter")] List<string> FirstLetter,
        [property: JsonPropertyName("initialCaps")] List<string> InitialCaps);

    public bool AddFirstLetter(string word) => _firstLetter.Add(word.Trim());

    public bool RemoveFirstLetter(string word) => _firstLetter.Remove(word.Trim());

    public bool AddInitialCaps(string word) => _initialCaps.Add(word.Trim());

    public bool RemoveInitialCaps(string word) => _initialCaps.Remove(word.Trim());
}

/// <summary>
/// The Replace/With table: what this build ships, and what the reader has done to it.
/// </summary>
/// <remarks>
/// Only the difference is stored. A settings file carrying all two hundred defaults would be
/// a copy of this build frozen at the moment somebody first added an entry — and the entry
/// they added would be lost among rows they never chose. Keeping the delta means a later
/// build's corrections reach a reader who has customised the list, and that deleting a default
/// stays deleted.
/// </remarks>
public sealed class AutocorrectTable
{
    /// <summary>
    /// The corrections this build ships.
    /// </summary>
    /// <remarks>
    /// Authored here rather than lifted: the reference's list is its own data (rule 4), and
    /// every other list of English typos with a licence attached carries one this project
    /// cannot take. What is here is the short tail of slips that are worth correcting without
    /// being asked — transpositions, doubled letters, the missing apostrophes — and the symbols
    /// that everybody types as punctuation and means as a character. The long tail is the
    /// spelling checker's job, which is what "Automatically use suggestions from the spelling
    /// checker" is for: the dictionary on this machine knows far more words than a table can.
    /// </remarks>
    public static IReadOnlyList<AutocorrectEntry> Defaults { get; } =
    [
        // The symbols. Typed as punctuation, meant as a character.
        new("(c)", "©"), new("(r)", "®"), new("(tm)", "™"), new("...", "…"),
        new("-->", "→"), new("<--", "←"), new("==>", "⇒"), new("<==", "⇐"),
        new("<=>", "⇔"), new(":)", "☺"), new(":-)", "☺"), new(":(", "☹"),
        new(":-(", "☹"), new("+-", "±"),

        // "i" on its own is the pronoun, and the only single letter worth correcting.
        new("i", "I"), new("i'm", "I'm"), new("i've", "I've"), new("i'll", "I'll"),
        new("i'd", "I'd"), new("im", "I'm"), new("ive", "I've"),

        // The apostrophes people drop because the key is out of the way.
        new("arent", "aren't"), new("cant", "can't"), new("couldnt", "couldn't"),
        new("didnt", "didn't"), new("doesnt", "doesn't"), new("dont", "don't"),
        new("hadnt", "hadn't"), new("hasnt", "hasn't"), new("havent", "haven't"),
        new("isnt", "isn't"), new("mustnt", "mustn't"), new("shouldnt", "shouldn't"),
        new("thats", "that's"), new("theyre", "they're"), new("theyve", "they've"),
        new("wasnt", "wasn't"), new("werent", "weren't"), new("whats", "what's"),
        new("wont", "won't"), new("wouldnt", "wouldn't"), new("youre", "you're"),
        new("youve", "you've"), new("youll", "you'll"),

        // Transpositions and slips of the hand.
        new("teh", "the"), new("hte", "the"), new("adn", "and"), new("anf", "and"),
        new("taht", "that"), new("tehn", "then"), new("thsi", "this"), new("waht", "what"),
        new("wiht", "with"), new("woudl", "would"), new("coudl", "could"), new("shoudl", "should"),
        new("nad", "and"), new("iam", "I am"),
        new("yuo", "you"), new("yoru", "your"), new("thier", "their"), new("theri", "their"),
        new("recieve", "receive"), new("recieved", "received"), new("recieving", "receiving"),
        new("beleive", "believe"), new("beleived", "believed"), new("acheive", "achieve"),
        new("acheived", "achieved"), new("freind", "friend"), new("wierd", "weird"),
        new("thne", "then"), new("jsut", "just"), new("liek", "like"),
        new("mroe", "more"), new("nwe", "new"), new("owuld", "would"), new("pelase", "please"),
        new("pleaes", "please"), new("tiem", "time"),
        new("tihs", "this"), new("whcih", "which"), new("whihc", "which"), new("wtih", "with"),
        new("yera", "year"), new("yeras", "years"), new("aslo", "also"), new("bcak", "back"),
        new("becuase", "because"), new("becasue", "because"), new("brodcast", "broadcast"),
        new("cahnge", "change"), new("chnage", "change"), new("dpeartment", "department"),
        new("emial", "email"), new("fromt he", "from the"),
        new("hvae", "have"), new("hwo", "how"), new("knwo", "know"), new("konw", "know"),
        new("mesage", "message"), new("mesages", "messages"), new("meetign", "meeting"),
        new("morrning", "morning"), new("mroning", "morning"), new("nto", "not"),
        new("onyl", "only"), new("otehr", "other"), new("perhpas", "perhaps"),
        new("recieveing", "receiving"), new("smoe", "some"), new("soem", "some"),
        new("tahn", "than"), new("tath", "that"), new("thta", "that"), new("tje", "the"),
        new("tkae", "take"), new("todya", "today"), new("tomorow", "tomorrow"),
        new("tommorow", "tomorrow"), new("tommorrow", "tomorrow"), new("veyr", "very"),
        new("wehn", "when"), new("werk", "work"), new("whne", "when"), new("wokr", "work"),
        new("wroking", "working"), new("yesterdya", "yesterday"),

        // The ones people spell wrong rather than mistype.
        new("accomodate", "accommodate"), new("acommodate", "accommodate"),
        new("alot", "a lot"), new("apparant", "apparent"), new("arguement", "argument"),
        new("beggining", "beginning"), new("begining", "beginning"), new("calender", "calendar"),
        new("cemetary", "cemetery"), new("collegue", "colleague"), new("comming", "coming"),
        new("commited", "committed"), new("commitee", "committee"), new("completly", "completely"),
        new("concious", "conscious"), new("definately", "definitely"), new("definatly", "definitely"),
        new("dissapoint", "disappoint"), new("embarass", "embarrass"), new("enviroment", "environment"),
        new("existance", "existence"), new("familar", "familiar"), new("finaly", "finally"),
        new("foriegn", "foreign"), new("goverment", "government"), new("gaurd", "guard"),
        new("greatful", "grateful"), new("happend", "happened"), new("harrass", "harass"),
        new("immediatly", "immediately"), new("independant", "independent"),
        new("intrest", "interest"), new("knowlege", "knowledge"), new("liason", "liaison"),
        new("libary", "library"), new("maintainance", "maintenance"), new("managment", "management"),
        new("millenium", "millennium"), new("mispell", "misspell"), new("neccessary", "necessary"),
        new("necesary", "necessary"), new("noticable", "noticeable"), new("occassion", "occasion"),
        new("occured", "occurred"), new("occurence", "occurrence"), new("paticular", "particular"),
        new("persistant", "persistent"), new("posession", "possession"), new("prefered", "preferred"),
        new("priviledge", "privilege"), new("probaly", "probably"), new("publically", "publicly"),
        new("questionaire", "questionnaire"), new("recomend", "recommend"),
        new("refered", "referred"),
        new("relevent", "relevant"), new("responsable", "responsible"), new("rythm", "rhythm"),
        new("seperate", "separate"), new("seperately", "separately"), new("succesful", "successful"),
        new("sucessful", "successful"), new("supercede", "supersede"), new("suprise", "surprise"),
        new("truely", "truly"), new("unfortunatly", "unfortunately"), new("untill", "until"),
        new("writting", "writing"), new("yeild", "yield"),
    ];

    /// <summary>
    /// Math AutoCorrect's own table.
    /// </summary>
    /// <remarks>
    /// The reference applies these inside an equation, and outside one only when its switch is
    /// on. There are no equations here — the editor has no equation model (§20) — so the switch
    /// is the whole of it, and what it buys is a way to type Greek and the operators in a
    /// message about mathematics without a character map.
    /// </remarks>
    public static IReadOnlyList<AutocorrectEntry> MathDefaults { get; } =
    [
        new("\\alpha", "α"), new("\\beta", "β"), new("\\gamma", "γ"),
        new("\\delta", "δ"), new("\\epsilon", "ε"), new("\\zeta", "ζ"),
        new("\\eta", "η"), new("\\theta", "θ"), new("\\lambda", "λ"),
        new("\\mu", "μ"), new("\\pi", "π"), new("\\rho", "ρ"),
        new("\\sigma", "σ"), new("\\tau", "τ"), new("\\phi", "φ"),
        new("\\chi", "χ"), new("\\psi", "ψ"), new("\\omega", "ω"),
        new("\\Delta", "Δ"), new("\\Gamma", "Γ"), new("\\Lambda", "Λ"),
        new("\\Omega", "Ω"), new("\\Phi", "Φ"), new("\\Pi", "Π"),
        new("\\Sigma", "Σ"), new("\\Theta", "Θ"),
        new("\\times", "×"), new("\\div", "÷"), new("\\pm", "±"),
        new("\\ne", "≠"), new("\\le", "≤"), new("\\ge", "≥"),
        new("\\approx", "≈"), new("\\equiv", "≡"), new("\\propto", "∝"),
        new("\\infty", "∞"), new("\\sqrt", "√"), new("\\sum", "∑"),
        new("\\prod", "∏"), new("\\int", "∫"), new("\\partial", "∂"),
        new("\\nabla", "∇"), new("\\in", "∈"), new("\\notin", "∉"),
        new("\\subset", "⊂"), new("\\cup", "∪"), new("\\cap", "∩"),
        new("\\forall", "∀"), new("\\exists", "∃"), new("\\therefore", "∴"),
        new("\\degree", "°"), new("\\rightarrow", "→"), new("\\leftarrow", "←"),
    ];

    private readonly Dictionary<string, string> _entries;
    private readonly Dictionary<string, string> _added;
    private readonly HashSet<string> _removed;

    /// <summary>The table as this build ships it, with nothing done to it.</summary>
    public AutocorrectTable() : this(null, null)
    {
    }

    private AutocorrectTable(Dictionary<string, string>? added, HashSet<string>? removed)
    {
        _added = added ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _removed = removed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Rebuild();
    }

    private void Rebuild()
    {
        _entries.Clear();
        foreach (var entry in Defaults)
        {
            if (!_removed.Contains(entry.Replace)) _entries[entry.Replace] = entry.With;
        }

        // The reader's own entries last, so replacing a default is a matter of adding it again.
        foreach (var (replace, with) in _added) _entries[replace] = with;
    }

    /// <summary>Every row the dialog shows, in the order it shows them.</summary>
    public IReadOnlyList<AutocorrectEntry> Entries =>
        [.. _entries.Select(e => new AutocorrectEntry(e.Key, e.Value))
                    .OrderBy(e => e.Replace, StringComparer.OrdinalIgnoreCase)];

    /// <summary>What this build ships that the reader has thrown away.</summary>
    public IReadOnlyCollection<string> Removed => _removed;

    /// <summary>What the reader has added or overridden.</summary>
    public IReadOnlyDictionary<string, string> Added => _added;

    /// <summary>The replacement for something typed, or null when the table has nothing to say.</summary>
    public string? Lookup(string typed) =>
        _entries.TryGetValue(typed, out var with) ? with : null;

    /// <summary>Adds a row, or changes the one that is there.</summary>
    public void Add(string replace, string with)
    {
        replace = replace.Trim();
        if (replace.Length == 0 || with.Length == 0) return;

        _added[replace] = with;
        _removed.Remove(replace);
        Rebuild();
    }

    /// <summary>Takes a row away, whether it is the reader's or this build's.</summary>
    public bool Remove(string replace)
    {
        replace = replace.Trim();
        if (!_entries.ContainsKey(replace)) return false;

        _added.Remove(replace);
        if (Defaults.Any(e => string.Equals(e.Replace, replace, StringComparison.OrdinalIgnoreCase)))
        {
            _removed.Add(replace);
        }

        Rebuild();
        return true;
    }

    // ---- Storage -----------------------------------------------------------------------------

    private sealed record Delta(
        [property: JsonPropertyName("added")] Dictionary<string, string> Added,
        [property: JsonPropertyName("removed")] List<string> Removed);

    /// <summary>The difference from this build's own list, as one JSON string for one setting.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new Delta(_added, [.. _removed]), AutocorrectJson.Options);

    /// <summary>Reads back what <see cref="ToJson"/> wrote. Anything unreadable is no change at all.</summary>
    public static AutocorrectTable FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutocorrectTable();

        try
        {
            var delta = JsonSerializer.Deserialize<Delta>(json, AutocorrectJson.Options);
            if (delta is null) return new AutocorrectTable();

            return new AutocorrectTable(
                new Dictionary<string, string>(delta.Added, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(delta.Removed, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            // A table that will not parse costs the reader their additions, not their message.
            return new AutocorrectTable();
        }
    }
}

internal static class AutocorrectJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// Correcting a word as it is finished: the reference's AutoCorrect, as far as an editor that
/// is a library rather than ours can carry it.
/// </summary>
/// <remarks>
/// Everything here is a decision about text — given what is in the paragraph before the caret
/// and the key that has just been pressed, what should be taken away and what typed in its
/// place. Nothing here touches a document, which is what makes it testable: the rules are hard
/// to get right, the editing is not, and mixing them would mean proving the rules through a
/// control that needs a window.
/// <para>
/// <b>What it will not do.</b> Ordinals with a superscript, because the editor has no
/// superscript; named styles from a heading, because it has none. Those say so where the
/// reader can see them, in the dialog, rather than being quietly absent.
/// </para>
/// </remarks>
public sealed class Autocorrect
{
    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    private readonly Func<string, bool>? _isSpelledCorrectly;
    private readonly Func<string, IReadOnlyList<string>>? _suggest;
    private readonly Dictionary<string, string?> _suggestions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dayNames;

    /// <summary>
    /// A corrector over a table, its exceptions, and — when there is one — the spelling checker.
    /// </summary>
    /// <param name="isSpelledCorrectly">
    /// Whether the dictionary knows a word. Null for a machine with no dictionary, where the
    /// suggestion rule simply never fires.
    /// </param>
    /// <param name="suggest">What the dictionary would offer instead, best first.</param>
    public Autocorrect(
        AutocorrectOptions? options = null,
        AutocorrectTable? table = null,
        AutocorrectExceptions? exceptions = null,
        Func<string, bool>? isSpelledCorrectly = null,
        Func<string, IReadOnlyList<string>>? suggest = null,
        CultureInfo? culture = null)
    {
        Options = options ?? AutocorrectOptions.Default;
        Table = table ?? new AutocorrectTable();
        Exceptions = exceptions ?? new AutocorrectExceptions();
        _isSpelledCorrectly = isSpelledCorrectly;
        _suggest = suggest;

        // The days in the language this desktop is in, and in English as well: mail written in
        // English on a French desktop is still mail written in English.
        var names = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.DayNames
            .Concat(CultureInfo.InvariantCulture.DateTimeFormat.DayNames)
            .Where(d => d.Length > 0);

        _dayNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public AutocorrectOptions Options { get; set; }

    public AutocorrectTable Table { get; }

    public AutocorrectExceptions Exceptions { get; }

    /// <summary>Whether finishing a word with this character should run the word rules.</summary>
    /// <remarks>
    /// Space and tab end a word; so does the punctuation that ends a sentence or a clause. A
    /// quotation mark does not — it is handled as itself, and the word inside the quotes is
    /// ended by whatever follows.
    /// </remarks>
    public static bool EndsWord(char ch) =>
        ch is ' ' or '\t' or '\n' or '\r' or '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}';

    /// <summary>
    /// A word has just been finished. What, if anything, should be done about it.
    /// </summary>
    /// <param name="before">Everything in the paragraph before the caret.</param>
    /// <param name="terminator">The character that ended it, which is not in <paramref name="before"/> yet.</param>
    /// <param name="startsCell">Whether the caret is in a table cell that has nothing else in it.</param>
    public AutocorrectAction? AtWordBoundary(string before, char terminator, bool startsCell = false)
    {
        if (before.Length == 0) return null;

        // A list marker is the whole of what has been typed, so it is answered before anything
        // reads it as a word: "1." is not a sentence that wants a capital.
        if (terminator == ' ' && ListMarker(before) is { } marker) return marker;

        // A word wrapped in stars or underscores, closed just before this: the marks come away
        // and the word inside them is typed again with the emphasis they asked for.
        if (Emphasis(before) is { } emphasis) return emphasis;

        var start = TokenStart(before);
        var token = before[start..];
        if (token.Length == 0) return null;

        // The marks first: a symbol, a fraction, a dash or a name out of the maths table is
        // matched whole, and none of them is a word the capital rules have anything to say
        // about. Whatever comes back is measured against the token, so the caller removes
        // exactly what was typed and nothing that was already there.
        if (!IsWord(token)
            && Replacement(token) is { } replaced
            && !string.Equals(replaced, token, StringComparison.Ordinal))
        {
            return new AutocorrectAction(token.Length, replaced);
        }

        // An address, a path or a hostname is not prose and is never corrected: the dot in the
        // middle of it is not a sentence ending either.
        if (LooksLikeAddress(token)) return null;

        // The word inside the token: "(hello" is a word in a bracket, and the bracket stays.
        // What follows it stays too — a closing quotation mark is between the word and the
        // caret, so it is taken away with the word and typed again after the correction.
        var wordStart = start;
        while (wordStart < before.Length && !IsWordCharacter(before[wordStart])) wordStart++;

        var wordEnd = before.Length;
        while (wordEnd > wordStart && !IsWordCharacter(before[wordEnd - 1])) wordEnd--;

        var word = before[wordStart..wordEnd];
        var trailing = before[wordEnd..];
        if (word.Length == 0 || !word.Any(char.IsLetter)) return null;

        var corrected = CorrectWord(word, before[..wordStart], terminator, startsCell);

        return corrected is null || string.Equals(corrected, word, StringComparison.Ordinal)
            ? null
            : new AutocorrectAction(word.Length + trailing.Length, corrected + trailing);
    }

    /// <summary>
    /// A character has been typed that stands for something else — a quotation mark, or the
    /// second star of a pair.
    /// </summary>
    /// <returns>
    /// An action whose <see cref="AutocorrectAction.ReplacesInput"/> is set: the character
    /// itself never reaches the document.
    /// </returns>
    public AutocorrectAction? AtCharacter(string before, char typed)
    {
        if (Options.SmartQuotes && typed is '"' or '\'')
        {
            var opening = OpensQuote(before);
            var mark = typed == '"'
                ? (opening ? '“' : '”')
                : (opening ? '‘' : '’');

            return new AutocorrectAction(0, mark.ToString()) { ReplacesInput = true };
        }

        return null;
    }

    /// <summary>
    /// Return has been pressed. Whether this paragraph was a rule drawn with hyphens.
    /// </summary>
    /// <remarks>
    /// The reference draws a paragraph border; the editor has a divider block and no paragraph
    /// borders, so this is that — the same mark, made of what is here (§the rule, 2).
    /// </remarks>
    public AutocorrectAction? AtParagraphBreak(string before)
    {
        if (!Options.BorderLines) return null;

        var line = before.TrimEnd();
        if (line.Length < 3) return null;

        var mark = line[0];
        if (mark is not ('-' or '_' or '=' or '*' or '~')) return null;
        if (!line.All(c => c == mark)) return null;

        // The Return that asked for the rule is not pressed as well: the divider carries the
        // caret to the line after it, and pressing Return too would leave a blank line between.
        return new AutocorrectAction(before.Length, string.Empty)
        {
            ReplacesInput = true,
            Format = AutocorrectFormat.Divider,
        };
    }

    // ---- The word rules ----------------------------------------------------------------------

    /// <summary>The table, then the maths table, in the case the typing asked for.</summary>
    private string? Replacement(string token)
    {
        if (Options.ReplaceAsYouType && Table.Lookup(token) is { } with) return Curl(MatchCase(token, with));

        if (Options.MathReplacements)
        {
            // Case is the author's here: \Delta and \delta are two different characters.
            var math = AutocorrectTable.MathDefaults
                .FirstOrDefault(e => string.Equals(e.Replace, token, StringComparison.Ordinal));

            if (math is not null) return math.With;
        }

        if (Options.Fractions && Fraction(token) is { } fraction) return fraction;

        // "word--word" is an em dash between them, which is what the pair was standing in for.
        // A row of hyphens on its own is not: that is a rule drawn across the page, and Return
        // is what asks for it.
        if (Options.Dashes && Dash(token) is { } dashed) return dashed;

        return null;
    }

    private string? CorrectWord(string word, string before, char terminator, bool startsCell)
    {
        var corrected = word;

        // The table is the first of the word rules rather than a rule apart, so that what it
        // answers is then capitalised if it begins a sentence: "teh" at the top of a paragraph
        // becomes "The", which is both rules in the order the reference applies them.
        //
        // A single letter ended by a full stop is an initial, or the "i" of "i.e." — neither is
        // the pronoun. "i" becomes "I" when a space ends it, not a stop.
        if (!(word.Length == 1 && terminator == '.') && Replacement(word) is { } replaced)
        {
            corrected = replaced;
        }

        // cAPS LOCK first: everything after it reads the word's shape, and this is the rule that
        // says the shape is an accident.
        if (Options.CapsLock && LooksLikeCapsLock(corrected))
        {
            corrected = char.ToUpper(corrected[0], CultureInfo.CurrentCulture)
                + corrected[1..].ToLower(CultureInfo.CurrentCulture);
        }

        if (Options.TwoInitialCapitals && HasTwoInitialCapitals(corrected)
            && !Exceptions.KeepsInitialCaps(corrected))
        {
            corrected = string.Concat(
                corrected[0], char.ToLower(corrected[1], CultureInfo.CurrentCulture), corrected[2..]);
        }

        if (Options.CapitalizeDays && _dayNames.Contains(corrected) && char.IsLower(corrected[0]))
        {
            corrected = char.ToUpper(corrected[0], CultureInfo.CurrentCulture) + corrected[1..];
        }

        // The spelling checker's own suggestion, for everything a table cannot hold. Only when
        // it is one keystroke away from what was typed: a checker asked about a word it has
        // never seen will always offer something, and something is not the same as a correction.
        if (Options.UseSpellingSuggestions && corrected == word && Suggestion(word) is { } suggested)
        {
            corrected = suggested;
        }

        var capitalize = (Options.CapitalizeSentences && StartsSentence(before))
            || (Options.CapitalizeTableCells && startsCell && before.Trim().Length == 0);

        if (capitalize && char.IsLower(corrected[0]))
        {
            corrected = char.ToUpper(corrected[0], CultureInfo.CurrentCulture) + corrected[1..];
        }

        return corrected;
    }

    /// <summary>Whether what precedes this word is the end of a sentence, or nothing at all.</summary>
    private bool StartsSentence(string before)
    {
        var text = before.TrimEnd();
        if (text.Length == 0) return true;

        // A closing bracket or quotation mark can stand between the full stop and the space.
        while (text.Length > 0 && text[^1] is '"' or '\'' or '”' or '’' or ')' or ']')
        {
            text = text[..^1];
        }

        if (text.Length == 0 || !SentenceEnders.Contains(text[^1])) return false;

        // "e.g." ends in a full stop and does not end a sentence; neither does an initial, and
        // neither does the "1." of a list somebody typed by hand.
        var start = TokenStart(text);
        var last = text[start..];

        if (last.Length > 1 && last.All(c => char.IsDigit(c) || c == '.')) return false;
        if (last.Length == 2 && char.IsLetter(last[0]) && last[1] == '.') return false;

        return Exceptions.EndsSentence(last);
    }

    /// <summary>The checker's first suggestion, when it is close enough to be a correction.</summary>
    private string? Suggestion(string word)
    {
        if (_isSpelledCorrectly is null || _suggest is null) return null;

        // A word of three letters or fewer has too many neighbours to guess between, and a word
        // carrying a digit or a capital in the middle is a code rather than prose.
        if (word.Length < 4 || word.Any(char.IsDigit)) return null;
        if (word.Skip(1).Any(char.IsUpper)) return null;

        if (_suggestions.TryGetValue(word, out var cached)) return cached;

        string? answer = null;

        if (!_isSpelledCorrectly(word))
        {
            foreach (var candidate in _suggest(word).Take(3))
            {
                // One keystroke away, and the same word rather than a different one: a
                // transposition, a doubled letter, a missing letter. Two edits is where a
                // checker starts guessing, and a guess typed into somebody's mail is worse
                // than a red squiggle.
                if (candidate.Contains(' ') || Distance(word, candidate) > 1) continue;

                answer = Curl(MatchCase(word, candidate));
                break;
            }
        }

        // Remembered either way: the answer for a word that is spelled correctly is "nothing",
        // and it costs a dictionary lookup to find out the first time.
        _suggestions[word] = answer;
        return answer;
    }

    // ---- The marks ---------------------------------------------------------------------------

    /// <summary>
    /// A pair of hyphens standing between two halves of a word, replaced by the dash it was
    /// standing in for. Nothing happens to a row of them, which is a rule rather than a dash.
    /// </summary>
    private static string? Dash(string token)
    {
        var at = token.IndexOf("--", StringComparison.Ordinal);
        if (at <= 0 || at + 2 >= token.Length) return null;
        if (token[at + 2] == '-') return null;
        if (!char.IsLetterOrDigit(token[at - 1]) || !char.IsLetterOrDigit(token[at + 2])) return null;

        return token[..at] + "—" + token[(at + 2)..];
    }

    /// <summary>The apostrophe a replacement carries follows the smart-quotes switch.</summary>
    private string Curl(string text) =>
        Options.SmartQuotes ? text.Replace('\'', '\u2019') : text;

    /// <summary>"1/2" and its two neighbours, which are the fractions a font can be relied on for.</summary>
    private static string? Fraction(string token) => token switch
    {
        "1/2" => "½",
        "1/4" => "¼",
        "3/4" => "¾",
        _ => null,
    };

    /// <summary>Whether a quotation mark here opens rather than closes.</summary>
    private static bool OpensQuote(string before)
    {
        if (before.Length == 0) return true;

        var previous = before[^1];
        return char.IsWhiteSpace(previous) || previous is '(' or '[' or '{' or '—' or '–' or '-' or '“' or '‘';
    }

    /// <summary>
    /// A pair of stars or underscores closed at the caret, and the words they were wrapped
    /// around.
    /// </summary>
    /// <remarks>
    /// Read when the word is finished rather than as the closing mark is typed, because the
    /// character that finishes it is also what carries the caret back out of the emphasis: an
    /// editor whose caret is at the end of a bold run types in bold, and this one offers no way
    /// to say otherwise except from outside a word.
    /// </remarks>
    private AutocorrectAction? Emphasis(string before)
    {
        if (!Options.BoldAndItalic || before.Length < 3) return null;

        var mark = before[^1];
        if (mark is not ('*' or '_')) return null;

        // Nothing to close if what precedes the mark is a space: "*word *" is a star.
        var body = before[..^1];
        if (body.Length == 0 || char.IsWhiteSpace(body[^1])) return null;

        var open = body.LastIndexOf(mark);
        if (open < 0) return null;

        // The opening mark has to start a word, or "a_b_c" would turn into a formatted mess.
        if (open > 0 && !char.IsWhiteSpace(body[open - 1]) && body[open - 1] is not ('(' or '[')) return null;

        var inner = body[(open + 1)..];
        if (inner.Length == 0 || inner.Contains(mark) || inner.All(char.IsWhiteSpace)) return null;

        return new AutocorrectAction(inner.Length + 2, inner)
        {
            Format = mark == '*' ? AutocorrectFormat.Bold : AutocorrectFormat.Italic,
        };
    }

    /// <summary>A paragraph that so far is nothing but a list marker.</summary>
    private AutocorrectAction? ListMarker(string before)
    {
        if (before.Length == 0 || char.IsWhiteSpace(before[^1])) return null;

        var marker = before.TrimStart();
        if (marker.Length == 0) return null;

        // The space that asked for the list is not typed into it: the reference turns "* " into
        // a bullet and leaves the item empty, not indented by one space.
        if (Options.BulletedLists && marker is "*" or "-" or "•" or "+")
        {
            return new AutocorrectAction(before.Length, string.Empty)
            {
                ReplacesInput = true,
                Format = AutocorrectFormat.Bullet,
            };
        }

        if (Options.NumberedLists && IsNumberMarker(marker))
        {
            return new AutocorrectAction(before.Length, string.Empty)
            {
                ReplacesInput = true,
                Format = AutocorrectFormat.Numbering,
            };
        }

        return null;
    }

    /// <summary>"1." and "1)" and "a." — a number or a letter, then a stop.</summary>
    private static bool IsNumberMarker(string marker)
    {
        if (marker.Length is < 2 or > 4) return false;
        if (marker[^1] is not ('.' or ')')) return false;

        var body = marker[..^1];
        return body.All(char.IsDigit) || (body.Length == 1 && char.IsLetter(body[0]));
    }

    // ---- Reading the text --------------------------------------------------------------------

    /// <summary>Where the run of non-space characters before the caret begins.</summary>
    private static int TokenStart(string before)
    {
        var start = before.Length;
        while (start > 0 && !char.IsWhiteSpace(before[start - 1])) start--;
        return start;
    }

    private static bool IsWordCharacter(char ch) => char.IsLetterOrDigit(ch) || ch is '\'' or '’';

    /// <summary>Whether a token is a word rather than a mark: "don't" is, "(c)" and "1/2" are not.</summary>
    private static bool IsWord(string token) => token.All(IsWordCharacter);

    /// <summary>Two capitals then a small letter: "THis", and not "ID" or "A".</summary>
    private static bool HasTwoInitialCapitals(string word) =>
        word.Length >= 3
        && char.IsUpper(word[0]) && char.IsUpper(word[1]) && char.IsLower(word[2])
        && word.Skip(2).All(c => !char.IsUpper(c));

    /// <summary>One small letter then capitals: the shape a caps lock left on makes.</summary>
    private static bool LooksLikeCapsLock(string word) =>
        word.Length >= 2
        && char.IsLower(word[0])
        && word.Skip(1).Any(char.IsUpper)
        && word.Skip(1).Where(char.IsLetter).All(char.IsUpper);

    /// <summary>
    /// An address, a URL or a path: punctuation doing work in the middle of a word. The dot in
    /// "example.com" is not the end of a sentence and the word before it is not a word.
    /// </summary>
    private static bool LooksLikeAddress(string token)
    {
        if (token.Contains('@') || token.Contains('/') || token.Contains('\\')) return true;

        for (var i = 1; i < token.Length - 1; i++)
        {
            if (token[i] == '.' && char.IsLetterOrDigit(token[i - 1]) && char.IsLetterOrDigit(token[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The replacement in the case the typing asked for: "Teh" becomes "The", "TEH" "THE".</summary>
    private static string MatchCase(string typed, string with)
    {
        if (typed.Length == 0 || with.Length == 0) return with;

        // All capitals, and more than one letter, so "I" does not make "THE" of every "i".
        if (typed.Where(char.IsLetter).Count() > 1 && typed.Where(char.IsLetter).All(char.IsUpper))
        {
            return with.ToUpper(CultureInfo.CurrentCulture);
        }

        if (char.IsUpper(typed[0]) && char.IsLower(with[0]))
        {
            return char.ToUpper(with[0], CultureInfo.CurrentCulture) + with[1..];
        }

        return with;
    }

    /// <summary>
    /// Edit distance, counting a transposition as one — which is what a typed word usually is
    /// away from the word that was meant.
    /// </summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        var older = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1
                    && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    current[j] = Math.Min(current[j], older[j - 2] + 1);
                }
            }

            (older, previous, current) = (previous, current, older);
        }

        return previous[b.Length];
    }

    /// <summary>What the reader would see this correction described as, for the log and the tests.</summary>
    public static string Describe(AutocorrectAction action) =>
        new StringBuilder()
            .Append(action.Format == AutocorrectFormat.None ? "replace" : action.Format.ToString().ToLowerInvariant())
            .Append(' ').Append(action.Remove).Append(" with \"").Append(action.Insert).Append('"')
            .ToString();
}
