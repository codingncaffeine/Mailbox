using System.Text;
using WeCantSpell.Hunspell;

namespace Mailbox.Editor;

/// <summary>
/// The Proofing page's switches, as the reference names them. All on by default, which is what
/// the checker did before it could be told otherwise.
/// </summary>
/// <param name="IgnoreUppercase">A run of capitals is an acronym or a product name, and no dictionary has them all.</param>
/// <param name="IgnoreWithNumbers">Anything carrying a digit is a code, a version or a measurement.</param>
/// <param name="IgnoreAddresses">A word touching an @, a slash, a colon is part of an address, a URL or a path.</param>
/// <param name="FlagRepeatedWords">"the the" is reported, as a word said twice.</param>
public sealed record SpellCheckOptions(
    bool IgnoreUppercase = true,
    bool IgnoreWithNumbers = true,
    bool IgnoreAddresses = true,
    bool FlagRepeatedWords = true)
{
    public static SpellCheckOptions Default { get; } = new();
}

/// <summary>One word the dictionary does not know, and where it is — or a word said twice, when <see cref="IsRepeated"/>.</summary>
/// <param name="Word">The word as written.</param>
/// <param name="Offset">Where it starts in the text that was checked.</param>
public sealed record Misspelling(string Word, int Offset, bool IsRepeated = false);

/// <summary>
/// Spelling, against the dictionaries this machine already has.
/// </summary>
/// <remarks>
/// Hunspell because it is what every other Linux application uses, which means the user's own
/// dictionaries — the ones their distribution installed and the words they have already taught
/// LibreOffice — are the ones this reads. A checker with a word list of its own would disagree
/// with the rest of the desktop about the same document.
/// <para>
/// The managed implementation, so nothing here has a native part (§16). It is MPL 1.1 / GPL 2.0
/// / LGPL 2.1 tri-licensed, which reaches GPL-3 through the LGPL arm, and on .NET 10 it has no
/// dependencies at all.
/// </para>
/// <para>
/// <b>No dictionary is not an error.</b> A great many desktops have none installed — this one
/// did while the code was written — and a mail client that refuses to run, or nags, over a
/// missing word list would be worse than one that quietly cannot check spelling. Everything
/// here reports that state rather than throwing, and the caller says so once.
/// </para>
/// </remarks>
public sealed class SpellCheck
{
    /// <summary>
    /// Where distributions put Hunspell dictionaries. Mutable for the tests that answer for a
    /// machine with none — the real directories are searched on every real machine, so a test
    /// asserting "no dictionary anywhere" is otherwise at the mercy of what the host installed.
    /// </summary>
    internal static string[] SystemDirectories =
    [
        "/usr/share/hunspell",
        "/usr/share/myspell",
        "/usr/share/myspell/dicts",
        "/usr/local/share/hunspell",
    ];

    private readonly WordList? _words;
    private readonly HashSet<string> _personal;
    private readonly string? _personalPath;

    private SpellCheck(WordList? words, HashSet<string> personal, string? personalPath)
    {
        _words = words;
        _personal = personal;
        _personalPath = personalPath;
    }

    /// <summary>Whether there is a dictionary to check against at all.</summary>
    public bool IsAvailable => _words is not null;

    /// <summary>The dictionary in use, for the log and for saying why nothing is checked.</summary>
    public string? Language { get; private init; }

    /// <summary>
    /// Loads the best dictionary for a language, or one that checks nothing.
    /// </summary>
    /// <param name="language">
    /// A locale like <c>en_GB</c>. Null reads the environment, which is where the user already
    /// said what language they work in.
    /// </param>
    public static async Task<SpellCheck> LoadAsync(
        string? language = null, string? personalPath = null, CancellationToken cancellation = default)
    {
        var wanted = Normalize(language ?? FromEnvironment());
        var personal = LoadPersonal(personalPath);

        if (Find(wanted) is not { } found)
        {
            return new SpellCheck(null, personal, personalPath);
        }

        try
        {
            // Off the calling thread: a dictionary is a few megabytes of word list and parsing
            // one takes long enough to be felt if it happens while somebody is typing.
            var words = await Task.Run(
                () => WordList.CreateFromFiles(found.Dictionary, found.Affix), cancellation);

            // An empty word list knows nothing, and a checker that knows nothing calls every
            // word in the message wrong — worse than not checking, and it reads as the
            // application being broken rather than the file.
            //
            // Only the empty case is caught. A file that is not a dictionary at all does not
            // throw either; it parses to a handful of nonsense roots, and telling that from a
            // small but real dictionary would need a threshold with no principled value. The
            // realistic failure — a .dic with no .aff beside it — is refused before this.
            if (words.IsEmpty) return new SpellCheck(null, personal, personalPath);

            return new SpellCheck(words, personal, personalPath) { Language = found.Language };
        }
        catch (Exception)
        {
            // A dictionary that will not parse is the same to the caller as no dictionary.
            return new SpellCheck(null, personal, personalPath);
        }
    }

