using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Protocols;

/// <summary>
/// One line of the conversation between the application and the process that shows its
/// Send/Receive Progress dialog as a toaster.
/// </summary>
/// <remarks>
/// The toaster is the same dialog, drawn by the same code, in a process of its own — the
/// application's <c>ProgressToaster</c> says why. What crosses is what the dialog is built from:
/// the table as it stands when the toaster starts, each progress report after that as the service
/// raised it, and the result the run finished with. The other process folds those into a
/// <see cref="SendReceiveTasks"/> of its own, so the two tables cannot disagree: the counting lives
/// in one class and both ends run it over the same inputs. The table goes whole rather than as the
/// account list, because a toaster that starts after the first report would otherwise show every
/// report it missed as a row still waiting — which the pixel comparison against the in-process
/// dialog caught, one row out of four. Back the other way come the three things the dialog can do — Cancel All,
/// the "don't show" box, being closed — and the two things the application waits to hear: that the
/// process is ready, and that the window is up.
/// </remarks>
public abstract record ToasterMessage
{
    private ToasterMessage()
    {
    }

    // ---- The application to the toaster ----------------------------------------------------

    /// <summary>
    /// The run to report on as it stands — its accounts, every row, the errors so far — and a
    /// point on the screen the application is on.
    /// </summary>
    public sealed record Begin(
        IReadOnlyList<string> Addresses,
        IReadOnlyList<TransferTask> Rows,
        IReadOnlyList<string> Errors,
        int X,
        int Y) : ToasterMessage
    {
        /// <summary>The message for a table as it stands now.</summary>
        public static Begin For(SendReceiveTasks tasks, int x, int y)
        {
            ArgumentNullException.ThrowIfNull(tasks);
            return new Begin([.. tasks.Addresses], tasks.Tasks, [.. tasks.Errors], x, y);
        }

        /// <summary>The table this message describes, standing where the sender's stood.</summary>
        public SendReceiveTasks Table() => SendReceiveTasks.From(Addresses, Rows, Errors);
    }

    /// <summary>A report from the service, exactly as the application's own table received it.</summary>
    public sealed record Progress(PollProgress Report) : ToasterMessage;

    /// <summary>How the run ended, account by account.</summary>
    public sealed record Finish(IReadOnlyList<AccountRunResult> Accounts) : ToasterMessage;

    /// <summary>Put the window up.</summary>
    public sealed record Show : ToasterMessage;

    /// <summary>Take the window down and end the process.</summary>
    public sealed record Close : ToasterMessage;

    // ---- The toaster to the application ----------------------------------------------------

    /// <summary>Started, themed, and holding the run: the window can be asked for.</summary>
    public sealed record Ready : ToasterMessage;

    /// <summary>The window is on screen, on the backend named.</summary>
    public sealed record Shown(string Backend) : ToasterMessage;

    /// <summary>Cancel All was pressed.</summary>
    public sealed record CancelAll : ToasterMessage;

    /// <summary>"Don't show this dialog box during Send/Receive" was ticked or cleared.</summary>
    public sealed record HideChanged(bool Hidden) : ToasterMessage;

    /// <summary>The window has gone, whoever closed it; the process is ending.</summary>
    public sealed record Closed : ToasterMessage;
}

