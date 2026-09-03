using Mailbox.Core.Localization;

namespace Mailbox.Protocols;

/// <summary>Where one task in a send/receive run has got to.</summary>
public enum TransferTaskState
{
    Waiting,
    Processing,
    Completed,
    Failed,
}

/// <summary>One row of the progress dialog's table.</summary>
public sealed record TransferTask(string Name, TransferTaskState State, string Progress = "")
{
    /// <summary>The tick, arrow or cross the reference draws in the leftmost column.</summary>
    public string Marker => State switch
    {
        TransferTaskState.Completed => "mark-complete",
        TransferTaskState.Processing => "forward",
        TransferTaskState.Failed => "warning",
        _ => string.Empty,
    };
}

/// <summary>
/// A send/receive run as the progress dialog sees it: one task per direction per account.
/// </summary>
/// <remarks>
/// The service reports what it is doing; this turns that into the table the reference shows, and
/// is deliberately separate from the dialog so the counting can be tested without a window. The
/// arithmetic is the part that goes wrong — "5 of 8 Tasks have completed successfully" is a
/// claim about both how many finished and how many of those worked.
/// <para>
/// Two tasks per account, sending first, because that is the order the service runs them in: a
/// reply queued a moment ago should leave before the poll that might bring its answer.
/// </para>
/// </remarks>
public sealed class SendReceiveTasks
{
    private const string Sending = "Sending";
    private const string Receiving = "Receiving";

    private readonly List<string> _addresses;
    private readonly Dictionary<(string Address, string Direction), TransferTask> _tasks = [];
    private readonly List<string> _errors = [];

    public SendReceiveTasks(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        _addresses = [.. addresses];

        foreach (var address in _addresses)
        {
            foreach (var direction in (string[])[Sending, Receiving])
            {
                _tasks[(address, direction)] =
                    new TransferTask($"{address} - {direction}", TransferTaskState.Waiting);
            }
        }
    }

    /// <summary>The table, in the order the run works through it.</summary>
    public IReadOnlyList<TransferTask> Tasks =>
    [
        .. _addresses.SelectMany(address =>
            new[] { _tasks[(address, Sending)], _tasks[(address, Receiving)] }),
    ];

    public int Total => _tasks.Count;

    /// <summary>Finished and worked. A failed task has finished but has not succeeded.</summary>
    public int Succeeded => _tasks.Values.Count(t => t.State == TransferTaskState.Completed);

    public int Failed => _tasks.Values.Count(t => t.State == TransferTaskState.Failed);

    public bool IsFinished => _tasks.Values.All(
        t => t.State is TransferTaskState.Completed or TransferTaskState.Failed);

    /// <summary>What the Errors tab lists, and why the dialog stays open.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>The line above the bar, worded as the reference words it.</summary>
    public string Headline =>
        string.Format(
            Strings.Plural(
                "{0} of {1} Task have completed successfully",
                "{0} of {1} Tasks have completed successfully",
                Total),
            Succeeded,
            Total);

    /// <summary>0 to 1, for the bar. Counts anything finished, failures included.</summary>
    public double Fraction => Total == 0 ? 0 : (double)(Succeeded + Failed) / Total;

    /// <summary>The task the run is on, for the line under the table.</summary>
    public string Current =>
        _tasks.Values.FirstOrDefault(t => t.State == TransferTaskState.Processing)?.Name
        ?? string.Empty;

    /// <summary>
    /// Folds in a report from the service.
    /// </summary>
    /// <remarks>
    /// Anything that is not the sending stage means the account has moved on to receiving, so
    /// its sending task is done. The receiver reports several stages — Connecting, then a count
    /// per message — and they all belong to one row.
    /// </remarks>
    public void Report(PollProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!_tasks.ContainsKey((progress.Account, Sending))) return;

        if (string.Equals(progress.Stage, Sending, StringComparison.Ordinal))
        {
            Set(progress.Account, Sending, TransferTaskState.Processing);
            return;
        }

        Complete(progress.Account, Sending);

        // What the Remaining column shows: a count once there is one to give, and the stage
        // before that. "Connecting" is worth saying; "Connecting 0 of 0" is not.
        var detail = progress.Total > 0
            ? $"{progress.Done} of {progress.Total}"
            : progress.Stage;

        Set(progress.Account, Receiving, TransferTaskState.Processing, detail);
    }

    /// <summary>Closes the run out from its result, which is what says whether it worked.</summary>
    public void Finish(SendReceiveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var account in result.Accounts)
        {
            if (!_tasks.ContainsKey((account.Address, Sending))) continue;

            if (account.Succeeded)
            {
                Complete(account.Address, Sending, $"{account.Sent} sent");
                Complete(account.Address, Receiving, $"{account.Received} received");
                continue;
            }

            // A run reports one error per account rather than one per direction, so the failure
            // is attributed to whichever half had not finished — sending, if it never got past it.
            var sendingDone = _tasks[(account.Address, Sending)].State == TransferTaskState.Completed;

            if (sendingDone)
            {
                // Sending got through and receiving is what failed. Sending keeps whatever it
                // reported.
                Set(account.Address, Receiving, TransferTaskState.Failed, "Failed");
            }
            else
            {
                // Sending failed, so receiving never ran — and a half that never ran did not
                // succeed. Marking it Completed said a receive had finished when nothing had been
                // received, and counted it towards "N of M Tasks have completed successfully".
                Set(account.Address, Sending, TransferTaskState.Failed, "Failed");
                Set(account.Address, Receiving, TransferTaskState.Failed, "Not run");
            }

            _errors.Add($"{account.Address}: {account.Error}");
        }

        // An account the run never reached — it was cancelled, or it is not in the result —
        // is finished as far as this dialog is concerned, and it did not succeed.
        foreach (var key in _tasks.Keys.ToList())
        {
            if (_tasks[key].State is TransferTaskState.Waiting or TransferTaskState.Processing)
            {
                _tasks[key] = _tasks[key] with { State = TransferTaskState.Failed, Progress = "Cancelled" };
            }
        }
    }

    private void Complete(string address, string direction, string? detail = null)
    {
        if (_tasks[(address, direction)].State == TransferTaskState.Failed) return;
        Set(address, direction, TransferTaskState.Completed, detail ?? "Completed");
    }

    private void Set(string address, string direction, TransferTaskState state, string detail = "")
    {
        if (!_tasks.TryGetValue((address, direction), out var task)) return;
        _tasks[(address, direction)] = task with { State = state, Progress = detail };
    }
}
