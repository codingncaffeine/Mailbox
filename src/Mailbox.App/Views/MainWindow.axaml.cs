using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Rules;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Rendering;
using Mailbox.Security;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

public partial class MainWindow : Window
{
    private readonly RibbonView _ribbon;
    private readonly KeyTipSession _keyTips = new();

    /// <summary>Alt has gone down with nothing else held, so releasing it opens the KeyTips.</summary>
    private bool _altAlone;

    public MainWindow()
    {
        InitializeComponent();

        // Set in code rather than XAML: Avalonia 12.1 exposes TextOptions through Get/Set
        // methods, not public attached-property fields. See Theming/TextRendering.cs.
        TextRendering.Apply(this);

        // Same frame as every other window that draws its own caption buttons: no system
        // decoration, transparent so the rounded root can draw the shape, own resize edges.
        WindowFrame.Apply(this);
        SetUpTitleBar();

        // The shipped layout with the user's edits over it, which for a first run is the
        // shipped layout unchanged.
        var layout = App.MailRibbon();
        var layoutMode = ShellLayoutModes.Resolve();

        _ribbon = new RibbonView(App.Commands, layout);
        RibbonDisplayMemory.Wire(_ribbon, RibbonWindow.Shell, Environment.GetEnvironmentVariable("MAILBOX_RIBBON"));
        _ribbon.CommandInvoked += OnRibbonCommand;
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);

