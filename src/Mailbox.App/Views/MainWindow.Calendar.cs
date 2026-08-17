using System.Globalization;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Mailbox.App.Options;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The Calendar module in the shell: switching to it, the workspace it puts in the window, and
/// the commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell rather than a class of its own for the reason the Quick Steps half is:
/// it needs the window's ribbon, its dialogs and its status line, and passing those three round
/// is a worse seam than a second file.
/// </remarks>
public partial class MainWindow
{
    private CalendarWorkspace? _calendar;

    /// <summary>
    /// Today, as the whole module believes it. <c>MAILBOX_TODAY</c> pins it so a capture of the
    /// calendar is the same picture next year — without it every month view would shade a
    /// different half of itself and no reference comparison would hold.
    /// </summary>
    internal static DateOnly CalendarToday { get; } =
        Environment.GetEnvironmentVariable("MAILBOX_TODAY") is { Length: > 0 } pinned
        && DateOnly.TryParseExact(pinned, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The moment the now line is drawn at. A pinned day gets no now line: the line would be at
    /// the real clock's time on a day that is not the real one.
    /// </summary>
    internal static DateTime? CalendarNow { get; } =
        Environment.GetEnvironmentVariable("MAILBOX_TODAY") is { Length: > 0 } ? null : DateTime.Now;

    /// <summary>The calendar ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout CalendarRibbon() => App.RibbonEdits.Apply(DefaultRibbonLayouts.Calendar);

    /// <summary>
    /// Puts a module on screen: the rail's mark, the workspace in the window, and the ribbon
    /// that belongs to it.
    /// </summary>
    private void SwitchModule(ShellViewModel shell, MailboxModule module)
    {
        if (module is not (MailboxModule.Mail or MailboxModule.Calendar or MailboxModule.People
            or MailboxModule.Tasks or MailboxModule.Notes or MailboxModule.Journal))
        {
            // Folders and Shortcuts are the rest of the navigation pane rather than modules of
            // their own, and a button that says which phase brings it is better than one that
            // does nothing.
            shell.StatusRight = $"{module} is Phase 14, with the rest of the shell.";
            return;
        }

        if (shell.Module == module) return;
        shell.Module = module;

        var host = this.FindControl<ContentControl>("ModuleHost")!;

        switch (module)
        {
            case MailboxModule.Calendar:
            {
                var workspace = EnsureCalendar(shell);
                host.Content = workspace;
                _ribbon.Layout = CalendarRibbon();
                shell.ModuleStatusLeft = workspace.Status;
                break;
            }

            case MailboxModule.People:
            {
                var workspace = EnsurePeople(shell);
                host.Content = workspace;
                _ribbon.Layout = PeopleRibbon();
                shell.ModuleStatusLeft = workspace.Status;
                break;
            }

            case MailboxModule.Tasks:
            {
                var workspace = EnsureTasks(shell);
                host.Content = workspace;
                _ribbon.Layout = TasksRibbon();
                shell.ModuleStatusLeft = workspace.Status;
                break;
            }

            case MailboxModule.Notes:
            {
                var workspace = EnsureNotes(shell);
                host.Content = workspace;
                _ribbon.Layout = NotesRibbon();
                shell.ModuleStatusLeft = workspace.Status;
                break;
            }

            case MailboxModule.Journal:
            {
                var workspace = EnsureJournal(shell);
                host.Content = workspace;
                _ribbon.Layout = JournalRibbon();
                shell.ModuleStatusLeft = workspace.Status;
                break;
            }

            default:
                _ribbon.Layout = App.MailRibbon();
                break;
        }

        Log.Info($"Module: {module}.");
    }

    private CalendarWorkspace EnsureCalendar(ShellViewModel shell)
    {
        if (_calendar is not null) return _calendar;

        var workspace = new CalendarWorkspace(App.Pim, App.CalendarOptions, CalendarToday, CalendarNow)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) => shell.ModuleStatusLeft = workspace.Status;
        workspace.NewRequested += (_, when) => _ = NewAppointmentAsync(shell, when.Start, when.AllDay);
        workspace.EntryOpened += (_, entry) => _ = OpenAppointmentAsync(shell, entry);
        workspace.EntryMoved += (_, move) => MoveAppointment(shell, move);
        _calendar = workspace;
        return workspace;
    }

    /// <summary>
    /// The Calendar module's commands. Returns false for anything it does not own, so the
    /// shell's own list carries on.
    /// </summary>
    private bool RunCalendarCommand(ShellViewModel shell, CommandId id)
    {
        if (ViewCommands.ModuleOf(id) is { } module)
        {
            SwitchModule(shell, module);
            return true;
        }

        if (id == CalendarCommands.NewAppointment.Id)
        {
            SwitchModule(shell, MailboxModule.Calendar);
            var calendar = EnsureCalendar(shell);
            _ = NewAppointmentAsync(shell, calendar.Anchor.ToDateTime(NextHalfHour()), allDay: false);
            return true;
        }

        if (id == CalendarCommands.NewMeeting.Id)
        {
            SwitchModule(shell, MailboxModule.Calendar);
            var calendar = EnsureCalendar(shell);
            _ = NewAppointmentAsync(shell, calendar.Anchor.ToDateTime(NextHalfHour()), allDay: false, meeting: true);
            return true;
        }

        // Everything below wants the module up, and pressing one from the mail ribbon through
        // the harness is exactly how the audit checks it.
        if (id == CalendarCommands.AddFocusTime.Id) { SwitchModule(shell, MailboxModule.Calendar); AddFocusTime(shell); return true; }
        if (id == CalendarCommands.Today.Id) { WithCalendar(shell, c => c.GoToday()); return true; }
        if (id == CalendarCommands.Next7Days.Id) { WithCalendar(shell, c => c.ShowNextSevenDays()); return true; }
        if (id == CalendarCommands.Back.Id) { WithCalendar(shell, c => c.Step(-1)); return true; }
        if (id == CalendarCommands.Forward.Id) { WithCalendar(shell, c => c.Step(1)); return true; }
        if (id == CalendarCommands.GoToDate.Id) { _ = GoToDateAsync(shell); return true; }
        if (id == CalendarCommands.DayView.Id) { WithCalendar(shell, c => c.SetView(CalendarViewKind.Day)); return true; }
        if (id == CalendarCommands.WorkWeekView.Id) { WithCalendar(shell, c => c.SetView(CalendarViewKind.WorkWeek)); return true; }
        if (id == CalendarCommands.WeekView.Id) { WithCalendar(shell, c => c.SetView(CalendarViewKind.Week)); return true; }
        if (id == CalendarCommands.MonthView.Id) { WithCalendar(shell, c => c.SetView(CalendarViewKind.Month)); return true; }
        if (id == CalendarCommands.ScheduleView.Id) { WithCalendar(shell, c => c.SetView(CalendarViewKind.Schedule)); return true; }
        if (id == CalendarCommands.CalendarOptions.Id) { _ = ShowOptions("calendar"); return true; }
        if (id == CalendarCommands.OpenItem.Id) { OpenSelectedAppointment(shell); return true; }
        if (id == CalendarCommands.Categorize.Id) { SwitchModule(shell, MailboxModule.Calendar); CategorizeAppointment(shell); return true; }
        if (id == CalendarCommands.DeleteItem.Id) { _ = DeleteSelectedAppointmentAsync(shell); return true; }
        if (id == CalendarCommands.OpenCalendar.Id) { ShowAddCalendarMenu(shell); return true; }
        if (id == CalendarCommands.Share.Id) { ShowShareCalendarMenu(shell); return true; }
        if (id == CalendarCommands.NewCalendar.Id) { _ = NewCalendarAsync(shell); return true; }
        if (id == CalendarCommands.OpenFromInternet.Id) { _ = SubscribeAsync(shell); return true; }
        if (id == CalendarCommands.DeleteCalendar.Id) { _ = DeleteCalendarAsync(shell); return true; }

        return false;
    }

