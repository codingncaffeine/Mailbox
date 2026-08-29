using Microsoft.Data.Sqlite;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Import;

/// <summary>One Thunderbird profile: the name its profiles.ini gives it, and where it lives.</summary>
public sealed record ThunderbirdProfile(string Name, string Directory);

/// <summary>What a whole profile import came to, one report per part.</summary>
public sealed record ThunderbirdReport(
    ImportReport Mail,
    PimImportReport AddressBooks,
    int Rules,
    int RulesSkipped,
    IReadOnlyList<string> Notes)
{
    public string Summary =>
        Mail.Summary
        + (AddressBooks.Imported > 0 || AddressBooks.AlreadyHere > 0 ? " " + AddressBooks.Summary : string.Empty)
        + (Rules > 0 ? $" {Rules} rule(s) imported" + (RulesSkipped > 0 ? $", {RulesSkipped} skipped" : string.Empty) + "." : string.Empty);
}

/// <summary>
/// Imports a Thunderbird profile: the mbox tree, the address books, and the filters that
/// translate.
/// </summary>
/// <remarks>
/// The mail is mbox files beside <c>.msf</c> indexes, with <c>.sbd</c> directories carrying the
/// hierarchy — <c>Mail/Local Folders</c> and every server under <c>Mail</c> and
/// <c>ImapMail</c>. The address books are SQLite (<c>abook.sqlite</c> and friends, one
/// <c>properties</c> table of card/name/value rows). The filters are
/// <c>msgFilterRules.dat</c>, translated where the vocabulary matches ours and skipped with a
/// note where it does not — a rule silently changed in meaning would be worse than one missing
/// and named. Everything inherits the importers' standing rules: the profile is never written
/// to, and re-running tops up.
/// </remarks>
public sealed class ThunderbirdImporter(MailRepository mail, long accountId, PimRepository? pim = null, Action<PimItem>? queuePut = null)
{
    private readonly MailRepository _mail = mail ?? throw new ArgumentNullException(nameof(mail));

    /// <summary>The profiles this machine has, newest-touched first. Empty when Thunderbird never ran.</summary>
    public static IReadOnlyList<ThunderbirdProfile> FindProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<ThunderbirdProfile>();

        foreach (var root in new[] { Path.Combine(home, ".thunderbird"), Path.Combine(home, ".mozilla-thunderbird") })
        {
            var ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) continue;

            string? name = null, path = null;
            var relative = true;

            void Take()
            {
                if (path is null) return;
                var directory = relative ? Path.Combine(root, path) : path;
                if (Directory.Exists(directory)) found.Add(new ThunderbirdProfile(name ?? Path.GetFileName(directory), directory));
            }

