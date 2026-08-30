using Mailbox.Contacts;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Plugins.Api;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Plugins;

/// <summary>
/// One plugin's view of the application: the <see cref="IPluginHost"/> its
/// <c>Initialize</c> receives. Every surface checks the manifest before it acts — a permission
/// the plugin did not declare refuses the call, records the use, and the Add-ins page says so.
/// Not a boundary — the manifest is disclosure, not enforcement — but it keeps an honest plugin honest and
/// makes a greedy one visible.
/// </summary>
internal sealed class HostFacade : IPluginHost, IPluginSettings, IPluginCommands,
    IPluginMail, IPluginPim, IPluginPipeline, IPluginReadingPane, IPluginColumns, IPluginAccounts
{
    private readonly PluginHost _host;
    private readonly PluginHost.Entry _entry;
    private readonly PluginHostServices _services;

    internal HostFacade(PluginHost host, PluginHost.Entry entry, PluginHostServices services)
    {
        _host = host;
        _entry = entry;
        _services = services;
    }

    private PluginManifest Manifest => _entry.Manifest!;

    public string PluginId => Manifest.Id;

    public string PluginDirectory => _entry.Directory;

    public Version ApiVersion => PluginApi.Version;

    public void Log(string message)
        => Mailbox.Core.Diagnostics.Log.Info($"[{PluginId}] {message}");

    public IPluginSettings Settings => this;

    public IPluginCommands Commands => Demand(PluginPermission.Ui);

    public IPluginMail Mail => Demand(PluginPermission.Mail);

    public IPluginPim Pim => Demand(PluginPermission.Pim);

    public IPluginPipeline Pipeline => this;

    public IPluginReadingPane ReadingPane => Demand(PluginPermission.Ui);

    public IPluginColumns Columns => Demand(PluginPermission.Ui);

    public IPluginAccounts Accounts => Demand(PluginPermission.Accounts);

    /// <summary>
    /// The read permission opens a surface; each write inside it checks its own. Checked at the
    /// property so a plugin fails at the first touch, where the missing name is obvious, rather
    /// than at whichever call happened to come first.
    /// </summary>
    private HostFacade Demand(string permission)
    {
        if (Manifest.Declares(permission)) return this;

        _host.RecordUndeclared(_entry, permission);
        throw new PluginPermissionException(permission);
    }

    // ---- Settings ------------------------------------------------------------------------------

    // Namespaced under the plugin's id, so one plugin cannot read or write another's — or the
    // application's, whose keys carry no "plugins." prefix.
    private string Key(string key) => $"plugins.{PluginId}.{key}";

    public string GetString(string key, string fallback = "") => _services.Settings.GetString(Key(key), fallback);

    public bool GetBool(string key, bool fallback = false) => _services.Settings.GetBool(Key(key), fallback);

    public double GetNumber(string key, double fallback = 0) => _services.Settings.GetNumber(Key(key), fallback);

    public void Set(string key, string value) => _services.Settings.Set(Key(key), value);

    public void Set(string key, bool value) => _services.Settings.Set(Key(key), value);

    public void Set(string key, double value) => _services.Settings.Set(Key(key), value);

    public void Remove(string key) => _services.Settings.Remove(Key(key));

    // ---- Commands and the ribbon ---------------------------------------------------------------

    public void Register(PluginCommand command, Action execute)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execute);

        var id = new CommandId($"plugin.{PluginId}.{command.Name}");

        var entry = new MailboxCommand
        {
            Id = id,
            Label = command.Label,
            Description = command.Description,
            Icon = command.Icon,
            Category = Manifest.Name,
            DefaultGesture = command.Gesture,

            // Additions never appear on the shipped ribbon, by the plugin contract; a plugin's own tab is
            // where its commands show, and Customize Ribbon is how they go anywhere else.
            InDefaultLayout = false,
            OwningPluginId = PluginId,
        };

        var run = execute;
        if (_services.RunOnUiThread is { } ui)
        {
            run = () => ui(execute);
        }

        _host.AddCommand(_entry, entry, run);
    }

    public void AddRibbonTab(PluginRibbonTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!PluginManifest.IsWellFormedId(tab.Name))
        {
            throw new ArgumentException(
                $"'{tab.Name}' is not a valid tab name; lowercase letters and digits only.");
        }

        // Refused with the accepted words named, because a misspelt module would otherwise be a
        // tab that silently never appears anywhere.
        var module = tab.Module.ToLowerInvariant() switch
        {
            "mail" => MailboxModule.Mail,
            "calendar" => MailboxModule.Calendar,
            "people" => MailboxModule.People,
            "tasks" => MailboxModule.Tasks,
            "notes" => MailboxModule.Notes,
            "journal" => MailboxModule.Journal,
            _ => throw new ArgumentException(
                $"'{tab.Module}' names no module; use mail, calendar, people, tasks, notes or journal."),
        };

        var tabId = $"plugin.{PluginId}.{tab.Name}";

        var groups = new List<RibbonGroup>();
        var simplified = new List<RibbonGroup>();

        foreach (var group in tab.Groups)
        {
            var commandIds = group.Commands
                .Select(name => new CommandId($"plugin.{PluginId}.{name}"))
                .ToList();

            foreach (var id in commandIds.Where(id => !_services.Commands.TryGet(id, out _)))
            {
                throw new InvalidOperationException(
                    $"The tab places '{id}', which is not registered. Register commands first.");
            }

            var groupId = $"{tabId}.{groups.Count}";

            groups.Add(new RibbonGroup
            {
                Id = groupId,
                Label = group.Label,
                Items = [.. commandIds.Select(id => RibbonItem.Large(id))],
            });

            // The Simplified bar is the ribbon a first run shows, so a plugin tab has a rendering
            // there too: the same clusters, drawn small and labelled as the reference draws its
            // own single row.
            simplified.Add(new RibbonGroup
            {
                Id = groupId,
                Label = group.Label,
                Items = [.. commandIds.Select(id => RibbonItem.Small(id))],
            });
        }

        var classic = new RibbonTab
        {
            Id = tabId,
            Label = tab.Label,
            Groups = groups,
        };

        _host.AddTab(_entry, module, classic, new SimplifiedBar { Groups = simplified });
    }

    // ---- Mail ----------------------------------------------------------------------------------

    IReadOnlyList<PluginAccount> IPluginMail.Accounts()
        => [.. _services.Mailboxes().Select(box => new PluginAccount(
            box.Address,
            box.Mail.Accounts().FirstOrDefault()?.DisplayName ?? box.Address))];

    IReadOnlyList<PluginFolder> IPluginMail.Folders(string account)
    {
        var (address, mail, accountId) = Box(account);
        return [.. mail.Folders(accountId).Select(f => new PluginFolder(
            address, f.Id, f.Name, f.Role == FolderRole.None ? null : f.Role.ToString().ToLowerInvariant()))];
    }

    IReadOnlyList<PluginMessageSummary> IPluginMail.Messages(string account, long folderId, int limit)
    {
        var (address, mail, _) = Box(account);
        return [.. mail.Messages(folderId, Math.Clamp(limit, 1, 1000))
            .Select(m => Summary(address, m))];
    }

    byte[]? IPluginMail.Raw(string account, long messageId)
        => Box(account).Mail.LoadRaw(messageId);

    void IPluginMail.MoveTo(string account, long messageId, long folderId)
        => Write(account).MoveMessage(messageId, folderId);

    void IPluginMail.Delete(string account, long messageId)
        => Write(account).DeleteMessage(messageId);

    void IPluginMail.SetRead(string account, long messageId, bool read)
        => Write(account).SetRead(messageId, read);

    internal static PluginMessageSummary Summary(string address, MessageSummary m)
        => new(address, m.Id, m.FolderId, m.Subject, m.DisplayFrom, m.Received, m.IsRead);

    private MailRepository Write(string account)
    {
        if (!Manifest.Declares(PluginPermission.MailWrite))
        {
            _host.RecordUndeclared(_entry, PluginPermission.MailWrite);
            throw new PluginPermissionException(PluginPermission.MailWrite);
        }

        return Box(account).Mail;
    }

    private (string Address, MailRepository Mail, long AccountId) Box(string account)
    {
        foreach (var (address, mail) in _services.Mailboxes())
        {
            if (string.Equals(address, account, StringComparison.OrdinalIgnoreCase))
            {
                var row = mail.Accounts().FirstOrDefault()
                    ?? throw new InvalidOperationException($"'{account}' has no account row.");
                return (address, mail, row.Id);
            }
        }

        throw new ArgumentException($"'{account}' names no account.", nameof(account));
    }

    // ---- PIM -----------------------------------------------------------------------------------

    IReadOnlyList<PluginCollection> IPluginPim.Collections()
        => [.. PimRepo().Collections().Select(c => new PluginCollection(c.Id, c.DisplayName, KindWord(c.Kind)))];

    IReadOnlyList<PluginItem> IPluginPim.Items(long collectionId)
        => [.. PimRepo().Items(collectionId).Select(i => new PluginItem(i.Id, i.Uid, i.RawPayload))];

    void IPluginPim.Save(long collectionId, string uid, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!Manifest.Declares(PluginPermission.PimWrite))
        {
            _host.RecordUndeclared(_entry, PluginPermission.PimWrite);
            throw new PluginPermissionException(PluginPermission.PimWrite);
        }

        var pim = PimRepo();
        var collection = pim.Collection(collectionId)
            ?? throw new ArgumentException($"{collectionId} names no collection.", nameof(collectionId));

        var existing = pim.ItemsByUid(collectionId, uid).FirstOrDefault(i => !i.IsOverride);

        // Through the same codecs the application's own editors save through, so a plugin-written
        // item has real columns — it lands on the calendar, in the to-do list, in the index —
        // rather than being raw text the views cannot read.
        var row = collection.Kind switch
        {
            CollectionKind.Events => PimEventCodec.ToItem(
                One(ICalendarCodec.Parse(text), "VEVENT"), collectionId, existing),
            CollectionKind.Tasks => PimTodoCodec.ToItem(
                One(TodoCodec.Parse(text), "VTODO"), collectionId, existing),
            CollectionKind.Journal => PimJournalCodec.ToItem(
                One(JournalCodec.Parse(text), "VJOURNAL"), collectionId, existing),
            CollectionKind.Contacts => PimContactCodec.ToItem(
                One(VCardCodec.Parse(text), "vCard"), collectionId, existing),
            _ => throw new InvalidOperationException($"Unknown collection kind {collection.Kind}."),
        };

        var written = existing is null ? pim.AddItem(row) : Update(pim, row);
        _services.QueuePut?.Invoke(written);

        static T One<T>(IReadOnlyList<T> parsed, string kind)
            => parsed.Count == 1
                ? parsed[0]
                : throw new ArgumentException($"The text must hold exactly one {kind}; it holds {parsed.Count}.");

        static PimItem Update(PimRepository pim, PimItem row)
        {
            pim.UpdateItem(row);
            return row;
        }
    }

    private PimRepository PimRepo()
        => _services.Pim ?? throw new InvalidOperationException("This host carries no PIM store.");

    private static string KindWord(CollectionKind kind) => kind switch
    {
        CollectionKind.Events => "calendar",
        CollectionKind.Tasks => "tasks",
        CollectionKind.Journal => "journal",
        _ => "addressbook",
    };

    // ---- Pipelines and the pane ----------------------------------------------------------------

    void IPluginPipeline.OnArrival(Func<ArrivingMessage, ArrivalAction> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        Demand(PluginPermission.Arrival);
        _host.AddArrivalHook(_entry, hook);
    }

    void IPluginPipeline.OnSending(Func<OutgoingMessage, SendDecision> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        Demand(PluginPermission.Sending);
        _host.AddSendingHook(_entry, hook);
    }

    void IPluginReadingPane.AddInfoBar(Func<PluginMessageSummary, PluginInfoBar?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _host.AddBarProvider(_entry, provider);
    }

    void IPluginColumns.Add(PluginColumn column, Func<PluginMessageSummary, string> value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);

        if (!PluginManifest.IsWellFormedId(column.Name))
        {
            throw new ArgumentException(
                $"'{column.Name}' is not a valid column name; lowercase letters and digits only.");
        }

        _host.AddColumn(
            _entry, $"plugin.{PluginId}.{column.Name}", column.Label,
            Math.Clamp(column.Width, 24, 600), value);
    }

    void IPluginAccounts.RegisterProvider(PluginAccountProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
        _host.AddProvider(_entry, provider);
    }
}