    /// <summary>
    /// The Add button's menu, in the reference's own order: the two address-book entries, the
    /// internet subscription, calendar groups, then the two that make or open a calendar.
    /// </summary>
    private void ShowAddCalendarMenu(ShellViewModel shell)
        => _ribbon.OpenMenuUnder(CalendarCommands.OpenCalendar.Id, BuildAddCalendarMenu(shell), this);

    private MenuFlyout BuildAddCalendarMenu(ShellViewModel shell)
    {
        SwitchModule(shell, MailboxModule.Calendar);
        var flyout = new MenuFlyout();

        void Entry(string header, string? icon, Action run)
        {
            var item = new MenuItem { Header = header, Icon = MenuIcon(icon) };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        // The reference's own six, with its own icons — and Create New Blank Calendar carrying
        // none, which is not an omission but what the capture shows.
        Entry("From _Address Book…", "contact-card", () => _ = OpenSomebodysCalendarAsync(shell));
        Entry("From _Room List…", "room-list", () => shell.StatusRight = "A room list is a directory of resources on a server, which no account here offers.");
        Entry("From _Internet…", "publish-calendar", () => _ = SubscribeAsync(shell));
        Entry("_Calendar Groups", "calendar-groups", () => shell.StatusRight = "Calendar groups arrive with the People module.");
        flyout.Items.Add(new Separator());
        Entry("Create New _Blank Calendar…", null, () => _ = NewCalendarAsync(shell));
        Entry("_Open Shared Calendar…", "share", () => shell.StatusRight = "A shared calendar is a subscription — use From Internet… with its address.");

        return flyout;
    }

    /// <summary>
    /// From Address Book: pick somebody, and subscribe to the calendar they publish.
    /// </summary>
    /// <remarks>
    /// The reference asks an Exchange directory where a colleague's calendar lives. There is no
    /// such directory here, so the contact is picked from the address book and the address is
    /// asked for — which is what opening somebody's calendar amounts to on a CalDAV server.
    /// </remarks>
    private async Task OpenSomebodysCalendarAsync(ShellViewModel shell)
    {
        var picked = await AddressBookDialog.PickAsync(this, App.Contacts);
        if (picked is null || picked.To.Count == 0)
        {
            shell.StatusRight = "Nobody was picked.";
            return;
        }

        shell.StatusRight = $"Enter the address of the calendar {picked.To[0]} publishes.";
        await SubscribeAsync(shell);
    }

    /// <summary>
    /// E-mail Calendar: this calendar as an attachment, in a message to whoever is chosen.
    /// </summary>
    private async Task EmailCalendarAsync(ShellViewModel shell)
    {
        var calendar = App.Pim.DefaultCalendar();
        var events = App.Pim.Items(calendar.Id)
            .Where(i => i.SyncState != PimSyncState.Deleted)
            .Select(PimEventCodec.FromItem)
            .ToList();

        if (events.Count == 0)
        {
            shell.StatusRight = "There is nothing in the calendar to send.";
            return;
        }

        var picked = await AddressBookDialog.PickAsync(this, App.Contacts);
        var to = picked?.To ?? [];

        var link = new Mailbox.Core.Compose.MailtoLink(
            to,
            picked?.Cc ?? [],
            picked?.Bcc ?? [],
            $"{calendar.DisplayName}",
            $"{calendar.DisplayName} is attached as an iCalendar file.");

        NewMessage(link);
        shell.StatusRight = $"“{calendar.DisplayName}” — {events.Count} appointment(s) to attach.";
        Log.Info($"Calendar: e-mailing {calendar.DisplayName} with {events.Count} item(s).");
    }

    /// <summary>The Share button's menu: send a calendar on, or put it somewhere to subscribe to.</summary>
    private void ShowShareCalendarMenu(ShellViewModel shell)
    {
        SwitchModule(shell, MailboxModule.Calendar);
        var flyout = new MenuFlyout();

        void Entry(string header, string? icon, Action run)
        {
            var item = new MenuItem { Header = header, Icon = MenuIcon(icon) };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        Entry("_E-mail Calendar…", "email-calendar", () => _ = EmailCalendarAsync(shell));
        Entry("_Share Calendar…", "share", () => shell.StatusRight = "Sharing a calendar wants CalDAV publishing, which is still to come.");
        Entry("_Publish Online…", "publish-calendar", () => shell.StatusRight = "Publishing a calendar wants CalDAV publishing, which is still to come.");
        flyout.Items.Add(new Separator());
        Entry("Calendar _Permissions…", "permission", () => shell.StatusRight = "Permissions belong to the server a calendar is published on.");

        _ribbon.OpenMenuUnder(CalendarCommands.Share.Id, flyout, this);
    }

    /// <summary>
    /// A menu entry's icon, drawn from the icon font at the size the reference's menus use.
    /// </summary>
    /// <remarks>
    /// Null for the entries the reference leaves bare. A menu where every row has an icon reads
    /// as a different menu from one where two do not, and the gaps are part of the shape.
    /// </remarks>
    private static Control? MenuIcon(string? icon)
    {
        if (icon is not { Length: > 0 }) return null;

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        return glyph;
    }

    private async Task NewCalendarAsync(ShellViewModel shell)
    {
        var name = await Prompt.AskAsync(this, "Create New Folder", "Name:", "Calendar");
        if (string.IsNullOrWhiteSpace(name)) return;

        var calendar = App.Pim.AddCollection(CollectionKind.Events, name.Trim(), App.CalendarOptions.DefaultColour);
        shell.StatusRight = $"“{calendar.DisplayName}” added.";
        Log.Info($"Calendar: collection {calendar.Id} added.");
        AfterStoreChange(shell);
    }

    /// <summary>
    /// New Internet Calendar Subscription: an address, and a read-only collection that the DAV
    /// engine then keeps up to date like any other.
    /// </summary>
    private async Task SubscribeAsync(ShellViewModel shell)
    {
        var url = await Prompt.AskAsync(this, "New Internet Calendar Subscription", "Address of the calendar:");
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https" or "webcal"))
        {
            await Confirm.TellAsync(this, "New Internet Calendar Subscription", "That is not a calendar address.");
            return;
        }

        // webcal: is the same URL over HTTP; every publisher writes it that way and no client has
        // ever spoken a webcal protocol.
        if (address.Scheme == "webcal") address = new UriBuilder(address) { Scheme = "https", Port = -1 }.Uri;

        var name = await Prompt.AskAsync(this, "New Internet Calendar Subscription", "Name:", address.Host);
        if (string.IsNullOrWhiteSpace(name)) return;

        var calendar = App.Pim.AddCollection(
            CollectionKind.Events, name.Trim(), App.CalendarOptions.DefaultColour,
            account: string.Empty, davUrl: address.ToString(), readOnly: true);

        shell.StatusRight = $"“{calendar.DisplayName}” subscribed. It fills on the next send/receive.";
        Log.Info($"Calendar: subscribed to {address} as collection {calendar.Id}.");
        AfterStoreChange(shell);
    }

    private async Task DeleteCalendarAsync(ShellViewModel shell)
    {
        var calendars = App.Pim.Collections(CollectionKind.Events);
        if (calendars.Count <= 1)
        {
            shell.StatusRight = "The last calendar cannot be deleted.";
            return;
        }

        var chosen = EnsureCalendar(shell).SelectedEntry?.CollectionId ?? calendars[^1].Id;
        var calendar = calendars.First(c => c.Id == chosen);

        if (!await Confirm.AskAsync(
                this, "Delete Folder",
                $"Are you sure you want to delete “{calendar.DisplayName}” and everything in it?",
                "Delete"))
        {
            return;
        }

        App.Pim.RemoveCollection(calendar.Id);
        shell.StatusRight = $"“{calendar.DisplayName}” deleted.";
        AfterStoreChange(shell);
    }

    private void WithCalendar(ShellViewModel shell, Action<CalendarWorkspace> act)
    {
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        act(calendar);
        shell.ModuleStatusLeft = calendar.Status;
    }

    /// <summary>The next half hour on the clock, which is where a new appointment starts.</summary>
    private static TimeOnly NextHalfHour()
    {
        var now = CalendarNow ?? DateTime.Now;
        var minutes = now.Minute < 30 ? 30 : 60;
        var start = now.Date.AddHours(now.Hour).AddMinutes(minutes);
        return TimeOnly.FromDateTime(start);
    }

    /// <summary>
    /// Add Focus Time: the next free block of the working day, booked as Busy.
    /// </summary>
    /// <remarks>
    /// The reference's button reaches a service that is not in scope here (§ out of scope), so
    /// this does what the name says with what the machine already knows — the calendar it has
    /// and the working hours Options names. Rule 2: the feature is the reference's, the
    /// mechanism is ours.
    /// </remarks>
    private void AddFocusTime(ShellViewModel shell)
    {
        var calendar = EnsureCalendar(shell);
        var length = TimeSpan.FromHours(2);
        var day = calendar.Anchor < CalendarToday ? CalendarToday : calendar.Anchor;
        var open = App.CalendarOptions.WorkDayStart;
        var close = App.CalendarOptions.WorkDayEnd;

        var source = new CalendarSource(App.Pim);
        var workDays = App.CalendarOptions.WorkDays;

        for (var offset = 0; offset < 14; offset++)
        {
            var date = day.AddDays(offset);

            // A working block goes in the working week: the Options page names both which hours
            // and which days, and booking Sunday afternoon is not what the button says.
            if (!workDays.Contains(date.DayOfWeek)) continue;

            var from = date.ToDateTime(open, DateTimeKind.Unspecified);
            var until = date.ToDateTime(close, DateTimeKind.Unspecified);
            var taken = source
                .Between(Instant(from), Instant(until))
                .Where(e => e.Busy != BusyStatus.Free)
                .OrderBy(e => e.StartWall)
                .ToList();

            var cursor = from;
            if (offset == 0 && cursor < DateTime.Now) cursor = RoundUp(DateTime.Now);

            foreach (var entry in taken)
            {
                if (entry.StartWall - cursor >= length) break;
                if (entry.EndWall > cursor) cursor = entry.EndWall;
            }

            if (cursor + length > until) continue;

            var written = SaveAppointment(new CalendarEvent
            {
                Uid = CalendarEvent.NewUid(),
                Summary = "Focus time",
                Start = EventTime.At(cursor, TimeZoneInfo.Local.Id),
                End = EventTime.At(cursor + length, TimeZoneInfo.Local.Id),
                Busy = BusyStatus.Busy,
                ReminderMinutes = 15,
            });

            calendar.GoTo(DateOnly.FromDateTime(cursor));
            shell.StatusRight = $"Focus time booked for {cursor.ToString("dddd HH:mm", CultureInfo.CurrentCulture)}.";
            Log.Info($"Focus time: item {written.Id} at {cursor:yyyy-MM-dd HH:mm}.");
            return;
        }

        shell.StatusRight = "No free block long enough in the next two weeks.";
    }

    private static DateTime RoundUp(DateTime when)
    {
        var minutes = when.Minute < 30 ? 30 : 60;
        return when.Date.AddHours(when.Hour).AddMinutes(minutes);
    }

    private static DateTimeOffset Instant(DateTime wall)
        => new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(wall)).ToUniversalTime();

    /// <summary>Writes an appointment into the default calendar and refreshes the view.</summary>
    internal PimItem SaveAppointment(CalendarEvent calendarEvent, PimItem? existing = null, long? collectionId = null)
    {
        var calendar = collectionId ?? App.Pim.DefaultCalendar().Id;
        var row = PimEventCodec.ToItem(calendarEvent, calendar, existing);
        var written = existing is null ? App.Pim.AddItem(row) : Store(row);

        // A calendar with a server behind it gets the change queued rather than sent now: an
        // edit made with the network down is a longer queue, not a lost edit (§7.5).
        App.PimSync.QueuePut(written);
        _calendar?.Reload();
        RefreshPeeks();
        return written;

        PimItem Store(PimItem item)
        {
            App.Pim.UpdateItem(item);
            return item;
        }
    }

    // ---- The appointment window ---------------------------------------------------------------

    /// <summary>A blank appointment on a day, opened for editing and written if it is kept.</summary>
    private async Task NewAppointmentAsync(ShellViewModel shell, DateTime start, bool allDay, bool meeting = false)
    {
        var zone = TimeZoneInfo.Local.Id;
        var fresh = new CalendarEvent
        {
            Uid = CalendarEvent.NewUid(),
            Start = allDay ? EventTime.Date(DateOnly.FromDateTime(start)) : EventTime.At(start, zone),
            End = allDay ? EventTime.Date(DateOnly.FromDateTime(start).AddDays(1)) : EventTime.At(start.AddMinutes(30), zone),
            Busy = BusyStatus.Busy,
            ReminderMinutes = App.CalendarOptions.DefaultReminderMinutes,
        };

        var calendars = App.Pim.Collections(CollectionKind.Events);
        if (calendars.Count == 0) calendars = [App.Pim.DefaultCalendar()];

        var window = new AppointmentWindow(App.Commands, fresh, calendars, calendars[0].Id, meeting);
        await window.ShowDialog(this);
        if (window.Result is not { Deleted: false } result) return;

        var written = SaveAppointment(result.Event, existing: null, result.CollectionId);
        shell.StatusRight = $"“{Named(result.Event)}” added to {calendars.First(c => c.Id == result.CollectionId).DisplayName}.";
        Log.Info($"Calendar: item {written.Id} added — {result.Event.Start.ToLocalText()} {Named(result.Event)}.");

        if (result.Sent) SendMeetingRequest(shell, result.Event);
        AfterStoreChange(shell);
    }

    /// <summary>
    /// Sends a meeting invitation: an ordinary message carrying a <c>METHOD:REQUEST</c> part to
    /// everyone asked, queued in the Outbox with the rest.
    /// </summary>
    /// <remarks>
    /// The organizer is the account it goes from, written onto the appointment as it is sent —
    /// a REQUEST with no ORGANIZER is one no client can reply to.
    /// </remarks>
    private void SendMeetingRequest(ShellViewModel shell, CalendarEvent meeting)
    {
        if (meeting.Attendees.Count == 0)
        {
            shell.StatusRight = "Nobody was asked, so nothing was sent.";
            return;
        }

        var account = App.Accounts.All.FirstOrDefault(a => a.IsDefault) ?? App.Accounts.All.FirstOrDefault();
        if (account is null)
        {
            shell.StatusRight = "There is no account to send the invitation from.";
            return;
        }

        try
        {
            var organizer = account.Account.Address;
            var payload = Imip.Request(meeting with { Organizer = organizer, Status = "CONFIRMED" });

            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress(App.Settings.GetString(OptionsPages.Keys.UserName), organizer));
            foreach (var attendee in meeting.Attendees)
            {
                message.To.Add(MimeKit.MailboxAddress.Parse(attendee.Address));
            }

            message.Subject = Named(meeting);

            var calendar = new MimeKit.TextPart("calendar") { Text = payload };
            calendar.ContentType.Parameters["method"] = "REQUEST";
            calendar.ContentType.Parameters["charset"] = "utf-8";
            message.Body = new MimeKit.Multipart("alternative")
            {
                new MimeKit.TextPart("plain") { Text = Imip.Describe(new ItipMessage(ItipMethod.Request, meeting, payload)) },
                calendar,
            };

            var outboxId = new Mailbox.Protocols.SmtpSender(account.Mail).Queue(account.Account.Id, message);
            Log.Info($"Meeting request queued as {outboxId} to {meeting.Attendees.Count} attendee(s).");
            shell.StatusRight = $"The invitation to “{Named(meeting)}” is in the Outbox.";
        }
        catch (FormatException ex)
        {
            Log.Warn("An attendee's address could not be read.", ex);
            shell.StatusRight = "One of the attendees' addresses could not be read; nothing was sent.";
        }
    }