    /// <summary>The Proofing switches this checker applies. Set by the host from its settings.</summary>
    public SpellCheckOptions Options { get; set; } = SpellCheckOptions.Default;

    /// <summary>The words the reader has taught it, for Custom Dictionaries to list.</summary>
    public IReadOnlyCollection<string> PersonalWords => _personal.OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Whether a word is spelled correctly, or is not a word worth checking.</summary>
    public bool IsCorrect(string word)
    {
        if (_words is null) return true;
        if (!WorthChecking(word, Options)) return true;

        return _personal.Contains(word) || _words.Check(word);
    }

    /// <summary>What the dictionary offers instead, best first.</summary>
    public IReadOnlyList<string> Suggest(string word)
        => _words is null ? [] : [.. _words.Suggest(word).Take(8)];

    /// <summary>
    /// Every word in a passage the dictionary does not know, in the order they appear.
    /// </summary>
    public IReadOnlyList<Misspelling> Check(string text)
    {
        if (_words is null || string.IsNullOrEmpty(text)) return [];

        var found = new List<Misspelling>();
        var offset = 0;
        string? previous = null;
        var previousEnd = -1;

        while (offset < text.Length)
        {
            // Step over anything that cannot start a word.
            while (offset < text.Length && !char.IsLetter(text[offset])) offset++;
            if (offset >= text.Length) break;

            var start = offset;

            // An apostrophe inside a word is part of it — "don't" is one word, and splitting it
            // reports "t" as a misspelling of nothing. So is a digit: "R2D2" and "3rd" are one
            // word each, for the numbers switch to decide about, not two letters and a number.
            while (offset < text.Length
                   && (char.IsLetterOrDigit(text[offset])
                       || ((text[offset] is '\'' or '’') && offset + 1 < text.Length
                           && char.IsLetter(text[offset + 1]))))
            {
                offset++;
            }

            var word = text[start..offset];

            // A word touching a break-free run of punctuation is probably an address or a URL:
            // look at what precedes and follows before deciding it is prose.
            var partOfSomethingElse = Options.IgnoreAddresses && IsPartOfSomethingElse(text, start, offset);
            if (!partOfSomethingElse && !IsCorrect(word))
            {
                found.Add(new Misspelling(word, start));
            }

            // The same word twice in a row, with nothing but space between: "the the". Only
            // real words — a repeated acronym or number is a list, not a slip.
            if (Options.FlagRepeatedWords
                && previous is not null
                && string.Equals(previous, word, StringComparison.OrdinalIgnoreCase)
                && text[previousEnd..start].All(char.IsWhiteSpace)
                && WorthChecking(word, SpellCheckOptions.Default)
                && !partOfSomethingElse)
            {
                found.Add(new Misspelling(word, start, IsRepeated: true));
            }

            previous = word;
            previousEnd = offset;
        }

        return found;
    }

    /// <summary>Forgets a word the reader taught it, and rewrites the personal list without it.</summary>
    public bool Remove(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || !_personal.Remove(word.Trim())) return false;
        if (_personalPath is null) return true;

        try
        {
            File.WriteAllLines(_personalPath, _personal.OrderBy(w => w, StringComparer.Ordinal), Encoding.UTF8);
        }
        catch (Exception)
        {
            // Forgotten for this session even if the list cannot be rewritten.
        }