            foreach (var line in File.ReadAllLines(ini))
            {
                var text = line.Trim();
                if (text.StartsWith('['))
                {
                    Take();
                    name = null;
                    path = null;
                    relative = true;
                }
                else if (text.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) name = text[5..];
                else if (text.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) path = text[5..];
                else if (text.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase)) relative = text[11..] == "1";
            }

            Take();
        }

        return [.. found.OrderByDescending(p => Directory.GetLastWriteTimeUtc(p.Directory))];
    }

    /// <summary>Imports one profile. Progress is (done, total) over the mbox files.</summary>
    public ThunderbirdReport Run(string profileDirectory, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        var notes = new List<string>();
        var mailReport = ImportMail(profileDirectory, progress, notes, cancellation);
        var books = ImportAddressBooks(profileDirectory, notes);
        var (rules, skipped) = ImportFilters(profileDirectory, notes);

        Log.Info($"Thunderbird import: {mailReport.Summary} {books.Summary} {rules} rule(s), {skipped} skipped.");
        return new ThunderbirdReport(mailReport, books, rules, skipped, notes);
    }

    // ---- Mail ----------------------------------------------------------------------------------

    private ImportReport ImportMail(string profile, Action<int, int>? progress, List<string> notes, CancellationToken cancellation)
    {
        var filer = new MessageFiler(_mail);
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var mboxes = new List<(string Path, IReadOnlyList<string> Folder)>();

        foreach (var serverRoot in new[] { "Mail", "ImapMail" }.Select(d => Path.Combine(profile, d)).Where(Directory.Exists))
        {
            foreach (var server in Directory.EnumerateDirectories(serverRoot))
            {
                CollectMboxes(server, [], mboxes);
            }
        }

        var done = 0;
        foreach (var (path, folder) in mboxes)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(done++, mboxes.Count);

            var folderId = Folder(folders, folder, notes);
            using var stream = File.OpenRead(path);
            foreach (var message in Mbox.Read(stream))
            {
                cancellation.ThrowIfCancellationRequested();
                filer.File(folderId, message.Raw, message.IsRead, message.IsFlagged,
                    fallbackDate: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                    name: Path.GetFileName(path));
            }
        }

        progress?.Invoke(mboxes.Count, mboxes.Count);
        return new ImportReport(folders.Count, filer.Imported, filer.AlreadyHere, 0, filer.Unreadable, [.. filer.Notes]);
    }

    /// <summary>An mbox is a file whose name carries no extension, or whose index sits beside it.</summary>
    private static void CollectMboxes(string directory, IReadOnlyList<string> parents, List<(string, IReadOnlyList<string>)> mboxes)
    {
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".msf", StringComparison.OrdinalIgnoreCase)) continue;
            if (name is "msgFilterRules.dat" or "filterlog.html" or "popstate.dat") continue;
            if (Path.HasExtension(name) && !File.Exists(file + ".msf")) continue;
            if (!Mbox.Looks(file) && new FileInfo(file).Length > 0) continue;

            mboxes.Add((file, [.. parents, Path.GetFileNameWithoutExtension(name)]));
        }

        foreach (var sub in Directory.EnumerateDirectories(directory, "*.sbd").OrderBy(d => d, StringComparer.Ordinal))
        {
            var parent = Path.GetFileNameWithoutExtension(Path.GetFileName(sub))!;
            parent = parent.EndsWith(".sbd", StringComparison.OrdinalIgnoreCase) ? parent[..^4] : parent;
            CollectMboxes(sub, [.. parents, Path.GetFileName(sub)[..^4]], mboxes);
        }
    }

    private long Folder(Dictionary<string, long> known, IReadOnlyList<string> path, List<string> notes)
    {
        var key = string.Join("/", path);
        if (known.TryGetValue(key, out var id)) return id;

        if (path.Count == 1 && WellKnownFolders.RoleFor(path[0]) is { } role
            && _mail.FolderWithRole(accountId, role) is { } existing)
        {
            known[key] = existing.Id;
            if (!string.Equals(existing.Name, path[0], StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"“{path[0]}” merged into {existing.Name}.");
            }

            return existing.Id;
        }

        long? parent = null;
        for (var i = 0; i < path.Count; i++)
        {
            var partial = string.Join("/", path.Take(i + 1));
            if (!known.TryGetValue(partial, out var levelId))
            {
                levelId = _mail.AddFolder(accountId, path[i], parentId: parent).Id;
                known[partial] = levelId;
            }

            parent = levelId;
        }

        return known[key];
    }

    // ---- Address books -------------------------------------------------------------------------

    private PimImportReport ImportAddressBooks(string profile, List<string> notes)
    {
        if (pim is null) return new PimImportReport(0, 0, 0, 0, 0, []);

        var contacts = 0;
        var already = 0;
        var book = new ContactBook(pim);
        var home = book.Default();

        // Fetched once, grown as the import writes: known by UID when the card kept one, and
        // by the pair a person is — primary address with name — when it did not.
        var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in book.Rows([home.Id]))
        {
            if (row.Contact.Uid.Length > 0) uids.Add(row.Contact.Uid);
            pairs.Add($"{row.Contact.PrimaryEmail}\n{row.Named()}");
        }

        foreach (var file in Directory.EnumerateFiles(profile, "*.sqlite").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("abook", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("history", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var contact in ReadAddressBook(file))
                {
                    if (uids.Contains(contact.Uid) || pairs.Contains($"{contact.PrimaryEmail}\n{contact.Named()}"))
                    {
                        already++;
                        continue;
                    }

                    var written = book.Save(contact.Uid.Length > 0 ? contact : contact with { Uid = Contact.NewUid() }, home.Id);
                    queuePut?.Invoke(written);
                    uids.Add(contact.Uid);
                    pairs.Add($"{contact.PrimaryEmail}\n{contact.Named()}");
                    contacts++;
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Could not read {name}: {ex.Message}");
            }
        }

        return new PimImportReport(0, 0, 0, contacts, already, notes);
    }

    /// <summary>Cards out of Thunderbird's SQLite: one properties table of card/name/value rows.</summary>
    private static IEnumerable<Contact> ReadAddressBook(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        var cards = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT card, name, value FROM properties";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var card = reader.GetString(0);
                if (!cards.TryGetValue(card, out var properties))
                {
                    properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    cards[card] = properties;
                }

                properties[reader.GetString(1)] = reader.GetString(2);
            }
        }

        foreach (var (card, p) in cards)
        {
            string Get(string key) => p.TryGetValue(key, out var value) ? value : string.Empty;

            var emails = new List<ContactEmail>();
            if (Get("PrimaryEmail") is { Length: > 0 } primary) emails.Add(new ContactEmail(primary));
            if (Get("SecondEmail") is { Length: > 0 } second) emails.Add(new ContactEmail(second));

            var phones = new List<ContactPhone>();
            if (Get("WorkPhone") is { Length: > 0 } work) phones.Add(new ContactPhone(work));
            if (Get("HomePhone") is { Length: > 0 } homePhone) phones.Add(new ContactPhone(homePhone, PhoneKind.Home));
            if (Get("CellularNumber") is { Length: > 0 } cell) phones.Add(new ContactPhone(cell, PhoneKind.Mobile));

            yield return new Contact
            {
                Uid = Get("UID") is { Length: > 0 } uid ? uid : card,
                DisplayName = Get("DisplayName"),
                FirstName = Get("FirstName"),
                LastName = Get("LastName"),
                Company = Get("Company"),
                JobTitle = Get("JobTitle"),
                Notes = Get("Notes"),
                Emails = emails,
                Phones = phones,
            };
        }
    }

    // ---- Filters -------------------------------------------------------------------------------

    private (int Imported, int Skipped) ImportFilters(string profile, List<string> notes)
    {
        var imported = 0;
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var file in Directory.EnumerateFiles(profile, "msgFilterRules.dat", SearchOption.AllDirectories))
        {
            foreach (var parsed in ThunderbirdFilters.Parse(File.ReadAllLines(file)))
            {
                if (parsed.Rule is { } rule)
                {
                    _mail.AddRule(rule, now);
                    imported++;
                }
                else
                {
                    skipped++;
                    if (parsed.Why is { Length: > 0 }) notes.Add(parsed.Why);
                }
            }
        }

        return (imported, skipped);
    }
}