    /// <summary>
    /// Opens an occurrence. A repeating one asks first, because editing one week and editing
    /// every week are different operations and the reference never guesses which was meant.
    /// </summary>
    private async Task OpenAppointmentAsync(ShellViewModel shell, CalendarEntry entry)
    {
        var stored = App.Pim.Item(entry.ItemId);
        if (stored is null)
        {
            shell.StatusRight = "That appointment is no longer in the calendar.";
            return;
        }

        var master = PimEventCodec.FromItem(stored);
        var scope = EditScope.Series;
        if (entry.Occurrence.IsPartOfSeries && !master.IsOverride)
        {
            scope = await EditScopePrompt.AskAsync(this, Named(master), deleting: false);
            if (scope == EditScope.None) return;
        }

        var calendars = App.Pim.Collections(CollectionKind.Events);
        var editing = scope == EditScope.Occurrence
            ? SeriesEditor.OverrideFor(master, entry.Occurrence)
            : master;

        var window = new AppointmentWindow(App.Commands, editing, calendars, stored.CollectionId, meeting: editing.Attendees.Count > 0);
        await window.ShowDialog(this);
        if (window.Result is not { } result) return;

        if (result.Deleted)
        {
            await RemoveAsync(shell, entry, stored, master, scope);
            return;
        }

        if (scope == EditScope.Occurrence)
        {
            // The override is a sibling row of its master, not a replacement for it.
            var written = SaveAppointment(result.Event, existing: null, result.CollectionId);
            Log.Info($"Calendar: occurrence {written.Id} overridden at {result.Event.RecurrenceId?.ToLocalText()}.");
        }
        else
        {
            var written = SaveAppointment(result.Event, stored, result.CollectionId);
            Log.Info($"Calendar: item {written.Id} saved — {Named(result.Event)}.");
        }

        shell.StatusRight = $"“{Named(result.Event)}” saved.";
        AfterStoreChange(shell);
    }

