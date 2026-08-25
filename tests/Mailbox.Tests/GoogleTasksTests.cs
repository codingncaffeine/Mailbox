using Mailbox.Google;
using Mailbox.Protocols.OAuth;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Google Tasks against <see cref="FakeGoogleTasks"/>: the round trip, the incremental poll, the
/// tombstones, and the collision this engine has to find for itself.
/// </summary>
/// <remarks>
/// The weight is on what a task loses and does not lose. Google's record is four fields wide and
/// this application's is not, so the tests that matter are the ones that prove a priority, a
/// category and a recurrence survive somebody ticking the task on a phone.
/// </remarks>
public class GoogleTasksTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>A token source that answers without a network, for the bearer the API wants.</summary>
    private sealed class Signed : IAccessTokenSource
    {
        public string UserName => "you@gmail.com";

        public Task<string> AccessTokenAsync(CancellationToken cancellation = default)
            => Task.FromResult("an-access-token");
    }

    private static (PimStore Store, PimRepository Repository, Collection List) Fresh(FakeGoogleTasks server)
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var list = repository.AddCollection(
            CollectionKind.Tasks, "My Tasks", "#0078D4", "you@gmail.com", GoogleTasks.UrlFor(server.DefaultList));
        return (store, repository, list);
    }

    private static GoogleTasksApi Api(FakeGoogleTasks server)
        => new(new Signed(), server, new Uri("https://tasks.googleapis.com/tasks/v1/"));

    private static TaskItem TaskOn(PimRepository repository, Collection list, string summaryContains)
    {
        var row = repository.Items(list.Id).First(i => i.Summary.Contains(summaryContains, StringComparison.OrdinalIgnoreCase));
        return PimTodoCodec.FromItem(row);
    }

    // ---- Which engine owns a collection ----

    [Fact]
    public void ATaskListOnGooglesHostIsOurs()
    {
        var google = new Collection(1, "you@gmail.com", CollectionKind.Tasks, "My Tasks", "", GoogleTasks.UrlFor("list-1"), null, null, null, true, false, false, 0);
        var dav = google with { DavUrl = "https://dav.example.net/calendars/you/tasks/" };
        var local = google with { DavUrl = null };

        Assert.True(GoogleTasks.Owns(google));
        Assert.False(GoogleTasks.Owns(dav));
        Assert.False(GoogleTasks.Owns(local));
        Assert.Equal("list-1", GoogleTasks.ListId(google));
        Assert.Empty(GoogleTasks.ListId(dav));
    }

    /// <summary>A list id with an awkward character survives the round trip through the URL.</summary>
    [Fact]
    public void AListIdIsReadBackOutOfItsUrl()
    {
        var url = GoogleTasks.UrlFor("MTIzNDU2Nzg5/OjA");
        var collection = new Collection(1, "you@gmail.com", CollectionKind.Tasks, "x", "", url, null, null, null, true, false, false, 0);

        Assert.Equal("MTIzNDU2Nzg5/OjA", GoogleTasks.ListId(collection));
    }

    // ---- The client ----

    [Fact]
    public async Task TheListsComeBack()
    {
        using var server = new FakeGoogleTasks();
        server.AddList("list-2", "Shopping");
        using var api = Api(server);

        var lists = await api.ListsAsync(Stop);

        Assert.Equal(2, lists.Count);
        Assert.Contains(lists, l => l.Title == "Shopping");
    }

    [Fact]
    public async Task APagedListIsFollowedToItsEnd()
    {
        using var server = new FakeGoogleTasks { PageSize = 2 };
        for (var i = 0; i < 7; i++) server.Publish($"Task {i}");
        using var api = Api(server);

        var tasks = await api.TasksAsync(server.DefaultList, null, Stop);

        Assert.Equal(7, tasks.Count);
        Assert.Equal(4, server.Requests.Count(r => r.StartsWith("GET", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Without a bearer the API answers 401, and the difference between that and a task being
    /// missing is what decides whether the reader is asked to sign in again.
    /// </summary>
    [Fact]
    public async Task AnUnauthenticatedRequestSaysSoDistinctly()
    {
        using var server = new FakeGoogleTasks();
        using var api = new GoogleTasksApi(new Unsigned(), server, new Uri("https://tasks.googleapis.com/tasks/v1/"));

        var refused = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListsAsync(Stop));

        Assert.True(refused.NeedsSignIn);
        Assert.False(refused.Gone);
        Assert.False(refused.WorthRetrying);
    }

    private sealed class Unsigned : IAccessTokenSource
    {
        public string UserName => "you@gmail.com";

        public Task<string> AccessTokenAsync(CancellationToken cancellation = default)
            => Task.FromResult(string.Empty);
    }

    [Fact]
    public async Task AQuotaIsWorthRetryingAndAMissingTaskIsNot()
    {
        using var server = new FakeGoogleTasks { NextFailure = System.Net.HttpStatusCode.TooManyRequests };
        using var api = Api(server);

        var quota = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListsAsync(Stop));
        Assert.True(quota.WorthRetrying);
        Assert.False(quota.NeedsSignIn);

        var missing = await Assert.ThrowsAsync<GoogleApiException>(
            () => api.TasksAsync("no-such-list", null, Stop));
        Assert.True(missing.Gone);
        Assert.False(missing.WorthRetrying);
    }

    /// <summary>Deleting something already gone is the outcome that was asked for, not a failure.</summary>
    [Fact]
    public async Task DeletingATaskThatIsAlreadyGoneIsNotAnError()
    {
        using var server = new FakeGoogleTasks();
        using var api = Api(server);

        await api.DeleteAsync(server.DefaultList, "task-never", Stop);
    }

    // ---- The codec ----

    [Fact]
    public void ADueDateIsADateAndNotAnInstant()
    {
        var task = new TaskItem { Uid = "u", Summary = "Ring back", Due = EventTime.At(new DateTime(2026, 8, 20, 17, 0, 0), "Europe/Berlin") };

        // Due on the 20th in Berlin is due on the 20th, whatever the UTC instant's date is.
        Assert.Equal(new DateOnly(2026, 8, 20), GoogleTaskCodec.ToGoogle(task).Due);
    }

    /// <summary>
    /// The one that matters. Google's record has no room for a priority, a category or a
    /// recurrence, so ticking a task on a phone must not be a way to lose all three.
    /// </summary>
    [Fact]
    public void WhatGoogleCannotCarryIsNotLostWhenGoogleAnswers()
    {
        var mine = new TaskItem
        {
            Uid = "u",
            Summary = "Book the hall",
            Description = "Ask about the piano",
            Urgency = TaskUrgency.High,
            Categories = ["Wedding", "Urgent"],
            Rrule = "FREQ=WEEKLY;BYDAY=MO",
            ReminderMinutes = 30,
            IsPrivate = true,
            Start = EventTime.Date(new DateOnly(2026, 8, 18)),
            Due = EventTime.Date(new DateOnly(2026, 8, 20)),
        };

        var theirs = new GoogleTask
        {
            Id = "task-1",
            Title = "Book the hall",
            Notes = "Ask about the piano",
            Status = GoogleTask.CompletedStatus,
            Due = new DateOnly(2026, 8, 20),
            Completed = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero),
        };

        var merged = GoogleTaskCodec.Merge(mine, theirs);

        Assert.True(merged.IsComplete);
        Assert.Equal(100, merged.PercentComplete);
        Assert.Equal(theirs.Completed, merged.CompletedUtc);

        // Everything Google was never told about is still here.
        Assert.Equal(TaskUrgency.High, merged.Urgency);
        Assert.Equal(["Wedding", "Urgent"], merged.Categories);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", merged.Rrule);
        Assert.Equal(30, merged.ReminderMinutes);
        Assert.True(merged.IsPrivate);
        Assert.Equal(new DateOnly(2026, 8, 18), DateOnly.FromDateTime(merged.Start!.Wall));
    }

    [Fact]
    public void ATaskThatOnlyGoogleHasIsBuiltFromWhatGoogleKnows()
    {
        var made = GoogleTaskCodec.Merge(null, new GoogleTask
        {
            Id = "task-9",
            Title = "Milk",
            Due = new DateOnly(2026, 8, 21),
        });

        Assert.Equal("Milk", made.Summary);
        Assert.Equal("task-9@tasks.google.com", made.Uid);
        Assert.False(made.IsComplete);
        Assert.Equal(new DateOnly(2026, 8, 21), DateOnly.FromDateTime(made.Due!.Wall));
    }

    [Fact]
    public void ClearingADueDateThereClearsItHere()
    {
        var mine = new TaskItem { Uid = "u", Summary = "Milk", Due = EventTime.Date(new DateOnly(2026, 8, 21)) };

        var merged = GoogleTaskCodec.Merge(mine, new GoogleTask { Id = "t", Title = "Milk", Due = null });

        Assert.Null(merged.Due);
    }

    [Fact]
    public void OnlyTheFourSharedFieldsCountAsADifference()
    {
        var task = new TaskItem { Uid = "u", Summary = "Milk", Urgency = TaskUrgency.High };
        var same = GoogleTaskCodec.ToGoogle(task, "t");

        Assert.False(GoogleTaskCodec.Differs(task with { Urgency = TaskUrgency.Low }, same));
        Assert.True(GoogleTaskCodec.Differs(task with { Summary = "Bread" }, same));
    }

    // ---- The sync ----

    [Fact]
    public async Task WhatIsThereArrivesHere()
    {
        using var server = new FakeGoogleTasks();
        server.Publish("Book the hall", "Ask about the piano", new DateOnly(2026, 8, 20));
        server.Publish("Milk");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);

        var result = await new GoogleTasksSync(api, repository).SyncAsync(list, Stop);

        Assert.Equal(2, result.Pulled);
        Assert.Equal(2, repository.Items(list.Id).Count);

        var hall = TaskOn(repository, list, "hall");
        Assert.Equal("Ask about the piano", hall.Description);
        Assert.Equal(new DateOnly(2026, 8, 20), DateOnly.FromDateTime(hall.Due!.Wall));

        store.Dispose();
    }

    /// <summary>The second poll asks only about what moved, which is the whole of incremental here.</summary>
    [Fact]
    public async Task ASecondPollAsksOnlyAboutWhatChanged()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");
        server.Publish("Milk");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);

        await sync.SyncAsync(list, Stop);

        server.Tick(TimeSpan.FromHours(1));
        server.Edit(id, title: "Book the hall and the band");

        var second = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal(1, second.Pulled);
        Assert.Equal("Book the hall and the band", TaskOn(repository, list, "hall").Summary);

        // Nothing moved for a third one to find.
        server.Tick(TimeSpan.FromHours(1));
        var third = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);
        Assert.Equal(0, third.Pulled);

        store.Dispose();
    }

    /// <summary>
    /// A completed task is asked for on purpose. Without <c>showCompleted</c> it simply vanishes
    /// from the answer, and a poll would take that for a task nobody had touched.
    /// </summary>
    [Fact]
    public async Task TickingItThereTicksItHere()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        server.Tick(TimeSpan.FromHours(1));
        server.Edit(id, complete: true);
        await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        var done = TaskOn(repository, list, "hall");
        Assert.True(done.IsComplete);
        Assert.Equal(100, done.PercentComplete);
        Assert.NotNull(done.CompletedUtc);

        store.Dispose();
    }

    [Fact]
    public async Task ADeletionThereRemovesItHere()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");
        server.Publish("Milk");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        server.Tick(TimeSpan.FromHours(1));
        server.Delete(id);
        var second = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal(1, second.Removed);
        Assert.Single(repository.Items(list.Id));

        store.Dispose();
    }

    [Fact]
    public async Task ATaskMadeHereGoesUpAndKeepsItsIdentity()
    {
        using var server = new FakeGoogleTasks();
        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);

        var made = new TaskItem { Uid = "local-1", Summary = "Ring the caterer", Due = EventTime.Date(new DateOnly(2026, 8, 22)) };
        var row = repository.AddItem(PimTodoCodec.ToItem(made, list.Id) with { RawPayload = TodoCodec.Serialize(made) });
        repository.Queue(list.Id, row.Id, "put");

        var result = await sync.SyncAsync(list, Stop);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, server.Count());

        // The row now knows its Google id, so the next change is a PATCH and not a second task.
        var stored = repository.Item(row.Id)!;
        Assert.False(string.IsNullOrEmpty(stored.DavHref));
        Assert.Equal(PimSyncState.Synced, stored.SyncState);
        Assert.Empty(repository.Queued(list.Id));

        var theirs = server.Task(stored.DavHref!)!.Value;
        Assert.Equal("Ring the caterer", theirs.Title);
        Assert.Equal(new DateOnly(2026, 8, 22), theirs.Due);

        store.Dispose();
    }

    [Fact]
    public async Task AnEditHereReachesTheServerAsAChangeRatherThanACopy()
    {
        using var server = new FakeGoogleTasks();
        server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        var edited = PimTodoCodec.FromItem(row) with { Summary = "Book the hall and the band" };
        repository.UpdateItem(PimTodoCodec.ToItem(edited, list.Id, row, PimSyncState.Modified) with
        {
            RawPayload = TodoCodec.Serialize(edited),
        });
        repository.Queue(list.Id, row.Id, "put");

        server.Tick(TimeSpan.FromHours(1));
        var result = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, server.Count());
        Assert.Equal("Book the hall and the band", server.Task(row.DavHref!)!.Value.Title);

        store.Dispose();
    }

    [Fact]
    public async Task ADeleteHereReachesTheServer()
    {
        using var server = new FakeGoogleTasks();
        server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        repository.SetSyncState(row.Id, PimSyncState.Deleted);
        repository.Queue(list.Id, row.Id, "delete");

        server.Tick(TimeSpan.FromHours(1));
        var result = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, server.Count());
        Assert.Empty(repository.Items(list.Id));

        store.Dispose();
    }

    /// <summary>
    /// The case this engine exists to handle. There is no <c>If-Match</c> to refuse the write, so
    /// the collision has to be found by comparing — and without that the phone's version is
    /// overwritten silently.
    /// </summary>
    [Fact]
    public async Task AChangeOnBothSidesIsAConflictRatherThanAnOverwrite()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        var mine = PimTodoCodec.FromItem(row) with { Summary = "Book the hall (mine)" };
        repository.UpdateItem(PimTodoCodec.ToItem(mine, list.Id, row, PimSyncState.Modified) with
        {
            RawPayload = TodoCodec.Serialize(mine),
        });
        repository.Queue(list.Id, row.Id, "put");

        server.Tick(TimeSpan.FromHours(1));
        server.Edit(id, title: "Book the hall (theirs)");

        var result = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Book the hall (theirs)", conflict.TheirTitle);
        Assert.Equal(0, result.Pushed);

        // Neither copy was written over: the local edit is still here and the server's still there.
        Assert.Equal("Book the hall (mine)", TaskOn(repository, list, "hall").Summary);
        Assert.Equal("Book the hall (theirs)", server.Task(id)!.Value.Title);

        store.Dispose();
    }

    [Fact]
    public async Task KeepingTheLocalCopySendsItOnTheNextPoll()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        var mine = PimTodoCodec.FromItem(row) with { Summary = "Book the hall (mine)" };
        repository.UpdateItem(PimTodoCodec.ToItem(mine, list.Id, row, PimSyncState.Modified) with
        {
            RawPayload = TodoCodec.Serialize(mine),
        });
        repository.Queue(list.Id, row.Id, "put");

        server.Tick(TimeSpan.FromHours(1));
        server.Edit(id, title: "Book the hall (theirs)");
        var conflict = Assert.Single((await sync.SyncAsync(repository.Collection(list.Id)!, Stop)).Conflicts);

        sync.KeepLocal(conflict);
        server.Tick(TimeSpan.FromHours(1));
        var settled = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Empty(settled.Conflicts);
        Assert.Equal(1, settled.Pushed);
        Assert.Equal("Book the hall (mine)", server.Task(id)!.Value.Title);

        store.Dispose();
    }

    [Fact]
    public async Task KeepingTheServersCopyDropsTheQueuedChange()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        var mine = PimTodoCodec.FromItem(row) with { Summary = "Book the hall (mine)" };
        repository.UpdateItem(PimTodoCodec.ToItem(mine, list.Id, row, PimSyncState.Modified) with
        {
            RawPayload = TodoCodec.Serialize(mine),
        });
        repository.Queue(list.Id, row.Id, "put");

        server.Tick(TimeSpan.FromHours(1));
        server.Edit(id, title: "Book the hall (theirs)");
        var conflict = Assert.Single((await sync.SyncAsync(repository.Collection(list.Id)!, Stop)).Conflicts);

        sync.KeepServer(conflict);
        Assert.Empty(repository.Queued(list.Id));

        server.Tick(TimeSpan.FromHours(1));
        await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal("Book the hall (theirs)", TaskOn(repository, list, "hall").Summary);

        store.Dispose();
    }

    /// <summary>
    /// A change waiting to go out for a task somebody deleted has nowhere to land. The row goes
    /// rather than being resurrected as a new task, which is what sending it again would do.
    /// </summary>
    [Fact]
    public async Task AnEditToATaskDeletedElsewhereDoesNotResurrectIt()
    {
        using var server = new FakeGoogleTasks();
        var id = server.Publish("Book the hall");

        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);
        await sync.SyncAsync(list, Stop);

        var row = repository.Items(list.Id).Single();
        repository.Queue(list.Id, row.Id, "put");

        server.Tick(TimeSpan.FromHours(1));
        server.Delete(id);

        await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Empty(repository.Items(list.Id));
        Assert.Equal(0, server.Count());

        store.Dispose();
    }

    /// <summary>
    /// A quota leaves the change queued with the reason on it, rather than losing it or stopping
    /// the whole list. The next poll sends it.
    /// </summary>
    [Fact]
    public async Task AChangeRefusedForNowStaysQueuedAndGoesLater()
    {
        using var server = new FakeGoogleTasks();
        var (store, repository, list) = Fresh(server);
        using var api = Api(server);
        var sync = new GoogleTasksSync(api, repository);

        var made = new TaskItem { Uid = "local-1", Summary = "Ring the caterer" };
        var row = repository.AddItem(PimTodoCodec.ToItem(made, list.Id) with { RawPayload = TodoCodec.Serialize(made) });
        repository.Queue(list.Id, row.Id, "put");

        // The pull goes through and the push meets the refusal.
        server.NextWriteFailure = System.Net.HttpStatusCode.TooManyRequests;
        var refused = await sync.SyncAsync(list, Stop);

        Assert.Equal(0, refused.Pushed);
        Assert.Equal(0, server.Count());

        var waiting = Assert.Single(repository.Queued(list.Id));
        Assert.Equal(1, waiting.Attempts);
        Assert.Contains("429", waiting.LastError);

        var later = await sync.SyncAsync(repository.Collection(list.Id)!, Stop);

        Assert.Equal(1, later.Pushed);
        Assert.Equal(1, server.Count());
        Assert.Empty(repository.Queued(list.Id));

        store.Dispose();
    }

    // ---- The lists themselves ----

    [Fact]
    public async Task ListsMadeElsewhereTurnUpAndListsRemovedGoAway()
    {
        using var server = new FakeGoogleTasks();
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        using var api = Api(server);

        var added = await GoogleTasks.RefreshListsAsync(api, repository, "you@gmail.com", Stop);
        Assert.Equal(1, added);

        server.AddList("list-2", "Shopping");
        await GoogleTasks.RefreshListsAsync(api, repository, "you@gmail.com", Stop);
        Assert.Equal(2, repository.Collections().Count(GoogleTasks.Owns));

        server.RemoveList("list-2");
        await GoogleTasks.RefreshListsAsync(api, repository, "you@gmail.com", Stop);
        Assert.Single(repository.Collections(), GoogleTasks.Owns);

        store.Dispose();
    }

    [Fact]
    public async Task ARenameAtGoogleReachesTheCollection()
    {
        using var server = new FakeGoogleTasks(listTitle: "My Tasks");
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        using var api = Api(server);

        await GoogleTasks.RefreshListsAsync(api, repository, "you@gmail.com", Stop);

        server.RemoveList(server.DefaultList);
        server.AddList(server.DefaultList, "The Wedding");
        await GoogleTasks.RefreshListsAsync(api, repository, "you@gmail.com", Stop);

        Assert.Equal("The Wedding", repository.Collections().Single(GoogleTasks.Owns).DisplayName);

        store.Dispose();
    }
}