        return true;
    }

    /// <summary>
    /// Teaches the dictionary a word, for good.
    /// </summary>
    /// <remarks>
    /// Written beside the mail store rather than into the system dictionary, which is not ours
    /// to edit and which a package update would overwrite.
    /// </remarks>
    public void Add(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        if (!_personal.Add(word.Trim())) return;
        if (_personalPath is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_personalPath)!);
            File.AppendAllText(_personalPath, word.Trim() + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Kept for this session even if it cannot be written. Losing the word at shutdown
            // is better than refusing to accept it now.
        }
    }

    /// <summary>Every dictionary this machine has, for a settings page to offer.</summary>
    public static IReadOnlyList<string> Available()
    {
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directories())
        {
            try
            {
                foreach (var dictionary in Directory.EnumerateFiles(directory, "*.dic"))
                {
                    var name = Path.GetFileNameWithoutExtension(dictionary);
                    if (File.Exists(Path.ChangeExtension(dictionary, ".aff"))) languages.Add(name);
                }
            }
            catch (Exception)
            {
                // A directory that is not there, or not readable. Neither is worth reporting.
            }
        }

        return [.. languages];
    }

    // ---- Finding one -------------------------------------------------------------------------

    private sealed record Found(string Language, string Dictionary, string Affix);

    /// <summary>
    /// The best dictionary for a locale: the exact one, then any variant of the language, then
    /// English, then whatever there is.
    /// </summary>
    /// <remarks>
    /// Falling back rather than failing, because <c>en_GB</c> against a machine carrying only
    /// <c>en_US</c> should check spelling and disagree about a few words, not check nothing.
    /// </remarks>
    private static Found? Find(string wanted)
    {
        var all = new List<Found>();

        foreach (var directory in Directories())
        {
            try
            {
                foreach (var dictionary in Directory.EnumerateFiles(directory, "*.dic"))
                {
                    var affix = Path.ChangeExtension(dictionary, ".aff");
                    if (!File.Exists(affix)) continue;

                    all.Add(new Found(
                        Path.GetFileNameWithoutExtension(dictionary), dictionary, affix));
                }
            }
            catch (Exception)
            {
                // As above.
            }
        }

        if (all.Count == 0) return null;

        var language = wanted.Split('_')[0];

        return all.FirstOrDefault(d => string.Equals(d.Language, wanted, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => d.Language.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => string.Equals(d.Language, language, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => d.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
               ?? all[0];
    }

    private static IEnumerable<string> Directories()
    {
        // DICPATH first: it is how a user says where their own dictionaries are, and every other
        // Hunspell application honours it.
        if (Environment.GetEnvironmentVariable("DICPATH") is { Length: > 0 } dicPath)
        {
            foreach (var part in dicPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part;
            }
        }

        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        yield return Path.Combine(data, "hunspell");

        foreach (var directory in SystemDirectories) yield return directory;
    }

    private static string FromEnvironment()
    {
        foreach (var name in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                && !value.StartsWith('C') && !value.StartsWith("POSIX", StringComparison.Ordinal))
            {
                return value;
            }
        }

        return "en_US";
    }

    /// <summary>A locale as a dictionary is named: <c>en_GB</c>, not <c>en_GB.UTF-8</c>.</summary>
    private static string Normalize(string locale)
    {
        var cut = locale.Split('.', '@')[0].Trim();
        return cut.Replace('-', '_');
    }

    private static HashSet<string> LoadPersonal(string? path)
    {
        var personal = new HashSet<string>(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return personal;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var word = line.Trim();
                if (word.Length > 0) personal.Add(word);
            }
        }
        catch (Exception)
        {
            // An unreadable personal dictionary means no personal words, not no spell check.
        }

        return personal;
    }

    // ---- What is worth checking ----------------------------------------------------------------

    /// <summary>
    /// Whether a token is prose rather than something that merely looks like a word.
    /// </summary>
    private static bool WorthChecking(string word, SpellCheckOptions options)
    {
        if (word.Length < 2) return false;

        // A run of capitals is an acronym or a product name, and no dictionary has them all.
        if (options.IgnoreUppercase && word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return false;

        // Anything carrying a digit is a code, a version or a measurement.
        return !options.IgnoreWithNumbers || !word.Any(char.IsDigit);
    }

    /// <summary>
    /// Whether the word at this position belongs to an address, a URL or a path.
    /// </summary>
    /// <remarks>
    /// Decided by what touches it rather than by matching a URL, because the point is only to
    /// stop underlining it. A word with an <c>@</c>, a <c>:</c> or a <c>/</c> immediately either
    /// side of it is part of something that is not prose.
    /// </remarks>
    private static bool IsPartOfSomethingElse(string text, int start, int end)
    {
        if (start > 0 && text[start - 1] is '@' or '/' or '\\' or '.' or ':' or '-') return true;

        return end < text.Length && text[end] is '@' or '/' or '\\' or ':';
    }
}