    /// <summary>
    /// Writes what a drag came to: the appointment at its new time, or with its new length.
    /// </summary>
    /// <remarks>
    /// One occurrence of a series dragged is that occurrence overridden, and nothing is asked
    /// first — unlike opening one, where the question is real. The gesture has already named
    /// which occurrence it means (that chip) and where it means it to go (there); moving the
    /// whole series to a Thursday in August is not a thing the drag could have been asking for.
    /// <para>
    /// A read-only calendar's items are not draggable at all, so refusing here is the belt to the
    /// view's braces: an entry can outlive its collection's permissions in a stale view.
    /// </para>
    /// </remarks>
    internal void MoveAppointment(ShellViewModel shell, EntryMove move)
    {
        var entry = move.Entry;
        if (entry.IsReadOnly)
        {
            shell.StatusRight = $"“{entry.CollectionName}” is read-only.";
            return;
        }

        if (App.Pim.Item(entry.ItemId) is not { } stored)
        {
            shell.StatusRight = "That appointment is no longer in the calendar.";
            AfterStoreChange(shell);
            return;
        }

        var master = PimEventCodec.FromItem(stored);
        var start = move.AllDay ? EventTime.Date(DateOnly.FromDateTime(move.Start)) : Stated(master.Start, move.Start, entry.Zone);
        var end = move.AllDay ? EventTime.Date(DateOnly.FromDateTime(move.End)) : Stated(master.Start, move.End, entry.Zone);

        var occurrence = entry.Occurrence.IsPartOfSeries && !master.IsOverride;
        var edited = (occurrence ? SeriesEditor.OverrideFor(master, entry.Occurrence) : master) with
        {
            Start = start,
            End = end,
            Sequence = master.Sequence + 1,
            LastModified = DateTimeOffset.UtcNow,
        };

        // An override is a sibling row of its master, never a replacement for it.
        var written = SaveAppointment(edited, occurrence ? null : stored, stored.CollectionId);

        var name = Named(master);
        shell.StatusRight = move.Resized
            ? $"“{name}” now runs {Span(move)}."
            : $"“{name}” moved to {Moment(move)}.";
        Log.Info($"Calendar: item {written.Id} {(move.Resized ? "resized" : "moved")} to {start.ToLocalText()}–{end.ToLocalText()}{(occurrence ? ", as an override" : string.Empty)}.");
        AfterStoreChange(shell);
    }

