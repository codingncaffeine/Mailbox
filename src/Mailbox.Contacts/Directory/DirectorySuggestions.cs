using Mailbox.Core.Compose;

namespace Mailbox.Contacts.Directory;

/// <summary>
/// The directories' half of the Auto-Complete List, fetched without making anybody wait.
/// </summary>
/// <remarks>
/// Everything else the completion list offers is already on this machine and answers in
/// microseconds. A directory is on a network and answers in tens or hundreds of milliseconds —
/// or, when it has stopped answering, in whatever its timeout is. Asking it on the keystroke
/// would put that between a key going down and the character appearing.
/// <para>
/// So it is asked <em>beside</em> the typing rather than in it. A prefix that has been asked
/// before is answered from here at once; one that has not returns nothing, starts a search, and
/// calls back when the answer lands so the list that is already open can take it. A reader
/// typing a name sees the local suggestions immediately and the directory's a moment later,
/// which is the behaviour every mail client with a directory has.
/// </para>
/// <para>
/// Three characters before anything is asked, because a directory search for "a" matches most
/// of a company and the answer is thrown away by the next keystroke anyway. And one search per
/// prefix in flight at a time: a fast typist would otherwise open a connection per character.
/// </para>
/// </remarks>
public sealed class DirectorySuggestions
{
    /// <summary>Below this many characters, a directory is not asked at all.</summary>
    public const int Shortest = 3;

    /// <summary>
    /// How many prefixes are remembered. Small: one message being addressed touches a handful,
    /// and a stale answer is worse than a second round trip.
    /// </summary>
    private const int Remembered = 32;

    private readonly Func<string, Task<DirectoryResult>> _search;
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<RecipientSuggestion>> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="search">Asks the directories.</param>
    /// <param name="post">Runs the arrival callback where the interface can be touched.</param>
    public DirectorySuggestions(Func<string, Task<DirectoryResult>> search, Action<Action> post)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>The last refusal a search came back with, for the log and the harness.</summary>
    public string LastRefusal { get; private set; } = string.Empty;

    /// <summary>
    /// What the directories offer for what has been typed — now, without waiting.
    /// </summary>
    /// <param name="typed">What is on the line.</param>
    /// <param name="arrived">
    /// Called once, later, when an answer this call did not have has landed. Nothing is called
    /// when the answer was already here.
    /// </param>
    public IReadOnlyList<RecipientSuggestion> Offer(string? typed, Action arrived)
    {
        ArgumentNullException.ThrowIfNull(arrived);

        var prefix = (typed ?? string.Empty).Trim();
        if (prefix.Length < Shortest) return [];

        lock (_gate)
        {
            if (_answers.TryGetValue(prefix, out var held)) return held;
            if (!_inFlight.Add(prefix)) return [];
        }

        _ = Task.Run(async () =>
        {
            IReadOnlyList<RecipientSuggestion> offered;
            try
            {
                var found = await _search(prefix);
                LastRefusal = found.Refusal;
                offered = Suggestions(found);
            }
            catch (Exception ex)
            {
                // Nothing here may throw into the typing. A directory that failed in a way the
                // search did not expect offers nothing for this prefix and is asked again for
                // the next one.
                LastRefusal = ex.Message;
                offered = [];
            }

            lock (_gate)
            {
                _inFlight.Remove(prefix);

                // Bounded, and the oldest goes: a dictionary that only grows is a leak in a
                // window somebody keeps open all day.
                if (_answers.Count >= Remembered) _answers.Remove(_answers.Keys.First());
                _answers[prefix] = offered;
            }

            if (offered.Count > 0) _post(arrived);
        });

        return [];
    }

    /// <summary>Forgets what was found, for a directory whose settings have changed.</summary>
    public void Forget()
    {
        lock (_gate) _answers.Clear();
    }

    /// <summary>
    /// One entry per address, as the address book's own suggestions are.
    /// </summary>
    /// <remarks>
    /// Labelled by where they came from rather than "Contact": somebody about to send to an
    /// address they have never written to should be able to see that the application got it from
    /// the company directory rather than from their own book.
    /// </remarks>
    private static IReadOnlyList<RecipientSuggestion> Suggestions(DirectoryResult found)
    {
        var offered = new List<RecipientSuggestion>();

        foreach (var person in found.People)
        {
            var name = person.Named();
            foreach (var email in person.Emails)
            {
                if (email.Address is not { Length: > 0 } address) continue;

                offered.Add(new RecipientSuggestion(
                    address,
                    name,
                    address,
                    Insert: name.Length > 0 ? $"{name} <{address}>" : address,
                    Detail: "Directory",
                    CanForget: false));
            }
        }

        return offered;
    }
}