/// <summary>
/// Writes and reads <see cref="ToasterMessage"/>s, one JSON object to a line.
/// </summary>
/// <remarks>
/// A line because a line is what a pipe hands a reader for free, and JSON because an account's
/// error is somebody else's text: a server's reply with a newline or a quote in it must arrive as
/// one message and not as the start of the next. A line that does not parse, or names something
/// this build does not know, is dropped rather than guessed at — the two ends are always the same
/// build, so that only happens to a line that was damaged, and a damaged line is not an
/// instruction.
/// </remarks>
public static class ToasterWire
{
    public static string Encode(ToasterMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        JsonObject line = message switch
        {
            ToasterMessage.Begin begin => new JsonObject
            {
                ["op"] = "begin",
                ["addresses"] = new JsonArray([.. begin.Addresses.Select(a => (JsonNode?)JsonValue.Create(a))]),
                ["rows"] = new JsonArray([.. begin.Rows.Select(r => (JsonNode?)new JsonObject
                {
                    ["name"] = r.Name,
                    ["state"] = r.State.ToString(),
                    ["progress"] = r.Progress,
                })]),
                ["errors"] = new JsonArray([.. begin.Errors.Select(e => (JsonNode?)JsonValue.Create(e))]),
                ["x"] = begin.X,
                ["y"] = begin.Y,
            },
            ToasterMessage.Progress progress => new JsonObject
            {
                ["op"] = "progress",
                ["account"] = progress.Report.Account,
                ["done"] = progress.Report.Done,
                ["total"] = progress.Report.Total,
                ["stage"] = progress.Report.Stage,
            },
            ToasterMessage.Finish finish => new JsonObject
            {
                ["op"] = "finish",
                ["accounts"] = new JsonArray([.. finish.Accounts.Select(a => (JsonNode?)new JsonObject
                {
                    ["address"] = a.Address,
                    ["received"] = a.Received,
                    ["sent"] = a.Sent,
                    ["error"] = a.Error,
                })]),
            },
            ToasterMessage.Show => new JsonObject { ["op"] = "show" },
            ToasterMessage.Close => new JsonObject { ["op"] = "close" },
            ToasterMessage.Ready => new JsonObject { ["op"] = "ready" },
            ToasterMessage.Shown shown => new JsonObject { ["op"] = "shown", ["backend"] = shown.Backend },
            ToasterMessage.CancelAll => new JsonObject { ["op"] = "cancel" },
            ToasterMessage.HideChanged hide => new JsonObject { ["op"] = "hide", ["hidden"] = hide.Hidden },
            ToasterMessage.Closed => new JsonObject { ["op"] = "closed" },
            _ => throw new ArgumentException($"No wire form for {message.GetType().Name}.", nameof(message)),
        };

        return line.ToJsonString();
    }

    /// <summary>The message on a line, or null when the line is not one.</summary>
    public static ToasterMessage? Decode(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;

            return Text(o, "op") switch
            {
                "begin" when o["addresses"] is JsonArray addresses && Strings(addresses) is { } list
                             && o["rows"] is JsonArray rows && Rows(rows) is { } table && table.Count == list.Count * 2
                             && o["errors"] is JsonArray errors && Strings(errors) is { } said
                             && Number(o, "x") is { } x && Number(o, "y") is { } y
                    => new ToasterMessage.Begin(list, table, said, x, y),

                "progress" when Text(o, "account") is { } account && Number(o, "done") is { } done
                                && Number(o, "total") is { } total && Text(o, "stage") is { } stage
                    => new ToasterMessage.Progress(new PollProgress(account, done, total, stage)),

                "finish" when o["accounts"] is JsonArray accounts && Results(accounts) is { } results
                    => new ToasterMessage.Finish(results),

                "show" => new ToasterMessage.Show(),
                "close" => new ToasterMessage.Close(),
                "ready" => new ToasterMessage.Ready(),
                "shown" => new ToasterMessage.Shown(Text(o, "backend") ?? string.Empty),
                "cancel" => new ToasterMessage.CancelAll(),
                "hide" when o["hidden"] is JsonValue v && v.TryGetValue<bool>(out var hidden)
                    => new ToasterMessage.HideChanged(hidden),
                "closed" => new ToasterMessage.Closed(),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Number(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    private static List<string>? Strings(JsonArray array)
    {
        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            list.Add(s);
        }

        return list;
    }

    private static List<TransferTask>? Rows(JsonArray array)
    {
        var list = new List<TransferTask>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject r
                || Text(r, "name") is not { } name
                || Text(r, "state") is not { } state
                || !Enum.TryParse<TransferTaskState>(state, ignoreCase: false, out var parsed)
                || !Enum.IsDefined(parsed)
                || Text(r, "progress") is not { } progress)
            {
                return null;
            }

            list.Add(new TransferTask(name, parsed, progress));
        }

        return list;
    }

    private static List<AccountRunResult>? Results(JsonArray array)
    {
        var list = new List<AccountRunResult>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject a
                || Text(a, "address") is not { } address
                || Number(a, "received") is not { } received
                || Number(a, "sent") is not { } sent)
            {
                return null;
            }

            list.Add(new AccountRunResult(address, received, sent, Text(a, "error")));
        }

        return list;
    }
}