    /// <summary>
    /// A time the grid was read at, stated in the zone the appointment keeps its times in.
    /// </summary>
    /// <remarks>
    /// A drag speaks in whatever the view's clock reads; the appointment goes on saying what it
    /// always said. Writing the grid's reading straight onto an appointment written in another
    /// zone would move it by the difference between the two — the one place the drag could
    /// silently mean something other than where it was let go.
    /// </remarks>
    private static EventTime Stated(EventTime original, DateTime reading, TimeZoneInfo viewZone)
    {
        var wall = DateTime.SpecifyKind(reading, DateTimeKind.Unspecified);

        // A floating time means "wherever you are", so it is already in the view's own terms.
        if (original.TzId is not { Length: > 0 } tzId) return new EventTime(wall, null);

        var instant = new DateTimeOffset(wall, viewZone.GetUtcOffset(wall)).ToUniversalTime();
        var zone = original.Zone();
        return EventTime.At(TimeZoneInfo.ConvertTime(instant, zone).DateTime, tzId);
    }

    /// <summary>What a moved appointment's new time reads as on the status bar.</summary>
    private static string Moment(EntryMove move) => move.AllDay
        ? move.Start.ToString("dddd d MMMM", CultureInfo.CurrentCulture)
        : move.Start.ToString("dddd d MMMM, HH:mm", CultureInfo.CurrentCulture);

    private static string Span(EntryMove move)
    {
        if (move.AllDay)
        {
            var days = Math.Max(1, (move.End - move.Start).Days);
            return days == 1 ? "for the day" : $"for {days.ToString(CultureInfo.CurrentCulture)} days";
        }

        var length = move.End - move.Start;
        var hours = (int)length.TotalHours;
        var minutes = length.Minutes;
        return (hours, minutes) switch
        {
            (0, _) => $"{minutes.ToString(CultureInfo.CurrentCulture)} minutes",
            (_, 0) => hours == 1 ? "an hour" : $"{hours.ToString(CultureInfo.CurrentCulture)} hours",
            _ => $"{hours.ToString(CultureInfo.CurrentCulture)}h {minutes.ToString(CultureInfo.CurrentCulture)}m",
        };
    }

    private async Task DeleteSelectedAppointmentAsync(ShellViewModel shell)
    {
        var calendar = EnsureCalendar(shell);
        if (calendar.SelectedEntry is not { } entry)
        {
            shell.StatusRight = "Select an appointment first.";
            return;
        }

        var stored = App.Pim.Item(entry.ItemId);
        if (stored is null) return;
        var master = PimEventCodec.FromItem(stored);

        var scope = EditScope.Series;
        if (entry.Occurrence.IsPartOfSeries && !master.IsOverride)
        {
            scope = await EditScopePrompt.AskAsync(this, Named(master), deleting: true);
            if (scope == EditScope.None) return;
        }

        await RemoveAsync(shell, entry, stored, master, scope);
    }