        // A plugin enabled, disabled or crashed changes what the bar holds. The mail layout is
        // the one plugin tabs ride, so refresh only when it is the one showing — another module
        // fetches a fresh layout on its way back in anyway.
        App.Plugins.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ShellViewModel s && s.Module == MailboxModule.Mail && _inlineCompose is null)
            {
                _ribbon.Layout = App.MailRibbon();
            }
        });
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        this.FindControl<ContentControl>("RibbonHost")!.Content = _ribbon;

        // The rendering diagnostics the text investigation needs go to the log, not the status
        // bar. In the reference the bar carries the item counts and nothing else.
        Log.Info($"Text rendering: {TextRendering.Describe()}");
        Log.Info($"UI font: {App.Fonts.Resolve("Segoe UI").Rendered}");
        if (Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface((Avalonia.Media.FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? Avalonia.Media.FontFamily.Default)), out var uiFace))
        {
            Log.Info($"UI font drawn as: {uiFace.FamilyName} (asked for {Application.Current!.FindResource("ui.fontfamily")})");
        }
        Log.Info($"Body font: {App.Fonts.Resolve("Calibri").Rendered}");

        var quickAccess = App.QuickAccess;

        // The toolbar's two placements and its hidden state need a click to reach, so the
        // harness poses them instead — without persisting, or every capture would leave the
        // next run arranged however the last photograph wanted it.
        switch (Environment.GetEnvironmentVariable("MAILBOX_QAT")?.Trim().ToLowerInvariant())
        {
            case "below": quickAccess.Pose(QuickAccessPlacement.BelowRibbon); break;
            case "above": quickAccess.Pose(QuickAccessPlacement.AboveRibbon); break;
            case "hidden": quickAccess.Pose(visible: false); break;
        }

        var shell = new ShellViewModel(
            App.Themes, App.Commands, layout, layoutMode, App.Accounts, quickAccess);

        _ribbon.IsQuickAccessVisible = quickAccess.IsVisible;
        _ribbon.QuickAccessVisibilityToggled += (_, _) =>
        {
            quickAccess.IsVisible = !quickAccess.IsVisible;
            _ribbon.IsQuickAccessVisible = quickAccess.IsVisible;
            shell.RaiseQuickAccessPlacement();
        };

        WireQuickAccess(shell);
        WireSearchBoxToListEdge();
        if (this.FindControl<ViewHeaderStrip>("HeaderStrip") is { } strip)
        {
            strip.ColumnResized += (_, e) => shell.ResizeColumn(e.Index, e.Width);
        }

        WireRail(shell);
        WireWindowMenu();
        WireToolbarCommands(shell);
        WireAccountButton(shell);
        WireArrangeMenu(shell);
        WireListInteraction(shell);
        WireReadingPane(shell);
        WireFolderMenu(shell);

        // The gallery is a rendering of the Quick Steps list: when the list changes, the ribbon
        // is rebuilt from it — unless an inline reply has the compose ribbon up, which is put
        // back with the mail ribbon when the reply closes.
        App.QuickSteps.Changed += (_, _) =>
        {
            if (_inlineCompose is null) _ribbon.Layout = App.MailRibbon();
        };
        this.FindControl<ContentControl>("UndoSendHost")!.Content = _undoSend;
        WireSchedule(shell);

        // Instant Search over the module on screen: the box searches what is in front of the
        // reader, which is what the reference's own does.
        shell.ModuleSearchRequested += (_, words) => SearchModule(shell, words);

        // The summary page, which an account's heading in the folder pane opens.
        shell.TodayRequested += (_, address) => ShowToday(shell, address);
        DataContext = shell;

        // The toasts stay with the notification server; what goes is the watch on their buttons.
        Closed += (_, _) => _notifier.Dispose();

        ApplyHarnessState(shell);
        ApplyModulePose(shell);

        // The posed selection, once more, after the list has bound and had its say. Loaded runs
        // before Background, which is where MAILBOX_RUN acts, so a run sees it.
        if (_pendingSelection is { } posed)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                shell.SelectedRow = posed;
                shell.SelectedMessage = posed;
            }, DispatcherPriority.Loaded);
        }

        // Presses ribbon commands by id, after the window has opened and the posed folder and
        // selection are in place: MAILBOX_RUN=mail.delete,mail.archive. A menu cannot be
        // photographed but what it does can, and this is how the audit checks that a button
        // does what §20 says rather than what its handler's name suggests.
        if (Environment.GetEnvironmentVariable("MAILBOX_RUN") is { Length: > 0 } run)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                foreach (var id in run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Log.Info($"Harness: running {id}.");

                    // Reply and forward open a window only when the Options page asks for one;
                    // otherwise they grow inline in this window, which this window's own capture
                    // shows. Photograph the next window only in the windowed case.
                    if (id is "mail.reply" or "mail.reply.all" or "mail.forward"
                        && App.MailOptions.OpenRepliesInNewWindow)
                    {
                        CaptureNextWindow();
                    }

                    RunCommand(new CommandId(id));
                    if (DataContext is ShellViewModel s) Log.Info($"Harness: status \u201c{s.StatusRight}\u201d");
                }
            }, DispatcherPriority.Background);
        }

        // Links the posed selection to whoever the value names, and reads both cards back —
        // which is the claim: the link is in both vCards, not in a view. Wants MAILBOX_STORE for
        // the same reason MAILBOX_GOOGLE does: this writes to pim.db, and a capture run's pim.db
        // is the machine's own unless posed.
        if (Environment.GetEnvironmentVariable("MAILBOX_LINK") is { Length: > 0 } linkTo)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (WindowCapture.IsRequested
                        && Environment.GetEnvironmentVariable("MAILBOX_STORE") is not { Length: > 0 })
                    {
                        Log.Warn("Harness: MAILBOX_LINK writes to pim.db and wants MAILBOX_STORE.");
                        return;
                    }

                    if (DataContext is not ShellViewModel s) return;
                    var people = EnsurePeople(s);

                    if (people.Selected is not { } mine)
                    {
                        Log.Info("Harness: link — nothing is selected.");
                        return;
                    }

                    var target = people.Rows.FirstOrDefault(r =>
                        r.Id != mine.Id
                        && r.Named().Contains(linkTo, StringComparison.CurrentCultureIgnoreCase));

                    if (target is null)
                    {
                        Log.Info($"Harness: link — nobody matches “{linkTo}”.");
                        return;
                    }

                    var linkedNow = App.Contacts.Link(mine.Id, target.Id);
                    if (App.Contacts.Repository.Item(mine.Id) is { } a) App.PimSync.QueuePut(a);
                    if (App.Contacts.Repository.Item(target.Id) is { } b) App.PimSync.QueuePut(b);

                    Log.Info($"Harness: link — {(linkedNow ? "linked" : "already linked")} "
                             + $"“{mine.Named()}” and “{target.Named()}”.");
                    Log.Info($"Harness: link — “{mine.Named()}” carries "
                             + $"[{string.Join(", ", App.Contacts.Full(mine.Id)?.Links ?? [])}]; "
                             + $"“{target.Named()}” carries "
                             + $"[{string.Join(", ", App.Contacts.Full(target.Id)?.Links ?? [])}].");

                    people.Reload();
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the link pose failed.", ex);
                }
            }, DispatcherPriority.Background);
        }

        // Accept, Tentative and Decline are buttons on a bar, which a capture cannot press:
        // MAILBOX_INVITE presses one on the selected message and the log says what the calendar
        // holds afterwards.
        if (Environment.GetEnvironmentVariable("MAILBOX_INVITE") is { Length: > 0 } invite)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (_reading?.Invitation is not { } bar)
                    {
                        Log.Info("Harness: the selected message carries no invitation.");
                        return;
                    }

                    bar.Respond(invite.Trim().ToLowerInvariant() switch
                    {
                        "tentative" => Mailbox.Scheduling.ItipResponse.Tentative,
                        "decline" => Mailbox.Scheduling.ItipResponse.Declined,
                        _ => Mailbox.Scheduling.ItipResponse.Accepted,
                    });

                    // Read the store back rather than the bar: the answer is a write, and what
                    // the calendar holds is the claim being checked.
                    foreach (var item in App.Pim.ItemsBetween(
                                 DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1)))
                    {
                        Log.Info($"Harness: calendar holds “{item.Summary}”, {item.Busy}, {item.Status}.");
                    }

                    if (DataContext is ShellViewModel s) Log.Info($"Harness: status “{s.StatusRight}”");
                },
                DispatcherPriority.Background);
        }

        // Quick Click is a click on a cell, which the harness cannot make: MAILBOX_QUICKCLICK
        // presses one on the selected row and the log says what the store holds after.
        if (Environment.GetEnvironmentVariable("MAILBOX_QUICKCLICK") is { Length: > 0 } quick)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ShellViewModel s || s.SelectedMessage is not { } row) return;

                var field = quick.Equals("category", StringComparison.OrdinalIgnoreCase)
                    ? Mailbox.Core.Views.ViewFields.Categories
                    : Mailbox.Core.Views.ViewFields.Flag;

                Log.Info($"Harness: quick click is set to \u201c{(App.QuickClick.HasCategory ? App.QuickClick.Category : "no category")}\u201d "
                    + $"and {App.QuickClick.Flag}, over {s.Categories().Count} categories.");
                QuickClick(s, new QuickClickEventArgs(MessageCells.QuickClickEvent, row, field));

                // Read back from the store rather than from the row: acting on it rebuilt the
                // view, and the row in hand is the one that was replaced.
                var after = s.SummaryOf(row);
                var tags = s.CurrentAccountForCategories()?.Mail.CategoriesFor([row.Id]) is { } map
                           && map.TryGetValue(row.Id, out var assigned) && assigned.Count > 0
                    ? string.Join(", ", assigned.Select(c => c.Name))
                    : "none";

                Log.Info($"Harness: quick click {field} on \u201c{row.Subject}\u201d — "
                    + $"flag {(after?.FollowUpDue is { } due ? due.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : after?.IsFlagged == true ? "set, no date" : "none")}, "
                    + $"categories {tags}.");
            }, DispatcherPriority.Background);
        }

        // A drag is a gesture, which the harness cannot make either: MAILBOX_DRAG presses one
        // into whichever calendar view is up and the log says where the appointment ended.
        // Posted after the module pose so the view has laid out and drawn what it is dragging.
        if (Environment.GetEnvironmentVariable("MAILBOX_DRAG") is { Length: > 0 } drag)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseCalendarDrag(s, drag);
                },
                DispatcherPriority.Background);
        }

        // The peek's own buttons, pressed the same way. After the peek pose has opened one, and
        // after it has drawn, since what a press aims at is where the view really put it.
        if (Environment.GetEnvironmentVariable("MAILBOX_PEEK_PRESS") is { Length: > 0 } press)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseCalendarPeekPress(s, press);
                },
                DispatcherPriority.Background);
        }

        // AutoArchive's turn, once the window is up and the shell has its accounts. Posted at
        // background priority so the first paint is not behind a prompt.
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ShellViewModel s) _ = AutoArchiveIfDueAsync(s);
        }, DispatcherPriority.Background);

        // Presses shortcuts through the window's own key handler: MAILBOX_KEY=Ctrl+Q, or several
        // in order — MAILBOX_KEY=F6,F6 — and the log says what each one did. Punctuation keys go
        // by their Oem names here, the comma being the separator.
        if (Environment.GetEnvironmentVariable("MAILBOX_KEY") is { Length: > 0 } keys)
        {
            // One post per chord, so each is pressed on its own pass of the loop as a person's
            // would be — the list rebuilds itself between keystrokes, and a run that pressed
            // them all in one pass would be testing something nobody can do.
            Opened += (_, _) =>
            {
                foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var one = key;
                    Dispatcher.UIThread.Post(() => PressChord(one), DispatcherPriority.Background);
                }
            };
        }

        // A folder operation over the posed folder, for reading the store back:
        // MAILBOX_FOLDER_OP=new:<name> | rename:<name> | delete | markread | empty | favourite |
        // move:<folder name part, or "top"> | copy:<folder name part, or "top"> — the destination
        // being another folder of the same account.
        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDER_OP") is { Length: > 0 } op)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                if (DataContext is not ShellViewModel s || s.SelectedFolder is not { } node || s.FolderOf(node) is not { } where) return;
                var colon = op.IndexOf(':');
                var verb = colon > 0 ? op[..colon] : op;
                var arg = colon > 0 ? op[(colon + 1)..] : string.Empty;
                var manager = new FolderManager(where.Account.Mail);
                switch (verb)
                {
                    case "new": await manager.CreateAsync(await ConnectionForAsync(where.Account), where.Account.Account.Id, arg, where.Folder.Id); break;
                    case "rename": await manager.RenameAsync(await ConnectionForAsync(where.Account), where.Folder, arg); break;
                    case "delete": await manager.DeleteAsync(await ConnectionForAsync(where.Account), where.Folder); break;
                    case "markread": s.MarkFolderRead(where.Account, where.Folder.Id); break;
                    case "empty": s.EmptyFolder(where.Account, where.Folder.Id); break;
                    case "favourite":
                        s.ToggleFavourite(where.Account, where.Folder);
                        Log.Info($"Harness: “{where.Folder.Name}” is {(s.IsFavourite(where.Account, where.Folder) ? "now" : "no longer")} a favourite: {App.Settings.GetString(Mailbox.Core.Folders.Favourites.Key)}");
                        break;
                    case "move":
                    case "copy":
                    {
                        long? destination = null;
                        if (!string.Equals(arg, "top", StringComparison.OrdinalIgnoreCase))
                        {
                            destination = where.Account.Mail.Folders(where.Account.Account.Id)
                                .FirstOrDefault(f => f.Id != where.Folder.Id && f.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))?.Id;
                            if (destination is null) { Log.Warn($"Harness: no folder named like '{arg}'."); break; }
                        }

                        if (verb == "move")
                        {
                            var moved = await manager.MoveAsync(await ConnectionForAsync(where.Account), where.Folder, destination);
                            Log.Info($"Harness: move {(moved ? "done" : "refused")}.");
                        }
                        else
                        {
                            var made = await manager.CopyAsync(await ConnectionForAsync(where.Account), where.Account.Account.Id, where.Folder, destination);
                            Log.Info($"Harness: copied as folder {made.Id} “{made.Name}” under {made.ParentId?.ToString() ?? "the top"}.");
                        }
                        break;
                    }
                }

                s.Refresh();
                Log.Info($"Harness: folder op {op} done.");
            }, DispatcherPriority.Background);
        }

        // MAILBOX_RESIZE_COLUMN=<index>:<width> — what letting go of a header's drag handle does,
        // for reading the folder's view back after: a drag cannot be posed.
        if (Environment.GetEnvironmentVariable("MAILBOX_RESIZE_COLUMN") is { Length: > 0 } resize)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ShellViewModel s) return;
                var parts = resize.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var index) && double.TryParse(parts[1], out var width))
                {
                    s.ResizeColumn(index, width);
                    Log.Info($"Harness: column {index} resized to {width}; the view's columns are now " +
                             string.Join(", ", s.CurrentView.Columns.Select(c => $"{c.Id}={c.Width}")));
                }
            }, DispatcherPriority.Background);
        }

        // Runs AutoArchive now, without the prompt: MAILBOX_AUTOARCHIVE=run — for reading the
        // store back after.
        if (Environment.GetEnvironmentVariable("MAILBOX_AUTOARCHIVE") == "run")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ShellViewModel s) return;
                var outcome = Archiver.RunAll(App.Accounts.All, App.AutoArchive, DateTimeOffset.Now);
                s.Refresh();
                s.StatusRight = "AutoArchive: " + outcome.Summary;
                Log.Info($"Harness: AutoArchive — {outcome.Summary}");
            }, DispatcherPriority.Background);
        }

        // Runs the reminder check now, as the minute timer would: MAILBOX_REMINDERS=check — and
        // presses the window's own Dismiss or Snooze on everything it is holding with
        // =dismiss / =snooze, since a button nobody has pressed is a claim nobody has tested.
        if (Environment.GetEnvironmentVariable("MAILBOX_REMINDERS") is { Length: > 0 } reminders)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ShellViewModel s) return;
                CheckReminders(s);
                Log.Info($"Harness: reminders window {( _reminders?.IsVisible == true ? "shown with " + _reminders.Current.Count : "not shown")}.");

                foreach (var item in _reminders?.Current ?? [])
                {
                    Log.Info($"Harness: reminder “{item.Subject}” — {item.DueIn(DateTimeOffset.Now)}, "
                        + (item.IsAppointment ? "an appointment" : item.IsTask ? "a task" : "a flagged message") + ".");
                }

                PressReminders(reminders.Trim());
                if (_reminders?.IsVisible == true) CaptureNextWindow();
            }, DispatcherPriority.Background);
        }

        // Presses a Snooze preset on the posed selection: MAILBOX_SNOOZE=<0..3>, or `wake` to
        // run the timer's wake pass now. A menu cannot be pressed by a capture; what it does can
        // be read back out of the store.
        if (Environment.GetEnvironmentVariable("MAILBOX_SNOOZE") is { Length: > 0 } snooze)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ShellViewModel s) return;

                if (snooze == "wake")
                {
                    var woken = s.WakeSnoozed(DateTimeOffset.UtcNow);
                    Log.Info($"Harness: woke {woken.Count} snoozed message(s).");
                    return;
                }

                if (int.TryParse(snooze, out var index))
                {
                    var presets = Mailbox.Core.SnoozePresets.For(DateTimeOffset.Now);
                    var (header, until) = presets[Math.Clamp(index, 0, presets.Count - 1)];
                    s.Snooze(SelectedRows(), until);
                    Log.Info($"Harness: {header} → status “{s.StatusRight}”");
                }
            }, DispatcherPriority.Background);
        }

        // Raises the new-mail toast for a message already in the store, as if it had just
        // arrived, and optionally presses one of its buttons: MAILBOX_NOTIFY=<subject part>
        // or MAILBOX_NOTIFY=<subject part>:reply|delete|read|default. The toast itself goes
        // through notify-send for real (the log says so); the button is pressed directly,
        // because a capture cannot click a notification — and what the button did is read back
        // out of the store, which is the whole point.
        if (Environment.GetEnvironmentVariable("MAILBOX_NOTIFY") is { Length: > 0 } notify)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => PoseNotification(notify), DispatcherPriority.Background);
        }

        // Which folder is open. Set after the window opens rather than with the rest of the
        // posed state: the folder pane's list pushes its own selection back as it binds, so a
        // folder chosen in the constructor is overwritten the moment it lays out.
        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDER") is { Length: > 0 } wanted)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is not ShellViewModel s) return;

                    // "unified:Inbox" names one of the All Accounts folders, which otherwise
                    // cannot be told from the six others with the same names.
                    var match = wanted.StartsWith("unified:", StringComparison.OrdinalIgnoreCase)
                        ? s.Folders.FirstOrDefault(
                            f => f.Kind == FolderNodeKind.Unified
                                 && f.Name.Contains(wanted["unified:".Length..], StringComparison.OrdinalIgnoreCase))
                        : s.Folders.FirstOrDefault(
                            f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

                    Log.Info(match is null
                        ? $"No folder matching '{wanted}' in: {string.Join(", ", s.Folders.Select(f => f.Name))}"
                        : $"Harness: opening the {match.Name} folder.");

                    if (match is null) return;
                    s.SelectedFolder = match;

                    // A unified folder draws two stores at once, and which account each row came
                    // from is the claim worth reading back — a capture shows the list but not
                    // whose mail is in it.
                    if (match.Kind == FolderNodeKind.Unified)
                    {
                        Log.Info($"Harness: All Accounts › {match.Name} — {s.Messages.Count} row(s) from "
                                 + $"{string.Join(", ", s.Messages.Select(m => m.Address).Distinct().Order())}.");

                        foreach (var line in s.Messages)
                        {
                            Log.Info($"Harness: unified row “{line.Subject}” — {line.Address}, "
                                     + $"{line.Received:yyyy-MM-dd HH:mm}, {(line.IsUnread ? "unread" : "read")}.");
                        }
                    }

                    // The posed selection again, now in the posed folder: MAILBOX_SELECT ran in
                    // the constructor against the first folder's rows, and the message it names
                    // may be in this one. Asserted below the layout that re-selects on its own.
                    if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } subject
                        && s.Messages.FirstOrDefault(m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase))
                            is { } row)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            s.SelectedRow = row;
                            s.SelectedMessage = row;
                            Log.Info($"Harness: selected “{row.Subject}” in {match.Name}.");
                        }, DispatcherPriority.Loaded);
                    }
                },
                DispatcherPriority.Loaded);
        }

        // Opens a compose window from a mailto: link and photographs it, so the parser-to-window
        // path can be checked. The real cold-start path is gated off during a capture, which is
        // why the harness has its own way in.
        if (Environment.GetEnvironmentVariable("MAILBOX_MAILTO") is { Length: > 0 } mailto)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    CaptureNextWindow();
                    ComposeFromCommandLine([mailto]);
                },
                DispatcherPriority.Background);
        }

        // Runs the search box, so a capture can show the results. MAILBOX_SEARCH_SCOPE picks the
        // scope (this/current/all) first, since a search re-runs when the scope changes.
        if (Environment.GetEnvironmentVariable("MAILBOX_SEARCH") is { Length: > 0 } query)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is not ShellViewModel s) return;

                    s.ScopeIndex = Environment.GetEnvironmentVariable("MAILBOX_SEARCH_SCOPE") switch
                    {
                        "this" => 0,
                        "all" => 2,
                        _ => 1,
                    };
                    s.SearchText = query;
                    Log.Info($"Harness: searched “{query}” — {s.SearchResultSummary}.");
                },
                DispatcherPriority.Loaded);
        }

        // Delivers a feed from a file: MAILBOX_FEED=<path>[|name]. The poll itself is HTTP and a
        // capture run has no business reaching the network, so what is posed is the half that has
        // to be provable — an entry becoming a message in its own folder.
        if (Environment.GetEnvironmentVariable("MAILBOX_FEED") is { Length: > 0 } feedPose)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PoseFeed(shell, feedPose),
                DispatcherPriority.Loaded);
        }

        // Delivers a Google Tasks answer from a file: MAILBOX_GOOGLE=<path>[|list name]. Same
        // reasoning as the feed above — the poll is HTTPS to somebody else's API, and what has to
        // be provable is the half that touches the store: what a merge keeps, and what a tombstone
        // removes.
        if (Environment.GetEnvironmentVariable("MAILBOX_GOOGLE") is { Length: > 0 } googlePose)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PoseGoogleTasks(googlePose),
                DispatcherPriority.Loaded);
        }

        // Presses a column header, which a capture run cannot click: MAILBOX_SORT=<column>[,again]
        // names one by its field or its title, and a second press is what reverses it. Logs what
        // the list is sorted by afterwards and the order the rows actually came out in, a
        // screenshot of a sorted list being no evidence that it was sorted.
        if (Environment.GetEnvironmentVariable("MAILBOX_SORT") is { Length: > 0 } sortPose)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PoseSort(sortPose),
                DispatcherPriority.Loaded);
        }

        // The status bar's progress, pressed: the dialog again. Once "don't show this during
        // Send/Receive" is ticked, this bar is the only way back to it, so it has to be a way
        // back to it.
        if (this.FindControl<Button>("TransferBar") is { } transferBar)
        {
            transferBar.Click += (_, _) => ShowProgressDialog(force: true);
        }

        // Lets the fidelity harness capture the peek states, which a screenshot otherwise
        // cannot reach because they need a click.
        WireHarnessPeek();

        // The compose window is its own window, so the harness captures that rather than the
        // shell. The value is which of its tabs to open on.
        WireHarnessCompose();

        // A collapsed ribbon only unrolls on a tab click, which a capture cannot make.
        if (string.Equals(
                Environment.GetEnvironmentVariable("MAILBOX_RIBBON"),
                "revealed",
                StringComparison.OrdinalIgnoreCase))
        {
            Opened += (_, _) => _ribbon.RevealCollapsedRibbon();
        }
    }

    /// <summary>
    /// Presses a column header and reads the ordering back.
    /// </summary>
    /// <remarks>
    /// A header is a button and a capture run cannot press one, so the command behind it is
    /// invoked exactly as the click would — through the column's own <c>Sort</c>, not by setting
    /// the arrangement, or the test would prove the arrangement works rather than that the header
    /// is wired to it.
    /// </remarks>
    private void PoseSort(string spec)
    {
        if (DataContext is not ShellViewModel shell) return;

        foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var column = shell.Columns.FirstOrDefault(
                c => string.Equals(c.Field, name, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c.Title, name, StringComparison.OrdinalIgnoreCase));

            if (column?.Sort is not { } sort || !sort.CanExecute(null))
            {
                Log.Info($"Harness: sort — no column named “{name}” that sorts. "
                         + $"Columns: {string.Join(", ", shell.Columns.Select(c => c.Field))}.");
                continue;
            }

            sort.Execute(null);

            var marked = shell.Columns.Where(c => c.SortMark.Length > 0).Select(c => c.Field + c.SortMark);
            Log.Info(
                $"Harness: sorted by “{name}” — arrangement {shell.Arrangement}, "
                + $"{(shell.SortDescending ? "descending" : "ascending")}; marked: "
                + $"{(marked.Any() ? string.Join(", ", marked) : "nothing")}.");

            // VisibleRows, not Messages: Messages is the folder's mail as it was read and
            // VisibleRows is what the list actually draws, arranged and grouped. Reading the
            // former would report the store's order and call it the list's.
            foreach (var row in shell.VisibleRows.OfType<MessageRow>().Take(8))
            {
                Log.Info($"Harness: sorted row — {row.Received:yyyy-MM-dd HH:mm}  {row.From}  “{row.Subject}”  {row.SizeBytes}B");
            }
        }
    }

    /// <summary>
    /// Photographs whichever window opens next.
    /// </summary>
    /// <remarks>
    /// Confirm and Prompt build their own window and hand back an answer rather than the
    /// window, so the harness finds it in the application's window list instead of being given
    /// it. Everything else here is passed the window it is photographing.
    /// </remarks>
    private void CaptureNextWindow()
    {
        if (WindowCapture.RequestedPath is not { } path) return;

        WindowCapture.AnotherWindowWillBeCaptured = true;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(700);
            await WindowCapture.WhileHeldAsync();

            var dialog = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)
                ?.Windows.FirstOrDefault(w => !ReferenceEquals(w, this));

            if (dialog is not null)
            {
                // A SizeToContent dialog beside an off-screen owner has measured against no
                // screen and is a pixel high; sized from its content, it needs a moment for the
                // windowing system to confirm the new size before the picture is taken.
                if (dialog.ClientSize.Height <= 1 && WindowCapture.SizeFromContent(dialog)) await Task.Delay(400);

                WindowCapture.Capture(dialog, path, WindowCapture.Scale);
                Console.WriteLine($"Captured {path}");
            }

            Environment.Exit(0);
        });
    }

    private void WireHarnessPeek()
    {
        switch (Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant())
        {
            case "calendar": Opened += (_, _) => TogglePeek(); break;

            // The rail's other peek: People's, which the hover opens the same way.
            case "peoplepeek":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (DataContext is not ShellViewModel shell) return;
                        _peekModule = MailboxModule.People;
                        OpenPeek();
                    },
                    DispatcherPriority.Background);
                break;

            // A flyout is a separate surface and never appears in a capture, so this one is
            // checked by reading back where it hangs and what it holds. No CaptureNextWindow:
            // a menu is not a window and waiting for one would time the run out.
            case "addmenu":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () => _ = ShowCalendarPeekAsync("addmenu"),
                    DispatcherPriority.Background);
                break;

            // The calendar module's own windows. Each opens over the shell, so the harness
            // photographs the next window rather than this one.
            case "appointment":
            case "newmeeting":
            case "recurrence":
            case "editscope":
            case "gotodate":
            case "conflict":
            case "conflicts":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        CaptureNextWindow();
                        _ = ShowCalendarPeekAsync(Environment.GetEnvironmentVariable("MAILBOX_PEEK")!.ToLowerInvariant());
                    },
                    DispatcherPriority.Background);
                break;

            // The People module's own windows, posed the same way.
            case "contact":
            case "contactgroup":
            case "addressbook":
            case "selectnames":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        CaptureNextWindow();

                        // Logged rather than dropped: a pose is a fire-and-forget task, and one
                        // that throws leaves a run with no window, no error and nothing to grep.
                        _ = ShowPeoplePeekAsync(Environment.GetEnvironmentVariable("MAILBOX_PEEK")!.ToLowerInvariant())
                            .ContinueWith(
                                t => Log.Warn("The People pose failed.", t.Exception!),
                                TaskContinuationOptions.OnlyOnFaulted);
                    },
                    DispatcherPriority.Background);
                break;

            // Undo Send's toast is there for a few seconds after a send, which a capture cannot
            // make happen. Posed against a fixed clock so the countdown reads the same every run
            // — a photograph of a number that changes is not a measurement.
            case "undosend":
                Opened += (_, _) => _undoSend.Offer(
                    new QueuedMessageEventArgs(
                        "you@example.com", 0, DateTimeOffset.UtcNow.AddSeconds(5),
                        "Re: Thursday"),
                    _ => { });
                break;
            case "docked": Opened += (_, _) => DockPeek(); break;

            // The whole To-Do Bar: every section at once, which is the arrangement that decides
            // how tall the calendar half is and so the one worth photographing.
            case "todobar":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel bar) return;
                    bar.AreTasksDocked = true;
                    bar.ArePeopleDocked = true;
                    DockPeek();
                    LogToDoBar(bar);
                };
                break;

            // The tasks section alone, which is what the reference's own menu allows.
            case "todotasks":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel bar) return;
                    ShowToDoTasks(bar, true);
                    LogToDoBar(bar);
                };
                break;

            // The People section alone: the favourite contacts, which is the third thing the
            // reference's menu switches on its own.
            case "todopeople":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel bar) return;
                    ShowToDoPeople(bar, true);
                    LogToDoBar(bar);
                };
                break;
            case "backstage": Opened += (_, _) => ShowBackstage(); break;

            // The bar's "…": what it lists at this width, which a capture cannot show.
            case "overflow":
                Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    var items = _ribbon?.OpenOverflowMenu() ?? [];
                    Log.Info($"Harness: the \u2026 menu holds {items.Count}: {string.Join(" | ", items)}");
                }, DispatcherPriority.Background);
                break;

            // Opens the ribbon's display-options menu, so a capture can check a popup's colours.
            case "menu":
                Opened += async (_, _) =>
                {
                    _ribbon?.OpenDisplayOptions();
                    await Task.Yield();
                };
                break;

            // Options is its own window, so the harness captures that rather than the shell.
            case "options":
                Opened += async (_, _) =>
                {
                    // Which page, for the harness. Every page but General needs a click to
                    // reach, and the two customization editors are the ones worth photographing.
                    var dialog = new OptionsWindow(
                        App.Themes, Environment.GetEnvironmentVariable("MAILBOX_OPTIONS_PAGE"));
                    dialog.Opened += async (_, _) =>
                    {
                        await Task.Delay(700);
                        if (WindowCapture.RequestedPath is { } path)
                        {
                            WindowCapture.Capture(dialog, path, WindowCapture.Scale);
                            Console.WriteLine($"Captured {path}");
                        }
                        Environment.Exit(0);
                    };
                    await dialog.ShowDialog(this);
                };
                break;

            // The small dialogs size themselves to their content, which is the awkward case for
            // a frame that draws its own rounded shape — nothing else in the application has a
            // window whose height is decided after it is built.
            case "confirm":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await Confirm.AskAsync(
                        this,
                        "Delete items",
                        "The selected messages will be deleted permanently. This cannot be "
                        + "undone.",
                        "Delete");
                };
                break;

            case "prompt":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await Prompt.AskAsync(this, "Rename", "Display name:", "New Group");
                };
                break;

            // The passphrase prompt, which is the one surface a run cannot reach on its own: getting
            // there means an operation meeting a key that will not open, and the seed's key opens on
            // an empty passphrase because nothing could have answered otherwise. So the request is
            // built from a key the posed ring really holds and the dialog is asked directly — the
            // picture is the point, and it had never been taken.
            case "passphrase":
                Opened += async (_, _) =>
                {
                    try
                    {
                        using var keys = CryptoStores.KeyRing();
                        var who = new MimeKit.MailboxAddress(
                            string.Empty,
                            (DataContext as ShellViewModel)?.CurrentAddress ?? "work@example.net");

                        if (keys.SigningKey(who) is not { } key)
                        {
                            Log.Warn($"Harness: no OpenPGP key for {who.Address} — pose a seeded store.");
                            return;
                        }

                        CaptureNextWindow();
                        await PassphraseDialog.UnlockAsync(
                            this, keys, CryptoStores.Passphrases,
                            [Mailbox.Security.OpenPgp.PassphraseVault.RequestFor(key)]);
                    }
                    catch (Exception ex)
                    {
                        // A pose that throws leaves no window, no error and no capture. Say so.
                        Log.Warn("Harness: the passphrase pose failed.", ex);
                    }
                };
                break;

            // The new-key dialog. Alone the picture is the point; with MAILBOX_KEYGEN posed as
            // well — name:A. Person,address:a.person@example.com,pass:secret — the dialog's own
            // Make is pressed and the ring is read back, which is the claim: a key exists that
            // did not, and the inventory lists it as ours. A capture run's ring is a throwaway
            // unless MAILBOX_STORE poses one (CryptoStores.Throwaway), so the machine's own is
            // never grown by a photograph.
            case "newkey":
                Opened += async (_, _) =>
                {
                    try
                    {
                        using var keys = CryptoStores.KeyRing();
                        var whose = (DataContext as ShellViewModel)?.CurrentAddress ?? "work@example.net";

                        if (Environment.GetEnvironmentVariable("MAILBOX_KEYGEN") is { Length: > 0 } spec)
                        {
                            // The log lines are the claim here, not the picture: the dialog
                            // closes itself on success, so the shell is what gets photographed —
                            // held open until the generation has finished and been read back.
                            using var busy = WindowCapture.Hold();

                            string name = "A. Person", address = whose, pass = string.Empty;
                            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var split = part.IndexOf(':');
                                if (split < 1) continue;
                                var value = part[(split + 1)..];
                                switch (part[..split].Trim().ToLowerInvariant())
                                {
                                    case "name": name = value; break;
                                    case "address": address = value; break;
                                    case "pass": pass = value; break;
                                }
                            }

                            var madeKey = await NewKeyDialog.HarnessAsync(
                                this, keys, CryptoStores.Passphrases, name, address, pass);

                            Log.Info(madeKey is null
                                ? "Harness: keygen — nothing was made."
                                : $"Harness: keygen — made {madeKey.ShortId} for “{madeKey.Owner}”, "
                                  + $"{madeKey.Algorithm} {madeKey.Bits}, secret half "
                                  + $"{(madeKey.HasSecret ? "held" : "missing")}, "
                                  + $"{(madeKey.IsUsable(DateTimeOffset.Now) ? "usable" : "not usable")}.");
                            Log.Info($"Harness: keygen — the ring holds "
                                     + $"{Mailbox.Security.OpenPgp.KeyInventory.Read(keys).Count} key(s).");
                        }
                        else
                        {
                            CaptureNextWindow();
                            await NewKeyDialog.MakeAsync(
                                this, keys, CryptoStores.Passphrases, "A. Person", whose);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A pose that throws leaves no window, no error and no capture. Say so.
                        Log.Warn("Harness: the new-key pose failed.", ex);
                    }
                };
                break;

            // The Linked Contacts manager, on whoever MAILBOX_SELECT picked in a posed People
            // module. The dialog writes as it goes, so this pose only photographs; the press
            // that writes is MAILBOX_LINK, which reads the store back.
            case "linkcontacts":
                Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (DataContext is not ShellViewModel s) return;
                        var people = EnsurePeople(s);
                        if (people.Selected is not { } row)
                        {
                            Log.Warn("Harness: linkcontacts — nothing is selected; pose MAILBOX_MODULE=people and MAILBOX_SELECT.");
                            return;
                        }

                        CaptureNextWindow();
                        _ = LinkContactsDialog.ManageAsync(this, App.Contacts, App.PimSync.QueuePut, row);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Harness: the linkcontacts pose failed.", ex);
                    }
                }, DispatcherPriority.Background);
                break;

            // The duplicate prompt, over an invented pair — reaching it for real means typing a
            // whole contact into a window a capture cannot type into. The invented cards say on
            // their face what the finder's three strengths look like.
            case "duplicate":
                Opened += async (_, _) =>
                {
                    try
                    {
                        var candidate = new Mailbox.Contacts.Contact
                        {
                            Uid = "posed-duplicate",
                            DisplayName = "A. Person",
                            Emails = [new Mailbox.Contacts.ContactEmail("a.person@example.com")],
                        };

                        var existing = new Mailbox.Contacts.ContactRow(
                            1, 1, "Contacts",
                            candidate with { Uid = "posed-existing" },
                            IsReadOnly: false);

                        CaptureNextWindow();
                        await DuplicateContactDialog.AskAsync(this, candidate,
                        [
                            new Mailbox.Contacts.DuplicateMatch(
                                existing, Mailbox.Contacts.DuplicateStrength.Certain,
                                "they share the address a.person@example.com"),
                        ]);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Harness: the duplicate pose failed.", ex);
                    }
                };
                break;

            // The dialogs behind the Backstage's menus, which otherwise take three clicks to
            // reach and so have never been photographed.
            // MAILBOX_ACCOUNTS_TAB poses one of its tabs, by index or name;
            // MAILBOX_ACCOUNTS_ACTION presses its buttons and logs what the store says after.
            case "accounts":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var accounts = new AccountSettingsDialog(Environment.GetEnvironmentVariable("MAILBOX_ACCOUNTS_TAB"));
                    if (Environment.GetEnvironmentVariable("MAILBOX_ACCOUNTS_ACTION") is { Length: > 0 } actions)
                    {
                        accounts.Opened += (_, _) => accounts.Harness(actions);
                    }
                    await accounts.ShowDialog(this);
                };
                break;

            // Adding an account, which is where a provider that no longer takes a password is
            // told apart from one that does. MAILBOX_ACCOUNT_ACTION types an address, pastes a
            // client ID and presses Sign in; a capture run has no browser to answer, so what the
            // press logs is the authorization request it would have opened.
            case "addaccount":
                Opened += async (_, _) =>
                {
                    try
                    {
                        CaptureNextWindow();
                        var wizard = new AccountWizard();
                        if (Environment.GetEnvironmentVariable("MAILBOX_ACCOUNT_ACTION") is { Length: > 0 } actions)
                        {
                            wizard.Opened += async (_, _) => await wizard.HarnessAsync(actions);
                        }

                        await wizard.ShowDialog(this);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Harness: the add-account pose failed.", ex);
                    }
                };
                break;

            // The certificate warning, which a run cannot otherwise reach: getting there means a
            // server whose certificate does not match, and a capture run has no business going to
            // the network to find one. The certificate is made here so the dialog has a real one
            // to describe — a made-up record would photograph the layout and prove nothing about
            // what it reads off a certificate.
            case "certificate":
                Opened += async (_, _) =>
                {
                    try
                    {
                        CaptureNextWindow();

                        using var key = System.Security.Cryptography.RSA.Create(2048);
                        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                            "CN=d8.my-control-panel.com",
                            key,
                            System.Security.Cryptography.HashAlgorithmName.SHA256,
                            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

                        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
                        names.AddDnsName("d8.my-control-panel.com");
                        request.CertificateExtensions.Add(names.Build());

                        using var certificate = request.CreateSelfSigned(
                            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(60));

                        var facts = Mailbox.Security.Tls.CertificateFacts.Read(certificate);
                        var fault = Environment.GetEnvironmentVariable("MAILBOX_CERT_FAULT") switch
                        {
                            "untrusted" => Mailbox.Security.Tls.CertificateFault.UntrustedRoot,
                            "expired" => Mailbox.Security.Tls.CertificateFault.Expired,
                            "both" => Mailbox.Security.Tls.CertificateFault.NameMismatch
                                      | Mailbox.Security.Tls.CertificateFault.UntrustedRoot,
                            _ => Mailbox.Security.Tls.CertificateFault.NameMismatch,
                        };

                        var refusal = new Mailbox.Security.Tls.CertificateRefusal(
                            "mail.example.com", 993, facts, fault);

                        Log.Info($"Harness: certificate — {string.Join(" ", refusal.Problems)} "
                                 + $"name-only: {refusal.NameOnly}.");

                        var agreed = await CertificateDialog.AskAsync(this, refusal);
                        Log.Info($"Harness: certificate — the dialog came back {(agreed ? "trusted" : "declined")}.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Harness: the certificate pose failed.", ex);
                    }
                };
                break;

            // The subscription prompt behind New… on the Internet Calendars tab.
            case "subscription":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new SubscriptionDialog(
                        "New Internet Calendar Subscription",
                        "Enter the location of the Internet Calendar you want to add to Mailbox:",
                        "Example: webcal://www.example.com/calendars/Calendar.ics").ShowDialog(this);
                };
                break;

            case "datafile":
                Opened += async (_, _) =>
                {
                    if (App.Accounts.Default is not { } open) return;
                    CaptureNextWindow();
                    await new DataFileSettingsDialog(open).ShowDialog(this);
                };
                break;

            // The two Set Quick Click dialogs, which otherwise take a ribbon click and a menu.
            case "quickclickcategory":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var categories = DataContext is ShellViewModel s ? s.Categories() : [];
                    await new SetQuickClickCategoryDialog(App.QuickClick, categories).ShowDialog(this);
                };
                break;

            case "quickclickflag":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new SetQuickClickFlagDialog(App.QuickClick).ShowDialog(this);
                };
                break;

            case "cleanup":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new MailboxCleanupDialog().ShowDialog(this);
                };
                break;

            case "recover":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new RecoverDeletedItemsDialog(DataContext is ShellViewModel s ? s.CurrentAddress : null).ShowDialog(this);
                };
                break;

            case "searchfolder":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new NewSearchFolderDialog(DataContext is ShellViewModel s ? s.CurrentAccountForCategories() : null).ShowDialog(this);
                };
                break;

            case "customflag":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s || s.SelectedMessage is not { } row) return;
                    CaptureNextWindow();
                    await new CustomFlagDialog(s.SummaryOf(row), reminderOn: true).ShowDialog(this);
                };
                break;

            case "autoarchive":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new AutoArchiveSettingsDialog(App.AutoArchive).ShowDialog(this);
                };
                break;

            case "readingpane":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new ReadingPaneOptionsDialog(App.MailOptions).ShowDialog(this);
                };
                break;

            case "keyboard":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new CustomizeKeyboardDialog(App.Keys, App.Commands).ShowDialog(this);
                };
                break;

            // Signatures and Stationery on either tab, and the Font dialog behind Font….
            case "signatures":
            case "stationery":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var tab = Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant() == "stationery" ? 1 : 0;
                    await new StationeryDialog(App.Signatures, App.Stationery, App.Accounts.All, App.Accounts.Default?.Account.Address, tab).ShowDialog(this);
                };
                break;

            case "font":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new FontDialog("Font", App.Stationery.Get(Mailbox.Core.Settings.StationeryUse.NewMessages), App.Fonts.InstalledFamilies).ShowDialog(this);
                };
                break;

            case "editoroptions":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new EditorOptionsDialog(App.MailOptions, App.Settings).ShowDialog(this);
                };
                break;

            case "newfolder":
            case "folderprops":
            case "folderarchive":
            case "gotofolder":
            case "movefolder":
                // After the folder pose, which is posted at normal priority: these dialogs are
                // about the selected folder, and MAILBOX_FOLDER has to have had its say first.
                CaptureNextWindow();
                Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
                {
                    if (DataContext is not ShellViewModel s || s.SelectedFolder is not { } node || s.FolderOf(node) is not { } where) return;
                    Window dialog = Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant() switch
                    {
                        "newfolder" => new NewFolderDialog(where.Account, where.Folder.Id),
                        "folderarchive" => new FolderPropertiesDialog(where.Account, where.Folder, startTab: 1),
                        "gotofolder" => FolderPicker("Go to Folder", null, (where.Account, where.Folder.Id), allowRoot: false),
                        "movefolder" => FolderPicker("Move Folder", "Move the selected folder to the folder:", (where.Account, where.Folder.ParentId), allowRoot: true, exclude: (where.Account, where.Folder.Id)),
                        _ => new FolderPropertiesDialog(where.Account, where.Folder),
                    };
                    await dialog.ShowDialog(this);
                }, DispatcherPriority.Background);
                break;

            case "archive":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s) return;
                    CaptureNextWindow();
                    await new ArchiveDialog(App.Accounts.All, App.AutoArchive, s.ViewAccount, s.ViewFolderId).ShowDialog(this);
                };
                break;

            // The View tab's dialogs over the folder on screen: Advanced View Settings and its
            // seven editors, Manage All Views, Apply View.
            case "viewsettings":
            case "showcolumns":
            case "groupby":
            case "viewsort":
            case "viewfilter":
            case "othersettings":
            case "conditionalformatting":
            case "formatcolumns":
            case "manageviews":
            case "applyview":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s) return;
                    CaptureNextWindow();
                    var view = s.CurrentView;
                    Window dialog = Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant() switch
                    {
                        "showcolumns" => new ShowColumnsDialog(view.Columns),
                        "groupby" => new GroupByDialog(view),
                        "viewsort" => new SortDialog(view),
                        "viewfilter" => new FilterDialog(Environment.GetEnvironmentVariable("MAILBOX_FILTER") ?? view.Filter, "Filter"),
                        "othersettings" => new OtherSettingsDialog(view),
                        "conditionalformatting" => new ConditionalFormattingDialog(view.Formats),
                        "formatcolumns" => new FormatColumnsDialog(view),
                        "manageviews" when s.ViewAccount is { } a => new ManageViewsDialog(a, view, s.SelectedFolderName),
                        "applyview" when s.ViewAccount is { } a => new ApplyViewToFoldersDialog(a, s.ViewFolderId),
                        _ => new AdvancedViewSettingsDialog(view, s.ViewAccount),
                    };
                    await dialog.ShowDialog(this);
                };
                break;

            case "quicksteps":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new ManageQuickStepsDialog(DataContext is ShellViewModel s ? s.CurrentAccountForCategories() : null).ShowDialog(this);
                };
                break;

            case "quickstepedit":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new EditQuickStepDialog(App.QuickSteps.All[3], DataContext is ShellViewModel s ? s.CurrentAccountForCategories() : null).ShowDialog(this);
                };
                break;

            case "categories":
                Opened += async (_, _) =>
                {
                    // An operation instead of the dialog when one is posed: the dialog asks for a
                    // name behind a prompt, and a prompt blocks a capture run.
                    if (Environment.GetEnvironmentVariable("MAILBOX_CATEGORY_OP") is { Length: > 0 } op)
                    {
                        PoseCategoryOp(op);
                        return;
                    }

                    CaptureNextWindow();
                    await new ColorCategoriesDialog(App.Categories, RewriteCategoryOnItems).ShowDialog(this);
                };
                break;

            // The Server Settings dialog for a chosen account, so its protocol-specific half —
            // POP3's leave-on-server, IMAP's "Mail to keep offline" — can be photographed.
            // MAILBOX_SERVER names the account by address; the default account otherwise.
            case "server":
                Opened += async (_, _) =>
                {
                    var address = Environment.GetEnvironmentVariable("MAILBOX_SERVER");
                    var account = (address is { Length: > 0 } ? App.Accounts.Find(address) : App.Accounts.Default)
                        ?.Account;
                    if (account is null) return;

                    CaptureNextWindow();
                    await new ServerSettingsDialog(account).ShowDialog(this);
                };
                break;

            case "rules":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new RulesAndAlertsDialog(DataContext is ShellViewModel s ? s.CurrentAddress : null).ShowDialog(this);
                };
                break;

            // The Rules Wizard on a new rule (MAILBOX_WIZARD_STEP picks the page), the Create
            // Rule dialog on the selected message, and Run Rules Now.
            case "rulewizard":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s || s.CurrentAccountForCategories() is not { } account) return;
                    CaptureNextWindow();
                    var wizard = new RuleWizard(account.Mail, account.Account.Id);
                    await wizard.ShowDialog(this);
                };
                break;

            case "createrule":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s || s.CurrentAccountForCategories() is not { } account
                        || _openMessage is not { } message) return;
                    CaptureNextWindow();
                    await new CreateRuleDialog(account.Mail, account.Account.Id, message).ShowDialog(this);
                };
                break;

            case "runrules":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel s || s.CurrentAccountForCategories() is not { } account) return;
                    CaptureNextWindow();
                    await new RunRulesNowDialog(account).ShowDialog(this);
                };
                break;

            // Junk Email Options, on the current account's lists. MAILBOX_JUNK_TAB picks a tab.
            case "junk":
                Opened += async (_, _) =>
                {
                    if (DataContext is not ShellViewModel shell
                        || shell.CurrentAccountForCategories() is not { } account) return;

                    CaptureNextWindow();
                    var dialog = new JunkOptionsDialog(account.Mail, App.MailOptions);
                    if (int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_JUNK_TAB"), out var tab))
                    {
                        dialog.Opened += (_, _) => dialog.SelectTab(tab);
                    }
                    await dialog.ShowDialog(this);
                };
                break;

            // A message in its own window, which otherwise takes a double-click to reach.
            case "message":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel shell) return;

                    CaptureNextWindow();
                    OpenMessageWindow(shell);
                };
                break;

            case "groups":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new SendReceiveGroupsDialog(App.Groups, AccountAddresses()).ShowDialog(this);
                };
                break;

            case "printlist":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel shell) return;

                    CaptureNextWindow();
                    PrintList(shell);
                };
                break;

            case "source":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel shell) return;

                    CaptureNextWindow();
                    ShowMessageSource(shell);
                };
                break;

            // A run posed mid-flight, since a real one on a scratch store finishes faster than
            // a capture can be taken. The addresses are invented, as all sample data is.
            case "progress":
            case "transferbar":
                Opened += (_, _) =>
                {
                    var first = Environment.GetEnvironmentVariable("MAILBOX_PROGRESS_ACCOUNT") ?? "you@example.com";
                    var tasks = new SendReceiveTasks([first, "other@example.com"]);
                    tasks.Report(new PollProgress(first, 0, 0, "Sending"));
                    tasks.Report(new PollProgress(first, 0, 0, "Connecting"));
                    tasks.Report(new PollProgress(first, 3, 8, "Downloading"));
                    tasks.Report(new PollProgress("other@example.com", 0, 0, "Sending"));

                    // The states a run really ends in, which the mid-flight pose above never
                    // reaches: MAILBOX_PROGRESS_STATE=finished shows the counts a completed run
                    // leaves behind, and =failed shows what an account that could not be reached
                    // looks like. Both are what a reader actually sees most of the time.
                    switch (Environment.GetEnvironmentVariable("MAILBOX_PROGRESS_STATE"))
                    {
                        case "finished":
                            tasks.Finish(new SendReceiveResult(
                            [
                                new AccountRunResult("you@example.com", 8, 2, null),
                                new AccountRunResult("other@example.com", 3, 1, null),
                            ]));
                            break;

                        case "failed":
                            tasks.Finish(new SendReceiveResult(
                            [
                                new AccountRunResult("you@example.com", 8, 2, null),
                                new AccountRunResult("other@example.com", 0, 0,
                                    "The server could not be reached."),
                            ]));
                            break;
                    }

                    foreach (var task in tasks.Tasks)
                    {
                        Log.Info($"Harness: progress row — “{task.Name}” state {task.State}, detail “{task.Progress}”.");
                    }

                    // The status bar's own indicator, posed with the same numbers the dialog is
                    // showing: it is the half a reader sees once the dialog has been told not to
                    // appear, so it wants photographing as much as the dialog does.
                    // MAILBOX_PEEK=transferbar poses it without the window over the top.
                    if (DataContext is ShellViewModel bar)
                    {
                        bar.IsTransferring = true;
                        bar.TransferProgress = tasks.Fraction;
                        bar.TransferTip = $"Receiving you@example.com — {tasks.Succeeded + tasks.Failed} of {tasks.Total}";
                        bar.StatusRight = "Receiving you@example.com…";

                        Log.Info(
                            $"Harness: send/receive — {tasks.Succeeded} of {tasks.Total} done, "
                            + $"bar at {bar.TransferProgress:P0}, {tasks.Errors.Count} error(s).");
                    }

                    if (string.Equals(
                            Environment.GetEnvironmentVariable("MAILBOX_PEEK"),
                            "transferbar",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    CaptureNextWindow();
                    var window = new SendReceiveProgressDialog(tasks, App.Settings, () => { });
                    window.Show(this);

                    // Refreshed through a state change, which is the sequence a real run puts it
                    // through and the one the pose never did: a row that says Processing and then
                    // says Completed is where stale text on a reused row shows up. Building the
                    // states first and showing the window once cannot produce it.
                    tasks.Report(new PollProgress(first, 0, 0, "Sending"));
                    window.Refresh();
                    tasks.Report(new PollProgress(first, 0, 0, "Connecting"));
                    window.Refresh();
                    tasks.Report(new PollProgress(first, 5, 9, "Downloading"));
                    window.Refresh();
                };
                break;
        }
    }

    private void WireHarnessCompose()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE")?.Trim().ToLowerInvariant()
            is { Length: > 0 } composeTab)
        {
            Opened += async (_, _) =>
            {
                var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
                WindowCapture.ApplyRequestedSize(compose);
                WindowCapture.HideWhileCapturing(compose);
                compose.SelectTab(composeTab == "1" ? "message" : composeTab);

                // The ribbon's left half is pale until the body has something in it, so the
                // harness can photograph either state.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_BODY") is { Length: > 0 })
                {
                    compose.PoseBodyText("The quick brown fox.");
                }

                // Posed before the window opens: a capture never gets a second layout pass,
                // so anything toggled afterwards photographs at its old size.
                compose.ShowOptionalFields();

                // The address rows are measured against the reference, and an empty field
                // cannot be measured — the thing being checked is where the text sits.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_HEADER") is { Length: > 0 })
                {
                    compose.PoseHeader("a.person@example.com", "b.person@example.com", "Subject line");
                }

                // Types into the To line and reports what the Auto-Complete List offered. A popup
                // is a separate surface and never appears in a capture, so the list is proved by
                // asking it rather than by photographing it.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_TYPE_TO") is { Length: > 0 } typed)
                {
                    compose.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        compose.PoseTyping(typed);
                        var (open, offered) = compose.ToLineCompletion;
                        Console.WriteLine($"Auto-complete on To for \"{typed}\": open={open}, offered={offered}");
                        foreach (var entry in compose.ToLineSuggestions)
                        {
                            Console.WriteLine($"  offers {entry}");
                        }
                    }, DispatcherPriority.Background);
                }

                // What the compose bar's "…" lists at this width, which a capture cannot show.
                if (Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant() == "overflow")
                {
                    compose.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        var items = compose.OverflowMenu();
                        Log.Info($"Harness: the compose \u2026 menu holds {items.Count}: {string.Join(" | ", items)}");
                    }, DispatcherPriority.Background);
                }

                // Presses Sign and Encrypt before the send, so what those two buttons do to a real
                // message can be read off the wire form rather than off a handler's name. Wants a
                // posed store to have any keys at all — see the seed's own ring.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_SEAL") is { Length: > 0 } seal)
                {
                    compose.Opened += (_, _) => Dispatcher.UIThread.Post(
                        () => compose.PressProtection(seal), DispatcherPriority.Loaded);
                }

                // Presses Send on a posed message, so what the window actually builds can be
                // read back out of the outbox and checked as MIME. Undo Send's hold keeps it
                // there long enough. The only way to audit the Send button is to press it.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_QUEUE") is { Length: > 0 })
                {
                    compose.PoseHeader("a.person@example.com", string.Empty, "Harness: queued");
                    compose.PoseRichBody();
                    compose.Opened += (_, _) => Dispatcher.UIThread.Post(
                        () => compose.PressSend(), DispatcherPriority.Background);
                }

                compose.Opened += async (_, _) =>
                {
                    await Task.Delay(700);
                    if (WindowCapture.RequestedPath is { } path)
                    {
                        WindowCapture.Capture(compose, path, WindowCapture.Scale);
                        Console.WriteLine($"Captured {path}");
                    }
                    Environment.Exit(0);
                };
                await compose.ShowDialog(this);
            };
        }

        // KeyTips exist only while Alt is held, which a capture cannot do. `tabs` poses the
        // first level; a tab id poses that tab's own. Driven through the real key handler
        // rather than around it, so what gets photographed is the traversal itself.
        if (Environment.GetEnvironmentVariable("MAILBOX_KEYTIPS")?.Trim().ToLowerInvariant()
            is { Length: > 0 } keyTips)
        {
            Opened += (_, _) =>
            {
                _keyTips.Begin(FirstLevelKeyTips());

                if (keyTips is not ("tabs" or "1"))
                {
                    // Slash-separated, so a third level can be reached: `home/zd` picks the Home
                    // tab and then the collapsed Delete group. The levels below the first are
                    // built after a layout pass, so each descent is posted rather than typed
                    // straight through.
                    var steps = keyTips.Split('/', StringSplitOptions.RemoveEmptyEntries);

                    if (_ribbon.Layout.FindTab(steps[0])?.KeyTip is { } tip)
                    {
                        foreach (var character in tip) _keyTips.HandleKey(KeyFor(character));
                        foreach (var step in steps.Skip(1)) Descend(step);
                    }
                }

                // Always reported, including for `tabs`. A capture cannot photograph a KeyTip
                // level that lives in a popup, so this line is the only way to see where the
                // traversal got to — and a level that reports nothing at all reads as a level
                // that is not there.
                Dispatcher.UIThread.Post(
                    () => Log.Info($"KeyTips: level {_keyTips.Depth}, {_keyTips.BadgeCount} badges"),
                    DispatcherPriority.Background);
            };
        }
    }

    /// <summary>
    /// Types one KeyTip after the level above it has been built. Harness only.
    /// </summary>
    /// <remarks>
    /// Each descent rebuilds something — a tab, or a group's flyout — and the badges for the
    /// level below cannot be placed until that has been laid out. Posting keeps the steps in
    /// the same order the dispatcher will run them in.
    /// </remarks>
    private void Descend(string tip)
        => Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var character in tip) _keyTips.HandleKey(KeyFor(character));
            },
            DispatcherPriority.Loaded);

    /// <summary>Types a KeyTip character into the traversal. Harness only.</summary>
    private static Avalonia.Input.Key KeyFor(char character)
        => char.IsAsciiDigit(character)
            ? Avalonia.Input.Key.D0 + (character - '0')
            : Avalonia.Input.Key.A + (char.ToUpperInvariant(character) - 'A');

    /// <summary>Opens the File view over everything, with its back arrow to return.</summary>
    private void ShowBackstage()
    {
        var host = this.FindControl<ContentControl>("BackstageHost")!;
        var backstage = new BackstageView();
        backstage.OptionsRequested += async (_, _) => await ShowOptions();
        backstage.AddAccountRequested += async (_, _) => await AddAccountAsync();
        backstage.ActionRequested += async (_, action) => await BackstageActionAsync(action);
        backstage.CloseRequested += (_, _) => CloseBackstage();

        // Exit quits, as the reference's does. Closing this window is how the application ends —
        // the lifetime is tied to it — and it runs the on-exit work the Options page asks for.
        backstage.ExitRequested += (_, _) => Close();

        host.Content = backstage;
        host.IsVisible = true;
    }

    private void CloseBackstage()
    {
        var host = this.FindControl<ContentControl>("BackstageHost")!;
        host.IsVisible = false;
        host.Content = null;
    }

    /// <summary>
    /// Opens a new message in its own window, as the reference does — not modally, because
    /// writing one message must not stop you reading another.
    /// </summary>
    /// <summary>
    /// A new message that opens on a draft rather than on nothing — what forwarding an item makes.
    /// </summary>
    /// <remarks>
    /// The same window a reply opens, prefilled the same way: a forwarded contact is a message
    /// with a vCard on it, and nothing about the window has to know which kind of item it came
    /// from.
    /// </remarks>
    private void NewMessage(Mailbox.Rendering.ReplyDraft draft, Mailbox.Rendering.ReplyKind kind)
    {
        var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);

        if (!App.MailOptions.AlwaysUseDefaultAccount
            && DataContext is ShellViewModel current
            && current.CurrentAddress is { Length: > 0 } address)
        {
            compose.SendFromAccount(address);
        }

        compose.Prefill(draft, kind);
        compose.Closed += (_, _) =>
        {
            if (DataContext is ShellViewModel shell) shell.Refresh();
        };

        compose.Queued += (_, e) => OnQueued(e);
        compose.Show(this);
    }

    private void NewMessage(Mailbox.Core.Compose.MailtoLink? mailto = null)
    {
        var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);

        // "Always use the default account when composing new messages" is off by default, and
        // off means what the reference means: a message written while looking at the work
        // account's inbox comes from the work account.
        if (!App.MailOptions.AlwaysUseDefaultAccount
            && DataContext is ShellViewModel current
            && current.CurrentAddress is { Length: > 0 } address)
        {
            compose.SendFromAccount(address);
        }

        // Filled from a mailto: link when the desktop handed us one — Mailbox as the system mail
        // client. Left blank for New Email.
        if (mailto is { } link) compose.ComposeFromMailto(link);

        compose.Closed += (_, _) =>
        {
            if (DataContext is ShellViewModel shell) shell.Refresh();
        };

        // §12's Undo Send. The window closes the moment it queues, so the offer to take it back
        // belongs here — and it is the shell that still exists to show it.
        compose.Queued += (_, e) => OnQueued(e);

        compose.Show(this);
    }

    /// <summary>
    /// Acts on the command line the desktop launched Mailbox with: a <c>mailto:</c> URI or
    /// <c>--compose</c> opens a compose window, filled from the link where there is one.
    /// </summary>
    /// <remarks>
    /// Cold start: Mailbox was not already running, so the desktop starts it with the URI. When
    /// it <em>is</em> running, single-instance activation hands the URI to the live process
    /// instead — that path calls <see cref="ComposeFromCommandLine"/> too.
    /// </remarks>
    public void ComposeFromCommandLine(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (Mailbox.Core.Compose.MailtoLink.Parse(arg) is { } link)
            {
                NewMessage(link);
                return;
            }

            if (string.Equals(arg, "--compose", StringComparison.Ordinal))
            {
                NewMessage();
                return;
            }

            // The desktop entry's other two actions. Their modules are Part IV, and a launcher
            // entry that opened nothing at all would read as broken, so each brings the window
            // forward and says what it waits on — as the rail buttons for the same modules do.
            if (string.Equals(arg, "--new-appointment", StringComparison.Ordinal))
            {
                if (DataContext is ShellViewModel s) s.StatusRight = "Appointments arrive with Phase 11.";
                return;
            }

            if (string.Equals(arg, "--new-contact", StringComparison.Ordinal))
            {
                RunCommand(PeopleCommands.NewContact.Id);
                return;
            }

            if (LooksLikeMailFile(arg))
            {
                OpenMessageFile(arg);
                return;
            }
        }
    }

    /// <summary>An <c>.eml</c> file, or a path the desktop handed us as one. The MIME-file side of §10.</summary>
    private static bool LooksLikeMailFile(string arg)
    {
        var path = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(arg, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : arg;

        return File.Exists(path)
            && Path.GetExtension(path).ToLowerInvariant() is ".eml" or ".mbox";
    }

    /// <summary>
    /// Opens a message file in its own window — a <c>.eml</c> double-clicked in the file manager,
    /// or handed over on the command line. Read through the same pane a stored message is, so a
    /// file from a stranger gets the same sanitizer and the same blocked-images bar.
    /// </summary>
    private void OpenMessageFile(string arg)
    {
        var path = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(arg, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : arg;

        try
        {
            var raw = File.ReadAllBytes(path);
            using var stream = new MemoryStream(raw);
            var message = MimeKit.MimeMessage.Load(stream);

            // No store behind a loose file, so the pane renders from the file rather than looking
            // up MIME by id — `mail` returns null, which is what tells it to.
            new MessageWindow(App.Themes, () => null, message, raw).Show(this);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the message file {path}.", ex);
            if (DataContext is ShellViewModel shell)
            {
                shell.StatusRight = $"Could not open {Path.GetFileName(path)}.";
            }
        }
    }

    /// <summary>
    /// A message has been queued to go: offer the way back while there is one, and send it when
    /// there is not.
    /// </summary>
    /// <remarks>
    /// "Send immediately when connected" is the Options page's name for the second half, and it
    /// is on by default: a message that sits in the outbox until somebody presses F9 is a
    /// message people think went. Undo Send's hold is respected — the send is scheduled for
    /// the moment it expires, and an undo before then cancels it.
    /// </remarks>
    private void OnQueued(QueuedMessageEventArgs queued)
    {
        var now = DateTimeOffset.UtcNow;
        var remaining = queued.Remaining(now);

        if (remaining > TimeSpan.Zero) _undoSend.Offer(queued, Withdraw);

        if (!App.MailOptions.SendImmediately) return;

        _pendingSend?.Stop();
        _pendingSend = new DispatcherTimer
        {
            // A moment past the hold, so the sender finds the row due rather than a second
            // early.
            Interval = remaining + TimeSpan.FromMilliseconds(750),
        };
        _pendingSend.Tick += (_, _) =>
        {
            _pendingSend?.Stop();
            _pendingSend = null;
            if (DataContext is ShellViewModel shell) _ = SendReceiveAsync(shell);
        };
        _pendingSend.Start();
    }

    /// <summary>The send waiting for a hold to expire, so an undo can cancel it.</summary>
    private DispatcherTimer? _pendingSend;

    /// <summary>
    /// Pulls a message back out of the outbox, if it is still there.
    /// </summary>
    /// <remarks>
    /// The store decides, not the toast: the withdrawal only succeeds while the item is queued
    /// and its hold has not expired, both checked in one transaction. So a send that started a
    /// moment ago wins and the reader is told the truth rather than being shown a compose window
    /// for a message already on its way.
    /// <para>
    /// It comes back as a window rather than as a draft, because the reason anybody presses Undo
    /// is that they want to change something.
    /// </para>
    /// </remarks>
    private void Withdraw(QueuedMessageEventArgs queued)
    {
        if (DataContext is not ShellViewModel shell) return;

        var account = App.Accounts.Find(queued.Address);

        if (account?.Mail.WithdrawOutbox(queued.OutboxId, DateTimeOffset.UtcNow)
            is not { Length: > 0 } raw)
        {
            shell.StatusRight = "That message has already gone.";
            return;
        }

        // Nothing to send now, so no need to connect for it. The next F9 or the schedule
        // will still send anything else that is waiting.
        _pendingSend?.Stop();
        _pendingSend = null;

        shell.Refresh();
        shell.StatusRight = "Message pulled back out of the Outbox.";

        try
        {
            using var stream = new MemoryStream(raw);
            var message = MimeKit.MimeMessage.Load(stream);

            var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
            compose.Restore(message);
            compose.Queued += (_, e) => OnQueued(e);
            compose.Closed += (_, _) => shell.Refresh();
            compose.Show(this);
        }
        catch (Exception ex)
        {
            // The message is out of the outbox and will not be sent, which is the half that
            // mattered. Failing to reopen it is worth a line rather than a dialog.
            Log.Warn("A withdrawn message could not be reopened.", ex);
            shell.StatusRight = "Message pulled back, but it could not be reopened.";
        }
    }

    private readonly UndoSendToast _undoSend = new();
    private readonly Notifications.DesktopNotifier _notifier = new();

    /// <summary>
    /// What the rules asked to be shown or played during the run: a New Item Alert as a toast
    /// with the rule's words and the message behind it, a Desktop Alert as the ordinary new-mail
    /// toast, a sound through the desktop's own player.
    /// </summary>
    private void ShowRuleAlerts()
    {
        while (App.Rules.Alerts.TryDequeue(out var alert))
        {
            switch (alert.Kind)
            {
                case Mailbox.Core.Rules.RuleActionKind.DisplayAlert:
                case Mailbox.Core.Rules.RuleActionKind.DesktopAlert:
                {
                    var described = DescribeArrival(alert.Address, alert.MessageId);
                    var summary = alert.Kind == Mailbox.Core.Rules.RuleActionKind.DisplayAlert && alert.Text.Length > 0
                        ? alert.Text
                        : described?.From ?? alert.Address;
                    var body = described is null
                        ? alert.RuleName
                        : (described.Subject.Length > 0 ? described.Subject : "(no subject)");

                    _notifier.Notify(ToastFor(new NewMailToast(summary, body, alert.Address, alert.MessageId)));
                    break;
                }

                case Mailbox.Core.Rules.RuleActionKind.PlaySound:
                    Notifications.Sounds.Play(alert.Text);
                    break;
            }
        }
    }

    /// <summary>What a new-mail toast says about one message, read back from its account's store.</summary>
    private static ArrivedMessage? DescribeArrival(string address, long id)
    {
        if (App.Accounts.Find(address)?.Mail.GetMessage(id) is not { } summary) return null;
        return new ArrivedMessage(summary.DisplayFrom, summary.Subject, summary.Preview);
    }

    /// <summary>
    /// The desktop notification for a toast: a click opens the message; a toast about one message
    /// also carries Reply, Delete and Mark Read (§10). Answers arrive on a background thread and
    /// are posted to the UI thread here, so the notifier never touches a window.
    /// </summary>
    private Notifications.Notification ToastFor(NewMailToast toast)
    {
        var actions = new List<Notifications.NotificationAction>
        {
            new(Notifications.NotificationAction.Default, "Open"),
        };

        if (toast.IsSingle)
        {
            actions.Add(new("reply", "Reply"));
            actions.Add(new("delete", "Delete"));
            actions.Add(new("read", "Mark Read"));
        }

        return new Notifications.Notification(toast.Summary, toast.Body)
        {
            Actions = actions,
            // A toast with buttons stays in the server's history, where the buttons still work
            // after the popup has gone; a bare count is worth a glance and not a log.
            Transient = !toast.IsSingle,
            Activated = action => Dispatcher.UIThread.Post(() => OnToastActivated(toast, action)),
        };
    }

    /// <summary>Harness only: the toast for a stored message, and one of its buttons.</summary>
    private void PoseNotification(string request)
    {
        if (DataContext is not ShellViewModel shell || shell.CurrentAddress is not { } address) return;

        var colon = request.LastIndexOf(':');
        var subject = colon > 0 ? request[..colon] : request;
        var action = colon > 0 ? request[(colon + 1)..].Trim().ToLowerInvariant() : null;

        var row = shell.Messages.FirstOrDefault(
            m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Log.Info($"Harness: no message matching '{subject}' to notify about.");
            return;
        }

        var result = new SendReceiveResult(
            [new AccountRunResult(address, 1, 0) { Arrived = [row.Id] }]);

        foreach (var toast in NewMailNotice.Toasts(result, DescribeArrival))
        {
            Log.Info($"Harness: toast “{toast.Summary}” / “{toast.Body.Replace('\n', '|')}” for #{toast.MessageId}.");
            _notifier.Notify(ToastFor(toast));

            if (action is { Length: > 0 })
            {
                Log.Info($"Harness: pressing {action} on the toast.");
                OnToastActivated(toast, action == "default" ? Notifications.NotificationAction.Default : action);
            }
        }
    }

    /// <summary>Acts on the button pressed on a new-mail toast.</summary>
    private void OnToastActivated(NewMailToast toast, string action)
    {
        if (DataContext is not ShellViewModel shell) return;

        Log.Info($"Notification action: {action} for {toast.Address}#{toast.MessageId?.ToString() ?? "-"}.");

        switch (action)
        {
            case Notifications.NotificationAction.Default:
                BringForward();
                if (toast.MessageId is { } shown) RevealMessage(shell, toast.Address, shown);
                break;

            case "reply":
                BringForward();
                if (toast.MessageId is { } id && RevealMessage(shell, toast.Address, id))
                {
                    Respond(shell, ReplyKind.Reply);
                }
                break;

            case "delete":
                if (toast.MessageId is { } gone && App.Accounts.Find(toast.Address) is { } account
                    && account.Mail.FolderWithRole(account.Account.Id, FolderRole.Deleted) is { } deleted)
                {
                    account.Mail.MoveMessages([gone], deleted.Id);
                    shell.Refresh();
                    shell.StatusRight = "1 message moved to Deleted Items.";
                }
                break;

            case "read":
                if (toast.MessageId is { } read && App.Accounts.Find(toast.Address) is { } owner)
                {
                    owner.Mail.SetRead([read], true);
                    shell.Refresh();
                }
                break;
        }
    }

    /// <summary>
    /// Shows the window and brings it to the front — from the tray, a notification, or a second
    /// launch handing over. Show as well as Activate, because a window started minimised to the
    /// tray has never been shown at all.
    /// </summary>
    public void BringForward()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Selects a message by account and id — its folder first, then the row — and scrolls the
    /// list to it. False when it is not there to select.
    /// </summary>
    /// <remarks>
    /// The list pushes its own selection back as it lays out over the rows the reveal replaced,
    /// the same trap the harness's posed selection hit — so the row is asserted again at
    /// <see cref="DispatcherPriority.Background"/>, below the layout pass that clears it. Loaded
    /// is not low enough from here: it runs before that layout when the reveal itself was
    /// posted, and the re-assertion finds nothing changed yet and changes nothing.
    /// </remarks>
    private bool RevealMessage(ShellViewModel shell, string address, long id)
    {
        if (shell.RevealMessage(address, id) is not { } row) return false;

        var list = this.FindControl<ListBox>("MessageList");
        Dispatcher.UIThread.Post(() =>
        {
            shell.SelectedRow = row;
            shell.SelectedMessage = row;
            list?.ScrollIntoView(row);
        }, DispatcherPriority.Background);

        return true;
    }

    private ReadingPaneBody? _reading;
    private readonly AttachmentStrip _attachments = new();

    /// <summary>The selected message as it arrived, kept for the source view and its own window.</summary>
    private MimeKit.MimeMessage? _openMessage;
    private byte[]? _openRaw;

    /// <summary>
    /// Puts the reading pane's body in place and keeps it on the selected message.
    /// </summary>
    /// <remarks>
    /// The message is parsed from what was received rather than from the row: the row carries a
    /// preview for the list, and a reading pane that rendered the preview would be showing a
    /// summary of the message instead of the message.
    /// </remarks>
    private void WireReadingPane(ShellViewModel shell)
    {
        _reading = new ReadingPaneBody(App.Themes, () => shell.CurrentMail)
        {
            MessageFontSize = shell.ReadingFontSize,
        };

        this.FindControl<ContentControl>("ReadingBody")!.Content = _reading;
        this.FindControl<ContentControl>("ReadingAttachments")!.Content = _attachments;

        // An answered invitation is two things: a write into the calendar, which the bar has
        // already done, and a message to the organizer, which only the shell can queue.
        _reading.InvitationAnswered += (_, answer) => SendInvitationReply(shell, answer);

        // The pane is the only thing that has opened the message, so it is the only thing that
        // knows whether the subject over it is the message's own or the placeholder an encrypted
        // one leaves outside itself. See RFC 9788 §4 and ShellViewModel.ReadingSubject.
        var pane = _reading;
        pane.HeaderChanged += (_, _) => shell.ReadFrom(pane.HeaderSubject, pane.HeaderFrom);

        shell.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ShellViewModel.SelectedMessage):
                    ShowSelectedMessage(shell);
                    break;

                case nameof(ShellViewModel.ReadingFontSize):
                    _reading.MessageFontSize = shell.ReadingFontSize;
                    break;
            }
        };

        ShowSelectedMessage(shell);
    }

    // ---- Read by looking: the Reading Pane options ---------------------------------------------

    private ViewModels.MessageRow? _viewed;
    private DispatcherTimer? _markReadTimer;

    /// <summary>
    /// The Reading Pane options at work: the message the pane showed until now is marked read
    /// when "mark item as read when selection changes" is on; the one it shows now is marked
    /// read after the wait when "mark items as read when viewed" is on — if it is still the one
    /// on show when the wait is up.
    /// </summary>
    private void MarkReadByLooking(ShellViewModel shell)
    {
        var options = App.MailOptions;
        var next = shell.SelectedMessage;

        if (_viewed is { } previous && !ReferenceEquals(previous, next) && previous.IsUnread && options.ReadingPaneMarkOnChange && shell.IsListed(previous))
        {
            shell.SetRead([previous], read: true, quiet: true);
        }

        _viewed = next;
        _markReadTimer?.Stop();
        _markReadTimer = null;

        if (next is not { IsUnread: true } || !options.ReadingPaneMarkOnView || !shell.ReadingPaneVisible) return;

        var wait = TimeSpan.FromSeconds(Math.Max(0, options.ReadingPaneMarkSeconds));
        if (wait == TimeSpan.Zero) { shell.SetRead([next], read: true, quiet: true); return; }

        _markReadTimer = new DispatcherTimer { Interval = wait };
        _markReadTimer.Tick += (_, _) =>
        {
            _markReadTimer?.Stop();
            _markReadTimer = null;
            if (ReferenceEquals(shell.SelectedMessage, next) && next.IsUnread && shell.IsListed(next)) shell.SetRead([next], read: true, quiet: true);
        };
        _markReadTimer.Start();
    }

    private void ShowSelectedMessage(ShellViewModel shell)
    {
        if (_reading is null) return;
        MarkReadByLooking(shell);

        var raw = shell.SelectedRaw;
        MimeKit.MimeMessage? message = null;

        if (raw is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(raw);
                message = MimeKit.MimeMessage.Load(stream);
            }
            catch (Exception ex)
            {
                // A message that will not parse is one the store holds and we cannot read. Say
                // so where it can be seen rather than showing an empty pane.
                Log.Warn("A message could not be parsed; showing its text.", ex);
            }
        }

        _openMessage = message;
        _openRaw = raw;

        // The row behind the pane, for a plugin's info-bar provider: the account and ids a
        // MimeMessage alone cannot say.
        _reading.PluginSummary = shell.SelectedMessage is { } selected
            ? new Mailbox.Plugins.Api.PluginMessageSummary(
                selected.Address, selected.Id, selected.FolderId, selected.Subject,
                selected.From, selected.Received, !selected.IsUnread)
            : null;

        // The pane first, then the strip from what the pane is showing: an encrypted message's
        // attachments are inside it, and the envelope has none worth offering.
        _reading.Show(message, shell.SelectedMessage?.Body ?? string.Empty, Verified(shell),
            suspectedJunk: shell.CurrentFolderRole == FolderRole.Junk);
        _attachments.Show(_reading.Carried);
        _ = _reading.ApplySenderPolicyAsync();
    }

    /// <summary>
    /// What was recorded about the selected message's signature when it arrived.
    /// </summary>
    /// <remarks>
    /// Read, never checked. Verifying resolves a name the sender chose, and §19 does not allow
    /// a lookup on the path that draws a message — so what the pane shows is what the poll
    /// found, and a message that was never checked says so rather than being checked now.
    /// </remarks>
    private static DkimResult? Verified(ShellViewModel shell)
    {
        if (shell.SelectedMessage is not { } selected) return null;
        if (shell.CurrentMail?.Authentication(selected.Id) is not { } stored) return null;

        return new DkimResult(
            Enum.TryParse<AuthVerdict>(stored.Dkim, ignoreCase: true, out var verdict)
                ? verdict
                : AuthVerdict.None,
            stored.SigningDomain);
    }

    /// <summary>
    /// View Source, which is one of the additions the shipped ribbon does not place.
    /// </summary>
    private void ShowMessageSource(ShellViewModel shell)
    {
        if (_openRaw is not { Length: > 0 } raw)
        {
            shell.StatusRight = "There is no message to show the source of.";
            return;
        }

        new MessageSourceWindow(shell.SelectedMessage?.Subject ?? string.Empty, raw).Show(this);
    }

    /// <summary>
    /// Printing goes through the engine, which is the only thing that knows how the message is
    /// laid out. There is nothing to print from the text fallback.
    /// </summary>
    private void PrintMessage(ShellViewModel shell)
    {
        if (_reading?.Print() != true)
        {
            shell.StatusRight = "This message cannot be printed: no web engine is available.";
        }
    }

    private async Task PrintToPdfAsync(ShellViewModel shell)
    {
        if (_reading is null) return;

        shell.StatusRight = await _reading.PrintToPdfAsync()
            ? "Saved as PDF."
            : "This message could not be written to PDF.";
    }

    /// <summary>
    /// The Table print style: the folder as a list.
    /// </summary>
    /// <remarks>
    /// Rendered into a window of its own rather than into the reading pane, which is showing a
    /// message the reader has not asked to lose. The window is the same engine and the same
    /// stylesheet, so the paper matches.
    /// </remarks>
    private void PrintList(ShellViewModel shell)
    {
        var rows = shell.PrintableRows();
        if (rows.Count == 0)
        {
            shell.StatusRight = "There is nothing in this folder to print.";
            return;
        }

        new PrintPreviewWindow(App.Themes, shell.SelectedFolderName, rows).Show(this);
    }

    /// <summary>Manage Quick Steps: the gallery's launcher. The ribbon follows the list when it closes.</summary>
    private async Task ManageQuickStepsAsync(ShellViewModel shell)
    {
        await new ManageQuickStepsDialog(shell.CurrentAccountForCategories()).ShowDialog(this);
    }

    /// <summary>New Search Folder, for an account or the current one, then selects what it made.</summary>
    private async Task NewSearchFolderAsync(ShellViewModel shell, OpenAccount? account)
    {
        var dialog = new NewSearchFolderDialog(account ?? shell.CurrentAccountForCategories());
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } made) return;

        var folder = made.Account.Mail.AddSearchFolder(made.Name, made.Query, DateTimeOffset.UtcNow);
        shell.SelectSearchFolder(folder.Id);
    }

    /// <summary>
    /// The folder pane's right-click menu: the search-folder entries on the Search Folders
    /// heading and on each search folder — New Search Folder, Customize This Search Folder,
    /// Rename Folder, Delete Folder.
    /// </summary>
    /// <summary>
    /// The reference's menu over a folder: New Folder, Rename, Delete, Mark All as Read, Clean Up
    /// Folder, Empty Folder, Properties. A role folder — Inbox, Sent Items, Deleted Items and
    /// the rest — cannot be renamed or deleted; Deleted Items and Junk Email offer Empty Folder.
    /// </summary>
    private void FillFolderMenu(MenuFlyout flyout, ShellViewModel shell, OpenAccount account, Folder folder)
    {
        void Entry(string header, Func<Task> run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await run();
            flyout.Items.Add(item);
        }

        var ordinary = folder.Role == FolderRole.None;
        Entry("New Folder…", () => NewFolderAsync(shell, account, folder.Id));
        Entry("Rename Folder", () => RenameFolderAsync(shell, account, folder), ordinary);
        Entry("Copy Folder…", () => CopyFolderAsync(shell, account, folder));
        Entry("Move Folder…", () => MoveFolderAsync(shell, account, folder), ordinary);
        Entry("Delete Folder", () => DeleteFolderAsync(shell, account, folder), ordinary);
        flyout.Items.Add(new Separator());
        Entry("Mark All as Read", () =>
        {
            var count = shell.MarkFolderRead(account, folder.Id);
            shell.StatusRight = count == 0 ? $"Nothing unread in {folder.Name}." : $"{count} message{(count == 1 ? "" : "s")} in {folder.Name} marked read.";
            return Task.CompletedTask;
        });
        Entry("Clean Up Folder", () =>
        {
            shell.SelectFolder(account, folder.Id);
            RunCommand(MailCommands.CleanUpFolder.Id);
            return Task.CompletedTask;
        });
        if (folder.Role is FolderRole.Deleted or FolderRole.Junk)
        {
            Entry("Empty Folder", () => EmptyFolderAsync(shell, account, folder));
        }

        flyout.Items.Add(new Separator());
        Entry(shell.IsFavourite(account, folder) ? "Remove from Favourites" : "Show in Favourites", () =>
        {
            shell.ToggleFavourite(account, folder);
            return Task.CompletedTask;
        });

        flyout.Items.Add(new Separator());
        Entry("Properties…", () => FolderPropertiesAsync(shell, account, folder));
    }

    /// <summary>The account's connection for a folder operation on the server — null for POP3, whose folders live here.</summary>
    /// <remarks>Asynchronous because reading the password is: see <see cref="AccountSettings.ToConnectionAsync"/>.</remarks>
    private static async Task<AccountConnection?> ConnectionForAsync(OpenAccount account)
        => account.Account.Protocol == MailProtocol.Imap
           && AccountSettings.Load(App.Settings, account.Account.Address) is { } settings
            ? await settings.ToConnectionAsync(account.Account, App.Secrets, App.OAuth).ConfigureAwait(true)
            : null;

    private async Task NewFolderAsync(ShellViewModel shell, OpenAccount account, long? parentId)
    {
        var dialog = new NewFolderDialog(account, parentId);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } wanted) return;

        try
        {
            var made = await Task.Run(async () => await new FolderManager(account.Mail).CreateAsync(await ConnectionForAsync(account), account.Account.Id, wanted.Name, wanted.ParentId));
            shell.SelectFolder(account, made.Id);
            shell.StatusRight = $"Folder “{made.Name}” created.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"New Folder failed: {ex.Message}");
            await Confirm.AskAsync(this, "Create New Folder", $"The folder could not be created: {ex.Message}", "OK", destructive: false);
        }
    }

    private async Task RenameFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var name = await Prompt.AskAsync(this, "Rename Folder", "New name:", folder.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == folder.Name) return;

        try
        {
            var all = account.Mail.Folders(account.Account.Id);
            var oldPath = ShellViewModel.FolderPath(all, folder);
            await Task.Run(async () => await new FolderManager(account.Mail).RenameAsync(await ConnectionForAsync(account), folder, name.Trim()));
            // A favourite keeps its place under its new name.
            var renamed = account.Mail.GetFolder(folder.Id);
            if (renamed is not null) App.Favourites.Repath(account.Account.Address, oldPath, ShellViewModel.FolderPath(account.Mail.Folders(account.Account.Id), renamed));
            shell.SelectFolder(account, folder.Id);
            shell.StatusRight = $"Folder renamed to “{name.Trim()}”.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Rename Folder failed: {ex.Message}");
            await Confirm.AskAsync(this, "Rename Folder", $"The folder could not be renamed: {ex.Message}", "OK", destructive: false);
        }
    }

    /// <summary>The picker every folder dialog is, with New… making folders the way New Folder does — on the server first for IMAP.</summary>
    private FolderPickerDialog FolderPicker(string title, string? prompt, (OpenAccount, long?)? preselect, bool allowRoot, (OpenAccount, long)? exclude = null)
    {
        var dialog = new FolderPickerDialog(title, prompt, App.Accounts.All, preselect, allowRoot, exclude)
        {
            MakeFolder = async (account, name, parent) =>
            {
                try
                {
                    return await Task.Run(async () => await new FolderManager(account.Mail).CreateAsync(await ConnectionForAsync(account), account.Account.Id, name, parent));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"New Folder failed: {ex.Message}");
                    return null;
                }
            },
        };
        return dialog;
    }

    /// <summary>Ctrl+Y: choose a folder from any account and open it.</summary>
    private async Task GoToFolderAsync(ShellViewModel shell)
    {
        var current = shell.SelectedFolder is { } node ? shell.FolderOf(node) : null;
        var dialog = FolderPicker("Go to Folder", null, current is { } c ? (c.Account, c.Folder.Id) : null, allowRoot: false);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { Folder: { } folder } chosen) return;

        shell.SelectFolder(chosen.Account, folder.Id);
    }

    /// <summary>Move Folder…: under another folder of the same account, or to its top; the tree comes along.</summary>
    private async Task MoveFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var dialog = FolderPicker("Move Folder", $"Move the selected folder to the folder:",
            (account, folder.ParentId), allowRoot: true, exclude: (account, folder.Id));
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;

        if (!string.Equals(chosen.Account.Account.Address, account.Account.Address, StringComparison.OrdinalIgnoreCase))
        {
            await Confirm.AskAsync(this, "Move Folder", "A folder can be moved within its own account. To put its mail in another account, move the messages.", "OK", destructive: false);
            return;
        }

        try
        {
            var oldPath = ShellViewModel.FolderPath(account.Mail.Folders(account.Account.Id), folder);
            var moved = await Task.Run(async () => await new FolderManager(account.Mail).MoveAsync(await ConnectionForAsync(account), folder, chosen.Folder?.Id));
            if (moved && account.Mail.GetFolder(folder.Id) is { } now)
            {
                App.Favourites.Repath(account.Account.Address, oldPath, ShellViewModel.FolderPath(account.Mail.Folders(account.Account.Id), now));
            }

            shell.Refresh();
            if (moved) shell.SelectFolder(account, folder.Id);
            shell.StatusRight = moved
                ? $"Folder “{folder.Name}” moved to {chosen.Folder?.Name ?? account.Account.Address}."
                : $"“{folder.Name}” cannot be moved into itself.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Move Folder failed: {ex.Message}");
            await Confirm.AskAsync(this, "Move Folder", $"The folder could not be moved: {ex.Message}", "OK", destructive: false);
        }
    }

    /// <summary>Copy Folder…: a new folder of the same name and contents, subfolders included, under the chosen one.</summary>
    private async Task CopyFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var dialog = FolderPicker("Copy Folder", $"Copy the selected folder to the folder:",
            (account, folder.ParentId), allowRoot: true, exclude: (account, folder.Id));
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;

        if (!string.Equals(chosen.Account.Account.Address, account.Account.Address, StringComparison.OrdinalIgnoreCase))
        {
            await Confirm.AskAsync(this, "Copy Folder", "A folder can be copied within its own account. To put its mail in another account, copy the messages.", "OK", destructive: false);
            return;
        }

        try
        {
            var made = await Task.Run(async () => await new FolderManager(account.Mail).CopyAsync(await ConnectionForAsync(account), account.Account.Id, folder, chosen.Folder?.Id));
            shell.Refresh();
            shell.SelectFolder(account, made.Id);
            shell.StatusRight = $"Folder “{folder.Name}” copied to {chosen.Folder?.Name ?? account.Account.Address}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Copy Folder failed: {ex.Message}");
            await Confirm.AskAsync(this, "Copy Folder", $"The folder could not be copied: {ex.Message}", "OK", destructive: false);
        }
    }

    private async Task DeleteFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var go = await Confirm.AskAsync(this, "Delete Folder",
            $"Delete the folder “{folder.Name}”, its subfolders and everything in them?", "Delete");
        if (!go) return;

        try
        {
            await Task.Run(async () => await new FolderManager(account.Mail).DeleteAsync(await ConnectionForAsync(account), folder));
            shell.Refresh();
            shell.StatusRight = $"Folder “{folder.Name}” deleted.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Delete Folder failed: {ex.Message}");
            await Confirm.AskAsync(this, "Delete Folder", $"The folder could not be deleted: {ex.Message}", "OK", destructive: false);
        }
    }

    private async Task EmptyFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var total = account.Mail.Messages(folder.Id, int.MaxValue).Count;
        if (total == 0) { shell.StatusRight = $"{folder.Name} is already empty."; return; }
        var go = await Confirm.AskAsync(this, "Empty Folder",
            $"Permanently delete {total:N0} item{(total == 1 ? "" : "s")} from {folder.Name}?", "Delete");
        if (!go) return;
        var count = shell.EmptyFolder(account, folder.Id);
        shell.StatusRight = $"{folder.Name} emptied: {count:N0} item{(count == 1 ? "" : "s")}.";
    }

    private async Task FolderPropertiesAsync(ShellViewModel shell, OpenAccount account, Folder folder, int tab = 0)
    {
        var dialog = new FolderPropertiesDialog(account, folder, tab);
        await dialog.ShowDialog(this);
        if (dialog.Policy is { } policy)
        {
            account.Mail.SetFolderAutoArchive(folder.Id, policy.Mode == Mailbox.Core.Archive.FolderArchiveMode.Default ? null : policy.ToJson());
        }

        if (dialog.NewName is { } name && name != folder.Name)
        {
            await Task.Run(async () => await new FolderManager(account.Mail).RenameAsync(await ConnectionForAsync(account), folder, name));
            shell.SelectFolder(account, folder.Id);
        }
    }

    private void WireFolderMenu(ShellViewModel shell)
    {
        if (this.FindControl<ListBox>("FolderList") is not { } folders) return;

        var flyout = new MenuFlyout();
        ViewModels.FolderNode? pressed = null;

        folders.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(folders).Properties.IsRightButtonPressed) return;
            pressed = (e.Source as Control)?.DataContext as ViewModels.FolderNode;
        }, RoutingStrategies.Tunnel);

        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();

            // An ordinary folder: the reference's menu over it.
            if (pressed is not null && shell.FolderOf(pressed) is { } where)
            {
                FillFolderMenu(flyout, shell, where.Account, where.Folder);
                return;
            }

            if (pressed is null || shell.SearchFolderAccount(pressed) is not { } account)
            {
                flyout.Items.Add(new MenuItem { Header = "No actions here yet", IsEnabled = false });
                return;
            }

            var make = new MenuItem { Header = "New Search Folder…" };
            make.Click += async (_, _) => await NewSearchFolderAsync(shell, account);
            flyout.Items.Add(make);

            if (shell.SearchFolderOf(pressed) is not { } search) return;

            flyout.Items.Add(new Separator());

            var customize = new MenuItem { Header = "Customize This Search Folder…" };
            customize.Click += async (_, _) =>
            {
                var dialog = new NewSearchFolderDialog(account, search);
                await dialog.ShowDialog(this);
                if (dialog.Result is not { } edited) return;
                account.Mail.UpdateSearchFolder(search.Id, edited.Name, edited.Query);
                shell.SelectSearchFolder(search.Id);
            };
            flyout.Items.Add(customize);

            var rename = new MenuItem { Header = "Rename Folder" };
            rename.Click += async (_, _) =>
            {
                var name = await Prompt.AskAsync(this, "Rename Folder", "New name:", search.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                account.Mail.UpdateSearchFolder(search.Id, name.Trim(), search.Query);
                shell.SelectSearchFolder(search.Id);
            };
            flyout.Items.Add(rename);

            var delete = new MenuItem { Header = "Delete Folder" };
            delete.Click += async (_, _) =>
            {
                var go = await Confirm.AskAsync(this, "Delete Folder",
                    $"Delete the search folder “{search.Name}”? The mail it shows stays where it is.", "Delete");
                if (!go) return;
                account.Mail.DeleteSearchFolder(search.Id);
                shell.Refresh();
            };
            flyout.Items.Add(delete);
        };

        folders.ContextFlyout = flyout;
    }

    private async Task ShowRecoverDeletedAsync(ShellViewModel shell)
    {
        await new RecoverDeletedItemsDialog(shell.CurrentAddress).ShowDialog(this);
        shell.Refresh();
    }

    /// <summary>Opens the selected message in a window of its own, as a double-click does.</summary>
    private void OpenMessageWindow(ShellViewModel shell)
    {
        if (_openMessage is not { } message)
        {
            shell.StatusRight = "There is no message to open.";
            return;
        }

        // A draft opens to be written, not read. Save and Send act on the row it came from,
        // so it does not multiply, and sending it takes it out of Drafts.
        if (shell.CurrentFolderRole == FolderRole.Drafts && shell.SelectedMessage is { } draft)
        {
            var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
            compose.EditDraft(draft.Id, message);
            compose.Queued += (_, e) => OnQueued(e);
            compose.Closed += (_, _) => shell.Refresh();
            compose.Show(this);
            return;
        }

        new MessageWindow(App.Themes, () => shell.CurrentMail, message, _openRaw, Verified(shell))
            .Show(this);
    }

    /// <summary>Opens the Options dialog modally over the shell, optionally on a given page.</summary>
    /// <remarks>
    /// The two customization pages edit the ribbon and the toolbar as they go, so the shell
    /// takes both back on the way out rather than waiting for OK. The reference applies them on
    /// OK; every other page in this dialog has always written as it went, and a Cancel that
    /// undid two of the thirteen would be the confusing half of both behaviours.
    /// </remarks>
    private async Task ShowOptions(string? page = null)
    {
        var dialog = new OptionsWindow(App.Themes, page);
        await dialog.ShowDialog<bool>(this);

        if (!dialog.CustomizationChanged) return;

        _ribbon.Layout = App.MailRibbon();

        if (DataContext is ShellViewModel shell)
        {
            shell.RebuildQuickAccess();
            WireToolbarCommands(shell);
            _ribbon.IsQuickAccessVisible = App.QuickAccess.IsVisible;
        }
    }

    // ------------------------------------------------------------------------------------
    // Calendar peek and dock
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The peek in the layer, whichever module opened it — the calendar's or People's, the rail
    /// opening two kinds now.
    /// </summary>
    private Control? _peekPopup;

    /// <summary>The floating peek when it is the calendar's, which is what its own poses want.</summary>
    private PeekView? _floatingPeek => _peekPopup as PeekView;

    /// <summary>
    /// Gives each rail module a command. Mail and Calendar switch the window over; the rest say
    /// which phase brings them, which is better than a button that does nothing.
    /// </summary>
    /// <remarks>
    /// Until Phase 11 the Calendar button toggled the peek, because there was no module to switch
    /// to. Now it switches, and the peek is what a <em>hover</em> over the button opens — the
    /// reference's own arrangement, and the reason the peek was built as its own control.
    /// </remarks>
    private void WireRail(ShellViewModel shell)
    {
        foreach (var tab in shell.Modules)
        {
            var module = tab.Module;
            tab.Activate = new RelayCommand(() =>
            {
                ClosePeek();
                SwitchModule(shell, module);
            });
        }
    }

    /// <summary>
    /// The dwell before a hover over the rail's Calendar icon opens the peek, and the grace after
    /// the pointer leaves it — long enough to cross the gap between the icon and the peek.
    /// </summary>
    private static readonly TimeSpan PeekDwell = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PeekGrace = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? _peekTimer;

    /// <summary>Which module's peek the pointer is waiting on, or null when none is.</summary>
    private MailboxModule? _peekModule;

    /// <summary>
    /// Pointer over a rail icon. Calendar and People have peeks; the rest have nothing to show.
    /// </summary>
    private void RailPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ModuleTab tab } || !HasPeek(tab.Module)) return;

        // A section already docked in the To-Do Bar has nothing to pop up: it is on screen.
        if (DataContext is ShellViewModel shell
            && ((tab.Module == MailboxModule.Calendar && shell.IsCalendarDocked)
                || (tab.Module == MailboxModule.People && shell.ArePeopleDocked)))
        {
            return;
        }

        _peekModule = tab.Module;
        SchedulePeek(PeekDwell, open: true);
    }

    private void RailPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ModuleTab tab } || !HasPeek(tab.Module)) return;
        SchedulePeek(PeekGrace, open: false);
    }

    private static bool HasPeek(MailboxModule module)
        => module is MailboxModule.Calendar or MailboxModule.People;

    /// <summary>
    /// Opens or closes the peek after a pause, replacing whatever was already waiting. One timer
    /// for both: crossing from the icon to the peek and back is a stream of these, and a second
    /// timer would let an old close fire under a new open.
    /// </summary>
    private void SchedulePeek(TimeSpan after, bool open)
    {
        _peekTimer?.Stop();
        _peekTimer = new DispatcherTimer { Interval = after };
        _peekTimer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (open) OpenPeek();
            else ClosePeek();
        };
        _peekTimer.Start();
    }

    private void TogglePeek()
    {
        if (_peekPopup is not null) ClosePeek();
        else OpenPeek();
    }

    private void OpenPeek()
    {
        if (_peekPopup is not null) return;
        if (DataContext is not ShellViewModel shell) return;

        // People's peek is the other one the rail opens, and it is built the same way: a popup in
        // the layer, anchored beside the icon, kept open by the pointer being in it.
        if (_peekModule == MailboxModule.People)
        {
            OpenPeoplePeek(shell);
            return;
        }

        if (shell.IsCalendarDocked) return;
        var peek = BuildPeek(shell, docked: false);

        ShowPeekPopup(peek);
    }

    /// <summary>
    /// Puts a peek in the layer beside the rail, and keeps it there while the pointer is in it.
    /// </summary>
    /// <remarks>
    /// Anchored just right of the rail, level with the icon that opened it — the position the
    /// reference uses so a peek reads as belonging to that module. Its own height often exceeds
    /// what is above the icon, so it is held inside the layer. Both peeks come through here, which
    /// is what stops the second one growing its own idea of a dwell.
    /// </remarks>
    private void ShowPeekPopup(Control peek)
    {
        var layer = this.FindControl<Canvas>("PeekLayer")!;
        Canvas.SetLeft(peek, PeekGap);
        Canvas.SetTop(peek, PeekTop(layer));

        // The pointer crossing into the peek is what keeps it open, and leaving it is what
        // closes it — the icon's own exit fires as soon as the gap is crossed.
        peek.PointerEntered += (_, _) => _peekTimer?.Stop();
        peek.PointerExited += (_, _) => SchedulePeek(PeekGrace, open: false);

        layer.Children.Add(peek);
        _peekPopup = peek;
    }

    /// <summary>What separates the peek from the rail, measured off the reference.</summary>
    private const double PeekGap = 6;

    /// <summary>
    /// Where the peek's top goes: level with the bottom of the Calendar icon, which in this
    /// window is above the layer itself, so in practice it is held against the layer's top.
    /// </summary>
    private double PeekTop(Canvas layer)
    {
        var icon = RailButton(MailboxModule.Calendar);
        var bottom = icon?.TranslatePoint(new Point(0, icon.Bounds.Height), layer)?.Y ?? 0;
        var room = layer.Bounds.Height > 0 ? layer.Bounds.Height : Bounds.Height;
        var height = PeekLayout.PopupHeight + (2 * (PeekLayout.FrameY + PeekLayout.Outline));
        return Math.Max(0, Math.Min(bottom, room - height));
    }

    /// <summary>The rail's button for a module, which the hover and the anchor both want.</summary>
    private Control? RailButton(MailboxModule module)
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (button.DataContext is ModuleTab tab && tab.Module == module) return button;
        }

        return null;
    }

    private void ClosePeek()
    {
        _peekTimer?.Stop();
        if (_peekPopup is null) return;
        this.FindControl<Canvas>("PeekLayer")!.Children.Remove(_peekPopup);
        _peekPopup = null;
    }

    private Control? _floatingRibbon;

    /// <summary>
    /// Shows the ribbon body over the content while the ribbon is collapsed to its tab strip,
    /// or takes it away when passed null.
    /// </summary>
    /// <remarks>
    /// It goes on the same overlay canvas the calendar peek uses. An ordinary control in the
    /// window rather than a popup, so it clips and z-orders with everything else and the
    /// fidelity harness can photograph it — a popup is a separate surface and would not appear
    /// in a window capture at all.
    /// </remarks>
    private void ShowFloatingRibbon(Control? body)
    {
        var layer = this.FindControl<Canvas>("PeekLayer")!;

        if (_floatingRibbon is not null)
        {
            layer.Children.Remove(_floatingRibbon);
            _floatingRibbon = null;
        }

        if (body is null) return;

        // Full width of the workspace, hard against its top edge, so it reads as the ribbon
        // having unrolled rather than as a panel that happens to be there.
        body.Width = layer.Bounds.Width > 0 ? layer.Bounds.Width : Width;
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);

        layer.Children.Add(body);
        _floatingRibbon = body;
    }

    /// <summary>
    /// The little corner button, and View · To-Do Bar · Calendar: the floating peek becomes the
    /// bar's calendar section down the right edge, where it takes the reading pane's place.
    /// </summary>
    private void DockPeek()
    {
        ClosePeek();
        if (DataContext is not ShellViewModel shell) return;

        shell.IsCalendarDocked = true;
        RebuildToDoBar(shell);
    }

    private void UndockPeek()
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.IsCalendarDocked = false;
        RebuildToDoBar(shell);
    }

    /// <summary>View · To-Do Bar · Tasks, which is the bar's other section.</summary>
    private void ShowToDoTasks(ShellViewModel shell, bool showing)
    {
        shell.AreTasksDocked = showing;
        RebuildToDoBar(shell);
    }

    /// <summary>
    /// Puts the bar's sections back in the pane, or takes the pane away when none is left.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than shown and hidden, because how tall the calendar section is depends on
    /// whether it is sharing the pane with the tasks — a section that stayed as it was would
    /// keep the whole height it had when it was alone.
    /// </remarks>
    private void RebuildToDoBar(ShellViewModel shell)
    {
        var host = this.FindControl<ContentControl>("DockHost")!;
        if (!shell.IsToDoBarVisible)
        {
            host.Content = null;
            return;
        }

        host.Content = new ToDoBar(
            shell.IsCalendarDocked ? BuildPeek(shell, docked: true) : null,
            shell.AreTasksDocked ? BuildToDoTasks(shell) : null,
            shell.ArePeopleDocked ? BuildToDoPeople(shell) : null);
    }

    /// <summary>View · To-Do Bar · People, which is the bar's third section.</summary>
    private void ShowToDoPeople(ShellViewModel shell, bool showing)
    {
        shell.ArePeopleDocked = showing;
        RebuildToDoBar(shell);
    }

    /// <summary>The To-Do Bar's calendar section, when it is showing.</summary>
    private PeekView? DockedPeek => (this.FindControl<ContentControl>("DockHost")?.Content as ToDoBar)?.Peek;

    /// <summary>
    /// What the bar is holding, which is the only way to check a pane made of two drawn views.
    /// </summary>
    private void LogToDoBar(ShellViewModel shell)
    {
        if (this.FindControl<ContentControl>("DockHost")?.Content is not ToDoBar bar)
        {
            Log.Info("Harness: the To-Do Bar is off.");
            return;
        }

        Log.Info($"Harness: To-Do Bar — calendar {(shell.IsCalendarDocked ? "on" : "off")}, "
            + $"tasks {(shell.AreTasksDocked ? "on" : "off")}, "
            + $"people {(shell.ArePeopleDocked ? "on" : "off")}; "
            + $"{bar.Peek?.Agenda.Count ?? 0} appointment(s), {bar.Tasks?.Rows.Count ?? 0} task(s), "
            + $"{bar.People?.Rows.Count ?? 0} favourite(s).");

        foreach (var row in bar.People?.Rows ?? [])
        {
            Log.Info($"Harness: To-Do Bar favourite {row.Named()}.");
        }

        foreach (var row in bar.Peek?.Agenda ?? [])
        {
            Log.Info($"Harness: To-Do Bar appointment {row.Time} {row.Subject}.");
        }

        foreach (var row in bar.Tasks?.Rows ?? [])
        {
            Log.Info($"Harness: To-Do Bar task “{row.Summary}” — {Mailbox.Scheduling.TaskBook.Heading(row.Band)}.");
        }

        // The bar's own list takes the same press the module's does, which is what proves the
        // pane writes rather than only draws.
        if (bar.Tasks is { } list && Environment.GetEnvironmentVariable("MAILBOX_TASK_PRESS") is { Length: > 0 } press)
        {
            PressTask(shell, list, press.Trim());
        }
    }

    /// <summary>Reads the store again into the bar's tasks section, after a write anywhere.</summary>
    private void RefreshToDoTasks()
    {
        if ((this.FindControl<ContentControl>("DockHost")?.Content as ToDoBar)?.Tasks is not { } tasks) return;
        tasks.Rows = new Mailbox.Scheduling.TaskBook(App.Pim, App.Mailboxes).Rows(CalendarToday);
    }

    /// <summary>
    /// Wires the client-side-decorated caption: buttons on the right, drag anywhere on the
    /// bar, double-click to maximize. Avalonia gives us the extended client area but none of
    /// the behaviour, so all of it is ours.
    /// </summary>

    /// <summary>
    /// Puts the shell into a state a screenshot can be taken of. Every one of these is
    /// reachable by clicking, and a capture cannot click — so a control wired to state nobody
    /// photographs is a control nobody has actually checked.
    /// </summary>
    /// <summary>What MAILBOX_SELECT asked for, re-asserted once the list has laid out.</summary>
    private static ViewModels.MessageRow? _pendingSelection;

    private static void ApplyHarnessState(ShellViewModel shell)
    {
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_STATE") ?? string.Empty;

        foreach (var state in wanted.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (state.Trim())
            {
                case "unread": shell.ShowUnread.Execute(null); break;
                case "sort-from": shell.SortBy("From"); break;
                case "sort-subject": shell.SortBy("Subject"); break;
                case "sort-asc": shell.ToggleSort.Execute(null); break;
                case "group-collapsed": shell.ToggleGroupCollapsed("Today"); break;
                case "nav-collapsed": shell.ToggleNav.Execute(null); break;
                case "no-reading": shell.HideReadingPane.Execute(null); break;
                case "reading-bottom": shell.ReadingPaneAtBottom = true; shell.ReadingPaneVisible = true; break;
                case "zoom-in": shell.ZoomIn.Execute(null); break;
                case "zoom-out": shell.ZoomOut.Execute(null); break;
                case "attachments": shell.Filter = ShellViewModel.ListFilter.HasAttachments; break;
                case "flagged": shell.Filter = ShellViewModel.ListFilter.Flagged; break;
                case "important": shell.Filter = ShellViewModel.ListFilter.Important; break;
                case "categorized": shell.Filter = ShellViewModel.ListFilter.Categorized; break;
                case "thisweek": shell.Filter = ShellViewModel.ListFilter.ThisWeek; break;
                case "focused": shell.FocusedInboxOn = true; break;
                case "other": shell.FocusedInboxOn = true; shell.ShowOther = true; break;
                case "conversations": shell.ShowAsConversations = true; break;
                case "view-compact": shell.ChangeView(Mailbox.Core.Views.MailView.CompactName); break;
                case "view-single": shell.ChangeView(Mailbox.Core.Views.MailView.SingleName); break;
                case "view-preview": shell.ChangeView(Mailbox.Core.Views.MailView.PreviewName); break;
                default: Log.Warn($"Unknown MAILBOX_STATE: {state}"); break;
            }
        }

        // The posed selection, after the posed state: a state that changes what the list shows
        // — Focused / Other, unread only — replaces the rows, and a row chosen before that would
        // be one the list no longer holds. Which message the reading pane is showing: the bars
        // above it only appear for certain mail — one with a tracking pixel, one pretending to
        // be someone else — and a capture cannot click a row to find one.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } subject)
        {
            var match = shell.Messages.FirstOrDefault(
                m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase));

            // Both, and after the window has opened. SelectedMessage is what the pane shows and
            // nothing binds it, so it survives being set here; SelectedRow is what the list binds,
            // and the list pushes its own selection back as it lays out, so one set now is gone
            // by the time anything reads it — the same trap MAILBOX_FOLDER hit. The commands over
            // a selection read the list, so the row has to be selected there too, and it has to
            // be selected after the layout that would have cleared it.
            if (match is not null)
            {
                shell.SelectedMessage = match;
                shell.SelectedRow = match;
                _pendingSelection = match;
            }
        }
    }

    /// <summary>
    /// The arrangement menu behind the "By Date" label. Built from the arrangement list rather
    /// than written out, so adding one is a single entry in the engine.
    /// </summary>
    private void WireArrangeMenu(ShellViewModel shell)
    {
        if (this.FindControl<Button>("ArrangeButton") is not { } button) return;
        button.Flyout = ArrangeFlyout(shell);
    }

    /// <summary>The arrangement menu — behind the list's "By Date" label and the ribbon's Arrange By alike.</summary>
    private static MenuFlyout ArrangeFlyout(ShellViewModel shell)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        void Build()
        {
            var items = new List<MenuItem>();

            foreach (var arrangement in shell.Arrangements_)
            {
                var chosen = arrangement;
                var item = new MenuItem
                {
                    Header = Store.Lists.Arrangements.Label(chosen),
                    Icon = chosen == shell.Arrangement
                        ? new TextBlock { Text = "\u2713" }
                        : null,
                };
                item.Click += (_, _) => { shell.Arrangement = chosen; Build(); };
                items.Add(item);
            }

            var conversations = new MenuItem
            {
                Header = shell.ShowAsConversations
                    ? "Show as Conversations ✓"
                    : "Show as Conversations",
            };
            conversations.Click += (_, _) =>
            {
                shell.ShowAsConversations = !shell.ShowAsConversations;
                Build();
            };

            var newest = new MenuItem { Header = shell.SortDescending ? "Newest on top ✓" : "Newest on top" };
            newest.Click += (_, _) => { shell.SortDescending = true; Build(); };

            var oldest = new MenuItem { Header = shell.SortDescending ? "Oldest on top" : "Oldest on top ✓" };
            oldest.Click += (_, _) => { shell.SortDescending = false; Build(); };

            flyout.ItemsSource = items.Concat([conversations, newest, oldest]).ToList();
        }

        Build();
        return flyout;
    }

    // ---- The View tab's first cluster ------------------------------------------------------------

    /// <summary>
    /// Change View: the gallery's three, the account's saved views, then Manage Views, Save
    /// Current View As a New View and Apply Current View to Other Mail Folders — the reference's
    /// menu, each entry a command.
    /// </summary>
    private void ShowChangeViewMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, Action run, bool ticked = false, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = ticked ? new TextBlock { Text = "\u2713" } : null };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        var current = shell.CurrentViewName;
        foreach (var name in shell.ViewNames)
        {
            var chosen = name;
            Entry(chosen, () => shell.ChangeView(chosen), ticked: string.Equals(chosen, current, StringComparison.OrdinalIgnoreCase));
            if (chosen == Mailbox.Core.Views.MailView.PreviewName && shell.ViewNames.Count > 3) flyout.Items.Add(new Separator());
        }

        flyout.Items.Add(new Separator());
        Entry(ViewCommands.ManageViews.Label, () => _ = ManageViewsAsync(shell), enabled: shell.ViewAccount is not null);
        Entry(ViewCommands.SaveViewAs.Label, () => _ = SaveViewAsAsync(shell), enabled: shell.ViewAccount is not null);
        Entry(ViewCommands.ApplyViewToFolders.Label, () => _ = ApplyViewToFoldersAsync(shell), enabled: shell.ViewAccount is not null);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Current View: View Settings… and Reset View.</summary>
    private void ShowCurrentViewMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();
        var settings = new MenuItem { Header = ViewCommands.OpenViewSettings.Label };
        settings.Click += (_, _) => _ = ShowViewSettingsAsync(shell);
        var reset = new MenuItem { Header = ViewCommands.ResetView.Label };
        reset.Click += (_, _) => shell.ResetView();
        flyout.Items.Add(settings);
        flyout.Items.Add(reset);
        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Layout: the folder pane, the reading pane and the To-Do Bar, as the reference's menu has them.</summary>
    private void ShowLayoutMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        MenuItem Sub(string header) { var item = new MenuItem { Header = header }; flyout.Items.Add(item); return item; }
        void Entry(MenuItem parent, string header, Action run, bool ticked)
        {
            var item = new MenuItem { Header = header, Icon = ticked ? new TextBlock { Text = "\u2713" } : null };
            item.Click += (_, _) => run();
            parent.Items.Add(item);
        }

        var folder = Sub("Folder Pane");
        Entry(folder, "Normal", () => shell.NavCollapsed = false, !shell.NavCollapsed);
        Entry(folder, "Minimized", () => shell.NavCollapsed = true, shell.NavCollapsed);

        var reading = Sub("Reading Pane");
        Entry(reading, "Right", () => { shell.ReadingPaneAtBottom = false; shell.ReadingPaneVisible = true; }, shell.ReadingPaneVisible && !shell.ReadingPaneAtBottom);
        Entry(reading, "Bottom", () => { shell.ReadingPaneAtBottom = true; shell.ReadingPaneVisible = true; }, shell.ReadingPaneVisible && shell.ReadingPaneAtBottom);
        Entry(reading, "Off", () => shell.ReadingPaneVisible = false, !shell.ReadingPaneVisible);
        reading.Items.Add(new Separator());
        var options = new MenuItem { Header = "Options…" };
        options.Click += async (_, _) => await new ReadingPaneOptionsDialog(App.MailOptions).ShowDialog(this);
        reading.Items.Add(options);

        // To-Do Bar · Calendar is the docked pane, not the popup: the menu's own tick reads
        // "is the calendar docked", and it did not use to be what the entry set. Each entry is a
        // section of the bar and switches only itself, as the reference's own three do.
        var todo = Sub("To-Do Bar");
        Entry(todo, "Calendar", () => { if (shell.IsCalendarDocked) UndockPeek(); else DockPeek(); }, shell.IsCalendarDocked);
        Entry(todo, "Tasks", () => ShowToDoTasks(shell, !shell.AreTasksDocked), shell.AreTasksDocked);
        Entry(todo, "People", () => ShowToDoPeople(shell, !shell.ArePeopleDocked), shell.ArePeopleDocked);
        Entry(todo, "Off", () =>
        {
            shell.AreTasksDocked = false;
            shell.ArePeopleDocked = false;
            if (shell.IsCalendarDocked) UndockPeek();
            else RebuildToDoBar(shell);
        }, !shell.IsToDoBarVisible);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>View Settings…: Advanced View Settings over the folder's view; OK applies, Reset Current View resets.</summary>
    private async Task ShowViewSettingsAsync(ShellViewModel shell)
    {
        var dialog = new AdvancedViewSettingsDialog(shell.CurrentView, shell.ViewAccount);
        await dialog.ShowDialog(this);
        if (dialog.ResetRequested) { shell.ResetView(); return; }
        if (dialog.Result is { } edited) shell.UpdateView(edited);
    }

    private async Task ManageViewsAsync(ShellViewModel shell)
    {
        if (shell.ViewAccount is not { } account) return;
        var dialog = new ManageViewsDialog(account, shell.CurrentView, shell.SelectedFolderName);
        await dialog.ShowDialog(this);
        if (dialog.CurrentModified is { } modified) shell.UpdateView(modified);
        if (dialog.Applied is { } applied) shell.ChangeView(applied.Name);
        shell.RaiseViewNames();
    }

    private async Task SaveViewAsAsync(ShellViewModel shell)
    {
        if (shell.ViewAccount is null) return;
        var name = await Prompt.AskAsync(this, "Save Current View As a New View", "Name of new view:", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!shell.SaveViewAs(name))
        {
            await Confirm.AskAsync(this, "Save Current View", "That name is one of the views that ship; choose another.", "OK", destructive: false);
        }
    }

    private async Task ApplyViewToFoldersAsync(ShellViewModel shell)
    {
        if (shell.ViewAccount is not { } account) return;
        var dialog = new ApplyViewToFoldersDialog(account, shell.ViewFolderId);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } folders) shell.ApplyViewTo(folders);
    }

    /// <summary>
    /// The window menu behind the app icon. With no system frame the window owns this too.
    /// </summary>
    private void WireWindowMenu()
    {
        if (this.FindControl<Button>("WindowMenuButton") is not { } button) return;

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        MenuItem Item(string header, Action run, Func<bool>? enabled = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled?.Invoke() ?? true };
            item.Click += (_, _) => run();
            return item;
        }

        void Rebuild()
        {
            var maximized = WindowState == WindowState.Maximized;
            menu.ItemsSource = new[]
            {
                Item("Restore", () => WindowState = WindowState.Normal, () => maximized),
                Item("Minimize", () => WindowState = WindowState.Minimized),
                Item("Maximize", () => WindowState = WindowState.Maximized, () => !maximized),
                Item("Close", Close),
            };
        }

        Rebuild();
        button.Flyout = menu;
        button.Click += (_, _) => Rebuild();
    }

    /// <summary>
    /// Routes the toolbar buttons through the same handler the ribbon uses.
    /// </summary>
    /// <remarks>
    /// The Quick Access Toolbar, the reading pane's actions and the Modern command bar are all
    /// built from the same view model, and none of them was bound to anything — the identical
    /// command was live on the ribbon and dead everywhere else. Anything that stands for a
    /// command routes to one place, so wiring a command wires every way of reaching it.
    /// </remarks>
    private void WireToolbarCommands(ShellViewModel shell)
    {
        foreach (var button in shell.QuickAccess
                     .Concat(shell.ReadingPaneActions)
                     .Concat(shell.CommandBar))
        {
            var id = button.Command;
            button.Invoke = new RelayCommand(() => RunCommand(id));
        }
    }

    /// <summary>
    /// Hangs the customize menu off the chevron at the end of the Quick Access Toolbar.
    /// </summary>
    private void WireQuickAccess(ShellViewModel shell)
    {
        if (shell.QuickAccessCustomization is not { } customization) return;

        // One chevron per placement, each with its own flyout — a single flyout cannot be
        // attached to two controls.
        foreach (var name in new[] { "QuickAccessCustomize", "QuickAccessCustomizeBelow" })
        {
            if (this.FindControl<Button>(name) is not { } chevron) continue;

            chevron.Flyout = QuickAccessFlyout.Build(
                App.Commands,
                customization,
                changed: () =>
                {
                    shell.RebuildQuickAccess();

                    // The buttons are new objects, so whatever bound their commands has to run
                    // again or the rebuilt toolbar is a row of controls that do nothing.
                    WireToolbarCommands(shell);
                    _ribbon.IsQuickAccessVisible = customization.IsVisible;
                },
                moreCommands: () => _ = ShowOptions("qat"));
        }
    }

    /// <summary>
    /// Hangs the account panel off the avatar. Done here rather than in
    /// <see cref="SetUpTitleBar"/> because that runs before the view model exists.
    /// </summary>
    private void WireAccountButton(ShellViewModel shell)
    {
        if (this.FindControl<Button>("AccountButton") is not { } account) return;

        account.Flyout = AccountFlyout.Build(
            shell.AccountAddress,
            shell.AccountInitial,
            // Both go where the Backstage already goes. They were writing "not wired yet" at a
            // point where the thing they needed had been built for two phases.
            onViewAccount: ShowBackstage,
            onAddAccount: () => _ = AddAccountAsync());
    }

    private void SetUpTitleBar()
    {
        var caption = new CaptionButtons(this);
        this.FindControl<ContentControl>("CaptionHost")!.Content = caption;

        if (Environment.GetEnvironmentVariable("MAILBOX_HOVER") is { Length: > 0 } hovered)
        {
            // A pointer is the one thing a capture run does not have, so the state is posed.
            // "ribbon:<command-id>" reaches the bar, "rail:<module>" the rail — which is how
            // the hover that opens the peek is exercised — and anything else is a caption button.
            if (hovered.StartsWith("rail:", StringComparison.OrdinalIgnoreCase))
            {
                var name = hovered["rail:".Length..].Trim();
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (!Enum.TryParse<MailboxModule>(name, ignoreCase: true, out var module))
                        {
                            Log.Info($"Harness: {name} is not a module.");
                            return;
                        }

                        // Through the same handler the pointer reaches, dwell and all: a peek
                        // that only ever opened from a direct call would prove nothing about
                        // what a hover does.
                        RailPointerEntered(RailButton(module), new PointerEventArgs(
                            PointerEnteredEvent, this, new Pointer(0, PointerType.Mouse, true),
                            null, default, 0, new PointerPointProperties(), KeyModifiers.None));
                        Log.Info($"Harness: hovering the rail's {module} icon.");
                    },
                    DispatcherPriority.Loaded);
            }
            else if (hovered.StartsWith("ribbon:", StringComparison.OrdinalIgnoreCase))
            {
                var id = new CommandId(hovered["ribbon:".Length..].Trim());
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        _ribbon.UpdateLayout();
                        _ribbon.ForceHover(id);
                        Log.Info($"Harness: hovering {id}.");
                    },
                    DispatcherPriority.Loaded);
            }
            else
            {
                Opened += (_, _) => caption.ForceHover(hovered.ToLowerInvariant());
            }
        }

        if (this.FindControl<Control>("TitleBar") is not { } bar) return;

        WindowFrame.Drags(this, bar);
    }

    /// <summary>
    /// Phase 0 has no behaviour behind the commands yet; this proves the catalogue round-trip
    /// from ribbon click to a resolved command. Phases 2 onward attach real handlers.
    /// </summary>
    private void OnRibbonCommand(object? sender, RibbonCommandEventArgs e)
    {
        // A collapsed ribbon rolls back up once it has been used, which is the whole bargain of
        // the mode: it is there when wanted and gone the rest of the time.
        _ribbon.CloseFloatingBody();

        // While an inline reply is open the ribbon shows the compose tabs, and its commands act
        // on that surface rather than on the message list behind it.
        if (_inlineCompose is { } surface)
        {
            surface.Invoke(e.Command);
            return;
        }

        RunCommand(e.Command);
    }

    /// <summary>
    /// Puts the floating ribbon away when the click lands outside it and outside the tab strip
    /// that raised it. Tunnelled, so it runs before whatever was clicked handles the press.
    /// </summary>
    /// <summary>
    /// The title bar's search box lines up with the start of the message list — the app rail
    /// plus the folder pane, as wide as the pane is right now. Its offset token is the default
    /// widths' sum; dragging the splitter, or collapsing the pane, used to leave the box where
    /// it was while the list moved, and the reference's box follows the list.
    /// </summary>
    private void WireSearchBoxToListEdge()
    {
        if (this.FindControl<Border>("TitleSearchHost") is not { } search
            || this.FindControl<Border>("ListPane") is not { } list) return;

        // Never over the Quick Access Toolbar, though: with the folder pane collapsed to the
        // rail the list starts at the rail's edge, and the box stops short of the toolbar's
        // last button instead of covering it.
        var toolbar = this.FindControl<Control>("QuickAccessTitleGroup");

        void Follow()
        {
            if (list.TranslatePoint(default, this) is not { } origin) return;
            var left = Math.Round(origin.X);
            if (toolbar is { IsVisible: true } && toolbar.TranslatePoint(new Point(toolbar.Bounds.Width, 0), this) is { } end)
            {
                left = Math.Max(left, Math.Round(end.X) + 12);
            }

            if (left <= 0 || Math.Abs(search.Margin.Left - left) < 0.5) return;
            search.Margin = new Thickness(left, search.Margin.Top, search.Margin.Right, search.Margin.Bottom);
        }

        list.LayoutUpdated += (_, _) => Follow();
    }

    /// <summary>
    /// Ctrl+E and F3: the cursor goes to whichever search box the layout is showing — the title
    /// bar's, or the one over the list in the Modern layout — with what is typed there selected,
    /// as the reference selects it.
    /// </summary>
    private void FocusSearchBox(ShellViewModel shell)
    {
        var box = shell.ShowListSearch
            ? this.FindControl<TextBox>("ListSearchBox")
            : this.FindControl<TextBox>("TitleSearchBox");
        if (box is null) return;

        box.Focus();
        box.SelectAll();
        Log.Debug("Search box focused.");
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        // A click anywhere but inside the peek dismisses it, the rail's own icon included —
        // that one switches module and the peek would be left hanging over the new one.
        if (_peekPopup is { } peek && !IsWithin(source, peek)) ClosePeek();

        if (_floatingRibbon is null) return;
        if (IsWithin(source, _floatingRibbon) || IsWithin(source, _ribbon)) return;

        _ribbon.CloseFloatingBody();
    }

    private static bool IsWithin(Visual node, Visual? ancestor)
        => ancestor is not null
           && (ReferenceEquals(node, ancestor) || node.GetVisualAncestors().Contains(ancestor));

    /// <summary>
    /// The single place a command arrives, whichever control raised it. Phases 2 onward replace
    /// the placeholder with real handlers; until then every route reports the same thing, which
    /// is at least honest about what is and is not built.
    /// </summary>
    private void RunCommand(CommandId id)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (!App.Commands.TryGet(id, out var command)) return;

        Log.Debug($"Command invoked: {command.Id}");

        if (id == MailCommands.SendReceiveAll.Id) { _ = SendReceiveAsync(shell); return; }
        if (id == MailCommands.WorkOffline.Id) { _ = ToggleWorkOfflineAsync(shell); return; }
        if (id == ViewCommands.Refresh.Id) { shell.Refresh(); return; }
        if (id == MailCommands.Search.Id) { FocusSearchBox(shell); return; }
        if (id == MailCommands.GoToFolder.Id) { _ = GoToFolderAsync(shell); return; }
        if (id == MailCommands.GoToInbox.Id) { shell.GoTo(FolderRole.Inbox); return; }
        if (id == MailCommands.GoToOutbox.Id) { shell.GoTo(FolderRole.Outbox); return; }
        if (id == MailCommands.PermanentDelete.Id) { _ = ConfirmPermanentDeleteAsync(shell, SelectedRows()); return; }
        if (id == MailCommands.MarkAsRead.Id) { shell.SetRead(SelectedRows(), read: true); return; }
        if (id == MailCommands.MarkAsUnread.Id) { shell.SetRead(SelectedRows(), read: false); return; }
        if (id == MailCommands.NewEmail.Id) { NewMessage(); return; }
        if (id == ViewCommands.ShowProgress.Id) { ShowProgressDialog(shell); return; }
        if (id == MailCommands.ViewSource.Id) { ShowMessageSource(shell); return; }
        if (id == MailCommands.TrackerReport.Id) { _ = _reading?.ShowTrackerReportAsync(); return; }
        if (id == MailCommands.AuthenticationDetails.Id) { _ = _reading?.ShowAuthenticationAsync(); return; }
        if (id == MailCommands.Print.Id) { PrintMessage(shell); return; }
        if (id == MailCommands.PrintToPdf.Id) { _ = PrintToPdfAsync(shell); return; }
        if (id == MailCommands.PrintList.Id) { PrintList(shell); return; }
        if (id == MailCommands.RecoverDeleted.Id) { _ = ShowRecoverDeletedAsync(shell); return; }
        if (id == MailCommands.NewSearchFolder.Id) { _ = NewSearchFolderAsync(shell, null); return; }
        if (id == ViewCommands.CancelAll.Id) { CancelTransfer(); return; }
        if (id == ViewCommands.SendReceiveGroups.Id) { ShowGroupsMenu(shell); return; }

        // The View tab's first cluster: Change View, Current View, Arrange By, Layout, and the
        // entries behind them as commands of their own.
        if (id == ViewCommands.ChangeView.Id) { ShowChangeViewMenu(shell); return; }
        if (id == ViewCommands.ViewSettings.Id) { ShowCurrentViewMenu(shell); return; }
        if (id == ViewCommands.ArrangeBy.Id) { ArrangeFlyout(shell).ShowAt(_ribbon ?? (Control)this, showAtPointer: true); return; }
        if (id == ViewCommands.LayoutMenu.Id) { ShowLayoutMenu(shell); return; }
        if (id == ViewCommands.ChangeViewCompact.Id) { shell.ChangeView(Mailbox.Core.Views.MailView.CompactName); return; }
        if (id == ViewCommands.ChangeViewSingle.Id) { shell.ChangeView(Mailbox.Core.Views.MailView.SingleName); return; }
        if (id == ViewCommands.ChangeViewPreview.Id) { shell.ChangeView(Mailbox.Core.Views.MailView.PreviewName); return; }
        if (id == ViewCommands.OpenViewSettings.Id) { _ = ShowViewSettingsAsync(shell); return; }
        if (id == ViewCommands.ResetView.Id) { shell.ResetView(); return; }
        if (id == ViewCommands.ManageViews.Id) { _ = ManageViewsAsync(shell); return; }
        if (id == ViewCommands.SaveViewAs.Id) { _ = SaveViewAsAsync(shell); return; }
        if (id == ViewCommands.ApplyViewToFolders.Id) { _ = ApplyViewToFoldersAsync(shell); return; }

        // A Quick Step, by the command it is placed as; and the gallery's launcher, which manages them.
        if (App.QuickSteps.FindByCommand(id) is { } step) { _ = RunQuickStepAsync(shell, step, SelectedRows()); return; }
        if (id == MailCommands.QuickSteps.Id) { _ = ManageQuickStepsAsync(shell); return; }

        // The keyboard's own: open, step through the list, and the shortcut list itself.
        if (id == MailCommands.OpenItem.Id) { OpenMessageWindow(shell); return; }
        if (id == MailCommands.NextMessage.Id) { StepSelection(shell, 1); return; }
        if (id == MailCommands.PreviousMessage.Id) { StepSelection(shell, -1); return; }
        if (id == ViewCommands.KeyboardShortcuts.Id) { _ = ShowKeyboardShortcutsAsync(); return; }

        if (id == MailCommands.Reply.Id) { Respond(shell, ReplyKind.Reply); return; }
        if (id == MailCommands.ReplyAll.Id) { Respond(shell, ReplyKind.ReplyAll); return; }
        if (id == MailCommands.Forward.Id) { Respond(shell, ReplyKind.Forward); return; }

        if (RunCalendarCommand(shell, id)) return;
        if (RunPeopleCommand(shell, id)) return;
        if (RunTaskCommand(shell, id)) return;
        if (RunNoteCommand(shell, id)) return;
        if (RunJournalCommand(shell, id)) return;
        if (RunOverSelection(shell, id)) return;
        if (RunViewCommand(shell, id)) return;

        // A plugin's command, found the way a Quick Step is: the host owns the handler, and a
        // handler that throws disables its plugin rather than the window.
        if (App.Plugins.TryRun(id)) return;

        // Everything left is recorded in §20 with what it waits for; the status line names
        // the command so the plan can be checked against the window rather than the reverse.
        shell.StatusRight = $"{command.Label} — not wired yet ({command.Id})";
    }

    /// <summary>
    /// Reply, Reply All and Forward: a compose window opened on the message in the pane.
    /// </summary>
    /// <remarks>
    /// One method for the three, because they differ only in who is put in To and whether the
    /// attachments come along — <see cref="Reply.Build"/> knows those rules and this does not
    /// repeat them. What the reader has chosen about quoting comes from the Options page, and
    /// the reader's own addresses come from every account, so a reply to all never copies them.
    /// </remarks>
    private void Respond(ShellViewModel shell, ReplyKind kind, MimeKit.MimeMessage? message = null, IReadOnlyList<string>? to = null)
    {
        if ((message ?? _openMessage) is not { } original)
        {
            shell.StatusRight = "Select a message to reply to.";
            return;
        }

        // RFC 9788 §4.4.4 and §6.2, both MUSTs: a reply to a message that carried its own header
        // fields is addressed from those and from nothing outside them. The attack is a replay with
        // an extra address added to the outer Cc — answer that and the conversation is encrypted to
        // whoever added it. The body stays the envelope's, because a reply must not carry decrypted
        // content out in the clear (§19).
        var covered = ReferenceEquals(original, _openMessage) ? _reading?.Protected : null;
        if (covered is not null) original = HeaderProtection.Addressed(original, covered, original.Body);

        var styleIndex = kind == ReplyKind.Forward
            ? App.MailOptions.ForwardStyleIndex
            : App.MailOptions.ReplyStyleIndex;

        var draft = Reply.Build(original, kind, new ReplyOptions
        {
            OwnAddresses = [.. App.Accounts.All.Select(a => a.Account.Address)],
            Style = Enum.IsDefined((QuoteStyle)styleIndex) ? (QuoteStyle)styleIndex : QuoteStyle.Include,
            Prefix = App.MailOptions.ReplyPrefix,
            PlainText = App.MailOptions.ComposeFormat == ComposeFormat.PlainText,
        });

        // A Quick Step's forward already knows who to: the To line is filled in.
        if (to is { Count: > 0 }) draft = draft with { To = to };

        // The account the message arrived in, which is what a reply means.
        var address = shell.CurrentAddress;

        // The reference grows the reply where the message is; the Options page's "Open replies
        // and forwards in a new window" is the switch back to a separate window. An inline reply
        // is already open — a reply to a reply — reuses the strip rather than stacking a second.
        if (App.MailOptions.OpenRepliesInNewWindow)
        {
            OpenReplyWindow(shell, draft, kind, address, covered?.ConfidentialFields ?? []);
        }
        else
        {
            OpenInlineReply(shell, draft, kind, address, covered?.ConfidentialFields ?? []);
        }
    }

    private void OpenReplyWindow(
        ShellViewModel shell,
        ReplyDraft draft,
        ReplyKind kind,
        string? address,
        IReadOnlyList<string> confidential)
    {
        var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);

        if (address is { Length: > 0 }) compose.SendFromAccount(address);

        compose.Prefill(draft, kind);
        compose.Answering(confidential);
        compose.Queued += (_, e) => OnQueued(e);
        compose.Closed += (_, _) => shell.Refresh();

        // The harness presses Send on the reply too, so what a reply actually puts on the wire
        // can be read back out of the outbox — the threading headers most of all.
        if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_QUEUE") is { Length: > 0 })
        {
            if (kind == ReplyKind.Forward) compose.PoseHeader("b.person@example.com", string.Empty, string.Empty);
            compose.Opened += (_, _) => Dispatcher.UIThread.Post(() => compose.PressSend(), DispatcherPriority.Background);
        }

        compose.Show(this);
    }

    /// <summary>The compose surface shown inline in the reading pane, or null when reading.</summary>
    private ComposeSurface? _inlineCompose;

    /// <summary>
    /// Grows a reply where the message is: the reading pane's read view is covered by a compose
    /// surface, and the shell's ribbon becomes the compose ribbon aimed at it.
    /// </summary>
    /// <remarks>
    /// The same <see cref="ComposeSurface"/> the compose window hosts, so a reply written here is
    /// the same reply written there — the serializer, the threading headers, the Auto-Complete
    /// List, all of it. What differs is the chrome: no title bar, a strip of its own for Pop Out
    /// and Discard, and the shell's ribbon rather than a window's.
    /// </remarks>
    private void OpenInlineReply(
        ShellViewModel shell,
        ReplyDraft draft,
        ReplyKind kind,
        string? address,
        IReadOnlyList<string> confidential)
    {
        // A reply already open — a reply to a reply, or Forward pressed twice — is dismissed
        // first, so there is one inline surface at a time rather than a stack nobody asked for.
        if (_inlineCompose is not null) CloseInlineCompose(shell);

        var surface = new ComposeSurface(App.Commands, App.Accounts, App.Contacts);
        if (address is { Length: > 0 }) surface.SendFromAccount(address);
        surface.Prefill(draft, kind);
        surface.Answering(confidential);

        // Every handler is guarded against a surface that has since been popped out into a
        // window: the window re-subscribes for itself, and these must go quiet rather than
        // fire alongside it. A control's events cannot be unsubscribed by lambda, so the guard
        // is the identity check.
        surface.Queued += (_, e) => { if (ReferenceEquals(_inlineCompose, surface)) OnQueued(e); };
        surface.EnablementChanged += (_, _) => { if (ReferenceEquals(_inlineCompose, surface)) _ribbon.RefreshEnablement(); };
        surface.CloseRequested += (_, _) => { if (ReferenceEquals(_inlineCompose, surface)) CloseInlineCompose(shell); };

        _inlineCompose = surface;

        // The ribbon becomes the compose ribbon, aimed at the surface. Its enablement predicate
        // and its commands both point there until the reply closes.
        _savedRibbonEnabled = _ribbon.CommandEnabled;
        _ribbon.Layout = DefaultRibbonLayouts.Compose;
        _ribbon.CommandEnabled = surface.IsCommandEnabled;
        _ribbon.RefreshEnablement();

        this.FindControl<ContentControl>("ReadingComposeHost")!.Content = InlineComposeChrome(shell, surface);
        this.FindControl<ContentControl>("ReadingComposeHost")!.IsVisible = true;

        // The harness sends the inline reply too, so its wire form — the threading headers most
        // of all — can be read back out of the outbox exactly as the windowed reply's is.
        if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_QUEUE") is { Length: > 0 })
        {
            if (kind == ReplyKind.Forward) surface.PoseHeader("b.person@example.com", string.Empty, string.Empty);
            Dispatcher.UIThread.Post(() => surface.PressSend(), DispatcherPriority.Background);
        }

        // The harness pops the reply out, so the live re-parent into a window is exercised and
        // photographed — a control moving between two visual trees is the fiddly part.
        if (Environment.GetEnvironmentVariable("MAILBOX_INLINE_POPOUT") is { Length: > 0 })
        {
            CaptureNextWindow();
            Dispatcher.UIThread.Post(() => PopOutInline(shell), DispatcherPriority.Background);
        }
    }

    private Func<CommandId, bool>? _savedRibbonEnabled;

    /// <summary>
    /// The inline reply's own chrome: a thin bar carrying Pop Out and Discard above the surface.
    /// Send lives in the surface's own header, as it does in the window.
    /// </summary>
    private Control InlineComposeChrome(ShellViewModel shell, ComposeSurface surface)
    {
        var popOut = new Button { Content = "Pop Out", Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(popOut, "Open this reply in its own window");
        popOut.Click += (_, _) => PopOutInline(shell);

        var discard = new Button { Content = "Discard", Padding = new Thickness(10, 4) };
        ToolTip.SetTip(discard, "Discard this reply");
        discard.Click += (_, _) => surface.Invoke(ComposeCommands.Discard.Id);

        var bar = new Border
        {
            Padding = new Thickness(12, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("reading.header.background.brush"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("border.subtle.brush"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { popOut, discard },
            },
        };
        DockPanel.SetDock(bar, Dock.Top);

        var host = new DockPanel { LastChildFill = true };
        host.Children.Add(bar);
        host.Children.Add(surface);

        return new Border
        {
            Child = host,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("reading.background.brush"),
        };
    }

    /// <summary>Takes the live inline reply out of the pane and into its own window, state intact.</summary>
    private void PopOutInline(ShellViewModel shell)
    {
        if (_inlineCompose is not { } surface) return;

        // Detach the surface from the inline chrome before the window adopts it — a control has
        // exactly one parent, and clearing the host's Content is not enough: the surface's
        // immediate parent is the chrome's own panel, and that reference is what the window would
        // collide with. Remove it from there, then drop the empty chrome.
        if (surface.Parent is Panel panel) panel.Children.Remove(surface);
        this.FindControl<ContentControl>("ReadingComposeHost")!.Content = null;

        var compose = new ComposeWindow(App.Commands, surface);
        compose.Queued += (_, e) => OnQueued(e);
        compose.Closed += (_, _) => shell.Refresh();

        // Put the reading pane back the way it was, without discarding: the reply lives in the
        // window now, not gone.
        RestoreReadingRibbon();
        this.FindControl<ContentControl>("ReadingComposeHost")!.IsVisible = false;
        _inlineCompose = null;

        compose.Show(this);
    }

    /// <summary>Dismisses the inline reply and puts the reading pane back to reading.</summary>
    private void CloseInlineCompose(ShellViewModel shell)
    {
        if (_inlineCompose is null) return;

        this.FindControl<ContentControl>("ReadingComposeHost")!.IsVisible = false;
        this.FindControl<ContentControl>("ReadingComposeHost")!.Content = null;
        _inlineCompose = null;

        RestoreReadingRibbon();
        shell.Refresh();
    }

    private void RestoreReadingRibbon()
    {
        _ribbon.Layout = App.MailRibbon();
        _ribbon.CommandEnabled = _savedRibbonEnabled;
        _ribbon.RefreshEnablement();
        _savedRibbonEnabled = null;
    }

    /// <summary>
    /// The Home tab's commands over the selected messages.
    /// </summary>
    /// <remarks>
    /// The shell has had every one of these operations since Phase 3 — the Delete key, the
    /// hover actions and the shortcuts all call them — and the ribbon buttons for the same
    /// things reported "not wired" until session 4's audit pressed them. They call the same
    /// operations now, so a thing done from the ribbon, the keyboard, a hover or the row's
    /// menu is one thing done four ways.
    /// </remarks>
    private bool RunOverSelection(ShellViewModel shell, CommandId id)
    {
        var rows = SelectedRows();

        if (id == MailCommands.Delete.Id) { shell.Delete(rows, permanently: false); return true; }
        if (id == MailCommands.Archive.Id) { shell.MoveTo(rows, FolderRole.Archive); return true; }
        if (id == MailCommands.Junk.Id) { ShowJunkMenu(shell, rows); return true; }
        if (id == MailCommands.Ignore.Id) { _ = IgnoreAsync(shell, rows); return true; }
        if (id == MailCommands.CleanUp.Id) { ShowCleanUpMenu(shell, rows); return true; }
        if (id == MailCommands.CleanUpConversation.Id) { shell.CleanUp(rows, wholeFolder: false, withSubfolders: false); return true; }
        if (id == MailCommands.CleanUpFolder.Id) { shell.CleanUp(rows, wholeFolder: true, withSubfolders: false); return true; }
        if (id == MailCommands.CleanUpFolderAndSubfolders.Id) { shell.CleanUp(rows, wholeFolder: true, withSubfolders: true); return true; }
        if (id == MailCommands.BlockSender.Id) { shell.BlockSenders(rows); return true; }
        if (id == MailCommands.NeverBlockSender.Id) { shell.NeverBlockSenders(rows, domain: false); return true; }
        if (id == MailCommands.NeverBlockDomain.Id) { shell.NeverBlockSenders(rows, domain: true); return true; }
        if (id == MailCommands.NeverBlockGroup.Id) { shell.NeverBlockRecipients(rows); return true; }
        if (id == MailCommands.NotJunk.Id) { shell.MarkJunk(rows); return true; }
        if (id == MailCommands.JunkOptions.Id) { ShowJunkOptions(shell); return true; }

        // The reference's Unread/Read button toggles: unread if the selection is all read, read
        // otherwise. Same for the flag.
        if (id == MailCommands.Unread.Id) { shell.SetRead(rows, read: rows.Any(r => r.IsUnread)); return true; }
        if (id == MailCommands.FollowUp.Id) { ShowFollowUpMenu(shell, rows); return true; }

        // Insert: flag what is not flagged, and mark what is flagged complete. Unlike the click
        // on the flag column, which takes the flag off again, this is the reference's own reading
        // of the key — "flag a message or mark a flagged message as complete".
        if (id == MailCommands.ToggleFlag.Id)
        {
            if (rows.Count == 0) return true;
            if (rows.All(r => r.IsFlagged)) shell.MarkFollowUpComplete(rows);
            else if (App.QuickClick.Flag == QuickFlag.Complete) shell.MarkFollowUpComplete(rows);
            else shell.FlagForFollowUp(rows, QuickClickSettings.DueDate(App.QuickClick.Flag, DateTimeOffset.Now));
            return true;
        }

        if (id == MailCommands.Categorize.Id) { ShowCategorizeMenu(shell, rows); return true; }
        if (id == MailCommands.Snooze.Id) { ShowSnoozeMenu(shell, rows); return true; }
        if (id == MailCommands.MoveToOther.Id) { shell.SetFocused(rows, focused: false, always: false); return true; }
        if (id == MailCommands.MoveToFocused.Id) { shell.SetFocused(rows, focused: true, always: false); return true; }
        if (id == MailCommands.AlwaysMoveToOther.Id) { shell.SetFocused(rows, focused: false, always: true); return true; }
        if (id == MailCommands.AlwaysMoveToFocused.Id) { shell.SetFocused(rows, focused: true, always: true); return true; }
        if (id == MailCommands.MoveTo.Id) { ShowMoveMenu(shell, rows); return true; }
        if (id == MailCommands.Rules.Id) { ShowRulesMenu(shell, rows); return true; }
        if (id == MailCommands.NewItems.Id) { ShowNewItemsMenu(); return true; }
        if (id == MailCommands.FilterEmail.Id) { ShowFilterMenu(shell); return true; }

        return false;
    }

    /// <summary>The View tab's toggles that have state behind them.</summary>
    private static bool RunViewCommand(ShellViewModel shell, CommandId id)
    {
        if (id == ViewCommands.ReverseSort.Id) { shell.SortDescending = !shell.SortDescending; return true; }
        if (id == ViewCommands.TighterSpacing.Id) { shell.CompactRows = !shell.CompactRows; return true; }
        if (id == ViewCommands.ShowFocusedInbox.Id)
        {
            shell.FocusedInboxOn = !shell.FocusedInboxOn;
            shell.StatusRight = shell.FocusedInboxOn ? "Focused Inbox is on." : "Focused Inbox is off.";
            return true;
        }

        return false;
    }

    /// <summary>The Categorize menu: the account's six, ticked where the whole selection has one.</summary>
    /// <summary>
    /// The flag menu, in the reference's order: the date presets, a custom date, Complete, and
    /// Clear. Today's the default a click on the flag column takes; this is the menu the ribbon's
    /// Follow Up opens.
    /// </summary>
    private void ShowFollowUpMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        if (rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Select a message first", IsEnabled = false });
            flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
            return;
        }

        var now = DateTimeOffset.Now;

        // The reference's own presets, each under the flag. The dates are the Quick Click
        // settings' own arithmetic, so the menu and a single click in the Flag column cannot
        // disagree about what "This Week" means.
        void Preset(QuickFlag flag)
        {
            var item = new MenuItem { Header = QuickClickSettings.Label(flag), Icon = FlagArtwork() };
            item.Click += (_, _) => shell.FlagForFollowUp(rows, QuickClickSettings.DueDate(flag, now));
            flyout.Items.Add(item);
        }

        Preset(QuickFlag.Today);
        Preset(QuickFlag.Tomorrow);
        Preset(QuickFlag.ThisWeek);
        Preset(QuickFlag.NextWeek);
        Preset(QuickFlag.NoDate);

        var custom = new MenuItem { Header = "Custom…", Icon = FlagArtwork() };
        custom.Click += async (_, _) => await CustomFlagAsync(shell, rows, reminderOn: false);
        flyout.Items.Add(custom);

        flyout.Items.Add(new Separator());

        var remind = new MenuItem
        {
            Header = "Add Reminder…",
            Icon = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("reminder", 16),
                FontFamily = IconFont.Family,
                FontSize = 12,
            },
        };
        remind.Click += async (_, _) => await CustomFlagAsync(shell, rows, reminderOn: true);
        flyout.Items.Add(remind);

        var complete = new MenuItem { Header = "Mark Complete", Icon = Tick() };
        complete.Click += (_, _) => shell.MarkFollowUpComplete(rows);
        flyout.Items.Add(complete);

        // Greyed rather than absent when nothing in the selection carries a flag, as the
        // reference greys it.
        var clear = new MenuItem { Header = "Clear Flag", IsEnabled = rows.Any(r => r.IsFlagged) };
        clear.Click += (_, _) => shell.ClearFollowUpFlag(rows);
        flyout.Items.Add(clear);

        flyout.Items.Add(new Separator());

        var quick = new MenuItem { Header = "Set Quick Click…" };
        quick.Click += async (_, _) => await new SetQuickClickFlagDialog(App.QuickClick).ShowDialog(this);
        flyout.Items.Add(quick);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// The Snooze menu (§12): presets in the flag menu's shape — Later Today, Tomorrow, This
    /// Weekend, Next Week, Custom — and Unsnooze for a message that is snoozed. The presets
    /// are the reference's own times: four hours from now, and eight in the morning otherwise.
    /// </summary>
    private void ShowSnoozeMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        if (rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Select a message first", IsEnabled = false });
            flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
            return;
        }

        foreach (var (header, until) in Mailbox.Core.SnoozePresets.For(DateTimeOffset.Now))
        {
            var item = new MenuItem { Header = header };
            var when = until;
            item.Click += (_, _) => shell.Snooze(rows, when);
            flyout.Items.Add(item);
        }

        var custom = new MenuItem { Header = "Custom…" };
        custom.Click += async (_, _) =>
        {
            var entered = await Prompt.AskAsync(this, "Snooze until", "Date and time (yyyy-MM-dd HH:mm):",
                DateTime.Now.AddHours(4).ToString("yyyy-MM-dd HH:mm"));
            if (entered is null) return;

            if (DateTime.TryParse(entered, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var when) && when > DateTime.Now)
            {
                shell.Snooze(rows, new DateTimeOffset(when));
            }
            else
            {
                shell.StatusRight = $"Could not read “{entered}” as a time still to come.";
            }
        };
        flyout.Items.Add(custom);

        if (rows.Any(r => r.IsSnoozed))
        {
            flyout.Items.Add(new Separator());
            var wake = new MenuItem { Header = "Unsnooze" };
            wake.Click += (_, _) => shell.Unsnooze(rows);
            flyout.Items.Add(wake);
        }

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// Ignore Conversation, asking first the first time as the reference does — with the
    /// "don't show this message again" that makes it the last time.
    /// </summary>
    private async Task IgnoreAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) { shell.StatusRight = "Select a message first."; return; }

        if (!shell.IsIgnored(rows) && App.MailOptions.ConfirmIgnore)
        {
            var (go, dontAsk) = await Confirm.AskAsync(this, "Ignore Conversation",
                "The selected conversation and all future messages will be moved to the Deleted Items folder.",
                "Ignore Conversation", destructive: false, dontShowAgain: "Don't show this message again");
            if (dontAsk) App.MailOptions.ConfirmIgnore = false;
            if (!go) return;
        }

        shell.IgnoreConversation(rows);
    }

    /// <summary>Clean Up's menu: Conversation, Folder, Folder &amp; Subfolders.</summary>
    private void ShowCleanUpMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, bool enabled, Action run)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        Entry(MailCommands.CleanUpConversation.Label, rows.Count > 0, () => RunCommand(MailCommands.CleanUpConversation.Id));
        Entry(MailCommands.CleanUpFolder.Label, shell.CurrentFolder is not null, () => RunCommand(MailCommands.CleanUpFolder.Id));
        Entry(MailCommands.CleanUpFolderAndSubfolders.Label, shell.CurrentFolder is not null, () => RunCommand(MailCommands.CleanUpFolderAndSubfolders.Id));

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>The Custom flag dialog over the selection — from Custom… or Add Reminder….</summary>
    private async Task CustomFlagAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows, bool reminderOn)
    {
        if (rows.Count == 0) return;

        var current = shell.SummaryOf(rows[0]);
        var dialog = new CustomFlagDialog(current, reminderOn);
        await dialog.ShowDialog(this);

        if (dialog.Cleared) { shell.ClearFollowUpFlag(rows); return; }
        if (dialog.Result is not { } flag) return;

        shell.SetCustomFlag(rows, flag);
    }

    // ---- Reminders (§9) ------------------------------------------------------------------------

    private RemindersWindow? _reminders;
    private readonly HashSet<(string, long)> _announced = [];

    /// <summary>
    /// What the minute timer asks: has any flag's reminder time come? The Reminders window shows
    /// what is due, a toast announces each item once, and the alarm sounds — each as the
    /// Options page allows.
    /// </summary>
    private void CheckReminders(ShellViewModel shell)
    {
        var now = DateTimeOffset.UtcNow;
        var due = new List<DueReminder>();
        foreach (var account in App.Accounts.All)
        {
            foreach (var message in account.Mail.DueReminders(now)) due.Add(DueReminder.ForMessage(account, message));
        }

        // Appointments join the same queue, as §9 asks: one window over every module, with one
        // Dismiss All, rather than a second window for the calendar.
        foreach (var appointment in Mailbox.Scheduling.AppointmentReminders.Due(App.Pim, now))
        {
            due.Add(DueReminder.ForAppointment(appointment));
        }

        // And the tasks, on the same terms: one window over every module, one Dismiss All. A
        // task's alarm hangs from its due date and does not stop when the date passes — an
        // overdue task is what a reminder is for.
        foreach (var task in Mailbox.Scheduling.TaskReminders.Due(App.Pim, now))
        {
            due.Add(DueReminder.ForTask(task));
        }

        // Announced once per item per time: a snoozed reminder that comes round again is a new
        // announcement, and its key carries the time so it is.
        var fresh = due.Where(d => _announced.Add(d switch
        {
            { IsAppointment: true } => ("calendar", d.Appointment!.ItemId ^ d.Appointment.StartsUtc.ToUnixTimeSeconds()),
            { IsTask: true } => ("tasks", d.Task!.ItemId ^ d.Task.DueUtc.ToUnixTimeSeconds()),
            _ => (d.Account!.Account.Address, d.Message!.Id ^ (d.Message.Reminder?.ToUnixTimeSeconds() ?? 0)),
        })).ToList();

        if (due.Count == 0)
        {
            _reminders?.Show([]);
            return;
        }

        if (App.MailOptions.ShowReminders)
        {
            _reminders ??= NewRemindersWindow(shell);
            _reminders.Show(due);
        }

        if (fresh.Count == 0) return;

        if (App.MailOptions.PlayReminderSound) Notifications.Sounds.PlayAlarm();

        if (App.MailOptions.DisplayDesktopAlert)
        {
            foreach (var item in fresh)
            {
                _notifier.Notify(ToastFor(new NewMailToast(
                    "Reminder: " + item.Subject, item.DueIn(DateTimeOffset.Now),
                    item.Account?.Account.Address ?? string.Empty, item.Message?.Id ?? 0)));
            }
        }
    }

    /// <summary>
    /// Presses Dismiss or Snooze in the Reminders window and says what is left afterwards.
    /// </summary>
    /// <remarks>
    /// The queue is asked again rather than the window: dismissing writes to whichever store the
    /// item came from — a mail file, or the PIM one per occurrence — and it is that write, not the
    /// list emptying, that has to be true.
    /// </remarks>
    private void PressReminders(string spec)
    {
        if (_reminders is not { } window || window.Current.Count == 0) return;

        var held = window.Current.ToList();
        switch (spec.ToLowerInvariant())
        {
            case "dismiss":
                window.PressDismiss(held);
                break;
            case "snooze":
                window.PressSnooze(held, TimeSpan.FromHours(1));
                break;
            default:
                return;
        }

        var now = DateTimeOffset.UtcNow;
        var appointments = Mailbox.Scheduling.AppointmentReminders.Due(App.Pim, now).Count;
        var tasks = Mailbox.Scheduling.TaskReminders.Due(App.Pim, now).Count;
        var mail = App.Accounts.All.Sum(a => a.Mail.DueReminders(now).Count);

        Log.Info($"Harness: {spec} pressed on {held.Count}; the queue now holds "
            + $"{mail} message(s), {appointments} appointment(s), {tasks} task(s).");
    }

    private RemindersWindow NewRemindersWindow(ShellViewModel shell)
    {
        var window = new RemindersWindow();
        window.OpenRequested += (_, item) =>
        {
            BringForward();
            RevealMessage(shell, item.Address, item.MessageId);
        };
        window.OpenAppointmentRequested += (_, itemId) =>
        {
            BringForward();
            _ = OpenAppointmentByIdAsync(shell, itemId);
        };
        window.OpenTaskRequested += (_, itemId) =>
        {
            BringForward();
            _ = OpenTaskByIdAsync(shell, itemId);
        };
        Closed += (_, _) =>
        {
            try { window.Close(); } catch (Exception) { /* already gone */ }
        };
        return window;
    }

    /// <summary>
    /// The Categorize menu, in the reference's own order: Clear All Categories at the head, the
    /// categories under a rule, and All Categories… with Set Quick Click… under another.
    /// </summary>
    /// <remarks>
    /// Clear All Categories comes first rather than last, which reads oddly until it is used:
    /// the list below is what one chooses from, and clearing is the one action that is not a
    /// choice from that list. It greys with nothing selected rather than disappearing, so the
    /// menu keeps its shape whatever the selection.
    /// </remarks>
    private void ShowCategorizeMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var categories = shell.Categories();

        var clear = new MenuItem
        {
            Header = "Clear All Categories",
            IsEnabled = rows.Count > 0 && categories.Count > 0,
        };
        clear.Click += (_, _) => shell.ClearCategories(rows);
        flyout.Items.Add(clear);
        flyout.Items.Add(new Separator());

        if (categories.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No categories are defined", IsEnabled = false });
        }

        foreach (var category in categories)
        {
            var item = new MenuItem
            {
                Header = category.Name,
                // The swatch is the colour; a tick takes its place where every selected message
                // already carries it, which is how the reference shows one applied.
                Icon = rows.Count > 0 && shell.AllHave(rows, category)
                    ? Tick()
                    : CategorySwatch(category.ColourToken),
                IsEnabled = rows.Count > 0,
            };

            var chosen = category;
            item.Click += (_, _) => shell.ToggleCategory(rows, chosen);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        // Create, rename, recolour, shortcut and delete — the reference puts the way in here.
        var all = new MenuItem { Header = "All Categories…", Icon = CategorizeArtwork() };
        all.Click += (_, _) => _ = new ColorCategoriesDialog(App.Categories, RewriteCategoryOnItems).ShowDialog(this);
        flyout.Items.Add(all);

        var quick = new MenuItem { Header = "Set Quick Click…" };
        quick.Click += async (_, _) =>
            await new SetQuickClickCategoryDialog(App.QuickClick, shell.Categories()).ShowDialog(this);
        flyout.Items.Add(quick);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// A single click on a row's Flag or Categories cell: the reference's Quick Click.
    /// </summary>
    /// <remarks>
    /// The flag cell always acts — the nominated flag is Today until it is changed — and toggles
    /// rather than only setting, so the same click takes a flag off again. The categories cell
    /// has nothing nominated to begin with; rather than doing nothing and looking broken, the
    /// first click opens Set Quick Click… so the choice can be made where it was reached for.
    /// </remarks>
    private async void QuickClick(ShellViewModel shell, QuickClickEventArgs e)
    {
        IReadOnlyList<ViewModels.MessageRow> rows = [e.Row];

        if (e.Field == Mailbox.Core.Views.ViewFields.Flag)
        {
            if (e.Row.IsFlagged) shell.ClearFollowUpFlag(rows);
            else if (App.QuickClick.Flag == QuickFlag.Complete) shell.MarkFollowUpComplete(rows);
            else shell.FlagForFollowUp(rows, QuickClickSettings.DueDate(App.QuickClick.Flag, DateTimeOffset.Now));
            return;
        }

        if (e.Field != Mailbox.Core.Views.ViewFields.Categories) return;

        var categories = shell.Categories();
        if (!App.QuickClick.HasCategory || categories.Count == 0)
        {
            var dialog = new SetQuickClickCategoryDialog(App.QuickClick, categories);
            await dialog.ShowDialog(this);
            if (!App.QuickClick.HasCategory) return;
        }

        if (categories.FirstOrDefault(c => string.Equals(
                c.Name, App.QuickClick.Category, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            shell.ToggleCategory(rows, chosen);
        }
    }

    /// <summary>The four swatches, for the menu entry that opens the category list.</summary>
    private static Control CategorizeArtwork()
        => new Mailbox.Controls.Ribbon.RibbonArtwork("categorize", 16);

    /// <summary>The red flag, which the Follow Up menu puts against each of its presets.</summary>
    private static Control FlagArtwork()
        => new Mailbox.Controls.Ribbon.RibbonArtwork("followup", 16);

    /// <summary>
    /// The Junk menu, in the reference's order: Block Sender, Never Block Sender, Never Block
    /// Sender's Domain, Never Block this Group or Mailing List, Not Junk, and the options dialog.
    /// </summary>
    /// <remarks>
    /// Each list entry acts on the selection's senders and then does what the list implies —
    /// blocking a sender also files their message as junk and trains the filter on it; clearing
    /// one also brings the message back if it was in Junk. Not Junk is the reference's own
    /// button, greyed unless the selection is in the Junk folder.
    /// </remarks>
    private void ShowJunkMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var inJunk = shell.CurrentFolderRole == FolderRole.Junk;

        // Every entry runs its command through the dispatcher, so the menu, the catalogue and
        // the harness all reach one handler.
        void Entry(MailboxCommand command, string? header = null, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = header ?? command.Label, IsEnabled = enabled && rows.Count > 0 };
            ToolTip.SetTip(item, tip ?? command.Description);
            item.Click += (_, _) => RunCommand(command.Id);
            flyout.Items.Add(item);
        }

        var domains = shell.SenderDomains(rows);

        Entry(MailCommands.BlockSender, enabled: !inJunk);
        Entry(MailCommands.NeverBlockSender);
        Entry(MailCommands.NeverBlockDomain,
            header: domains.Count == 1 ? $"Never Block Sender's Domain (@{domains[0]})" : null,
            enabled: domains.Count > 0);
        Entry(MailCommands.NeverBlockGroup);
        flyout.Items.Add(new Separator());
        Entry(MailCommands.NotJunk, enabled: inJunk,
            tip: inJunk ? null : "Only for messages in the Junk Email folder");
        flyout.Items.Add(new Separator());

        var options = new MenuItem { Header = "Junk Email Options…" };
        options.Click += (_, _) => RunCommand(MailCommands.JunkOptions.Id);
        flyout.Items.Add(options);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Junk Email Options, on the current account's lists.</summary>
    private void ShowJunkOptions(ShellViewModel shell)
    {
        if (shell.CurrentAccountForCategories() is not { } account)
        {
            shell.StatusRight = "No account is set up yet. File, Add Account.";
            return;
        }

        _ = new JunkOptionsDialog(account.Mail, App.MailOptions).ShowDialog(this);
    }

    /// <summary>
    /// The Rules menu, in the reference's order: Always Move Messages From the selection's
    /// sender, Always Move Messages To its recipient, Create Rule from the message, and Manage
    /// Rules &amp; Alerts.
    /// </summary>
    private void ShowRulesMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var account = shell.CurrentAccountForCategories();
        var message = rows.Count == 1 ? _openMessage : null;
        var from = message?.From.Mailboxes.FirstOrDefault();
        var to = message?.To.Mailboxes.FirstOrDefault(m => !App.Accounts.All.Any(a => string.Equals(a.Account.Address, m.Address, StringComparison.OrdinalIgnoreCase)))
                 ?? message?.To.Mailboxes.FirstOrDefault();

        void Entry(string header, bool enabled, Func<Task> run)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await run();
            flyout.Items.Add(item);
        }

        var fromName = from is null ? null : (from.Name is { Length: > 0 } ? from.Name : from.Address);
        var toName = to is null ? null : (to.Name is { Length: > 0 } ? to.Name : to.Address);

        Entry(fromName is null ? "Always Move Messages From: …" : $"Always Move Messages From: {fromName}",
            from is not null && account is not null,
            () => AlwaysMoveAsync(shell, account!, new RuleCondition(RuleConditionKind.From) { Values = [from!.Address] }, fromName!));

        Entry(toName is null ? "Always Move Messages To: …" : $"Always Move Messages To: {toName}",
            to is not null && account is not null,
            () => AlwaysMoveAsync(shell, account!, new RuleCondition(RuleConditionKind.SentTo) { Values = [to!.Address] }, toName!));

        flyout.Items.Add(new Separator());

        Entry("Create Rule…", message is not null && account is not null, async () =>
        {
            var dialog = new CreateRuleDialog(account!.Mail, account.Account.Id, message!);
            await dialog.ShowDialog(this);
            if (dialog.Result is { } rule && dialog.RunNow) RunRuleOnCurrentFolder(shell, account, rule);
        });

        Entry("Manage Rules & Alerts…", true, async () =>
        {
            await new RulesAndAlertsDialog(shell.CurrentAddress).ShowDialog(this);
            shell.Refresh();
        });

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// Always Move Messages From/To: a folder is chosen, a rule is written that moves matching
    /// mail there, and it runs on the folder at once — the reference's three steps in one.
    /// </summary>
    private async Task AlwaysMoveAsync(ShellViewModel shell, OpenAccount account, RuleCondition condition, string who)
    {
        var folder = await RuleValues.FolderAsync(this, account.Mail, account.Account.Id, null);
        if (folder is null) return;

        var rule = account.Mail.AddRule(new MailRule
        {
            Name = who,
            Conditions = [condition],
            Actions =
            [
                new RuleAction(RuleActionKind.MoveToFolder) { FolderId = folder.Id, FolderName = folder.Name },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
        }, DateTimeOffset.UtcNow);

        RunRuleOnCurrentFolder(shell, account, rule);
    }

    private void RunRuleOnCurrentFolder(ShellViewModel shell, OpenAccount account, MailRule rule)
    {
        var folder = shell.CurrentFolder ?? account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (folder is null) return;

        var count = App.Rules.RunNow(account.Mail, folder, [rule]);
        shell.Refresh();
        shell.StatusRight = count == 0
            ? $"Rule “{rule.Name}” created; nothing in {folder.Name} matched it."
            : $"Rule “{rule.Name}” created and applied to {count} message{(count == 1 ? "" : "s")}.";
    }

    /// <summary>The Move menu: every folder of the account the selection is in.</summary>
    private void ShowMoveMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        if (rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Select a message first", IsEnabled = false });
        }
        else
        {
            foreach (var folder in shell.FoldersOfSelection(rows))
            {
                var item = new MenuItem { Header = folder.Name };
                var target = folder;
                item.Click += (_, _) => shell.MoveToFolder([.. rows.Select(r => r.Id)], target);
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>New Items: a new message today, and the other kinds when their modules exist.</summary>
    private void ShowNewItemsMenu()
    {
        if (DataContext is not ShellViewModel shell) return;

        var flyout = new MenuFlyout();

        void Entry(string header, string icon, Action run)
        {
            var item = new MenuItem { Header = header, Icon = MenuIcon(icon) };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        // The reference's own list, in its own order, with the mnemonics it underlines. Every
        // one of them makes its thing now — the modules they were waiting for are all up.
        Entry("_E-mail Message", "mail-new", () => NewMessage());
        Entry("_Appointment", "new-appointment", () => RunCommand(CalendarCommands.NewAppointment.Id));
        Entry("_Meeting", "meeting", () => RunCommand(CalendarCommands.NewMeeting.Id));
        Entry("_Contact", "contact-card", () => RunCommand(PeopleCommands.NewContact.Id));
        Entry("Contact _Group", "contact-group", () => RunCommand(PeopleCommands.NewContactGroup.Id));
        Entry("_Task", "new-task", () => RunCommand(TaskCommands.NewTask.Id));
        flyout.Items.Add(new Separator());

        var using_ = new MenuItem { Header = "E-mail Message _Using" };
        using_.Items.Add(new MenuItem { Header = "Plain Text", IsEnabled = false });
        using_.Items.Add(new MenuItem { Header = "Rich Text", IsEnabled = false });
        ToolTip.SetTip(using_, "Choosing a format per message arrives with the stationery work.");
        flyout.Items.Add(using_);

        var more = new MenuItem { Header = "M_ore Items" };
        var note = new MenuItem { Header = "Note", Icon = MenuIcon("note") };
        note.Click += (_, _) => RunCommand(NoteCommands.NewNote.Id);
        more.Items.Add(note);

        var entry = new MenuItem { Header = "Journal Entry", Icon = MenuIcon("journal") };
        entry.Click += (_, _) => RunCommand(JournalCommands.NewEntry.Id);
        more.Items.Add(entry);

        var form = new MenuItem { Header = "Choose Form…", IsEnabled = false };
        ToolTip.SetTip(form, "Custom forms are a plugin's business (§13).");
        more.Items.Add(form);
        flyout.Items.Add(more);

        // New Items has its own button on the classic ribbon and hangs off the New chevron on the
        // Simplified bar — whichever module's New that is — so the menu falls back to whichever of
        // the two is on screen.
        var under = _ribbon.ControlFor(MailCommands.NewEmail.Id)
                    ?? _ribbon.ControlFor(PeopleCommands.NewContact.Id)
                    ?? _ribbon.ControlFor(TaskCommands.NewTask.Id)
                    ?? (Control)this;

        _ribbon.OpenMenuUnder(MailCommands.NewItems.Id, flyout, under);
        shell.StatusRight = string.Empty;
    }

    /// <summary>Filter Email: the reference's filters, one at a time, and Snoozed beside them.</summary>
    private void ShowFilterMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        void Entry(string label, ShellViewModel.ListFilter filter)
        {
            var item = new MenuItem { Header = label, Icon = shell.Filter == filter ? Tick() : null };
            item.Click += (_, _) => shell.Filter = shell.Filter == filter ? ShellViewModel.ListFilter.None : filter;
            flyout.Items.Add(item);
        }

        Entry("Unread", ShellViewModel.ListFilter.Unread);
        Entry("Has Attachments", ShellViewModel.ListFilter.HasAttachments);
        Entry("Flagged", ShellViewModel.ListFilter.Flagged);
        Entry("Important", ShellViewModel.ListFilter.Important);
        Entry("Categorized", ShellViewModel.ListFilter.Categorized);
        Entry("This Week", ShellViewModel.ListFilter.ThisWeek);

        flyout.Items.Add(new Separator());

        // Snoozed mail is nowhere until it comes back; this is where to see what is waiting.
        var snoozed = new MenuItem { Header = "Snoozed", Icon = shell.ShowSnoozed ? Tick() : null };
        snoozed.Click += (_, _) => shell.ShowSnoozed = !shell.ShowSnoozed;
        flyout.Items.Add(snoozed);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// The message row's right-click menu, in the reference's order.
    /// </summary>
    /// <remarks>
    /// Built when it opens rather than once: the ticks and the folder list change with the
    /// selection. Anything on it that has no command behind it yet is greyed with the phase in
    /// its tooltip, which is what the ribbon does for the same commands.
    /// </remarks>
    private MenuFlyout RowMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var rows = SelectedRows();

            void Command(string label, CommandId id, bool works = true, string? waitsOn = null)
            {
                var item = new MenuItem { Header = label, IsEnabled = works && rows.Count > 0 };
                if (!works && waitsOn is not null) ToolTip.SetTip(item, waitsOn);
                item.Click += (_, _) => RunCommand(id);
                flyout.Items.Add(item);
            }

            Command("Reply", MailCommands.Reply.Id);
            Command("Reply All", MailCommands.ReplyAll.Id);
            Command("Forward", MailCommands.Forward.Id);
            flyout.Items.Add(new Separator());

            var read = new MenuItem
            {
                Header = rows.Any(r => r.IsUnread) ? "Mark as Read" : "Mark as Unread",
                IsEnabled = rows.Count > 0,
            };
            read.Click += (_, _) => RunCommand(MailCommands.Unread.Id);
            flyout.Items.Add(read);

            Command("Categorize…", MailCommands.Categorize.Id);
            Command(rows.Any(r => !r.IsFlagged) ? "Follow Up" : "Clear Flag", MailCommands.FollowUp.Id);
            Command("Snooze", MailCommands.Snooze.Id);
            flyout.Items.Add(new Separator());

            Command("Rules…", MailCommands.Rules.Id);
            Command("Move…", MailCommands.MoveTo.Id);

            // With Focused Inbox on, the reference offers the other half and its "always".
            if (shell.ShowFocusedPivot)
            {
                Command(shell.ShowOther ? "Move to Focused" : "Move to Other",
                    shell.ShowOther ? MailCommands.MoveToFocused.Id : MailCommands.MoveToOther.Id);
                Command(shell.ShowOther ? "Always Move to Focused" : "Always Move to Other",
                    shell.ShowOther ? MailCommands.AlwaysMoveToFocused.Id : MailCommands.AlwaysMoveToOther.Id);
            }

            Command(shell.IsIgnored(rows) ? "Stop Ignoring Conversation" : "Ignore", MailCommands.Ignore.Id);
            Command("Junk", MailCommands.Junk.Id);
            flyout.Items.Add(new Separator());

            Command("Delete", MailCommands.Delete.Id);
            Command("Archive", MailCommands.Archive.Id);
        };

        return flyout;
    }

    /// <summary>A category's colour, as a small square, from its token.</summary>
    private static Control CategorySwatch(string token)
    {
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2) };
        swatch[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(token + ".brush");
        return swatch;
    }

    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };

    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the groups that asked to be checked on a timer.
    /// </summary>
    /// <remarks>
    /// A minute is the resolution: the shortest schedule anyone sets is measured in minutes, and
    /// a timer that wakes more often than the thing it is waiting for is a laptop battery spent
    /// on nothing.
    /// <para>
    /// A group whose turn comes while a run is in flight waits for the next tick rather than
    /// queueing. Two send/receives at once would open two sessions to the same server, and the
    /// second would find nothing the first had not already taken.
    /// </para>
    /// </remarks>
    private void WireSchedule(ShellViewModel shell)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };

        timer.Tick += (_, _) =>
        {
            WakeSnoozed(shell);
            CheckReminders(shell);

            if (_transferring || App.Transfer.WorkOffline) return;

            var now = DateTimeOffset.UtcNow;

            foreach (var group in App.Groups.All.Where(g => g.ScheduleEnabled))
            {
                var due = !_lastRun.TryGetValue(group.Name, out var last)
                          || now - last >= TimeSpan.FromMinutes(group.ScheduleMinutes);

                if (!due) continue;

                _lastRun[group.Name] = now;
                _ = SendReceiveAsync(shell, group);
                return;
            }
        };

        // Started rather than run: a client that polls the instant it opens is one that
        // reconnects on every restart, which is a way to get an account rate-limited.
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();

        WireIdleWatchers(shell);
    }

    /// <summary>
    /// Brings back the snoozed messages whose time has come, and announces them the way new mail
    /// is announced — a message that returns is the new mail it was snoozed to become.
    /// </summary>
    private void WakeSnoozed(ShellViewModel shell)
    {
        var woken = shell.WakeSnoozed(DateTimeOffset.UtcNow);
        if (woken.Count == 0 || !App.MailOptions.DisplayDesktopAlert) return;

        var result = new SendReceiveResult(
        [
            .. woken.GroupBy(w => w.Address).Select(g =>
                new AccountRunResult(g.Key, g.Count(), 0) { Arrived = [.. g.Select(w => w.MessageId)] }),
        ]);

        foreach (var toast in NewMailNotice.Toasts(result, DescribeArrival))
        {
            _notifier.Notify(ToastFor(toast));
        }
    }

    private readonly List<ImapIdleWatcher> _watchers = [];

    /// <summary>
    /// Puts every IMAP account under IDLE, so the server announces new mail rather than waiting
    /// for the poll timer. A change runs the same send/receive a manual one does, on the UI
    /// thread and only when nothing is already in flight — the watcher does the waiting, the
    /// shell does the syncing, so there is one path through the store.
    /// </summary>
    private void WireIdleWatchers(ShellViewModel shell)
    {
        void OnChange(object? _, string address) => Dispatcher.UIThread.Post(() =>
        {
            if (_transferring || App.Transfer.WorkOffline) return;
            shell.StatusRight = $"New mail on {address}…";
            _ = SendReceiveAsync(shell);
        });

        // A capture run poses accounts; none of them has a server to watch.
        if (WindowCapture.IsRequested) return;

        Opened += async (_, _) =>
        {
            foreach (var target in (await AccountConnectionsAsync())
                         .Where(t => t.Connection.Protocol == MailProtocol.Imap))
            {
                var watcher = new ImapIdleWatcher(target.Connection);
                watcher.ChangeDetected += OnChange;
                watcher.Start();
                _watchers.Add(watcher);
            }
        };

        Closed += (_, _) =>
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        };
    }

    /// <summary>
    /// The Send/Receive Groups menu: each defined group, then the dialog that defines them.
    /// </summary>
    /// <remarks>
    /// Built when it is asked for rather than once, because it lists what the dialog behind it
    /// can change. A menu cached at startup would go stale the first time a group is renamed.
    /// </remarks>
    private void ShowGroupsMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        foreach (var group in App.Groups.All)
        {
            var item = new MenuItem { Header = group.Name };
            var chosen = group;
            item.Click += (_, _) => _ = SendReceiveAsync(shell, chosen);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var define = new MenuItem { Header = "Define Send/Receive Groups…" };
        define.Click += async (_, _) =>
        {
            await new SendReceiveGroupsDialog(App.Groups, AccountAddresses()).ShowDialog(this);
        };
        flyout.Items.Add(define);

        // Dropped from the ribbon control that raised it where there is one, and from the
        // window otherwise — a menu with nothing to hang off still has to appear somewhere.
        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>
    /// Every account, for the groups dialog.
    /// </summary>
    /// <remarks>
    /// Not the transfer targets: those are accounts with server settings, and an account still
    /// being set up belongs in a group as much as a working one. Excluding it would make the
    /// dialog say there are no accounts on a machine that plainly has one.
    /// </remarks>
    private static IReadOnlyList<string> AccountAddresses()
        => [.. App.Accounts.All.Select(a => a.Account.Address)];

    /// <summary>
    /// Show Progress, from the Send/Receive tab. Reopens the dialog for the run in flight, or
    /// says there is nothing to show rather than opening an empty one.
    /// </summary>
    private void ShowProgressDialog(ShellViewModel shell)
    {
        if (_tasks is null)
        {
            shell.StatusRight = "Nothing is being sent or received.";
            return;
        }

        // The checkbox turns the dialog off for a run that opens it by itself, not for a user
        // who has just asked for it.
        ShowProgressDialog(force: true);
    }

    /// <summary>
    /// Send/Receive All Folders. Runs off the UI thread and reports through the status bar and
    /// the progress dialog, which is also where it is cancelled from.
    /// </summary>
    /// <param name="group">
    /// Which group to run, or null for Send/Receive All Folders — every group that asked to be
    /// included, which is what the reference means by F9.
    /// </param>
    /// <param name="retrying">
    /// True on the second run of a pair, after the reader has agreed to a certificate the first
    /// was refused. It stops a server that refuses whatever it is shown from asking forever.
    /// </param>
    private async Task SendReceiveAsync(
        ShellViewModel shell, SendReceiveGroup? group = null, bool retrying = false)
    {
        if (_transferring) return;

        var accounts = InGroup(await AccountConnectionsAsync(), group);
        if (accounts.Count == 0)
        {
            shell.StatusRight = group is null
                ? "No account is set up yet. File, Add Account."
                : $"No account in \u201c{group.Name}\u201d is set up.";
            return;
        }

        _transferring = true;

        // The dialog from the last run goes first. It is bound to that run's tasks, and this line
        // replaces them — so a dialog left open by a failure would sit there showing the failure
        // while the new run succeeded behind it, and ShowProgressDialog below would decline to
        // open a second one because it could see the first. Which is exactly what happened after
        // a certificate was trusted and the retry went through: eight messages arrived and the
        // window on screen still said Failed.
        CloseProgressDialog();

        _tasks = new SendReceiveTasks(accounts.Select(a => a.Connection.Address));
        _cancellation = new CancellationTokenSource();
        ShowProgressDialog();

        shell.IsTransferring = true;
        shell.TransferProgress = 0;
        shell.TransferTip = "Send/Receive in progress";

        void OnProgress(object? _, PollProgress p) => Dispatcher.UIThread.Post(() =>
        {
            shell.StatusRight = $"{p.Stage} {p.Account}…";
            _tasks?.Report(p);
            _progress?.Refresh();

            // The status bar's own bar, which is all a reader sees once the dialog has been told
            // not to appear.
            if (_tasks is { } running)
            {
                shell.TransferProgress = running.Fraction;
                shell.TransferTip = $"{p.Stage} {p.Account} — {running.Succeeded + running.Failed} of {running.Total}";
            }
        });

        App.Transfer.Progress += OnProgress;

        try
        {
            var result = await Task.Run(() =>
                App.Transfer.RunAsync(accounts, DateTimeOffset.UtcNow, _cancellation.Token));

            _tasks.Finish(result);
            shell.StatusRight = result.Summary();
            shell.Refresh();

            // The Options page's "Display a Desktop Alert": a toast when a run brought new mail.
            // One per message while there are few, naming the sender and subject with Reply,
            // Delete and Mark Read on it, as the reference's alert offers; past that, one toast
            // with the count and — with more than one account — where. Nothing pops when nothing
            // arrived, which is most polls.
            if (App.MailOptions.DisplayDesktopAlert)
            {
                foreach (var toast in NewMailNotice.Toasts(result, DescribeArrival))
                {
                    _notifier.Notify(ToastFor(toast));
                }
            }

            ShowRuleAlerts();

            // An account whose server-side rules could not be put on the server gets another try
            // now that the server has answered a poll.
            _ = SieveSync.RepublishStaleAsync();

            // Send/Receive is one button in the reference and it covers the calendars too, so the
            // DAV engine runs on the same press (§7.5) rather than on a second one.
            await SyncCalendarsAsync(shell, _cancellation.Token);

            // And the feeds, for the same reason: the reference checks a subscription once per
            // download interval, which is this press.
            await PollFeedsAsync(shell, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _tasks.Finish(new SendReceiveResult([]));
            shell.StatusRight = "Send/receive cancelled.";
        }
        catch (Exception ex)
        {
            Log.Crash("send/receive", ex);
            _tasks.Finish(new SendReceiveResult([]));
            shell.StatusRight = "Send/receive could not finish. See the log.";
        }
        finally
        {
            App.Transfer.Progress -= OnProgress;
            _transferring = false;

            // The bar goes with the transfer it was reporting on, whether that ended in success,
            // failure or a cancellation — a status bar still showing progress for something that
            // stopped is worse than one showing nothing.
            shell.IsTransferring = false;
            shell.TransferProgress = 0;

            _progress?.Refresh();

            // A run that worked has nothing left to say, so the dialog goes when it does. One
            // that did not is the reason the dialog has an Errors tab, and stays.
            if (_tasks.Errors.Count == 0) CloseProgressDialog();

            _cancellation.Dispose();
            _cancellation = null;
        }

        // A run refused a certificate has a question for the reader rather than an error for them
        // to read. The account wizard already asks this on the way in; a server met later — a
        // second host for the same account, a renewal, a provider that moved — was refused with
        // nothing but a line in the log, and no way to answer from inside the application at all.
        if (!retrying) await AskAboutRefusedCertificatesAsync(shell, group);
    }

    /// <summary>
    /// Offers the certificates a run was refused, and runs it again if any were agreed to.
    /// </summary>
    /// <remarks>
    /// Once, and only after the run has finished: asking mid-flight would put a dialog over a
    /// transfer that is still working on other accounts, and the refusal is recorded rather than
    /// raised precisely so the question can wait for a moment when it can be answered.
    /// </remarks>
    private async Task AskAboutRefusedCertificatesAsync(ShellViewModel shell, SendReceiveGroup? group)
    {
        if (App.Trust.Refused is not { Count: > 0 } refusals) return;

        var agreed = false;

        foreach (var refusal in refusals)
        {
            Log.Info($"Send/receive was refused {refusal.Host}:{refusal.Port}; asking.");
            if (await CertificateDialog.AskAsync(this, refusal))
            {
                App.Trust.Pin(refusal);
                agreed = true;
            }
        }

        // Cleared either way: a certificate the reader declined should not be put to them again
        // on the next press, and one they agreed to is pinned and will not come back.
        App.Trust.ClearRefusals();

        if (!agreed) return;

        shell.StatusRight = "Trying again with the certificate you trusted…";
        await SendReceiveAsync(shell, group, retrying: true);
    }

    /// <summary>
    /// The RSS feeds, read on the same press the mail is.
    /// </summary>
    /// <remarks>
    /// Into the default account's tree, under RSS Feeds, one folder per subscription — where the
    /// reference keeps them. What arrives is mail as far as everything downstream is concerned.
    /// </remarks>
    private async Task PollFeedsAsync(ShellViewModel shell, CancellationToken cancellation)
    {
        if (App.Feeds.All.Count == 0) return;
        if (App.Accounts.All.FirstOrDefault() is not { } account) return;

        var report = await App.FeedReader.PollAsync(account, DateTimeOffset.UtcNow, cancellation);
        if (report.Delivered == 0 && report.Failed.Count == 0) return;

        shell.Refresh();
        shell.StatusRight = "Feeds: " + report.Summary;
        Log.Info($"Feeds: {report.Summary}.");
    }

    /// <summary>
    /// Narrows a run to a group, or to whatever Send/Receive All covers.
    /// </summary>
    /// <remarks>
    /// The filter is here rather than in the service: which accounts a run covers is a
    /// preference, and the service's job is to run whatever it is handed.
    /// </remarks>
    private static List<TransferTarget> InGroup(
        List<TransferTarget> accounts, SendReceiveGroup? group)
    {
        var addresses = accounts.Select(a => a.Connection.Address).ToList();

        var wanted = group is null
            ? App.Groups.AccountsForSendReceiveAll(addresses)
            : App.Groups.AccountsIn(group, addresses);

        return [.. accounts.Where(a => wanted.Contains(a.Connection.Address, StringComparer.OrdinalIgnoreCase))];
    }

    private bool _transferring;
    private SendReceiveTasks? _tasks;
    private SendReceiveProgressDialog? _progress;
    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// Opens the progress dialog for the run in flight, unless the user has turned it off.
    /// </summary>
    /// <remarks>
    /// Shown rather than shown modally: a send/receive that blocks the window until it finishes
    /// is a mail client that stops being a mail client every time it checks for mail.
    /// </remarks>
    private void ShowProgressDialog(bool force = false)
    {
        if (_tasks is null) return;
        if (!force && App.Settings.GetBool(SendReceiveProgressDialog.HideSetting)) return;
        if (_progress is not null) return;

        _progress = new SendReceiveProgressDialog(_tasks, App.Settings, CancelTransfer);
        _progress.Closed += (_, _) => _progress = null;
        _progress.Show(this);
    }

    private void CloseProgressDialog()
    {
        _progress?.Close();
        _progress = null;
    }

    /// <summary>Cancel All, from the progress dialog or the Send/Receive tab.</summary>
    private void CancelTransfer()
    {
        _cancellation?.Cancel();
        if (DataContext is ShellViewModel shell) shell.StatusRight = "Cancelling…";
    }

    /// <summary>
    /// Everything the Backstage's Account Information page can ask for. One place, so a new
    /// entry in either menu is a case here rather than another handler wired somewhere else.
    /// </summary>
    /// <summary>
    /// The Backstage acts on whichever window opened it. BackstageActions holds the behaviour;
    /// this supplies the three things only this window knows.
    /// </summary>
    private BackstageHost BackstageContext() => new(
        this,
        message => { if (DataContext is ShellViewModel s) s.StatusRight = message; },
        () => { if (DataContext is ShellViewModel s) s.Refresh(); },
        CloseBackstage);

    private Task BackstageActionAsync(string action)
        => BackstageActions.RunAsync(BackstageContext(), action);

    private Task AddAccountAsync() => BackstageActions.AddAccountAsync(BackstageContext());

    /// <summary>
    /// Alt on its own opens the KeyTip traversal; Alt as a modifier does not.
    /// </summary>
    /// <remarks>
    /// Which of the two it is only becomes clear on release, so the decision waits for
    /// <see cref="OnKeyUp"/> — deciding on the way down would put badges over the ribbon every
    /// time someone reached for Alt+Tab.
    /// </remarks>
    private static bool IsAltKey(Avalonia.Input.Key key)
        => key is Avalonia.Input.Key.LeftAlt
            or Avalonia.Input.Key.RightAlt
            or Avalonia.Input.Key.System;

    protected override void OnKeyUp(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Handled || !IsAltKey(e.Key) || !_altAlone) return;

        _altAlone = false;
        e.Handled = true;

        if (_keyTips.IsActive) _keyTips.End();
        else _keyTips.Begin(FirstLevelKeyTips());
    }

    /// <summary>
    /// What Alt reveals first: the tabs, and the Quick Access Toolbar numbered left to right.
    /// </summary>
    private IReadOnlyList<KeyTipTarget> FirstLevelKeyTips()
    {
        var targets = new List<KeyTipTarget>(_ribbon.TabKeyTips());

        // The toolbar has two homes and may be hidden altogether, so this asks which one is
        // actually on screen rather than assuming the title bar.
        var qat = new[] { "QuickAccessBar", "QuickAccessBarBelow" }
            .Select(this.FindControl<ItemsControl>)
            .FirstOrDefault(bar => bar is { IsEffectivelyVisible: true });

        if (qat is null) return targets;

        var position = 0;
        foreach (var button in qat.GetVisualDescendants().OfType<Button>())
        {
            // The reference numbers the QAT rather than lettering it, and stops at nine.
            if (++position > 9) break;

            var invoke = button.Command;
            var parameter = button.CommandParameter;

            targets.Add(new KeyTipTarget
            {
                Tip = position.ToString(CultureInfo.InvariantCulture),
                Target = button,
                Activate = () =>
                {
                    if (invoke?.CanExecute(parameter) == true) invoke.Execute(parameter);
                },
            });
        }

        return targets;
    }

    /// <summary>
    /// F9 runs a send/receive, as every mail client since the nineties has.
    /// </summary>
    /// <remarks>
    /// These belong in the command catalogue so the shortcut editor in Phase 8 can rebind them.
    /// They are here for now because nothing yet reads a gesture table.
    /// </remarks>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (IsAltKey(e.Key))
        {
            _altAlone = e.KeyModifiers is Avalonia.Input.KeyModifiers.Alt
                or Avalonia.Input.KeyModifiers.None;
            return;
        }

        _altAlone = false;

        // While badges are up the keyboard belongs to the traversal. A key it does not
        // recognise dismisses it and is then handled normally, rather than vanishing.
        if (_keyTips.IsActive)
        {
            if (_keyTips.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            _keyTips.End();
        }

        // Alt+Down opens a split button's menu while the button has the focus, which is the one
        // meaning of that chord the calendar's own must not take: the ribbon is asked first and
        // only answers when something of its own is focused.
        if (e.Key is Avalonia.Input.Key.Down && e.KeyModifiers == Avalonia.Input.KeyModifiers.Alt
            && _ribbon?.OpenFocusedMenu() == true)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ShellViewModel shell) return;

        var control = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);

        // Ctrl+Shift+1..9 run the Quick Step with that shortcut.
        if (control && shift && RunQuickStepShortcut(shell, e.Key))
        {
            e.Handled = true;
            return;
        }

        // Esc gives up a search, from the box or from the list — the folder comes back. Not a
        // command, because it undoes a state rather than doing something, and because a command
        // would fire wherever the caret was.
        if (e.Key is Avalonia.Input.Key.Escape && shell.SearchText.Length > 0)
        {
            shell.SearchText = string.Empty;
            (this.FindControl<Control>("MessageList") ?? (Control)this).Focus();
            e.Handled = true;
            return;
        }

        // F6 walks the panes, Shift+F6 and Ctrl+Shift+Tab walk them backwards.
        if (e.Key is Avalonia.Input.Key.F6 || (control && shift && e.Key is Avalonia.Input.Key.Tab))
        {
            CycleRegion(shell, shift || e.Key is Avalonia.Input.Key.Tab ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Page and Home and End move the message in the reading pane while the pane has the
        // focus, which is where F6 leaves it.
        if (e.KeyModifiers is Avalonia.Input.KeyModifiers.None && ScrollReadingPane(e.Key))
        {
            e.Handled = true;
            return;
        }

        // Everything else the keyboard does here goes through the key map — a command's own
        // shortcut, or the one the reader gave it in Customize Keyboard — asked for the module
        // that is open, so Delete throws away whichever kind of item is in front of the reader.
        if (Keystroke.Of(e) is not { } chord || IsTyping(chord)) return;
        if (App.Keys.CommandFor(chord, shell.Module) is { } id)
        {
            RunCommand(id);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Pages the reading pane, and says whether it was the reading pane's key to take.
    /// </summary>
    /// <remarks>
    /// Only while the focus is inside the pane: Home and End belong to the message list when the
    /// list has them, and to the message being read when the pane does. A rendered message shown
    /// in the web view keeps its own keys — they never reach here — so this is what moves the
    /// text that is drawn rather than browsed.
    /// </remarks>
    private bool ScrollReadingPane(Avalonia.Input.Key key)
    {
        if (key is not (Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown
            or Avalonia.Input.Key.Home or Avalonia.Input.Key.End))
        {
            return false;
        }

        if (this.FindControl<Border>("ReadingPane") is not { IsEffectivelyVisible: true } pane) return false;

        var inside = false;
        for (var node = FocusManager?.GetFocusedElement() as Visual; node is not null && !inside; node = node.GetVisualParent())
        {
            inside = ReferenceEquals(node, pane);
        }

        if (!inside || pane.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller) return false;

        switch (key)
        {
            case Avalonia.Input.Key.PageUp: scroller.PageUp(); break;
            case Avalonia.Input.Key.PageDown: scroller.PageDown(); break;
            case Avalonia.Input.Key.Home: scroller.ScrollToHome(); break;
            default: scroller.ScrollToEnd(); break;
        }

        return true;
    }

    /// <summary>
    /// F6: the focus moves to the next pane, and round again at the end.
    /// </summary>
    /// <remarks>
    /// The panes are the ones on screen, so the reading pane is skipped when it is off and the
    /// calendar's workspace stands where the message list does in the other module. The pane a
    /// key lands on is made focusable as it is asked for rather than in the markup — a border is
    /// not a tab stop, and making it one would put it in Tab's way as well as F6's.
    /// </remarks>
    private void CycleRegion(ShellViewModel shell, int by)
    {
        var regions = new List<Control>();

        void Add(string name)
        {
            if (this.FindControl<Control>(name) is { IsEffectivelyVisible: true } found) regions.Add(found);
        }

        Add("FolderList");
        if (shell.Module == MailboxModule.Mail)
        {
            Add("MessageList");
            if (shell.ReadingPaneVisible) Add("ReadingPane");
        }
        else
        {
            Add("ModuleHost");
        }

        if (regions.Count == 0) return;

        // Which pane the focus is in now: the walk upwards, because the focus is on something
        // inside the pane rather than on the pane itself.
        var at = -1;
        for (var node = FocusManager?.GetFocusedElement() as Visual; node is not null && at < 0; node = node.GetVisualParent())
        {
            at = regions.FindIndex(r => ReferenceEquals(r, node));
        }

        var next = regions[at < 0 ? 0 : ((at + by) % regions.Count + regions.Count) % regions.Count];
        next.Focusable = true;
        next.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Ctrl+. and Ctrl+, — the next and previous message, whatever the list is arranged by.
    /// </summary>
    /// <remarks>
    /// The step is over the rows on screen rather than over the folder, so a collapsed group or a
    /// filter is respected, and the group headers between them are stepped past: selecting one
    /// collapses it, which is not what asking for the next message means.
    /// </remarks>
    private static void StepSelection(ShellViewModel shell, int by)
    {
        var rows = shell.VisibleRows;
        if (rows.Count == 0) return;

        var from = shell.SelectedRow is { } current ? IndexOf(rows, current) : -1;
        for (var at = from + by; at >= 0 && at < rows.Count; at += by)
        {
            if (rows[at] is ViewModels.GroupHeaderRow) continue;
            shell.SelectedRow = rows[at];
            return;
        }

        static int IndexOf(IReadOnlyList<object> rows, object row)
        {
            for (var at = 0; at < rows.Count; at++)
            {
                if (ReferenceEquals(rows[at], row)) return at;
            }

            return -1;
        }
    }

    /// <summary>
    /// "?" — the list of every command and the key that runs it, which is Customize Keyboard.
    /// </summary>
    private async Task ShowKeyboardShortcutsAsync()
        => await new CustomizeKeyboardDialog(App.Keys, App.Commands).ShowDialog(this);

    /// <summary>
    /// Whether a chord belongs to the box the caret is in rather than to the window.
    /// </summary>
    /// <remarks>
    /// A box that takes text owns the plain keys while it has the focus: Insert, "?" and Enter
    /// are the reader typing, not Flag, the shortcut list and Open. Only a chord holding Ctrl or
    /// Alt — or a function key, which types nothing — is the window's to run. The keys the box
    /// itself acts on, Delete and Backspace among them, never reach here: it has marked them
    /// handled already.
    /// </remarks>
    private bool IsTyping(Mailbox.Core.Keyboard.Chord chord)
        => Keystroke.IsTyping(chord) && FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// Presses one chord as a person would, and says what it did.
    /// </summary>
    /// <remarks>
    /// Through the window's own key handler rather than straight to the command, so what the pose
    /// exercises is what a keystroke does — the panes F6 walks, the menu Alt+Down opens, the keys
    /// a text box keeps to itself — and not only the map's answer. What it did is read back from
    /// the store and the focus rather than assumed.
    /// </remarks>
    private void PressChord(string key)
    {
        // "focus:<control name or ribbon command id>" puts the focus somewhere first, a keystroke
        // meaning different things in different places: Alt+Down opens the menu of the split
        // button that has the focus and moves the calendar when nothing on the bar does.
        if (key.StartsWith("focus:", StringComparison.OrdinalIgnoreCase))
        {
            var what = key["focus:".Length..].Trim();
            _ribbon.UpdateLayout();

            var target = this.FindControl<Control>(what) ?? _ribbon.ControlFor(new CommandId(what));
            if (target is not null)
            {
                target.Focusable = true;
                target.Focus();
            }

            Log.Info($"Harness: focus asked for {what} — {(FocusManager?.GetFocusedElement() as Control)?.Name ?? target?.GetType().Name ?? "nothing"} has it.");
            return;
        }

        var chord = Mailbox.Core.Keyboard.Chord.Parse(key);
        var module = (DataContext as ShellViewModel)?.Module ?? MailboxModule.Mail;
        var command = chord is null ? null : App.Keys.CommandFor(chord, module);
        Log.Info($"Harness: key {key} → {(command?.Value ?? "nothing")}.");

        if (chord is not null && Enum.TryParse<Avalonia.Input.Key>(chord.Key, out var pressed))
        {
            // Raised at whatever has the focus and left to bubble, which is the path a real
            // keystroke takes: the list gets Home before the window does, and the window sees
            // only what the list did not take.
            var at = FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? this;
            at.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Source = at,
                Key = pressed,
                KeyModifiers = Keystroke.Modifiers(chord.Modifiers),
            });
        }

        if (DataContext is not ShellViewModel shell) return;

        var row = shell.SelectedMessage;
        var after = row is null ? null : shell.SummaryOf(row);
        var focused = FocusManager?.GetFocusedElement() as Control;
        Log.Info($"Harness: after {key} — focus on {focused?.Name ?? focused?.GetType().Name ?? "nothing"}, "
            + $"selected “{row?.Subject ?? "nothing"}” of {SelectedRows().Count}, "
            + $"flag {(after?.FollowUpDue is { } due ? due.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : after?.IsFlagged == true ? "set, no date" : "none")}, "
            + $"{(after?.IsRead == true ? "read" : "unread")}, search “{shell.SearchText}”, "
            + $"status “{shell.StatusRight}”.");
    }

    /// <summary>Runs a query across every mailbox — what Mailbox Cleanup's Find buttons hand over.</summary>
    public void SearchEverywhere(string query)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.Scope = ShellViewModel.SearchScope.AllMailboxes;
        shell.SearchText = query;
    }

    /// <summary>
    /// AutoArchive's turn, at start: when it is due — the interval has passed since the last
    /// run — it asks first when the settings say to, then runs off the UI thread and says what
    /// it did. Never in a capture run.
    /// </summary>
    private async Task AutoArchiveIfDueAsync(ShellViewModel shell)
    {
        if (WindowCapture.IsRequested) return;
        var options = App.AutoArchive;
        if (!options.Enabled || !Mailbox.Core.Archive.AutoArchive.IsDue(options.LastRun, options.EveryDays, DateTimeOffset.Now)) return;
        if (App.Accounts.All.Count == 0) return;

        if (options.Prompt)
        {
            var go = await Confirm.AskAsync(this, "AutoArchive",
                "Would you like to AutoArchive your old items now?", "Yes", destructive: false);
            if (!go) return;
        }

        var outcome = await Task.Run(() => Archiver.RunAll(App.Accounts.All, options, DateTimeOffset.Now));
        options.LastRun = DateTimeOffset.Now;
        shell.Refresh();
        shell.StatusRight = "AutoArchive: " + outcome.Summary;
    }

    /// <summary>The list pane's width: the token's beside the reading pane, the rest of the window without it.</summary>
    /// <summary>The height the reading pane opens at under the list; the splitter above it changes it from there.</summary>
    private const double ReadingPaneBottomHeight = 320;

    /// <summary>
    /// Lays the list and the reading pane out for the pane's placement: beside the list at the
    /// token's list width (Right), under a full-width list (Bottom), or the list alone (Off).
    /// </summary>
    private void FitListPane(ShellViewModel shell)
    {
        if (this.FindControl<Grid>("PaneGrid") is not { } grid || this.FindControl<Border>("ListPane") is not { } pane) return;
        if (grid.ColumnDefinitions.Count < 6 || grid.RowDefinitions.Count < 3) return;
        var reading = this.FindControl<Border>("ReadingPane");
        var beside = this.FindControl<GridSplitter>("ReadingSplitter");
        var under = this.FindControl<GridSplitter>("ReadingSplitterBottom");

        var bottom = shell.ReadingPaneVisible && shell.ReadingPaneAtBottom;

        if (shell.ReadingPaneVisible && !bottom)
        {
            grid.ColumnDefinitions[3].Width = GridLength.Auto;
            grid.ColumnDefinitions[5].Width = new GridLength(1, GridUnitType.Star);
            pane[!WidthProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("list.width.value");
        }
        else
        {
            pane.ClearValue(WidthProperty);
            grid.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[5].Width = GridLength.Auto;
        }

        // Where the pane and its splitter go: column 5 of the first row beside the list, or the
        // third row across the list's columns under it, with the row given its opening height.
        Grid.SetColumnSpan(pane, bottom ? 3 : 1);
        if (reading is not null)
        {
            Grid.SetRow(reading, bottom ? 2 : 0);
            Grid.SetColumn(reading, bottom ? 3 : 5);
            Grid.SetColumnSpan(reading, bottom ? 3 : 1);
        }

        grid.RowDefinitions[2].Height = bottom ? new GridLength(ReadingPaneBottomHeight) : GridLength.Auto;
        if (beside is not null) beside.IsVisible = shell.ReadingPaneVisible && !bottom;
        if (under is not null) under.IsVisible = bottom;
    }

    /// <summary>
    /// The row actions and dragging to a folder.
    /// </summary>
    /// <remarks>
    /// Both are handled on the list rather than per row: rows are virtualized and recycled, so
    /// anything attached to one is attached to whatever scrolls into its place next.
    /// </remarks>
    private void WireListInteraction(ShellViewModel shell)
    {
        if (this.FindControl<ListBox>("MessageList") is not { } list) return;

        // The list's width decides whether the Compact view draws the card or the line — the
        // reference's "use compact layout in widths smaller than N characters".
        list.SizeChanged += (_, e) => shell.ListWidth = e.NewSize.Width;
        shell.ListWidth = list.Bounds.Width;

        // Home and End go to the first and last message, which in a grouped list is not the
        // first and last row: the list's own Home lands on a group header, and selecting one
        // folds the group rather than showing a message. Taken before the list sees them.
        list.AddHandler(KeyDownEvent, (object? _, Avalonia.Input.KeyEventArgs e) =>
        {
            if (e.Key is not (Avalonia.Input.Key.Home or Avalonia.Input.Key.End)) return;
            if (e.KeyModifiers is not (Avalonia.Input.KeyModifiers.None or Avalonia.Input.KeyModifiers.Control)) return;

            var rows = shell.VisibleRows;
            var wanted = e.Key is Avalonia.Input.Key.Home
                ? rows.FirstOrDefault(r => r is not ViewModels.GroupHeaderRow)
                : rows.LastOrDefault(r => r is not ViewModels.GroupHeaderRow);

            if (wanted is null) return;

            shell.SelectedRow = wanted;
            list.SelectedItem = wanted;
            list.ScrollIntoView(wanted);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Quick Click: one click on a row's Flag or Categories cell does what Set Quick Click…
        // nominated, without a menu.
        list.AddHandler(MessageCells.QuickClickEvent, (object? _, QuickClickEventArgs e) => QuickClick(shell, e));

        // With the reading pane off the list takes the window, as the reference's does; with it
        // on, the list is its token's width beside the pane.
        FitListPane(shell);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.ReadingPaneVisible) or nameof(ShellViewModel.ReadingPaneAtBottom)) FitListPane(shell);
        };

        // The action buttons. Found by walking up from what was clicked, because the button
        // itself lives inside a template the list owns.
        list.AddHandler(Button.ClickEvent, (object? sender, RoutedEventArgs e) =>
        {
            if (e.Source is not Button { Tag: string action } button) return;
            if (button.DataContext is not ViewModels.MessageRow row) return;

            e.Handled = true;
            switch (action)
            {
                case "archive": shell.MoveTo([row], FolderRole.Archive); break;
                case "delete": shell.Delete([row], permanently: false); break;
                case "flag": shell.SetFlagged([row], !row.IsFlagged); break;
                case "read": shell.SetRead([row], row.IsUnread); break;
            }
        }, RoutingStrategies.Tunnel);

        // Double-click opens the message in its own window, as the reference does. The click
        // that selects the row has already run, so the window shows what is on screen.
        list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Button) return;
            OpenMessageWindow(shell);
        };

        // Right-click: the reference's menu over a message, in its order, over the selection.
        // Every entry runs the same command the ribbon button does, so a thing done from here
        // is the thing done from there. Entries whose command is not built yet say what they
        // wait for, the way the ribbon's do.
        list.ContextFlyout = RowMenu(shell);

        // A right-click on a row that is not selected selects it first, as the reference does,
        // so the menu acts on what is under the pointer rather than on whatever was selected
        // before. A right-click inside the selection leaves the selection alone.
        list.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not ViewModels.MessageRow pressed) return;

            if (!SelectedRows().Contains(pressed))
            {
                list.SelectedItems?.Clear();
                list.SelectedItem = pressed;
            }
        }, RoutingStrategies.Tunnel);

        // Dragging out of the list. Begun from the press, which is what the platform needs;
        // Avalonia holds it until the pointer actually moves, so a plain click still selects.
        list.AddHandler(PointerPressedEvent, async (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not ViewModels.MessageRow pressed) return;
            if (_dragging) return;

            // Whatever is selected, or the row under the pointer when it is not one of them.
            var rows = SelectedRows();
            if (!rows.Contains(pressed)) rows = [pressed];

            _dragging = true;
            try
            {
                using var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(MessageDragFormat, Pack(rows)));

                await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            }
            finally
            {
                _dragging = false;
            }
        }, RoutingStrategies.Bubble);

        WireFolderDropTarget(shell);
    }

    /// <summary>
    /// Our own drag format, in-process only. Message ids mean nothing outside this application
    /// and offering them to other windows would be inviting a paste of numbers.
    /// </summary>
    private static readonly DataFormat<byte[]> MessageDragFormat =
        DataFormat.CreateBytesApplicationFormat("mailbox-message-ids");

    private bool _dragging;

    /// <summary>
    /// Message ids as bytes. The platform's drag payload is bytes, and eight per id is both
    /// exact and cheap — no text encoding to get wrong, and no ambiguity about the separator.
    /// </summary>
    private static byte[] Pack(IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var bytes = new byte[rows.Count * sizeof(long)];
        for (var i = 0; i < rows.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(long)), rows[i].Id);
        }

        return bytes;
    }

    private static IReadOnlyList<long> Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(long) != 0) return [];

        var ids = new long[bytes.Length / sizeof(long)];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = BitConverter.ToInt64(bytes, i * sizeof(long));
        }

        return ids;
    }

    /// <summary>
    /// The folder pane accepts messages dropped on it. The drop target is the list rather than
    /// each row, so the folder under the pointer is worked out at drop time from what is
    /// actually there.
    /// </summary>
    private void WireFolderDropTarget(ShellViewModel shell)
    {
        if (this.FindControl<ListBox>("FolderList") is not { } folders) return;

        DragDrop.SetAllowDrop(folders, true);

        folders.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(MessageDragFormat) && FolderUnder(e) is not null
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        });

        folders.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            e.Handled = true;
            if (Unpack(e.DataTransfer.TryGetValue(MessageDragFormat)) is not { Count: > 0 } ids) return;
            if (FolderUnder(e) is not { } target) return;

            shell.MoveToFolder(ids, target);
        });

        WireFolderTypeAhead(shell, folders);
    }

    /// <summary>
    /// A letter or a digit in the folder pane jumps to the next folder whose name begins with it.
    /// </summary>
    /// <remarks>
    /// "Next" rather than "first", so pressing the same key again walks the folders that share a
    /// letter and comes round to the start — which is how a long list is reached by keyboard. The
    /// search wraps through the whole pane, so it always lands somewhere if anything matches.
    /// </remarks>
    private static void WireFolderTypeAhead(ShellViewModel shell, ListBox folders)
    {
        folders.AddHandler(KeyDownEvent, (object? _, Avalonia.Input.KeyEventArgs e) =>
        {
            if (e.KeyModifiers is not Avalonia.Input.KeyModifiers.None) return;

            var typed = e.Key switch
            {
                >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => (char)('a' + (e.Key - Avalonia.Input.Key.A)),
                >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => (char)('0' + (e.Key - Avalonia.Input.Key.D0)),
                >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad9 => (char)('0' + (e.Key - Avalonia.Input.Key.NumPad0)),
                _ => '\0',
            };

            if (typed == '\0') return;

            var nodes = shell.Folders;
            if (nodes.Count == 0) return;

            var from = shell.SelectedFolder is { } current ? nodes.IndexOf(current) : -1;
            for (var step = 1; step <= nodes.Count; step++)
            {
                var node = nodes[(from + step) % nodes.Count];
                if (node.Name.Length == 0 || char.ToLowerInvariant(node.Name[0]) != typed) continue;

                shell.SelectedFolder = node;
                folders.ScrollIntoView(node);
                e.Handled = true;
                return;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Which folder row the pointer is over, or null when it is over nothing.</summary>
    private static ViewModels.FolderNode? FolderUnder(DragEventArgs e)
        => (e.Source as Control)?.DataContext as ViewModels.FolderNode;

    /// <summary>What the list has highlighted, headers excluded.</summary>
    private IReadOnlyList<ViewModels.MessageRow> SelectedRows()
    {
        var list = this.FindControl<ListBox>("MessageList");
        if (list?.SelectedItems is not { } selected) return [];

        return [.. selected.OfType<ViewModels.MessageRow>()];
    }

    private async Task ConfirmPermanentDeleteAsync(ShellViewModel shell,
        IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        var confirmed = await Confirm.AskAsync(
            this,
            "Delete permanently",
            rows.Count == 1
                ? $"Permanently delete \"{rows[0].Subject}\"?\n\nThis cannot be undone."
                : $"Permanently delete {rows.Count:N0} messages?\n\nThis cannot be undone.",
            "Delete");

        if (confirmed) shell.Delete(rows, permanently: true);
    }

    private async Task ToggleWorkOfflineAsync(ShellViewModel shell)
    {
        App.Transfer.SetWorkOffline(!App.Transfer.WorkOffline, await AccountConnectionsAsync());
        shell.StatusRight = App.Transfer.WorkOffline ? "Working offline." : "Working online.";
    }

    /// <summary>
    /// Turns the open accounts into something the transfer service can use, pulling each
    /// password out of the keyring as late as possible. An account whose servers were never
    /// filled in is skipped rather than attempted against an empty hostname.
    /// </summary>
    /// <summary>
    /// Every account's connection, passwords and all.
    /// </summary>
    /// <remarks>
    /// Asynchronous, and awaited rather than blocked on, because this is where the application
    /// froze: pressing Send/Receive read each account's password out of the keyring from the UI
    /// thread, and the continuation of that read needed the UI thread to run on. Neither could
    /// move, there was no timeout, and the only way out was killing the process.
    /// </remarks>
    private static async Task<List<TransferTarget>> AccountConnectionsAsync(CancellationToken cancellation = default)
    {
        var targets = new List<TransferTarget>();

        foreach (var open in App.Accounts.All)
        {
            var settings = AccountSettings.Load(App.Settings, open.Account.Address);
            if (settings is null) continue;

            targets.Add(new TransferTarget(
                await settings.ToConnectionAsync(open.Account, App.Secrets, App.OAuth, cancellation)
                    .ConfigureAwait(true),
                open.Mail));
        }

        return targets;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
