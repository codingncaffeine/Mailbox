using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The doors onto the feeds engine: a scripted sequence of presses in one run, and the two
/// pickers that stood between a harness and the subscription list.
/// </summary>
/// <remarks>
/// <b>Why a script rather than one pose per press.</b> Everything the polling layer claims is a
/// claim about the <em>second</em> poll of a feed — that it costs one conditional request and no
/// body, that a revised entry replaces the row it is already in rather than arriving beside it,
/// that a paused feed is passed over by the pass that reads everything and read anyway by the
/// one that names it. None of that can be posed one run at a time: a capture run's settings are
/// a scratch copy, so the ETag a run learns dies with it, and two runs of the same pose are two
/// first polls. So the steps run in order inside one process, each awaited, against a publisher
/// the caller controls.
/// <para>
/// Every step goes in through the same door a reader uses — <c>RunCommand</c> for the ribbon's
/// commands, the nav row's own button for choosing a feed — and the run waits on the module's own
/// in-flight flag rather than on a guessed delay, so a step never reads the store half-way
/// through the one before it.
/// </para>
/// <para>
/// <b>And the two pickers.</b> OPML in and out are the only way anybody moves between readers, and
/// both ends were a desktop file picker: a headless run could not answer one, so "it wrote
/// nothing" and "it did nothing" were the same evidence. The export half reuses the calendar's
/// own <c>MAILBOX_EXPORT</c>; the import half is <c>MAILBOX_OPEN</c>, which is the open-picker
/// read-back that had never existed. Both only under a capture, so a reader is always asked.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase11APoses()
    {
        // MAILBOX_FEED_POLL=<step>;<step>;… — the scripted sequence. At Background, after the
        // module switch and the article pick have had their pass, because the first step is
        // usually a command pressed over what those left selected.
        if (Environment.GetEnvironmentVariable("MAILBOX_FEED_POLL") is { Length: > 0 } script)
        {
            var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => _ = RunFeedScriptAsync(script, hold), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Where an Open picker should read from when the harness is driving:
    /// <c>MAILBOX_OPEN=opml:/tmp/in.opml</c>.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="HarnessSavePath"/>, and the door the plan recorded as missing:
    /// with only the save half posed, an import that read nothing and an import that was never
    /// reached both logged "windows: none". Only under <c>MAILBOX_CAPTURE</c>, so a reader is
    /// always shown a picker whatever is in their environment.
    /// </remarks>
    internal static string? HarnessOpenPath(string kind)
    {
        if (!WindowCapture.IsRequested) return null;
        if (Environment.GetEnvironmentVariable("MAILBOX_OPEN") is not { Length: > 0 } spec) return null;

        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.IndexOf(':');
            if (split < 1) continue;
            if (!part[..split].Trim().Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;

            var path = part[(split + 1)..].Trim();
            if (path.Length == 0) continue;

            Log.Info($"Harness: open — {kind} is read from {path} instead of a picker.");
            return path;
        }

        return null;
    }

    // ---- The script ------------------------------------------------------------------------------

    /// <summary>
    /// Runs the posed steps in order, each waited out before the next.
    /// </summary>
    /// <remarks>
    /// The steps, all of which read back what they did:
    /// <list type="bullet">
    /// <item><c>run:&lt;command-id&gt;</c> — presses a ribbon command through the dispatcher and
    /// waits for the module to stop reading feeds;</item>
    /// <item><c>feed:&lt;name&gt;</c> — presses a feed's own row in the nav, which is what makes
    /// it the selected feed for Update This Feed, Pause and the options dialog;</item>
    /// <item><c>pick:&lt;n&gt;</c> and <c>open:&lt;n&gt;</c> — choose, and choose-and-open, the
    /// nth article showing;</item>
    /// <item><c>hit:&lt;url&gt;</c> — one plain request through the reader's own fetcher, which
    /// is how a posed publisher is told to change what it serves between two polls;</item>
    /// <item><c>settle:&lt;ms&gt;</c> — a beat for work that is not on the dispatcher;</item>
    /// <item><c>dump</c> — every subscription and every article filed, with the numbers a
    /// screenshot cannot carry.</item>
    /// </list>
    /// </remarks>
    private async Task RunFeedScriptAsync(string script, IDisposable? hold)
    {
        try
        {
            foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await RunFeedStepAsync(step).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the feed script failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    private async Task RunFeedStepAsync(string step)
    {
        if (DataContext is not ShellViewModel shell) return;

        var split = step.IndexOf(':');
        var verb = (split < 0 ? step : step[..split]).Trim().ToLowerInvariant();
        var arg = split < 0 ? string.Empty : step[(split + 1)..].Trim();

        switch (verb)
        {
            case "run":
                Log.Info($"Harness: feed script — running {arg}.");
                RunCommand(new CommandId(arg));
                await SettledAsync().ConfigureAwait(true);
                Log.Info($"Harness: feed script — {arg} left “{shell.StatusRight}”.");
                break;

            case "feed":
                Log.Info($"Harness: feed script — {PressNavRow(shell, arg)}.");
                break;

            case "pick":
            case "open":
                if (int.TryParse(arg, CultureInfo.InvariantCulture, out var nth))
                {
                    Log.Info($"Harness: feed script — article {nth}: "
                        + $"{EnsureFeeds(shell).PoseSelect(nth, verb == "open")}.");
                }

                break;

            case "hit":
                var answer = await App.FeedReader.Fetch.GetAsync(arg).ConfigureAwait(true);
                Log.Info($"Harness: feed script — asked {arg}, {(int)answer.Status} "
                    + $"({answer.Text.Length} characters).");
                break;

            case "settle":
                await Task.Delay(int.TryParse(arg, CultureInfo.InvariantCulture, out var ms) ? ms : 500)
                    .ConfigureAwait(true);
                break;

            case "dump":
                DumpFeedState(shell, arg);
                break;

            default:
                Log.Warn($"Harness: feed script — “{verb}” is not a step.");
                break;
        }
    }

    /// <summary>
    /// Waits until the module is not part-way through reading feeds.
    /// </summary>
    /// <remarks>
    /// The commands fire their work off rather than awaiting it, as a button must — so a script
    /// that pressed the next one straight away would be racing the poll it had just started. The
    /// module's own in-flight flag is the honest thing to wait on; the ceiling only stops a run
    /// hanging on a publisher that never answers.
    /// </remarks>
    private async Task SettledAsync()
    {
        for (var waited = 0; waited < 60_000; waited += 50)
        {
            await Task.Delay(50).ConfigureAwait(true);
            if (!_feedsUpdating && waited > 150) return;
        }

        Log.Warn("Harness: feed script — a poll was still running after a minute.");
    }

    /// <summary>Presses a feed's own row in the nav, which is how a reader chooses one.</summary>
    private string PressNavRow(ShellViewModel shell, string name)
    {
        var feeds = EnsureFeeds(shell);
        if (feeds.NavButton(name) is not { } button) return $"there is no row reading “{name}”";

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return $"pressed the row “{name}” — selected feed is now "
            + $"“{feeds.SelectedFeed?.Name ?? "(none)"}”";
    }

    // ---- The read-back ----------------------------------------------------------------------------

    /// <summary>
    /// Every subscription and every article, with the numbers the claims are made in.
    /// </summary>
    /// <remarks>
    /// Written as one block rather than left to a capture because none of it is visible: what a
    /// poll cost is an ETag and a Last-Modified on a subscription, whether an article was filled
    /// in is a count of characters, and whether a thumbnail will draw is a column with an address
    /// in it. A photograph of the article list says none of those things.
    /// </remarks>
    private void DumpFeedState(ShellViewModel shell, string arg)
    {
        Log.Info($"Harness: feeds{(arg.Length > 0 ? $" [{arg}]" : string.Empty)} — "
            + $"{App.Feeds.All.Count} subscription(s).");

        foreach (var feed in App.Feeds.InOrder)
        {
            Log.Info($"Harness:   feed “{feed.Name}” {feed.Url}"
                + $" heading=“{feed.Category}”"
                + $" paused={feed.Paused}"
                + $" etag={(feed.Etag.Length > 0 ? feed.Etag : "-")}"
                + $" modified={(feed.LastModified.Length > 0 ? feed.LastModified : "-")}"
                + $" checked={Stamp(feed.LastChecked)}"
                + $" due={Stamp(feed.NextDueUtc)}"
                + $" limit={feed.ProviderLimitMinutes?.ToString(CultureInfo.InvariantCulture) ?? "-"}"
                + $" uselimit={feed.UseProviderLimit}"
                + $" every={feed.RefreshMinutes}"
                + $" keep={feed.KeepMost}"
                + $" fulltext={feed.ReadFullArticle}"
                + $" failures={feed.Failures}"
                + $" error={(feed.LastError.Length > 0 ? feed.LastError : "-")}");
        }

        if (FeedAccount() is not { } account)
        {
            Log.Info("Harness:   there is no feeds account, so nothing has been filed.");
            return;
        }

        var folders = account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == FeedReceiver.RootFolder);

        if (root is null)
        {
            Log.Info("Harness:   no RSS Feeds folder exists yet.");
            return;
        }

        foreach (var folder in folders.Where(f => Inside(folders, f, root.Id)).OrderBy(f => f.Name))
        {
            var articles = account.Mail.Messages(folder.Id, limit: 200);
            Log.Info($"Harness:   folder “{FolderPath(folders, folder, root.Id)}” — {articles.Count} article(s).");

            foreach (var article in articles)
            {
                Log.Info($"Harness:     #{article.Id} “{article.Subject}”"
                    + $" read={article.IsRead} flagged={article.IsFlagged}"
                    + $" received={Stamp(article.Received)}"
                    + $" bytes={article.SizeBytes}"
                    + $" chars={Characters(account, article.Id)}"
                    + $" image={(article.FeedImage.Length > 0 ? article.FeedImage : "-")}"
                    + $" link={(article.FeedLink.Length > 0 ? article.FeedLink : "-")}"
                    + $" uid={article.ServerUid ?? "-"}"
                    + $" msgid={article.MessageId ?? "-"}");
            }
        }
    }

    /// <summary>How much article a filed message actually carries.</summary>
    private static string Characters(OpenAccount account, long id)
    {
        if (account.Mail.LoadRaw(id) is not { } raw) return "?";

        try
        {
            using var stream = new MemoryStream(raw);
            var message = MimeKit.MimeMessage.Load(stream);
            var text = message.TextBody ?? FeedParser.PlainText(message.HtmlBody ?? string.Empty);
            var filled = message.Headers[ArticleFill.FilledHeader] is { Length: > 0 };

            return text.Trim().Length.ToString(CultureInfo.InvariantCulture) + (filled ? "+page" : string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return "unreadable";
        }
    }

    private static bool Inside(IReadOnlyList<Folder> all, Folder folder, long rootId)
    {
        var at = folder;
        for (var depth = 0; depth < 8 && at.ParentId is { } parent; depth++)
        {
            if (parent == rootId) return true;
            if (all.FirstOrDefault(f => f.Id == parent) is not { } next) return false;
            at = next;
        }

        return false;
    }

    private static string FolderPath(IReadOnlyList<Folder> all, Folder folder, long rootId)
    {
        var parts = new List<string> { folder.Name };
        var at = folder;

        for (var depth = 0; depth < 8 && at.ParentId is { } parent && parent != rootId; depth++)
        {
            if (all.FirstOrDefault(f => f.Id == parent) is not { } next) break;
            parts.Insert(0, next.Name);
            at = next;
        }

        return string.Join('/', parts);
    }

    private static string Stamp(DateTimeOffset? when)
        => when is { } moment ? moment.UtcDateTime.ToString("s", CultureInfo.InvariantCulture) : "-";
}