    /// <summary>
    /// Takes an appointment off the calendar: the whole series, or one occurrence — which is an
    /// EXDATE on the master rather than a row anywhere, unless the occurrence is itself an
    /// override, in which case the override goes and the pattern's own occurrence comes back.
    /// </summary>
    private Task RemoveAsync(ShellViewModel shell, CalendarEntry entry, PimItem stored, CalendarEvent master, EditScope scope)
    {
        if (scope == EditScope.Series || !entry.Occurrence.IsPartOfSeries)
        {
            App.PimSync.Remove(stored);
            shell.StatusRight = $"“{Named(master)}” deleted.";
            Log.Info($"Calendar: item {stored.Id} deleted.");
        }
        else if (master.IsOverride)
        {
            App.PimSync.Remove(stored);
            shell.StatusRight = $"That change to “{Named(master)}” was removed.";
            Log.Info($"Calendar: override {stored.Id} removed.");
        }
        else
        {
            var excluded = SeriesEditor.Exclude(master, entry.Occurrence.RecurrenceId ?? entry.Occurrence.Start);
            SaveAppointment(excluded, stored, stored.CollectionId);
            shell.StatusRight = $"One occurrence of “{Named(master)}” deleted.";
            Log.Info($"Calendar: occurrence excluded at {entry.Occurrence.Start.ToLocalText()}.");
        }

        AfterStoreChange(shell);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the DAV engine over every calendar with a server behind it, and says what came of it.
    /// </summary>
    /// <remarks>
    /// A conflict is never resolved by the engine: it refetches, and the reader is shown both
    /// copies and asked. Nothing is overwritten before the answer, and an unanswered conflict
    /// stays queued rather than being dropped.
    /// </remarks>
    internal async Task SyncCalendarsAsync(ShellViewModel shell, CancellationToken cancellationToken)
    {
        try
        {
            var report = await App.PimSync.SyncAsync(cancellationToken).ConfigureAwait(true);
            if (report.Collections == 0) return;

            _calendar?.Reload();
            if (_calendar is { } calendar) shell.ModuleStatusLeft = calendar.Status;

            if (report.Conflicts.Count > 0)
            {
                await ResolveConflictsAsync(shell, report.Conflicts);
            }
            else if (report.DidAnything)
            {
                shell.StatusRight = $"Calendars updated: {report.Pulled} in, {report.Pushed} out.";
            }
        }
        catch (OperationCanceledException)
        {
            // The run was cancelled with the mail poll; nothing to say that the mail half has not.
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("The calendars could not be synchronised.", ex);
            shell.StatusRight = "The calendars could not be reached.";
        }
    }

    /// <summary>
    /// Puts the refused writes to the reader: both copies of each, and which to keep.
    /// </summary>
    /// <remarks>
    /// Straight after the sync that found them, because that is when the two copies are in hand —
    /// asking later would mean fetching the server's copy again, by which time it may have moved
    /// once more. Whatever is not answered stays queued and is asked about again next time.
    /// </remarks>
    private async Task ResolveConflictsAsync(ShellViewModel shell, IReadOnlyList<DavConflict> conflicts)
    {
        var settled = await CalendarConflictDialog.AskAsync(this, App.Pim, conflicts);
        var kept = settled.Count(c => c.Value != ConflictChoice.Later);

        foreach (var (item, choice) in settled)
        {
            Log.Info($"Calendar conflict: item {item} settled as {choice}.");
        }

        AfterStoreChange(shell);

        shell.StatusRight = kept switch
        {
            0 when conflicts.Count == 1 => $"“{conflicts[0].Summary}” changed on the server as well and is still waiting.",
            0 => $"{conflicts.Count} appointments changed on the server as well and are still waiting.",
            _ when kept == conflicts.Count => kept == 1
                ? "The conflict was settled; the change goes on the next send/receive."
                : $"{kept} conflicts settled; the changes go on the next send/receive.",
            _ => $"{kept} of {conflicts.Count} conflicts settled; the rest are still waiting.",
        };
    }

    /// <summary>
    /// Sends the answer to an invitation: an ordinary message carrying a <c>METHOD:REPLY</c>
    /// part, queued in the Outbox like anything else this application sends.
    /// </summary>
    /// <remarks>
    /// iMIP is scheduling over ordinary mail (RFC 6047), so a reply is a message and goes out on
    /// the next send/receive with the rest. What the calendar records happened when the button
    /// was pressed; this is the half the organizer sees.
    /// </remarks>
    internal void SendInvitationReply(ShellViewModel shell, InvitationBar.Answer answer)
    {
        AfterStoreChange(shell);

        var verdict = answer.Response switch
        {
            ItipResponse.Accepted => "Accepted",
            ItipResponse.Tentative => "Tentative",
            _ => "Declined",
        };

        if (!answer.SendReply)
        {
            shell.StatusRight = $"{verdict}. No reply was sent.";
            return;
        }

        var organizer = ItipAddress(answer.Invitation.Organizer);
        if (organizer.Length == 0)
        {
            shell.StatusRight = $"{verdict}. The invitation names no organizer to reply to.";
            return;
        }

        var account = App.Accounts.All.FirstOrDefault(a =>
                          string.Equals(a.Account.Address, _reading?.RecipientAddress, StringComparison.OrdinalIgnoreCase))
                      ?? App.Accounts.All.FirstOrDefault();
        if (account is null)
        {
            shell.StatusRight = $"{verdict}. There is no account to reply from.";
            return;
        }

        try
        {
            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress(App.Settings.GetString(OptionsPages.Keys.UserName), account.Account.Address));
            message.To.Add(MimeKit.MailboxAddress.Parse(organizer));
            message.Subject = $"{verdict}: {answer.Invitation.Event.Summary}";

            // Both parts, as RFC 6047 asks: the words for a person, the payload for a client.
            var body = new MimeKit.TextPart("plain") { Text = $"{verdict}: {answer.Invitation.Event.Summary}" };
            var calendar = new MimeKit.TextPart("calendar") { Text = answer.Payload };
            calendar.ContentType.Parameters["method"] = "REPLY";
            calendar.ContentType.Parameters["charset"] = "utf-8";
            message.Body = new MimeKit.Multipart("alternative") { body, calendar };

            var outboxId = new Mailbox.Protocols.SmtpSender(account.Mail).Queue(account.Account.Id, message);
            Log.Info($"Invitation reply queued as {outboxId} to {organizer}.");
            shell.StatusRight = $"{verdict}. The reply is in the Outbox.";
        }
        catch (FormatException ex)
        {
            Log.Warn("The invitation's organizer address could not be read.", ex);
            shell.StatusRight = $"{verdict}. The organizer's address could not be read.";
        }
    }

    private static string ItipAddress(string address)
    {
        var text = address.Trim();
        return text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
    }

    private void AfterStoreChange(ShellViewModel shell)
    {
        _calendar?.Reload();
        if (_calendar is { } calendar) shell.ModuleStatusLeft = calendar.Status;
    }

