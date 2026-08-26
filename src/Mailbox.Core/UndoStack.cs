namespace Mailbox.Core;

/// <summary>One thing that was done, and how to take it back.</summary>
/// <param name="Description">What was done, in the words the status line uses: "Delete", "Move".</param>
/// <param name="Undo">Puts things back as they were.</param>
/// <param name="Redo">Does it again, for a reader who undid one press too many.</param>
public sealed record UndoStep(string Description, Action Undo, Action Redo);

/// <summary>
/// What Ctrl+Z takes back.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Undo is one of the two commands on the shipped Quick Access Toolbar
/// and had no handler at all: pressing it wrote "Undo — not wired yet" into the status bar, and
/// the most reflexive gesture in a mail client — Ctrl+Z after deleting the wrong message —
/// answered with a developer string.
/// <para>
/// <b>What it holds.</b> Steps, not state: each one carries the two closures that put a change
/// back and do it again, which is what suits a store where the change is already committed. A
/// journal of rows would mean a second model of every operation beside the one that performs
/// it; a pair of closures is written where the operation is, next to the code that knows what it
/// did.
/// </para>
/// <para>
/// <b>What it deliberately does not hold.</b> Anything that cannot be put back — a permanent
/// delete, a message that has gone to a server. Offering to undo those would be worse than not
/// offering at all, because the offer is what a reader trusts when they press the wrong key.
/// </para>
/// </remarks>
public sealed class UndoStack
{
    /// <summary>
    /// How many steps are kept.
    /// </summary>
    /// <remarks>
    /// Deep enough that a run of small mistakes is recoverable, shallow enough that a step's
    /// closures — which hold row ids and folder ids from a session that may have moved on — do
    /// not accumulate. The reference keeps a comparable handful and forgets the rest silently.
    /// </remarks>
    public const int Depth = 25;

    private readonly List<UndoStep> _done = [];
    private readonly List<UndoStep> _undone = [];

    /// <summary>The steps a batch is collecting, or null when none is open.</summary>
    private List<UndoStep>? _batch;
    private string _batchDescription = string.Empty;
    private int _batchDepth;

    /// <summary>Raised whenever what can be undone or redone changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a step is in flight, so that undoing does not record itself.</summary>
    public bool IsReplaying { get; private set; }

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    /// <summary>What the next Ctrl+Z would take back, for the status line and the tooltip.</summary>
    public string? NextUndo => _done.Count > 0 ? _done[^1].Description : null;

    public string? NextRedo => _undone.Count > 0 ? _undone[^1].Description : null;

    /// <summary>
    /// Records something that has just been done.
    /// </summary>
    /// <remarks>
    /// Ignored while a step is being replayed: an undo that performed the reverse operation
    /// through the same code path would otherwise push a step of its own and the stack would
    /// never empty.
    /// </remarks>
    public void Push(string description, Action undo, Action redo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);

        if (IsReplaying) return;

        // Inside a batch the step is held: what reaches the stack is the one step the batch
        // closes with, so the reader takes back the command they pressed rather than its parts.
        if (_batch is not null)
        {
            _batch.Add(new UndoStep(description, undo, redo));
            return;
        }

        _done.Add(new UndoStep(description, undo, redo));
        if (_done.Count > Depth) _done.RemoveAt(0);

        // A new action makes the redo branch unreachable, which is what every editor does and
        // what a reader expects: there is one past, and it is the one they are in.
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes back the last step. Returns what it was, or null when there was none.</summary>
    public string? Undo() => Replay(_done, _undone, step => step.Undo);

    /// <summary>Does the last undone step again.</summary>
    public string? Redo() => Replay(_undone, _done, step => step.Redo);

    /// <summary>Forgets everything, for a change of account or store that makes the ids meaningless.</summary>
    public void Clear()
    {
        if (_done.Count == 0 && _undone.Count == 0) return;

        _done.Clear();
        _undone.Clear();

        // A batch collected against ids that are about to mean nothing goes with them.
        _batch = null;
        _batchDepth = 0;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Collects everything recorded inside it into one step, for a command that is several.
    /// </summary>
    /// <remarks>
    /// A Quick Step is one press to the reader and any number of operations underneath — move it,
    /// mark it read, categorize it — each of which records itself. Without this, taking one back
    /// means pressing Ctrl+Z once per operation, and how many that is is not something the reader
    /// can see. Junk is the same shape for a different reason: it trains the filter and then
    /// moves the message, and only both together are the thing that was done.
    /// <para>
    /// A batch opened inside another joins it and the outer description is the one kept, so a
    /// command that batches for its own reasons is still one step inside a Quick Step. Nothing is
    /// pushed for a batch that collected nothing.
    /// </para>
    /// </remarks>
    public IDisposable Batch(string description)
    {
        // A replayed step records nothing, so there is nothing here to collect — and opening one
        // would leave a batch across an undo that the caller never closes.
        if (IsReplaying) return Nothing.Instance;

        if (_batchDepth++ == 0)
        {
            _batch = [];
            _batchDescription = description;
        }

        return new Scope(this);
    }

    private void CloseBatch()
    {
        if (_batchDepth == 0 || --_batchDepth > 0) return;

        var collected = _batch ?? [];
        _batch = null;

        if (collected.Count == 0) return;

        // Taken back newest first, as the stack itself takes steps back; done again oldest first,
        // in the order they happened.
        Push(
            _batchDescription,
            () => { for (var i = collected.Count - 1; i >= 0; i--) collected[i].Undo(); },
            () => { foreach (var step in collected) step.Redo(); });
    }

    /// <summary>What a caller holds while a batch is open; disposing it closes the batch.</summary>
    private sealed class Scope(UndoStack owner) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed) return;

            _closed = true;
            owner.CloseBatch();
        }
    }

    /// <summary>The scope handed back when there is no batch to open, so callers need no branch.</summary>
    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }

    private string? Replay(List<UndoStep> from, List<UndoStep> to, Func<UndoStep, Action> which)
    {
        if (from.Count == 0) return null;

        var step = from[^1];
        from.RemoveAt(from.Count - 1);

        IsReplaying = true;

        try
        {
            which(step)();
        }
        finally
        {
            IsReplaying = false;
        }

        to.Add(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return step.Description;
    }
}
