using Mailbox.Junk;

namespace Mailbox.Store;

/// <summary>
/// The junk classifier's corpus, backed by one account's store.
/// </summary>
/// <remarks>
/// The adapter between <see cref="Mailbox.Junk"/>'s pure <see cref="IJunkCorpus"/> and the
/// <c>junk_tokens</c> / <c>junk_corpus</c> tables. It holds no state of its own — every read and
/// write goes to the store — so two of them over one account agree, and the corpus is exactly
/// what is on disk. One corpus per account file, because that is how the mail is filed.
/// </remarks>
public sealed class JunkCorpus(MailRepository repository) : IJunkCorpus
{
    private readonly MailRepository _repository = repository;

    public long SpamMessages => _repository.JunkMessageTotals().Spam;

    public long HamMessages => _repository.JunkMessageTotals().Ham;

    public (long Spam, long Ham) Counts(string token)
        => _repository.JunkCounts([token]).GetValueOrDefault(token);

    public IReadOnlyDictionary<string, (long Spam, long Ham)> CountsFor(IReadOnlyCollection<string> tokens)
        => _repository.JunkCounts(tokens);

    public void Train(IReadOnlyCollection<string> tokens, bool spam)
        => _repository.TrainJunk(tokens, spam, add: true);

    public void Untrain(IReadOnlyCollection<string> tokens, bool spam)
        => _repository.TrainJunk(tokens, spam, add: false);
}