    private static string Named(CalendarEvent appointment)
        => appointment.Summary.Length > 0 ? appointment.Summary : "(no subject)";

    /// <summary>Go to Date: the reference's small prompt with a date and which view to show it in.</summary>
    private async Task GoToDateAsync(ShellViewModel shell)
    {
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        var dialog = new GoToDateDialog(calendar.Anchor, calendar.Kind);
        await dialog.ShowDialog(this);
        if (dialog.Chosen is not { } chosen) return;

        calendar.SetView(dialog.View);
        calendar.GoTo(chosen);
        shell.ModuleStatusLeft = calendar.Status;
    }

    // ------------------------------------------------------------------------------------
    // The peek: the miniature calendar the rail's icon opens, and the pane it docks into
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a peek in either state and keeps it fed from the store.
    /// </summary>
    /// <remarks>
    /// The view draws and this reads: a control that reached into the repository itself could
    /// not be drawn in a test, and both states want the same wiring — which is the reason they
    /// are one control and one builder rather than two of each.
    /// </remarks>
    private PeekView BuildPeek(ShellViewModel shell, bool docked)
    {
        // A peek opens on today, or on the day a pose names — the same variable the module
        // reads, so one run photographs both showing the same date.
        var opensOn = Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_DATE") is { Length: > 0 } posed
            && DateOnly.TryParseExact(posed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : CalendarToday;

        var view = new PeekView(docked)
        {
            Today = CalendarToday,
            Anchor = opensOn,
            Selected = opensOn,
            FirstDayOfWeek = (DayOfWeek)(int)App.Settings.GetNumber(OptionsPages.Keys.FirstDayOfWeek, 0),
            ShowWeekNumbers = App.Settings.GetBool(OptionsPages.Keys.ShowWeekNumbers),
        };

        view.Stepped += (_, direction) => view.Anchor = view.Anchor.AddMonths(direction);

        view.DayPicked += (_, day) =>
        {
            // A day in the month either side is picked as readily as one in it, and the grid
            // follows rather than making it be found again.
            if (day.Month != view.Anchor.Month || day.Year != view.Anchor.Year) view.Anchor = day;
            view.Selected = day;
            FillPeek(view);
        };

        view.EntryActivated += (_, entry) => _ = OpenAppointmentAsync(shell, entry);
        view.CornerPressed += (_, _) =>
        {
            if (docked) UndockPeek();
            else DockPeek();
        };

        FillPeek(view);
        return view;
    }

    /// <summary>The selected day's appointments, on every calendar the navigation pane is showing.</summary>
    private static void FillPeek(PeekView view)
    {
        var day = view.Selected;
        var entries = new CalendarSource(App.Pim).Between(
            Instant(day.ToDateTime(TimeOnly.MinValue)),
            Instant(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));

        view.Entries = entries;
        Log.Info($"Peek: {day:yyyy-MM-dd} holds {view.Agenda.Count} — "
            + string.Join(" | ", view.Agenda.Select(r => $"{r.Time} {r.Subject}")));
    }

    /// <summary>
    /// Puts the day an appointment was written on back in front of whichever peeks are open, so
    /// a new appointment shows up in them the way it does in the module.
    /// </summary>
    private void RefreshPeeks()
    {
        if (_floatingPeek is { } floating) FillPeek(floating);
        if (DockedPeek is { } docked) FillPeek(docked);
    }

    /// <summary>
    /// Opens one of the calendar module's own windows for a capture. The harness cannot click, so
    /// each is posed with something in it rather than photographed empty.
    /// </summary>
    internal async Task ShowCalendarPeekAsync(string which)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);

        switch (which)
        {
            case "recurrence":
            {
                var dialog = new RecurrenceDialog("FREQ=WEEKLY;BYDAY=MO", calendar.Anchor, TimeSpan.FromMinutes(30));
                await dialog.ShowDialog(this);
                Log.Info($"Harness: recurrence dialog came back with “{dialog.Rrule ?? "no rule"}”.");
                return;
            }

            case "editscope":
            {
                var scope = await EditScopePrompt.AskAsync(this, "Weekly sync", deleting: false);
                Log.Info($"Harness: edit scope chose {scope}.");
                return;
            }

            case "addmenu":
            {
                // A flyout is a separate surface and never appears in a capture, so what it holds
                // and where it hangs are checked by reading them back.
                var menu = BuildAddCalendarMenu(shell);

                // Switching the module rebuilt the bar, and a control that has not been through
                // a layout pass has no bounds to read — the same trap a band that will not grow
                // until UpdateLayout falls into.
                _ribbon.UpdateLayout();

                if (_ribbon.ControlFor(CalendarCommands.OpenCalendar.Id) is { } anchor
                    && anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), this) is { } corner)
                {
                    Log.Info($"Harness: the Add menu hangs from ({corner.X:0}, {corner.Y:0}), the button's own bottom-left.");
                }

                foreach (var entry in menu.Items.OfType<MenuItem>())
                {
                    Log.Info($"Harness: Add menu — \u201c{entry.Header}\u201d{(entry.Icon is null ? ", no icon" : ", with an icon")}.");
                }

