using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// A task list and a notebook over the same engine the calendars use: what makes them different
/// is one payload each, and this is what proves the seam holds for a third and fourth noun.
/// </summary>
public class TaskSyncTests
{
    private static string Vtodo(string uid, string summary, string extra = "") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VTODO
        UID:{uid}
        DTSTAMP:20260801T000000Z
        DUE:20260820T170000Z
        SUMMARY:{summary}
        {extra}
        END:VTODO
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n");

    private static string Vjournal(string uid, string summary) => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VJOURNAL
        UID:{uid}
        DTSTAMP:20260801T000000Z
        DTSTART:20260816T090000Z
        SUMMARY:{summary}
        DESCRIPTION:{summary}\nand a second line
        END:VJOURNAL
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n");

    private static (PimStore Store, PimRepository Repository, Collection Collection) Fresh(FakeDavServer server, CollectionKind kind)
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var collection = repository.AddCollection(
            kind, kind == CollectionKind.Tasks ? "Tasks" : "Notes", "#0078D4", "you@example.net", server.CalendarUrl.ToString());
        return (store, repository, collection);
    }

    [Fact]
    public async Task ATaskListPullsItsTasks()
    {
        using var server = new FakeDavServer();
        server.Publish("one.ics", Vtodo("one@test", "Send the numbers", "PERCENT-COMPLETE:40\r\nSTATUS:IN-PROCESS"));
        server.Publish("two.ics", Vtodo("two@test", "Book the room"));

        using var client = new DavClient(handler: server);
        var (store, repository, list) = Fresh(server, CollectionKind.Tasks);
        using var _ = store;

        var result = await DavSync.For(client, repository, list).SyncAsync(list, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pulled);
        var items = repository.Items(list.Id);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(CollectionKind.Tasks, i.Kind));

        var task = PimTodoCodec.FromItem(items.Single(i => i.Summary == "Send the numbers"));
        Assert.Equal(TaskProgress.InProgress, task.Progress);
        Assert.Equal(40, task.PercentComplete);
        Assert.Equal(new DateTime(2026, 8, 20, 17, 0, 0), task.Due!.Wall);
    }

    [Fact]
    public async Task ATaskMadeHereGoesUpWrappedInACalendar()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, list) = Fresh(server, CollectionKind.Tasks);
        using var _ = store;

        var row = repository.AddItem(PimTodoCodec.ToItem(
            new TaskItem { Uid = "mine@mailbox", Summary = "Written here", Due = EventTime.Date(new DateOnly(2026, 8, 20)) },
            list.Id));
        repository.Queue(list.Id, row.Id, "put");

        var result = await DavSync.For(client, repository, list).SyncAsync(list, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        var sent = server.PayloadOf(repository.Item(row.Id)!.DavHref!);

        // A bare VTODO is what the store keeps and what every real server refuses; what goes up
        // is a whole VCALENDAR round it.
        Assert.Contains("BEGIN:VCALENDAR", sent, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VTODO", sent, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Written here", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotebookPullsItsNotes()
    {
        using var server = new FakeDavServer();
        server.Publish("one.ics", Vjournal("one@test", "Shopping"));

        using var client = new DavClient(handler: server);
        var (store, repository, notes) = Fresh(server, CollectionKind.Journal);
        using var _ = store;

        var result = await DavSync.For(client, repository, notes).SyncAsync(notes, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pulled);
        var note = PimJournalCodec.FromItem(Assert.Single(repository.Items(notes.Id)));
        Assert.Equal("Shopping", note.Summary);
        Assert.True(note.IsNote);
        Assert.Contains("second line", note.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoteMadeHereGoesUpWrappedInACalendarToo()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, notes) = Fresh(server, CollectionKind.Journal);
        using var _ = store;

        var note = new JournalEntry { Uid = "mine@mailbox" }.WithBody("Ring the plumber");
        var row = repository.AddItem(PimJournalCodec.ToItem(note, notes.Id));
        repository.Queue(notes.Id, row.Id, "put");

        var result = await DavSync.For(client, repository, notes).SyncAsync(notes, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        var sent = server.PayloadOf(repository.Item(row.Id)!.DavHref!);
        Assert.Contains("BEGIN:VCALENDAR", sent, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VJOURNAL", sent, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Ring the plumber", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditMadeHereAndNotYetSentIsNotOverwrittenByThePull()
    {
        using var server = new FakeDavServer();
        var href = server.Publish("one.ics", Vtodo("one@test", "As the server has it"));

        using var client = new DavClient(handler: server);
        var (store, repository, list) = Fresh(server, CollectionKind.Tasks);
        using var _ = store;
        var sync = DavSync.For(client, repository, list);

        await sync.SyncAsync(list, TestContext.Current.CancellationToken);
        var row = Assert.Single(repository.Items(list.Id));
        repository.UpdateItem(row with { Summary = "As it is here", SyncState = PimSyncState.Modified });

        // The server changes it as well, and the pull finds a row whose own change has not gone
        // up. Writing over it would settle a conflict by losing the edit.
        server.Publish("one.ics", Vtodo("one@test", "Changed on the server"));
        await sync.SyncAsync(repository.Collection(list.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal("As it is here", Assert.Single(repository.Items(list.Id)).Summary);
        Assert.True(server.Has(href));
    }
}