/// <summary>The folder spellings every importer merges by. Outbox is absent on purpose.</summary>
public static class WellKnownFolders
{
    public static FolderRole? RoleFor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "inbox" => FolderRole.Inbox,
        "sent" or "sent items" or "sent mail" or "sent-mail" or "sent messages" => FolderRole.Sent,
        "drafts" or "draft" => FolderRole.Drafts,
        "trash" or "deleted items" or "deleted messages" or "wastebasket" => FolderRole.Deleted,
        "junk" or "spam" or "junk email" or "junk e-mail" => FolderRole.Junk,
        "archive" or "archives" => FolderRole.Archive,
        _ => null,
    };
}

/// <summary>
/// Translates <c>msgFilterRules.dat</c> where the vocabulary matches ours, and says why where
/// it does not — a rule silently changed in meaning is worse than one missing and named.
/// </summary>
public static class ThunderbirdFilters
{
    public sealed record Result(MailRule? Rule, string? Why);

    public static IReadOnlyList<Result> Parse(IReadOnlyList<string> lines)
    {
        var results = new List<Result>();

        string? name = null;
        var enabled = true;
        var conditionText = string.Empty;
        var actions = new List<(string Action, string Value)>();
        string? pendingAction = null;

        void Take()
        {
            if (name is null) return;

            // An action with no value of its own — Mark read, Mark flagged, Delete, Stop
            // execution — is still pending when the next filter's name arrives, and was thrown
            // away here rather than kept. Every filter whose last action takes no value was
            // therefore refused as "no action translates", except the last one in the file,
            // which the loop below flushes. Four of the seven actions that translate take no
            // value, so a real .dat lost most of what it carried and said something untrue
            // about why.
            if (pendingAction is not null)
            {
                actions.Add((pendingAction, string.Empty));
                pendingAction = null;
            }

            results.Add(Translate(name, enabled, conditionText, actions));
            name = null;
            enabled = true;
            conditionText = string.Empty;
            actions.Clear();
            pendingAction = null;
        }

        foreach (var line in lines)
        {
            var (key, value) = KeyValue(line);
            switch (key)
            {
                case "name":
                    Take();
                    name = value;
                    break;
                case "enabled":
                    enabled = value != "no";
                    break;
                case "condition":
                    conditionText = value;
                    break;
                case "action":
                    if (pendingAction is not null) actions.Add((pendingAction, string.Empty));
                    pendingAction = value;
                    break;
                case "actionValue":
                    if (pendingAction is not null)
                    {
                        actions.Add((pendingAction, value));
                        pendingAction = null;
                    }

                    break;
            }
        }

        // The file's last filter needs no flush of its own any more: Take does it, for that one
        // and for every filter before it.
        Take();
        return results;
    }