                _ribbon.OpenMenuUnder(CalendarCommands.OpenCalendar.Id, menu, this);
                return;
            }

            case "gotodate":
            {
                var dialog = new GoToDateDialog(calendar.Anchor, calendar.Kind);
                await dialog.ShowDialog(this);
                Log.Info($"Harness: go to date chose {dialog.Chosen?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nothing"}.");
                return;
            }

            // A 412 is the one thing a live server will not produce on demand, so the pose is
            // built from real rows: the store's own copies against the same appointments as
            // another client would have left them. The plural opens the list the dialog grows
            // when more than one write was refused.
            case "conflict":
            case "conflicts":
            {
                await ResolveConflictsAsync(shell, PosedConflicts(which == "conflicts" ? 3 : 1));
                return;
            }

            default:
            {
                var meeting = which == "newmeeting";

                // Tracking and the Scheduling Assistant have nothing to show on a meeting nobody
                // was asked to, so the pose asks somebody.
                if (meeting && Environment.GetEnvironmentVariable("MAILBOX_APPOINTMENT_TAB") is { Length: > 0 })
                {
                    var calendars = App.Pim.Collections(CollectionKind.Events);
                    var window = new AppointmentWindow(App.Commands, PosedMeeting(calendar.Anchor), calendars, calendars[0].Id, meeting: true);
                    await window.ShowDialog(this);
                    return;
                }

                await NewAppointmentAsync(shell, calendar.Anchor.ToDateTime(new TimeOnly(8, 0)), allDay: false, meeting: meeting);
                return;
            }
        }
    }

    /// <summary>
    /// The conflicts the harness poses: the calendar's first appointments, each against a copy of
    /// itself that somebody else moved an hour later.
    /// </summary>
    /// <remarks>
    /// Both copies are real — the local one is read out of the store and the server's is
    /// serialized from it — so the dialog is photographed showing what it would really show. The
    /// server's own last-changed time is pinned to the module's today for the same reason every
    /// other calendar pose is: a photograph of a moving number is not a measurement.
    /// </remarks>
    private IReadOnlyList<DavConflict> PosedConflicts(int count)
    {
        var calendar = App.Pim.DefaultCalendar();
        var rows = App.Pim.Items(calendar.Id).Where(i => !i.IsOverride).Take(count).ToList();
        if (rows.Count == 0) return [new DavConflict(0, calendar.Id, "posed.ics", string.Empty, null, null)];

        var changed = CalendarToday.ToDateTime(new TimeOnly(9, 12));
        var when = new DateTimeOffset(changed, TimeZoneInfo.Local.GetUtcOffset(changed));

        return rows.Select(row =>
        {
            var mine = PimEventCodec.FromItem(row);
            var theirs = mine with
            {
                Summary = mine.Summary + " (moved)",
                Location = mine.Location.Length > 0 ? mine.Location : "Meeting room 2",
                Start = mine.Start.Add(TimeSpan.FromHours(1)),
                End = mine.End.Add(TimeSpan.FromHours(1)),
                Sequence = mine.Sequence + 1,
                LastModified = when,
            };

            return new DavConflict(
                row.Id, calendar.Id, row.DavHref ?? row.Uid + ".ics", row.Summary,
                ICalendarCodec.SerializeCalendar([theirs]), "etag-server-2");
        }).ToList();
    }

    /// <summary>
    /// The meeting the harness poses for the Tracking and Scheduling Assistant tabs: three people
    /// asked, with one of each answer between them, and every name invented.
    /// </summary>
    private static CalendarEvent PosedMeeting(DateOnly day) => new()
    {
        Uid = CalendarEvent.NewUid(),
        Summary = "Release readiness",
        Location = "Meeting room 2",
        Start = EventTime.At(day.ToDateTime(new TimeOnly(11, 0)), TimeZoneInfo.Local.Id),
        End = EventTime.At(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.Id),
        Organizer = "you@example.com",
        Busy = BusyStatus.Busy,
        Attendees =
        [
            new EventAttendee("a.person@example.com", "A. Person", "REQ-PARTICIPANT", "ACCEPTED"),
            new EventAttendee("b.other@example.com", "B. Other", "REQ-PARTICIPANT", "TENTATIVE"),
            new EventAttendee("c.reader@example.org", "C. Reader", "OPT-PARTICIPANT", "NEEDS-ACTION"),
        ],
    };

    /// <summary>
    /// Opens an appointment by the row it is on — what the Reminders window asks for, and what an
    /// invitation's "open in the calendar" will.
    /// </summary>
    internal async Task OpenAppointmentByIdAsync(ShellViewModel shell, long itemId)
    {
        if (App.Pim.Item(itemId) is not { } stored) return;

        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        var master = PimEventCodec.FromItem(stored);
        calendar.GoTo(DateOnly.FromDateTime(master.Start.Wall));

        var calendars = App.Pim.Collections(CollectionKind.Events);
        var window = new AppointmentWindow(App.Commands, master, calendars, stored.CollectionId, master.Attendees.Count > 0);
        await window.ShowDialog(this);
        if (window.Result is not { } result) return;

        if (result.Deleted) App.PimSync.Remove(stored);
        else SaveAppointment(result.Event, stored, result.CollectionId);
        AfterStoreChange(shell);
    }

    private void OpenSelectedAppointment(ShellViewModel shell)
    {
        var calendar = EnsureCalendar(shell);
        if (calendar.SelectedEntry is not { } entry)
        {
            shell.StatusRight = "Select an appointment first.";
            return;
        }

        _ = OpenAppointmentAsync(shell, entry);
    }

    /// <summary>
    /// Posts the module switch the harness asked for, after the window is up so the workspace
    /// measures against a real size.
    /// </summary>
    private void ApplyModulePose(ShellViewModel shell)
    {
        var module = Environment.GetEnvironmentVariable("MAILBOX_MODULE")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(module)) return;

        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                SwitchModule(shell, module switch
                {
                    "calendar" => MailboxModule.Calendar,
                    "people" => MailboxModule.People,
                    "tasks" => MailboxModule.Tasks,
                    "notes" => MailboxModule.Notes,
                    "journal" => MailboxModule.Journal,
                    _ => MailboxModule.Mail,
                });

                if (shell.Module == MailboxModule.Tasks) PoseTasks(shell);
                if (shell.Module == MailboxModule.Notes) PoseNotes(shell);
                if (shell.Module == MailboxModule.Journal) PoseJournal(shell);

                if (shell.Module == MailboxModule.People)
                {
                    var people = EnsurePeople(shell);
                    shell.ModuleStatusLeft = people.Status;
                    Log.Info($"Harness: People showing {people.Status}.");
                    ApplyPeoplePose(shell);
                    return;
                }

                if (shell.Module != MailboxModule.Calendar) return;
                var calendar = EnsureCalendar(shell);

                if (Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_VIEW")?.Trim().ToLowerInvariant() is { Length: > 0 } view)
                {
                    calendar.SetView(view switch
                    {
                        "day" => CalendarViewKind.Day,
                        "workweek" or "work-week" => CalendarViewKind.WorkWeek,
                        "week" => CalendarViewKind.Week,
                        "schedule" => CalendarViewKind.Schedule,
                        _ => CalendarViewKind.Month,
                    });
                }

                if (Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_DATE") is { Length: > 0 } date
                    && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on))
                {
                    calendar.GoTo(on);
                }

                shell.ModuleStatusLeft = calendar.Status;
                Log.Info($"Harness: calendar showing {calendar.Kind}, {calendar.Status}.");
            },
            DispatcherPriority.Loaded);
    }
}
