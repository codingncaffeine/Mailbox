using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>The PIM store: collections, items, and the items a span of time can show.</summary>
public class PimRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static (PimStore Store, PimRepository Repo) Fresh()
    {
        var store = PimStore.Transient();
        return (store, new PimRepository(store));
    }

    private static PimItem Event(long calendar, string uid, string summary, DateTimeOffset start, TimeSpan length, string? rrule = null, bool allDay = false)
        => new()
        {
            CollectionId = calendar,
            Uid = uid,
            Kind = CollectionKind.Events,
            RawPayload = $"BEGIN:VEVENT\r\nUID:{uid}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\n",
            Summary = summary,
            StartsUtc = start,
            EndsUtc = start + length,
            StartsLocal = start.ToString("yyyy-MM-dd'T'HH:mm:ss"),
            EndsLocal = (start + length).ToString("yyyy-MM-dd'T'HH:mm:ss"),
            TzId = "Europe/London",
            AllDay = allDay,
            Rrule = rrule,
            LastModified = Now,
            SyncState = PimSyncState.New,
        };

    [Fact]
    public void AFreshStoreIsMigratedAndTheDefaultCalendarIsMadeOnFirstAsk()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        Assert.Equal(PimMigrations.Latest, store.Version);
        Assert.Empty(repo.Collections());

        var calendar = repo.DefaultCalendar();
        Assert.Equal("Calendar", calendar.DisplayName);
        Assert.True(calendar.IsDefault);
        Assert.True(calendar.IsLocal);
        Assert.Equal(CollectionKind.Events, calendar.Kind);
        // Asked again, it is the same one.
        Assert.Equal(calendar.Id, repo.DefaultCalendar().Id);
        Assert.Single(repo.Collections(CollectionKind.Events));
    }

    [Fact]
    public void CollectionsAreMadeRenamedColouredAndDefaultedPerKind()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var home = repo.AddCollection(CollectionKind.Events, "Home", "#0F6CBD");
        var work = repo.AddCollection(CollectionKind.Events, "Work", "#C00000", account: "you@example.com", davUrl: "https://dav.example.com/cal/work/");
        var tasks = repo.AddCollection(CollectionKind.Tasks, "Tasks");

        Assert.True(home.IsDefault);
        Assert.False(work.IsDefault);
        Assert.False(work.IsLocal);
        Assert.True(tasks.IsDefault);
        Assert.Equal(["Home", "Work"], repo.Collections(CollectionKind.Events).Select(c => c.DisplayName));

        repo.SetDefaultCollection(work.Id);
        Assert.Equal("Work", repo.DefaultCalendar().DisplayName);
        Assert.False(repo.Collection(home.Id)!.IsDefault);
        Assert.True(repo.Collection(tasks.Id)!.IsDefault);

        repo.RenameCollection(home.Id, "Personal");
        repo.SetCollectionColor(home.Id, "#00B050");
        repo.SetCollectionVisible(home.Id, false);
        var back = repo.Collection(home.Id)!;
        Assert.Equal(("Personal", "#00B050", false), (back.DisplayName, back.Color, back.IsVisible));

        repo.SetCollectionSync(work.Id, "ctag-1", "token-1");
        Assert.Equal(("ctag-1", "token-1"), (repo.Collection(work.Id)!.Ctag, repo.Collection(work.Id)!.SyncToken));
    }

    [Fact]
    public void AnItemRoundTripsWithEveryColumn()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var calendar = repo.DefaultCalendar();
        var start = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var item = Event(calendar.Id, "uid-1", "Stand-up", start, TimeSpan.FromMinutes(30), "FREQ=WEEKLY;BYDAY=MO,WE,FR") with
        {
            Description = "Notes",
            Location = "Room 4",
            Busy = "tentative",
            ReminderMinutes = 15,
            Categories = "Blue,Green",
            Organizer = "you@example.com",
            Sequence = 2,
        };

        var stored = repo.AddItem(item);
        Assert.True(stored.Id > 0);
        var back = repo.Item(stored.Id)!;
        Assert.Equal(item with { Id = stored.Id }, back);

        repo.UpdateItem(back with { Summary = "Daily stand-up", SyncState = PimSyncState.Modified });
        Assert.Equal("Daily stand-up", repo.Item(stored.Id)!.Summary);
        Assert.Equal(PimSyncState.Modified, repo.Item(stored.Id)!.SyncState);

        repo.SetSyncState(stored.Id, PimSyncState.Synced, etag: "\"e1\"", href: "/cal/uid-1.ics");
        var synced = repo.Item(stored.Id)!;
        Assert.Equal((PimSyncState.Synced, "\"e1\"", "/cal/uid-1.ics"), (synced.SyncState, synced.Etag, synced.DavHref));
    }

    [Fact]
    public void ASpanShowsWhatTouchesItAndEveryRepeatingMasterThatStartedBefore()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var calendar = repo.DefaultCalendar();
        var day = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

        repo.AddItem(Event(calendar.Id, "before", "Yesterday", day.AddDays(-1).AddHours(9), TimeSpan.FromHours(1)));
        repo.AddItem(Event(calendar.Id, "in", "Today", day.AddHours(9), TimeSpan.FromHours(1)));
        repo.AddItem(Event(calendar.Id, "spans", "Overnight", day.AddHours(-2), TimeSpan.FromHours(4)));
        repo.AddItem(Event(calendar.Id, "after", "Tomorrow", day.AddDays(1).AddHours(9), TimeSpan.FromHours(1)));
        repo.AddItem(Event(calendar.Id, "weekly", "Weekly", day.AddDays(-30).AddHours(10), TimeSpan.FromHours(1), "FREQ=WEEKLY"));
        repo.AddItem(Event(calendar.Id, "later-series", "Starts next month", day.AddDays(30).AddHours(10), TimeSpan.FromHours(1), "FREQ=DAILY"));
        var master = repo.AddItem(Event(calendar.Id, "series", "Series", day.AddDays(-7).AddHours(14), TimeSpan.FromHours(1), "FREQ=DAILY"));
        repo.AddItem(Event(calendar.Id, "series", "Moved occurrence", day.AddDays(3).AddHours(16), TimeSpan.FromHours(1)) with { IsOverride = true, RecurrenceId = "20260823T140000Z" });

        var found = repo.ItemsBetween(day, day.AddDays(1)).Select(i => i.Uid).ToList();
        Assert.Contains("in", found);
        Assert.Contains("spans", found);
        Assert.Contains("weekly", found);
        Assert.Contains("series", found);
        Assert.Equal(2, found.Count(u => u == "series"));   // the master and its override
        Assert.DoesNotContain("before", found);
        Assert.DoesNotContain("after", found);
        Assert.DoesNotContain("later-series", found);

        // A hidden calendar's items are not shown; asked for by id, they are.
        var hidden = repo.AddCollection(CollectionKind.Events, "Hidden");
        repo.SetCollectionVisible(hidden.Id, false);
        repo.AddItem(Event(hidden.Id, "hidden", "Hidden", day.AddHours(11), TimeSpan.FromHours(1)));
        Assert.DoesNotContain("hidden", repo.ItemsBetween(day, day.AddDays(1)).Select(i => i.Uid));
        Assert.Contains("hidden", repo.ItemsBetween(day, day.AddDays(1), [hidden.Id]).Select(i => i.Uid));

        // Deleting the master takes its override with it.
        Assert.Equal(2, repo.DeleteItem(master.Id));
        Assert.Empty(repo.ItemsByUid(calendar.Id, "series"));
    }

    [Fact]
    public void ItemsAreSearchableBySummaryLocationAndAttendee()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var calendar = repo.DefaultCalendar();
        var start = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var review = repo.AddItem(Event(calendar.Id, "r", "Quarterly review", start, TimeSpan.FromHours(1)) with { Location = "Boardroom" });
        repo.AddItem(Event(calendar.Id, "l", "Lunch", start.AddHours(3), TimeSpan.FromHours(1)));
        repo.SetAttendees(review.Id, [new PimRepository.Attendee("dana@example.com", "Dana Whitfield", "REQ-PARTICIPANT", "ACCEPTED", true)]);

        Assert.Equal(["Quarterly review"], repo.Search("quart").Select(i => i.Summary));
        Assert.Equal(["Quarterly review"], repo.Search("boardroom").Select(i => i.Summary));
        Assert.Equal(["Quarterly review"], repo.Search("dana").Select(i => i.Summary));
        Assert.Equal(["Lunch"], repo.Search("lun").Select(i => i.Summary));
        Assert.Empty(repo.Search("dinner"));
        Assert.Equal(["dana@example.com"], repo.Attendees(review.Id).Select(a => a.Address));
    }

    [Fact]
    public void APrivateItemStaysPrivateThroughTheStore()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var calendar = repo.AddCollection(CollectionKind.Events, "Mine");

        var kept = repo.AddItem(Event(calendar.Id, "p", "Nobody's business", Now, TimeSpan.FromHours(1)) with { IsPrivate = true });
        Assert.True(repo.Item(kept.Id)!.IsPrivate);

        // And it comes off again: the column is written on every update, not only when it is set.
        repo.UpdateItem(kept with { IsPrivate = false });
        Assert.False(repo.Item(kept.Id)!.IsPrivate);

        // The default is public, which is what the standard's absent CLASS means.
        var ordinary = repo.AddItem(Event(calendar.Id, "o", "Everybody's business", Now, TimeSpan.FromHours(1)));
        Assert.False(repo.Item(ordinary.Id)!.IsPrivate);
    }

    [Fact]
    public void RemovingACollectionRemovesItsItems()
    {
        var (store, repo) = Fresh();
        using var _ = store;
        var calendar = repo.AddCollection(CollectionKind.Events, "Temp");
        var item = repo.AddItem(Event(calendar.Id, "t", "Temp", Now, TimeSpan.FromHours(1)));
        repo.RemoveCollection(calendar.Id);
        Assert.Null(repo.Item(item.Id));
        Assert.Empty(repo.Collections());
    }
}