    private static (string Key, string Value) KeyValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 1) return (string.Empty, string.Empty);
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim().Trim('"');
        return (key, value);
    }

    private static Result Translate(string name, bool enabled, string conditionText, List<(string Action, string Value)> actions)
    {
        // "AND (subject,contains,x) AND (from,is,y)" — same-kind entries fold into one
        // any-of condition; a genuine mixed OR does not translate into our all-of rules and
        // is skipped by name rather than silently narrowed.
        var clauses = Clauses(conditionText);
        if (clauses.Count == 0 && conditionText.Trim() is not ("" or "ALL"))
        {
            return new Result(null, $"Filter “{name}”: its condition does not translate.");
        }

        var mixedOr = conditionText.Contains(" OR ", StringComparison.OrdinalIgnoreCase)
                      && clauses.Select(c => c.Kind).Distinct().Count() > 1;
        if (mixedOr)
        {
            return new Result(null, $"Filter “{name}”: OR across different fields does not translate.");
        }

        var conditions = clauses
            .GroupBy(c => c.Kind)
            .Select(g => new RuleCondition(g.Key) { Values = [.. g.Select(c => c.Value)] })
            .ToList();

        var ruleActions = new List<RuleAction>();
        foreach (var (action, value) in actions)
        {
            switch (action)
            {
                case "Move to folder":
                    ruleActions.Add(new RuleAction(RuleActionKind.MoveToFolder) { FolderName = FolderOf(value) });
                    break;
                case "Copy to folder":
                    ruleActions.Add(new RuleAction(RuleActionKind.CopyToFolder) { FolderName = FolderOf(value) });
                    break;
                case "Mark read":
                    ruleActions.Add(new RuleAction(RuleActionKind.MarkAsRead));
                    break;
                case "Mark flagged":
                    ruleActions.Add(new RuleAction(RuleActionKind.FlagForFollowUp));
                    break;
                case "Delete":
                    ruleActions.Add(new RuleAction(RuleActionKind.Delete));
                    break;
                case "AddTag":
                    ruleActions.Add(new RuleAction(RuleActionKind.AssignCategory) { Values = [value] });
                    break;
                case "Stop execution":
                    ruleActions.Add(new RuleAction(RuleActionKind.StopProcessing));
                    break;
                default:
                    return new Result(null, $"Filter “{name}”: the action “{action}” does not translate.");
            }
        }

        if (ruleActions.Count == 0)
        {
            return new Result(null, $"Filter “{name}”: no action translates.");
        }

        return new Result(new MailRule
        {
            Name = name,
            Enabled = enabled,
            Conditions = conditions,
            Actions = ruleActions,
        }, null);
    }

    private static List<(RuleConditionKind Kind, string Value)> Clauses(string condition)
    {
        var clauses = new List<(RuleConditionKind, string)>();

        var at = 0;
        while ((at = condition.IndexOf('(', at)) >= 0)
        {
            var end = condition.IndexOf(')', at);
            if (end < 0) break;

            var parts = condition[(at + 1)..end].Split(',', 3);
            at = end + 1;
            if (parts.Length != 3) continue;

            var field = parts[0].Trim().ToLowerInvariant();
            var op = parts[1].Trim().ToLowerInvariant();
            var value = parts[2].Trim();

            if (op is not ("contains" or "is" or "begins with" or "ends with")) return [];

            RuleConditionKind? kind = (field, op) switch
            {
                ("from", "is") => RuleConditionKind.From,
                ("from", _) => RuleConditionKind.SenderAddressContains,
                ("subject", _) => RuleConditionKind.SubjectContains,
                ("body", _) => RuleConditionKind.BodyContains,
                ("to", "is") or ("to or cc", "is") => RuleConditionKind.SentTo,
                ("to", _) or ("to or cc", _) or ("cc", _) => RuleConditionKind.RecipientAddressContains,
                _ => null,
            };

            if (kind is null) return [];
            clauses.Add((kind.Value, value));
        }

        return clauses;
    }

    /// <summary>The folder a Thunderbird action URI names: the last segment, URL-decoded.</summary>
    private static string FolderOf(string uri)
    {
        var last = uri.TrimEnd('/').Split('/').LastOrDefault() ?? uri;
        return Uri.UnescapeDataString(last);
    }
}
