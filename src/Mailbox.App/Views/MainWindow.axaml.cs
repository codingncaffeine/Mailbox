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

        // MAILBOX_TAB=folder|view|sendreceive|home opens the strip on one tab, so a capture can
        // photograph a tab other than the one the window remembers.
        if (Environment.GetEnvironmentVariable("MAILBOX_TAB") is { Length: > 0 } posedTab)
        {
            _ribbon.ActiveTabId = posedTab.Trim();
        }

        // MAILBOX_RIBBON_TRACE=1 says what the bar actually built — the tabs in the strip, which
        // one is active, and either the classic groups with the variant each settled on or the
        // Simplified row's count and its overflow. A capture proves a tab was photographed; only
        // this says whether the tab holds what the layout document declares, and comparing fifty
        // pictures by eye is how a missing group goes unnoticed. Read after the layout pass,
        // because before one the panel has not chosen a variant.
        if (Environment.GetEnvironmentVariable("MAILBOX_RIBBON_TRACE") == "1")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    _ribbon.UpdateLayout();
                    Log.Info($"Harness: ribbon {_ribbon.Describe()}");
                },
                DispatcherPriority.Background);
        }

        // What is usable given what is selected — and, for Undo and Redo, what has been done.
        // The inline reply strip layers the compose window's own answer over this one and puts
        // it back afterwards, so the two compose rather than replace each other.
        _ribbon.CommandEnabled = IsCommandUsable;
        _ribbon.CommandChecked = IsCommandChecked;
        _ribbon.CommandInvoked += OnRibbonCommand;
        _ribbon.MenuOpened += (id, menu) => MenuProbe.Record($"the menu under {id.Value}", menu);
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);

        // A plugin enabled, disabled or crashed changes what the bar holds — on whichever
        // module is showing, plugin tabs riding all six now. A module not on screen fetches a
        // fresh layout on its way back in anyway.
        App.Plugins.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not ShellViewModel s || _inlineCompose is not null) return;

            _ribbon.Layout = s.Module switch
            {
                MailboxModule.Calendar => CalendarRibbon(),
                MailboxModule.People => PeopleRibbon(),
                MailboxModule.Tasks => TasksRibbon(),
                MailboxModule.Notes => NotesRibbon(),
                MailboxModule.Journal => JournalRibbon(),
                MailboxModule.Feeds => FeedsRibbon(),
                _ => App.MailRibbon(),
            };
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

        _ribbon.FullScreenToggled += (_, _) => ToggleFullScreen(shell);

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

        // Undo and Redo are greyed while there is nothing to take back, so the bar has to hear
        // about a step being pushed, undone or redone.
        shell.Undo.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshCommandEnablement);

        DataContext = shell;

        // The status bar, as the bar itself reads it, just before the capture is taken. The
        // counts are the one part of the shell a photograph shows but nothing can check: a
        // number rendered at 11px is not evidence it agrees with the store, and every pose that
        // changes the folder, the filter or the module changes it. Logged rather than read off
        // the picture, so a claim about the counts can be compared against the store the pose
        // was given.
        if (WindowCapture.IsRequested)
        {
            Opened += (_, _) => DispatcherTimer.RunOnce(
                () => Log.Info(
                    $"Harness: status bar — left “{shell.StatusLeft}”, right “{shell.StatusRight}”, "
                    + $"zoom “{shell.ZoomLabel}” ({shell.ZoomPercent:0}%)."),
                TimeSpan.FromMilliseconds(700));
        }

        // The taskbar entry carries the same two mailboxes the notification area does: full
        // while there is unread post in an inbox, empty once it has been read. The title bar
        // draws its own icon from its own asset, so the chrome the pixel gate holds does not
        // move with this.
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.TotalUnread)) ShowUnreadOnTaskbar(shell);
        };

        ShowUnreadOnTaskbar(shell);

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
        // does what the inventory records rather than what its handler's name suggests.
        // Once per run, not once per window: Open in New Window makes a second shell, and a
        // second shell that ran the same command list again would open a third.
        // Selects every row in the list, so a command can be pressed over a selection rather than
        // over one message: MAILBOX_SELECT_ALL=1. Posted at Loaded, which is before the Background
        // pass MAILBOX_RUN acts on, so a run sees the whole selection. The per-account guard on
        // the unified mailbox — one press, one undo step, each account's own store written — has
        // no other way in, a selection being something only a pointer or Ctrl+A can make.
        // Posted from inside a Loaded pass rather than at it: MAILBOX_FOLDER's own handler runs at
        // Loaded and replaces the list, and a selection made in the same pass is thrown away with
        // the rows it was made over. The nesting is what puts this after the folder has changed —
        // the same trick MAILBOX_SELECT uses for the same reason.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT_ALL") is "1" or "true")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(Phase4BSelectAll, DispatcherPriority.Loaded),
                DispatcherPriority.Loaded);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_RUN") is { Length: > 0 } run && !_harnessRan)
        {
            _harnessRan = true;

            // One pass per command, as a person's presses arrive — not all of them in one pass of
            // the loop. A command that reloads the list replaces every row with a fresh object,
            // and the next command in the same pass then acts on a selection that no longer holds
            // anything: pressing the flag command twice reported the second press as a no-op and
            // left the message flagged rather than complete, which reads exactly like a command
            // that does not work. MAILBOX_KEY has posted one per pass for this reason since it was
            // written; this had not. And a pose that must let something settle first says so in
            // the list itself: `wait-5000` between two ids holds the next press that long, which
            // is how anything driven by idle-time machinery is caught up with.
            Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                // Held across the whole list: render-and-exit fires at the first idle moment,
                // and a wait- entry hands it one — a pose that said wait-6000 before its press
                // was photographed and gone at two seconds, press never run, exit code clean.
                using var hold = WindowCapture.Hold();

                foreach (var id in run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var one = id;

                    if (one.StartsWith("wait-", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(one["wait-".Length..], out var pause))
                    {
                        await System.Threading.Tasks.Task.Delay(pause);
                        continue;
                    }

                    var known = false;
                    string statusBefore = string.Empty;
                    var rowsBefore = 0;

                    await Dispatcher.UIThread.InvokeAsync(
                        () =>
                        {
                            known = App.Commands.TryGet(new CommandId(one), out _);
                            if (DataContext is ShellViewModel before)
                            {
                                statusBefore = before.StatusRight;
                                rowsBefore = before.Messages.Count;
                            }

                            Log.Info($"Harness: running {one}.");

                            // Reply and forward open a window only when the Options page asks for
                            // one; otherwise they grow inline in this window, which this window's
                            // own capture shows. Photograph the next window only in that case.
                            if (one is "mail.reply" or "mail.reply.all" or "mail.forward"
                                && App.MailOptions.OpenRepliesInNewWindow)
                            {
                                CaptureNextWindow();
                            }

                            // MAILBOX_CAPTURE_DIALOG=1 photographs whatever window the command
                            // opens rather than the shell behind it, which is the only way to look
                            // at a dialog: a modal is a window of its own and never appears in the
                            // shell's picture.
                            if (Environment.GetEnvironmentVariable("MAILBOX_CAPTURE_DIALOG") == "1")
                            {
                                CaptureNextWindow();
                            }

                            RunCommand(new CommandId(one));

                            // The status line and the windows: a press that opens a dialog writes
                            // no status, and "nothing happened" and "a dialog opened" read
                            // identically without this. Learnt from the row menu, which asks the
                            // same question.
                            if (DataContext is ShellViewModel s)
                            {
                                Log.Info($"Harness: status \u201c{s.StatusRight}\u201d, windows: {OtherWindows()}");
                            }
                        },
                        DispatcherPriority.Background);

                    // The settled read, which is the one the press sweep classifies from: a
                    // handler that hands off \u2014 a dialog shown from an async continuation, a
                    // task queued \u2014 has not answered when the press returns, and the immediate
                    // line above reads "nothing happened" about a command that is mid-happening.
                    // Same lesson as the caption door's settle, at the same 600ms.
                    await System.Threading.Tasks.Task.Delay(600);
                    await Dispatcher.UIThread.InvokeAsync(
                        () =>
                        {
                            if (DataContext is not ShellViewModel s) return;

                            Log.Info(
                                $"Harness: ran {one} \u2014 {(known ? "known" : "UNKNOWN to the catalogue")}, "
                                + $"status \u201c{statusBefore}\u201d\u2192\u201c{s.StatusRight}\u201d, "
                                + $"rows {rowsBefore}\u2192{s.Messages.Count}, windows: {OtherWindows()}");
                        },
                        DispatcherPriority.Background);
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

        // Imports a maildir into the posed store and logs the report — the counts are the
        // claim. Wants MAILBOX_STORE for the reason every writing pose does: a capture run's
        // accounts are the machine's own unless posed. MAILBOX_IMPORT=maildir:<dir>[,account:<addr>]
        if (Environment.GetEnvironmentVariable("MAILBOX_IMPORT") is { Length: > 0 } importSpec)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (WindowCapture.IsRequested
                        && Environment.GetEnvironmentVariable("MAILBOX_STORE") is not { Length: > 0 })
                    {
                        Log.Warn("Harness: MAILBOX_IMPORT writes to the accounts and wants MAILBOX_STORE.");
                        return;
                    }

                    string? kind = null, source = null, address = null;
                    foreach (var part in importSpec.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var split = part.IndexOf(':');
                        if (split < 1) continue;
                        var key = part[..split].Trim().ToLowerInvariant();
                        var value = part[(split + 1)..].Trim();
                        if (key == "account") address = value;
                        else
                        {
                            kind = key;
                            source = value;
                        }
                    }

                    if (kind is null || source is null)
                    {
                        Log.Warn("Harness: MAILBOX_IMPORT names no source (maildir:|thunderbird:|pst:|mbox:|eml:|ics:|vcf:).");
                        return;
                    }

                    var open = address is null
                        ? App.Accounts.All.FirstOrDefault()
                        : App.Accounts.All.FirstOrDefault(a =>
                            string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase));

                    if (open is null)
                    {
                        Log.Warn($"Harness: import — “{address}” names no account.");
                        return;
                    }

                    var summary = kind switch
                    {
                        "maildir" => new Mailbox.Import.MaildirImporter(open.Mail, open.Account.Id).Run(source).Summary,
                        "thunderbird" => new Mailbox.Import.ThunderbirdImporter(
                            open.Mail, open.Account.Id, App.Pim, App.PimSync.QueuePut).Run(source).Summary,
                        _ => string.Join(" | ", ImportFiles.Run([source], open, App.Pim, App.PimSync.QueuePut)),
                    };

                    Log.Info($"Harness: import — {summary}");
                    foreach (var folder in open.Mail.Folders(open.Account.Id))
                    {
                        var count = open.Mail.Messages(folder.Id).Count;
                        if (count > 0) Log.Info($"Harness: import — {folder.Name}: {count} message(s).");
                    }

                    if (DataContext is ShellViewModel s) s.Refresh();
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the import pose failed.", ex);
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

        // Pressing a chip, a slot or a day in the date navigator, and reading back what the grid
        // is showing. Its own file; it registers its own Opened handler.
        WirePhase6ADoors();

        // Writing a task no surface can write, and reading back what each of the three stores
        // behind the to-do list holds. Its own file; it writes before the window opens and
        // registers its own Opened handler for the read-back.
        WirePhase8ADoors();

        // Opening the folder pane's own menu over a row of the tree and pressing an entry of it.
        // Its own file; it registers its own Opened handler.
        WirePhase10ADoors();

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
                    Log.Info($"Harness: reminder “{item.Subject}” — {item.DueIn(Mailbox.Core.PosedClock.Now)}, "
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
                    var woken = s.WakeSnoozed(Mailbox.Core.PosedClock.Now);
                    Log.Info($"Harness: woke {woken.Count} snoozed message(s) as of "
                             + $"{Mailbox.Core.PosedClock.Now:yyyy-MM-dd HH:mm}.");
                    return;
                }

                if (int.TryParse(snooze, out var index))
                {
                    var presets = Mailbox.Core.SnoozePresets.For(Mailbox.Core.PosedClock.Now);
                    var (header, until) = presets[Math.Clamp(index, 0, presets.Count - 1)];
                    s.Snooze(SelectedRows(), until);
                    Log.Info($"Harness: {header} → until {until:yyyy-MM-dd HH:mm}, status “{s.StatusRight}”");
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

        // What either sound would be: MAILBOX_SOUND=arrival|reminder names the file the rule
        // picks and plays it, and arrival:<path> / reminder:<path> chooses that file first, as
        // the Options row's Browse… does. A capture cannot hear anything, so what is checked is
        // which file was chosen and which player took it — both of which the log says.
        if (Environment.GetEnvironmentVariable("MAILBOX_SOUND") is { Length: > 0 } sound)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => PoseSound(sound), DispatcherPriority.Background);
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

                    // Named plainly, as one of the All Accounts folders ("unified:Inbox"), or with
                    // the account it belongs to ("you@example.com/Inbox") — see FolderNamed.
                    var match = FolderNamed(s, wanted);

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

        // A file the desktop hands over on the command line — an invitation, a card, a saved
        // message — routed through the real ComposeFromCommandLine, so the MimeType claims can be
        // read back from the store rather than trusted. MAILBOX_OPEN_FILE=<path>.
        if (Environment.GetEnvironmentVariable("MAILBOX_OPEN_FILE") is { Length: > 0 } handed)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    ComposeFromCommandLine([handed]);
                    Dispatcher.UIThread.Post(() =>
                    {
                        Log.Info($"Harness: opened {handed} — status “{(DataContext as ShellViewModel)?.StatusRight}”; "
                                 + $"calendars {App.Pim.Collections(Mailbox.Store.Pim.CollectionKind.Events).Count}, "
                                 + $"contacts {App.Contacts.AddressBooks().Sum(b => App.Pim.Items(b.Id).Count)}.");
                    }, DispatcherPriority.ContextIdle);
                },
                DispatcherPriority.Background);
        }

        // Runs the search box, so a capture can show the results. MAILBOX_SEARCH_SCOPE picks the
        // scope (this/current/all) — after the text, and only when posed: a scope set by hand is
        // the reader's own choice, and one set before the search begins would be put back by the
        // Options page's default the moment the first keystroke lands. Left unposed, the search
        // runs at whatever the Search radios say, which is the default under test.
        if (Environment.GetEnvironmentVariable("MAILBOX_SEARCH") is { Length: > 0 } query)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is not ShellViewModel s) return;

                    s.SearchText = query;

                    if (Environment.GetEnvironmentVariable("MAILBOX_SEARCH_SCOPE") is { Length: > 0 } posed)
                    {
                        s.ScopeIndex = posed switch
                        {
                            "this" => 0,
                            "all" => 2,
                            _ => 1,
                        };
                    }

                    // Only the mail module fills SearchResultSummary; every other one narrows its
                    // own list and says so in its own status line. Reading the mail summary in the
                    // calendar answered "No results in Current Mailbox" over a grid that had found
                    // one, which is a read-back that reports the opposite of what happened.
                    Log.Info(s.Module == MailboxModule.Mail
                        ? $"Harness: searched “{query}” — {s.SearchResultSummary}."
                        : $"Harness: searched “{query}” in {s.Module} — {s.ModuleStatusLeft}.");
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

        // The arrangement, which is a grouping and a sort together rather than a column press:
        // MAILBOX_ARRANGE=<name>, or several in order. Through the shell's own setter, which is
        // what the menu behind the "By Date" label does.
        if (Environment.GetEnvironmentVariable("MAILBOX_ARRANGE") is { Length: > 0 } arrangePose)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseArrange(s, arrangePose);
                },
                DispatcherPriority.Loaded);
        }

        // What the list and the folder pane actually hold: MAILBOX_LIST=dump, MAILBOX_FOLDERS=dump.
        // Last of the posed work and at the lowest priority, so what they report is the list after
        // every other pose has had its say rather than part-way through the poses.
        if (Environment.GetEnvironmentVariable("MAILBOX_LIST") == "dump")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseListDump(s);
                },
                DispatcherPriority.ApplicationIdle);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDERS") == "dump")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseFolderDump(s);
                },
                DispatcherPriority.ApplicationIdle);
        }

        // The status bar's progress, pressed: the dialog again. Once "don't show this during
        // Send/Receive" is ticked, this bar is the only way back to it, so it has to be a way
        // back to it.
        if (this.FindControl<Button>("TransferBar") is { } transferBar)
        {
            transferBar.Click += (_, _) => ShowProgressDialog(force: true);
        }

        // The People module's own doors: the favourites, the menu a right-click opens, and what
        // the list and the card beside it are actually holding. Before the peeks, because two of
        // them draw the favourites and do it in their own Opened handler: a list filled after
        // them is a list they never saw.
        WirePeopleDoors();

        // Lets the fidelity harness capture the peek states, which a screenshot otherwise
        // cannot reach because they need a click.
        // The store engines: the corpus poses that fill and age a store, the backup engine, and
        // the read-backs that hold a dialog's numbers against SQLite's. Before the peek, because
        // Mailbox Cleanup and the Data File dialog take their numbers in their constructors — a
        // pose that fills a store from a handler registered after theirs is photographed by
        // neither. What has to run late runs late by priority instead.
        WirePhase14BDoors();

        WireHarnessPeek();

        // The address book as a set of records rather than as a list of rows, which is what a
        // merge, a link and a vCard round trip have to be read back against.
        WireAddressBookDoors();

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

        // Pressing a button inside a dialog, and reading the undo stack back. Wired last so both
        // act after every pose above has had its say — the stack the run finished with is the
        // claim, not the stack half-way through it.
        WirePhase4APoses();

        // The calendar's clocks, its subscriptions and where an export goes. After the above for
        // the same reason: what the calendar holds when the run ends is the claim.
        WirePhase6BDoors();

        // Where the panes ended up, and whether the bar's tasks and the module's agree. Last,
        // because both are measurements of the arrangement everything above has finished making.
        WirePhase8BPoses();

        // A scripted sequence of feed presses in one run, which is the only way at what a second
        // poll of a feed does.
        WirePhase11APoses();

        // The board steps, a row's own buttons, and what the article list ended up holding. Last
        // of all, for the reason above: an order is only an order once every save has been made.
        WirePhase11BDoors();

        // The summary page's links, the People peek's two buttons, and what the calendar peek's
        // agenda is hiding — all of which want the arrangement above to have settled first.
        WirePhase10BDoors();

        // What an export wrote, against the bytes the store holds. Last of all: the file has to
        // have been written before it can be compared with anything.
        WirePhase14ADoors();

        // The customization editors, which live inside a modal dialog and one of them behind a
        // button on the other, so nothing could reach either. Last, because both open a window
        // over this one and the claims they make are about what the shell holds afterwards.
        WirePhase12BDoors();

        // A message whose remote picture can really be fetched, and what the Trust Center's own
        // switches hold in this run. The delivery goes in before the folder pose picks a row.
        WirePhase12ADoors();

        // The menus a pointer opens, walked so every context menu can be read back.
        WirePhase17bDoors();

        // Typing into a system dialog's own fields, and reading back where a credential went.
        // Last, so a form is driven over whatever the poses above have already opened.
        WirePhase13ADoors();

        // An adversarial corpus through the reading pane, one case at a time, with the document
        // the engine was handed written out for each. After everything above: the cases are filed
        // into whichever folder the poses have left open.
        WirePhase15ADoors();

        // The chain states, the signature verdicts, the keyring's failure modes and where an
        // OAuth token ends up. The two deliveries go in at Send priority so a message is filed
        // before the folder pose builds the list it is meant to appear in.
        WirePhase15BDoors();

        // The system dialogs' tabbed pages, their report lists and — the door the plan listed as
        // missing — a dialog's own caption in its hovered and held states. Last of all, because
        // both hold the capture open and what they photograph is the window every pose above has
        // finished opening.
        WirePhase13BDoors();
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
    /// <summary>
    /// Lights a control inside a dialog about to be photographed, when
    /// <c>MAILBOX_HOVER=dialog:&lt;text&gt;</c> asks for one.
    /// </summary>
    /// <remarks>
    /// The third door the audit's inventory found missing. Every other hover pose acts on the
    /// shell, which exists before the pose runs; a dialog does not, so its hover has to be
    /// applied in the moment between the window opening and the picture being taken. Matched on
    /// the text a reader would point at — a button's caption — because that is what a capture is
    /// being asked a question about.
    /// </remarks>
    private static void HoverInside(Window dialog)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_HOVER") is not { Length: > 0 } hovered
            || !hovered.StartsWith("dialog:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var wanted = hovered["dialog:".Length..].Trim();

        foreach (var control in dialog.GetVisualDescendants().OfType<Control>())
        {
            var text = control switch
            {
                Button { Content: string label } => label,
                Button { Content: TextBlock inner } => inner.Text,
                TextBlock block => block.Text,
                _ => null,
            };

            if (text is null || !text.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            // The button rather than the label inside it: the hover state is the button's, and
            // lighting a TextBlock paints nothing.
            var target = control as Button
                         ?? control.GetVisualAncestors().OfType<Button>().FirstOrDefault()
                         ?? control;

            ((IPseudoClasses)target.Classes).Add(":pointerover");
            Log.Info($"Harness: hovering “{text}” in {dialog.GetType().Name}.");
            return;
        }

        Log.Info($"Harness: nothing in {dialog.GetType().Name} reads “{wanted}”.");
    }

    private void CaptureNextWindow()
    {
        if (WindowCapture.RequestedPath is not { } path) return;

        WindowCapture.AnotherWindowWillBeCaptured = true;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(700);
            await WindowCapture.WhileHeldAsync();

            // The newest window rather than the oldest: a press that opens a dialog over a dialog
            // — New Entry over the Address Book — means the one just opened, which is the last.
            var dialog = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)
                ?.Windows.LastOrDefault(w => !ReferenceEquals(w, this));

            if (dialog is not null)
            {
                // A SizeToContent dialog beside an off-screen owner has measured against no
                // screen and is a pixel high; sized from its content, it needs a moment for the
                // windowing system to confirm the new size before the picture is taken.
                if (dialog.ClientSize.Height <= 1 && WindowCapture.SizeFromContent(dialog)) await Task.Delay(400);

                HoverInside(dialog);
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

            // The All Apps menu — a flyout no capture shows, so opening it is the point: its
            // own build logs every entry with the command a press would run, and the probe adds
            // the one thing a log line about a popup has to carry, which is its size.
            case "allapps":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    async () =>
                    {
                        using var hold = WindowCapture.Hold();
                        RunCommand(ViewCommands.Apps.Id);
                        await Task.Delay(400);
                        if (MenuProbe.Last is { } shown) Log.Info($"Harness: {FlyoutProbe.Describe(shown.What, shown.Menu)}");
                    },
                    DispatcherPriority.Background);
                break;

            // The window menu behind the app icon, which a window drawing its own caption has to
            // provide itself. A popup, so it is measured rather than photographed; opened through
            // the button's own Click so the rebuild that greys Restore or Maximize runs first.
            case "windowmenu":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    async () =>
                    {
                        using var hold = WindowCapture.Hold();

                        if (this.FindControl<Button>("WindowMenuButton") is not { Flyout: MenuFlyout menu } button)
                        {
                            Log.Info("Harness: this window has no app-icon menu.");
                            return;
                        }

                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        MenuProbe.Show("the window menu", menu, button);
                        await Task.Delay(400);
                        Log.Info($"Harness: {FlyoutProbe.Describe("the window menu", menu)}");
                    },
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
            //
            // MAILBOX_UNDOSEND takes it further than a photograph: over a message really in the
            // outbox, with the real withdrawal behind the button, so the button can be pressed
            // and what it did read back out of the store. See Phase4BUndoSend.
            case "undosend":
                if (Environment.GetEnvironmentVariable("MAILBOX_UNDOSEND") is { Length: > 0 } undoSpec)
                {
                    Opened += (_, _) => Dispatcher.UIThread.Post(
                        () => Phase4BUndoSend(undoSpec), DispatcherPriority.Loaded);
                    break;
                }

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
            // MAILBOX_BACKSTAGE names a rail page to open on — openexport, print — the rail
            // being buttons a capture cannot press.
            case "backstage":
                Opened += (_, _) =>
                {
                    ShowBackstage();
                    if (this.FindControl<ContentControl>("BackstageHost")!.Content is not BackstageView view) return;

                    if (Environment.GetEnvironmentVariable("MAILBOX_BACKSTAGE") is { Length: > 0 } page)
                    {
                        // `page` or `page:action` — the second form presses one of the page's
                        // own buttons, which is the only way to audit what a section does: they
                        // are not menu entries and a capture cannot click them.
                        var (which, press) = page.Trim().ToLowerInvariant().Split(':', 2) is [var head, var tail]
                            ? (head, tail)
                            : (page.Trim().ToLowerInvariant(), null);

                        view.Open(which);

                        // Two of the Backstage's controls are not actions: the back arrow, and
                        // Add Account, which raises an event of its own. Both are pressed as the
                        // real buttons a reader presses — proving that CloseBackstage works is
                        // not the same as proving the arrow reaches it.
                        if (press is "back" or "addaccount")
                        {
                            Dispatcher.UIThread.Post(
                                () => PressBackstageButton(view, press),
                                DispatcherPriority.Background);
                            ReportBackstageAction(press);
                        }
                        else if (press is { Length: > 0 })
                        {
                            Dispatcher.UIThread.Post(
                                () => _ = BackstageActionAsync(press), DispatcherPriority.Background);
                            ReportBackstageAction(press);
                        }
                    }

                    // MAILBOX_BACKSTAGE_MENU=tools|settings[:<action>] names what the menu holds
                    // and presses one of its entries — a flyout never appears in a capture, so
                    // this is the only way either is checked.
                    if (Environment.GetEnvironmentVariable("MAILBOX_BACKSTAGE_MENU") is { Length: > 0 } menu)
                    {
                        var (which, press) = menu.Split(':', 2) is [var head, var tail] ? (head, tail) : (menu, null);
                        Dispatcher.UIThread.Post(() => view.PoseMenu(which, press), DispatcherPriority.Background);
                        if (press is { Length: > 0 }) ReportBackstageAction(press);
                    }
                };
                break;

            // The menu over a message: seventeen entries and six submenus, none of which a
            // capture can show. MAILBOX_SELECT picks what it acts on.
            case "rowmenu":
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (DataContext is ShellViewModel rowShell) LogRowMenu(rowShell);
                    },
                    DispatcherPriority.Background);
                break;

            // The bar's "…": what it lists at this width, which a capture cannot show.
            case "overflow":
                Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    var items = _ribbon?.OpenOverflowMenu() ?? [];
                    Log.Info($"Harness: the \u2026 menu holds {items.Count}: {string.Join(" | ", items)}");

                    // And how big the popup it opened in actually is. The contents alone are
                    // what a menu was built with; the presenter's size is whether a reader can
                    // see them, which is the audit's rule about popups and is what the
                    // in-process capture cannot photograph. Posted rather than read here: a
                    // flyout shown a statement ago has not been laid out yet, and its entries
                    // report no top level at all — which reads as a menu that never opened.
                    Dispatcher.UIThread.Post(DescribeRibbonFlyouts, DispatcherPriority.Background);
                }, DispatcherPriority.Background);
                break;

            // Opens the ribbon's display-options menu, so a capture can check a popup's colours.
            case "menu":
                Opened += async (_, _) =>
                {
                    _ribbon?.OpenDisplayOptions();
                    await Task.Yield();
                    Dispatcher.UIThread.Post(DescribeRibbonFlyouts, DispatcherPriority.Background);
                };
                break;

            // The theme editor is its own window, and MAILBOX_THEME_EDIT presses its machinery.
            case "themeeditor":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new ThemeEditorWindow(App.Themes).ShowDialog(this);
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

                        // Whichever secret key the ring holds, when the open account has none.
                        // This pose runs from Opened and MAILBOX_FOLDER switches accounts a
                        // moment later, so the address in hand here is whichever account came up
                        // first — and with a seeded store that is not the one the ring has a key
                        // for. The dialog is about a key, not about an account, so the fallback
                        // is the right answer rather than a workaround: without it the pose
                        // reported "pose a seeded store" over a seeded store, and the one surface
                        // the whole doors programme exists for could not be photographed.
                        var key = keys.SigningKey(who)
                                  ?? keys.SecretRings().FirstOrDefault()?.GetSecretKey();

                        if (key is null)
                        {
                            Log.Warn($"Harness: no OpenPGP key for {who.Address}, and none in the "
                                     + "ring either — pose a seeded store.");
                            return;
                        }

                        Log.Info($"Harness: passphrase — asking about "
                                 + $"{Convert.ToHexString(key.PublicKey.GetFingerprint())[^8..]}.");

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
            // The subscribe box, posed with an address so the results are on screen.
            // MAILBOX_SUBSCRIBE=<what to type>.
            case "subscribe":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var subscribe = new SubscribeDialog(App.FeedReader.Finder, App.Feeds);

                    if (Environment.GetEnvironmentVariable("MAILBOX_SUBSCRIBE") is { Length: > 0 } typed)
                    {
                        subscribe.Opened += (_, _) => subscribe.Pose(typed);
                    }

                    await subscribe.ShowDialog(this);
                };
                break;

            // The newsletters already in the mailbox, offered as feeds.
            case "newsletters":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new NewslettersDialog(App.Feeds, FeedAccount, () => App.Accounts.All).ShowDialog(this);
                };
                break;

            // The filters dashboard, which has no other way of being reached by a capture.
            case "mutefilters":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new MuteFiltersDialog(App.Mutes, App.Feeds, DateTimeOffset.UtcNow).ShowDialog(this);
                };
                break;

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

            // Modify Button, three clicks in behind Options › Quick Access Toolbar › Modify… —
            // and the only symbol picker in the application, so it wants a pose of its own to be
            // checkable in every theme.
            case "modifybutton":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new ModifyButtonDialog("Send/Receive All Folders", "send-receive").ShowDialog(this);
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

                        // Really out of date for the expired pose, rather than in date and merely
                        // said to be: the dialog's wording chooses between "it expired on" and
                        // "it is not valid until" by reading the certificate, so a certificate
                        // that is fine photographed a sentence the real path can never produce
                        // — "not valid until" a date a month in the past.
                        var expiredPose = Environment.GetEnvironmentVariable("MAILBOX_CERT_FAULT") == "expired";
                        using var certificate = request.CreateSelfSigned(
                            DateTimeOffset.UtcNow.AddDays(expiredPose ? -400 : -30),
                            DateTimeOffset.UtcNow.AddDays(expiredPose ? -35 : 60));

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
                    if (DataContext is not ShellViewModel s) return;
                    CaptureNextWindow();

                    // MAILBOX_SEARCHFOLDER picks a criterion in the list and presses OK. Through
                    // NewSearchFolderAsync — the shell's own consumer — rather than a dialog of
                    // its own, so what is proven is that the criterion the reader chose becomes a
                    // folder in the store and a row in the pane. See Phase4BSearchFolder.
                    if (Environment.GetEnvironmentVariable("MAILBOX_SEARCHFOLDER") is { Length: > 0 } pick)
                    {
                        var (criterion, then) = pick.Split('|', 2) is [var head, var tail] ? (head, tail) : (pick, null);
                        Dispatcher.UIThread.Post(() => Phase4BSearchFolder(criterion), DispatcherPriority.Background);
                        await NewSearchFolderAsync(s, null);
                        Phase4BReportSearchFolders(s);

                        // "|<command-id>" acts on the folder that was just made and reports it
                        // again: a saved query is only a search folder if its contents follow the
                        // mail, and one report cannot show that.
                        if (then is { Length: > 0 })
                        {
                            Log.Info($"Harness: search folder — running {then} on what it holds.");
                            s.SelectedMessage = s.Messages.FirstOrDefault();
                            s.SelectedRow = s.SelectedMessage;
                            RunCommand(new CommandId(then));
                            Phase4BReportSearchFolders(s);
                            Log.Info($"Harness: search folder — the list now draws {s.Messages.Count} row(s).");

                            // And again after a refresh, which is what a reader gets by leaving
                            // the folder and coming back: the two answers apart are what says
                            // whether the query re-runs on the change or on the visit.
                            s.Refresh();
                            Log.Info($"Harness: search folder — after a refresh the list draws {s.Messages.Count} row(s).");
                        }

                        return;
                    }

                    await new NewSearchFolderDialog(s.CurrentAccountForCategories()).ShowDialog(this);
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

            // AutoCorrect and its exceptions, which are Editor Options' own children.
            // MAILBOX_AUTOCORRECT_TAB names which of the three to open on.
            case "autocorrect":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var tab = int.TryParse(
                        Environment.GetEnvironmentVariable("MAILBOX_AUTOCORRECT_TAB"), out var which)
                        ? which
                        : 0;

                    await new AutocorrectDialog(App.Settings, tab).ShowDialog(this);
                };
                break;

            case "autocorrectexceptions":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    var exceptions = Mailbox.Editor.AutocorrectExceptions.FromJson(
                        App.Settings.GetString(MailOptions.AutocorrectExceptionsKey));

                    await new AutocorrectExceptionsDialog(exceptions, () => { }).ShowDialog(this);
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
                    var alerts = new RulesAndAlertsDialog(DataContext is ShellViewModel s ? s.CurrentAddress : null);

                    // MAILBOX_RULES_PRESS reports every toolbar button's enabled state and presses
                    // the ones it names — the reference greys six of them without a rule selected,
                    // which is a claim only a read-back can hold. See Phase4BRulesDialog.
                    if (Environment.GetEnvironmentVariable("MAILBOX_RULES_PRESS") is { Length: > 0 } press)
                    {
                        alerts.Opened += (_, _) => Dispatcher.UIThread.Post(
                            () => Phase4BRulesDialog(alerts, press), DispatcherPriority.Loaded);
                    }

                    await alerts.ShowDialog(this);
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

                    // MAILBOX_WIZARD_PRESS walks the pages through their own buttons and reports
                    // the rule that comes out — the wizard's five pages are otherwise five
                    // photographs of a wizard that has never been asked to do anything.
                    if (Environment.GetEnvironmentVariable("MAILBOX_WIZARD_PRESS") is { Length: > 0 } walk)
                    {
                        wizard.Opened += (_, _) => Dispatcher.UIThread.Post(
                            () => Phase4BWizard(wizard, walk), DispatcherPriority.Loaded);
                        wizard.Closed += (_, _) => Phase4BReportRule(wizard.Result, wizard.RunNow, "the wizard");
                    }

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
                    var runRules = new RunRulesNowDialog(account);

                    // MAILBOX_RULES_RUN presses the dialog's own buttons, so what Run Now did can
                    // be read back out of the store — see Phase4BRunRules.
                    if (Environment.GetEnvironmentVariable("MAILBOX_RULES_RUN") is { Length: > 0 } press)
                    {
                        runRules.Opened += (_, _) => Dispatcher.UIThread.Post(
                            () => Phase4BRunRules(runRules, press), DispatcherPriority.Loaded);
                    }

                    await runRules.ShowDialog(this);
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
            // The message window. MAILBOX_MESSAGE_RUN presses its ribbon by id — through the
            // window's own dispatcher, not the shell's — and the store is read back by the
            // press's own log lines; the capture then shows what the presses left.
            case "message":
                Opened += (_, _) =>
                {
                    if (DataContext is not ShellViewModel shell) return;

                    CaptureNextWindow();
                    OpenMessageWindow(shell);

                    if (Environment.GetEnvironmentVariable("MAILBOX_MESSAGE_RUN") is { Length: > 0 } presses)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var window = (Application.Current?.ApplicationLifetime
                                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                                ?.Windows.OfType<MessageWindow>().FirstOrDefault();

                            if (window is null)
                            {
                                Log.Warn("Harness: no message window opened to press.");
                                return;
                            }

                            foreach (var id in presses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                Log.Info($"Harness: message window running {id}.");
                                window.Press(new CommandId(id));
                            }
                        }, DispatcherPriority.Background);
                    }
                };
                break;

            case "groups":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();

                    // What the dialog's ticks decide, before and after it is used: which accounts
                    // each group covers and which a Send/Receive All reaches. A capture of the
                    // ticks proves the ticks — see Phase4BGroups.
                    Phase4BGroups(AccountAddresses());
                    await new SendReceiveGroupsDialog(App.Groups, AccountAddresses()).ShowDialog(this);
                    Phase4BGroups(AccountAddresses());
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

                    // The dialog opens on Tasks and there is no other way to the Errors tab, which
                    // is where an account that could not be reached says why — see Phase4BProgressTab.
                    if (Environment.GetEnvironmentVariable("MAILBOX_PROGRESS_TAB") is { Length: > 0 } tab)
                    {
                        Dispatcher.UIThread.Post(
                            () => Phase4BProgressTab(window, tab), DispatcherPriority.Loaded);
                    }

                    // Refreshed through a state change, which is the sequence a real run puts it
                    // through and the one the pose never did: a row that says Processing and then
                    // says Completed is where stale text on a reused row shows up. Building the
                    // states first and showing the window once cannot produce it.
                    //
                    // Only for the mid-flight pose. A report after Finish puts a finished task
                    // back into Processing, so running these under =finished or =failed
                    // photographed a run still going and both end states were unreachable — the
                    // opposite of what those two values say they show.
                    if (Environment.GetEnvironmentVariable("MAILBOX_PROGRESS_STATE") is not ("finished" or "failed"))
                    {
                        tasks.Report(new PollProgress(first, 0, 0, "Sending"));
                        window.Refresh();
                        tasks.Report(new PollProgress(first, 0, 0, "Connecting"));
                        window.Refresh();
                        tasks.Report(new PollProgress(first, 5, 9, "Downloading"));
                        window.Refresh();
                    }
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

                // The audit's compose lane — the whole address block, pressing an arbitrary
                // compose command, attaching a real file, and the states a capture cannot show.
                // Wired here rather than lower down so its Opened handler is registered before
                // MAILBOX_COMPOSE_QUEUE's: a pose that sets a header and a pose that sends have
                // to happen in that order.
                WirePhase5ADoors(compose);

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

                // Types into the body a character at a time and reads the body back. Autocorrect
                // fires on a keystroke and on nothing else, so posing text into the document
                // would prove nothing about it: the only way to audit a correction is to type
                // the word and look at what is there afterwards.
                if (Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_TYPE") is { Length: > 0 } keys)
                {
                    compose.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        compose.PoseBodyTyping(keys.Replace("\\n", "\n"));
                        Console.WriteLine($"Typed \"{keys}\"");
                        Console.WriteLine($"  body: {compose.BodyText.Replace("\n", "\\n")}");
                        Console.WriteLine($"  html: {compose.BodyHtml.Replace("\n", " ")}");
                    }, DispatcherPriority.Background);
                }

                // Presses formatting commands on the editor — MAILBOX_COMPOSE_RUN. Registered
                // here so its post runs after anything typed above and before the send below.
                PoseComposeEditor(compose);

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
                    // 700ms is enough to lay a window out and is not enough to open a certificate
                    // store: the first S/MIME send on a fresh store builds certificates.db and
                    // imports the system roots, and the run exited part-way through with no
                    // message queued and no line in the log saying why. MAILBOX_COMPOSE_SETTLE
                    // buys the time, for the poses that need it and no others.
                    await Task.Delay(
                        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_SETTLE"), out var wait)
                        && wait is > 0 and <= 60_000 ? wait : 700);

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
            // Posted below background, because MAILBOX_MODULE switches the module — and with it
            // the whole bar — from a background post of its own. Begun synchronously here, the
            // first level was taken on the mail bar whatever module the pose asked for: every
            // module reported the mail strip's badge count, which is a traversal of a ribbon
            // that is about to be replaced. ContextIdle runs after everything already queued.
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
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
                // The letters as well as the count: a count says a level is populated and cannot
                // say with what, and "is the collapsed group's own badge there" is exactly the
                // question a missing group KeyTip answers wrongly.
                Dispatcher.UIThread.Post(
                    () => Log.Info($"KeyTips: level {_keyTips.Depth}, {_keyTips.BadgeCount} badges"
                                   + $" [{string.Join(" ", _keyTips.Badges)}]"),
                    DispatcherPriority.Background);
            }, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// Measures whichever of the ribbon's own flyouts are open. Harness only.
    /// </summary>
    /// <remarks>
    /// A menu that reports itself open proves nothing — a 2×2 presenter is an empty one — and a
    /// popup is not in the application's window list, so the in-process capture photographs the
    /// shell behind it and reads as a success. <see cref="FlyoutProbe"/> reads the presenter's
    /// real size from inside the process instead.
    /// </remarks>
    private void DescribeRibbonFlyouts()
    {
        if (_ribbon is null) return;

        foreach (var (what, flyout) in _ribbon.Flyouts())
        {
            if (!flyout.IsOpen) continue;
            Log.Info($"Harness: {FlyoutProbe.Describe(what, flyout)}");
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
    /// <param name="page">A rail page to open on, or null for the Backstage's own first one.</param>
    private void ShowBackstage(string? page = null)
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
        if (DataContext is ShellViewModel shell) shell.BackstageOpen = true;

        if (page is { Length: > 0 }) backstage.Open(page);
    }

    private void CloseBackstage()
    {
        var host = this.FindControl<ContentControl>("BackstageHost")!;
        host.IsVisible = false;
        host.Content = null;
        if (DataContext is ShellViewModel shell) shell.BackstageOpen = false;
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

        // The design's Undo Send. The window closes the moment it queues, so the offer to take it back
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

            // The desktop entry's launcher actions. New Contact already ran its command; New
            // Appointment does now too — the Calendar module and the command both exist, so this
            // is the one line the contact case has always had.
            if (string.Equals(arg, "--new-appointment", StringComparison.Ordinal))
            {
                RunCommand(CalendarCommands.NewAppointment.Id);
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

            // The other file types the desktop entry claims — an invitation, a contact card, a
            // saved message. The desktop hands these over on the command line exactly as it hands
            // over an .eml, and a MimeType claim the application does nothing with is worse than
            // no claim: the file manager offers Mailbox and then Mailbox shrugs.
            if (LooksLikeHandedFile(arg, out var handed))
            {
                OpenHandedFile(handed);
                return;
            }
        }
    }

    /// <summary>An <c>.eml</c> file, or a path the desktop handed us as one. The MIME-file side of the desktop integration.</summary>
    private static bool LooksLikeMailFile(string arg)
    {
        var path = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(arg, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : arg;

        return File.Exists(path)
            && Path.GetExtension(path).ToLowerInvariant() is ".eml" or ".mbox";
    }

    /// <summary>
    /// A calendar, contact or message file the desktop entry's <c>MimeType</c> claims — the ones
    /// that go through an importer rather than the message pane.
    /// </summary>
    private static bool LooksLikeHandedFile(string arg, out string path)
    {
        path = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(arg, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : arg;

        return File.Exists(path)
            && Path.GetExtension(path).ToLowerInvariant() is ".ics" or ".ical" or ".ifb" or ".vcf" or ".vcard" or ".msg";
    }

    /// <summary>
    /// Opens a handed-over file by what it is: an invitation as a calendar, a card or a saved
    /// message through the same importer File · Import uses, so nothing is a second code path.
    /// </summary>
    private void OpenHandedFile(string path)
    {
        if (DataContext is not ShellViewModel shell) return;

        var extension = Path.GetExtension(path).ToLowerInvariant();

        // An invitation opens as a calendar — the named-collection import the reader gets from
        // File · Open Calendar, which is the right shape for a single .ics.
        if (extension is ".ics" or ".ical" or ".ifb")
        {
            _ = OpenCalendarPathAsync(shell, path);
            return;
        }

        // A card or a saved message: the shared importer routes by extension, mail to the open
        // account's Inbox and everything else to its kind's collection.
        var summary = ImportFiles.Run([path], App.Accounts.Default, App.Pim, App.PimSync.QueuePut);
        shell.StatusRight = summary.Count > 0 ? summary[0] : $"{Path.GetFileName(path)} imported.";
        AfterStoreChange(shell);
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

    /// <summary>
    /// The Options page's "Play a sound": mail arriving is announced once, however much of it
    /// arrived.
    /// </summary>
    /// <remarks>
    /// Once per run and not once per message — a poll that brings twenty would otherwise make
    /// twenty overlapping noises. It is deliberately not tied to the Desktop Alert switch: the
    /// reference draws them as two rows because they are two answers to the same question, and
    /// somebody who wants to be told without being shown is exactly who turns one off.
    /// <para>
    /// A reminder is not routed here. It has a chime of its own (<c>reminders.sound</c>) and its
    /// own switch, and a flag coming due is not mail arriving.
    /// </para>
    /// </remarks>
    private static void AnnounceArrival(int arrived)
    {
        if (arrived <= 0 || !App.MailOptions.PlayArrivalSound) return;
        Notifications.Sounds.PlayArrival(App.MailOptions.ArrivalSoundFile);
    }

    /// <summary>Names the sound one of the two occasions would make, then makes it.</summary>
    private static void PoseSound(string pose)
    {
        var (occasion, path) = pose.Split(':', 2) is [var head, var tail] ? (head, tail) : (pose, null);
        var reminder = occasion.StartsWith("reminder", StringComparison.OrdinalIgnoreCase);

        if (path is { Length: > 0 })
        {
            if (reminder) App.MailOptions.ReminderSoundFile = path;
            else App.MailOptions.ArrivalSoundFile = path;
        }

        var chosen = reminder ? App.MailOptions.ReminderSoundFile : App.MailOptions.ArrivalSoundFile;
        var name = Notifications.Sounds.NameFor(chosen, reminder ? "reminder.ogg" : "new-mail.ogg");
        var on = reminder ? App.MailOptions.PlayReminderSound : App.MailOptions.PlayArrivalSound;

        Log.Info($"Harness: {(reminder ? "reminder" : "arrival")} sound — "
                 + $"chosen “{(chosen.Length == 0 ? "(none)" : chosen)}”, page says “{name}”, "
                 + $"switch {(on ? "on" : "off")}.");

        if (!on) return;
        if (reminder) Notifications.Sounds.PlayReminder(chosen);
        else AnnounceArrival(1);
    }

    /// <summary>What a new-mail toast says about one message, read back from its account's store.</summary>
    private static ArrivedMessage? DescribeArrival(string address, long id)
    {
        if (App.Accounts.Find(address)?.Mail.GetMessage(id) is not { } summary) return null;
        return new ArrivedMessage(summary.DisplayFrom, summary.Subject, summary.Preview);
    }

    /// <summary>
    /// The desktop notification for a toast: a click opens the message; a toast about one message
    /// also carries Reply, Delete and Mark Read. Answers arrive on a background thread and
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

            // The buttons and the transient flag as well as the words: neither is in a capture,
            // and "stays in the server's history" is the claim the flag makes.
            var notification = ToastFor(toast);
            Phase4BReportToast(notification);
            _notifier.Notify(notification);

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

        var list = List;
        Dispatcher.UIThread.Post(() =>
        {
            shell.SelectedRow = row;
            shell.SelectedMessage = row;
            list?.ScrollIntoView(row);
        }, DispatcherPriority.Background);

        return true;
    }

    /// <summary>Whether <c>MAILBOX_RUN</c> has already fired in this process.</summary>
    private static bool _harnessRan;

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
        _reading.InvitationRemoved += (_, _) =>
        {
            shell.StatusRight = "The meeting was removed from the calendar.";
            AfterStoreChange(shell);
        };

        // The header bar's own button: mark this one and fetch it now, rather than making the
        // reader find Mark to Download and then Process Marked Headers for a single message.
        _reading.DownloadRequested += (_, _) => _ = DownloadSelectedHeaderAsync(shell);

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
                    RefreshCommandEnablement();
                    break;

                // Another module has a selection of its own, and a different set of commands
                // asking about it.
                case nameof(ShellViewModel.Module):
                    RefreshCommandEnablement();
                    break;

                case nameof(ShellViewModel.ReadingFontSize):
                    _reading.MessageFontSize = shell.ReadingFontSize;
                    break;
            }
        };

        ShowSelectedMessage(shell);
        PrewarmMessageWindow(shell);
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

        // A header with nothing under it: the pane says so and offers to fetch it, rather than
        // drawing an empty message the reader would take for a broken one.
        _reading.HeaderOnly = shell.SelectedMessage?.IsHeaderOnly == true;

        // The pane first, then the strip from what the pane is showing: an encrypted message's
        // attachments are inside it, and the envelope has none worth offering.
        _reading.Show(message, shell.SelectedMessage?.Body ?? string.Empty, Verified(shell),
            suspectedJunk: shell.CurrentFolderRole == FolderRole.Junk);
        _attachments.Show(_reading.Carried);
        LogAttachmentStrip();
        _ = _reading.ApplySenderPolicyAsync();
    }

    /// <summary>
    /// What was recorded about the selected message's signature when it arrived.
    /// </summary>
    /// <remarks>
    /// Read, never checked. Verifying resolves a name the sender chose, and the render-path rule does not allow
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

        // A dismissed picker says nothing, as the Save As exports beside it do. Only a write
        // that was attempted and failed is worth the reader's attention.
        shell.StatusRight = await _reading.PrintToPdfAsync() switch
        {
            PdfSaveResult.Saved => "Saved as PDF.",
            PdfSaveResult.Failed => "This message could not be written to PDF.",
            _ => shell.StatusRight,
        };
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

        // A sentence, as every ordinary folder verb writes one.
        shell.StatusRight = $"Search folder “{made.Name}” created.";
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
    private void FillFolderMenu(MenuFlyout flyout, ShellViewModel shell, OpenAccount account, Folder folder, bool asFavourite = false)
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

        // Sort Subfolders A to Z orders this folder's children; Move Up and Move Down move this
        // folder among its own siblings. All three are local and nothing is sent: the ordinal is
        // how the pane draws an account's tree, and no mail server has an opinion about the order
        // its folders are shown in.
        var tree = account.Mail.Folders(account.Account.Id);
        var children = tree.Where(f => f.ParentId == folder.Id).ToList();
        var siblings = tree.Where(f => f.ParentId == folder.ParentId).ToList();
        var at = siblings.FindIndex(f => f.Id == folder.Id);

        flyout.Items.Add(new Separator());

        if (asFavourite)
        {
            // The menu was opened over the row under Favourites, so the moves act on the
            // favourites list — pressing Move Up here used to leave Favourites unchanged and
            // rewrite the account's tree below it, reporting the move as done.
            var address = account.Account.Address;
            var path = ShellViewModel.FolderPath(tree, folder);
            var place = App.Favourites.IndexOf(address, path);

            Entry("Move Up", () =>
            {
                if (App.Favourites.Move(address, path, -1)) shell.Refresh();
                shell.StatusRight = $"“{folder.Name}” moved up in Favourites.";
                return Task.CompletedTask;
            }, place > 0);

            Entry("Move Down", () =>
            {
                if (App.Favourites.Move(address, path, 1)) shell.Refresh();
                shell.StatusRight = $"“{folder.Name}” moved down in Favourites.";
                return Task.CompletedTask;
            }, place >= 0 && place < App.Favourites.All.Count - 1);
        }
        else
        {
            Entry("Sort Subfolders A to Z", () =>
            {
                account.Mail.OrderFolders([.. children.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => f.Id)]);
                shell.Refresh();
                shell.StatusRight = children.Count == 1
                    ? $"The folder under “{folder.Name}” sorted."
                    : $"The {children.Count} folders under “{folder.Name}” sorted.";
                return Task.CompletedTask;
            }, children.Count > 0);
            Entry("Move Up", () => ReorderSibling(shell, account, siblings, at, -1), at > 0);
            Entry("Move Down", () => ReorderSibling(shell, account, siblings, at, 1), at >= 0 && at < siblings.Count - 1);
        }

        flyout.Items.Add(new Separator());
        Entry("Properties…", () => FolderPropertiesAsync(shell, account, folder));
    }

    /// <summary>
    /// Move Up and Move Down: lifts the folder out of its run of siblings, puts it back one place
    /// along, and writes the whole run's ordinals.
    /// </summary>
    /// <remarks>
    /// Not a swap of two ordinals. Folders that have never been reordered all sit at 0 and are
    /// drawn by id after it, so swapping would move nothing the first time it was asked for;
    /// writing the run gives every sibling a place and the next press has something to trade.
    /// </remarks>
    private static Task ReorderSibling(ShellViewModel shell, OpenAccount account, List<Folder> siblings, int at, int by)
    {
        var to = at + by;
        if (at < 0 || to < 0 || to >= siblings.Count) return Task.CompletedTask;

        var moved = siblings[at];
        siblings.RemoveAt(at);
        siblings.Insert(to, moved);
        account.Mail.OrderFolders([.. siblings.Select(f => f.Id)]);

        shell.Refresh();
        shell.StatusRight = $"“{moved.Name}” moved {(by < 0 ? "up" : "down")}.";
        return Task.CompletedTask;
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
            await Confirm.SayAsync(this, "Create New Folder", $"The folder could not be created — {FolderTrouble(ex)}");
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
            await Confirm.SayAsync(this, "Rename Folder", $"The folder could not be renamed — {FolderTrouble(ex)}");
        }
    }

    /// <summary>The picker every folder dialog is, with New… making folders the way New Folder does — on the server first for IMAP.</summary>
    private FolderPickerDialog FolderPicker(string title, string? prompt, (OpenAccount, long?)? preselect, bool allowRoot, (OpenAccount, long)? exclude = null, OpenAccount? only = null)
    {
        // A picker offers the destinations it will honour: Move and Copy act within one account,
        // so they pass `only` and the other accounts' trees are simply not on the list.
        IReadOnlyList<OpenAccount> accounts = only is { } one ? [one] : App.Accounts.All;
        var dialog = new FolderPickerDialog(title, prompt, accounts, preselect, allowRoot, exclude)
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

    /// <summary>
    /// A folder failure as a person reads one. The store's own words — "SQLite Error 19: UNIQUE
    /// constraint failed: folders.account_id…" — stay in the log, where they are for.
    /// </summary>
    private static string FolderTrouble(Exception ex)
        => ex is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
            ? "a folder with that name is already there."
            : "the change was refused; the log has the detail.";

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
            (account, folder.ParentId), allowRoot: true, exclude: (account, folder.Id), only: account);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;

        // The picker preselects the folder's own parent, so OK with nothing changed is a
        // no-move: saying "moved" about it reported work that never happened.
        if (chosen.Folder?.Id == folder.ParentId || (chosen.Folder is null && folder.ParentId is null))
        {
            shell.StatusRight = $"“{folder.Name}” is already there.";
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
            await Confirm.SayAsync(this, "Move Folder", $"The folder could not be moved — {FolderTrouble(ex)}");
        }
    }

    /// <summary>Copy Folder…: a new folder of the same name and contents, subfolders included, under the chosen one.</summary>
    private async Task CopyFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var dialog = FolderPicker("Copy Folder", $"Copy the selected folder to the folder:",
            (account, folder.ParentId), allowRoot: true, exclude: (account, folder.Id), only: account);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;

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
            await Confirm.SayAsync(this, "Copy Folder", $"The folder could not be copied — {FolderTrouble(ex)}");
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
            await Confirm.SayAsync(this, "Delete Folder", $"The folder could not be deleted — {FolderTrouble(ex)}");
        }
    }

    private async Task EmptyFolderAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        var total = account.Mail.Messages(folder.Id, int.MaxValue).Count;
        if (total == 0) { shell.StatusRight = $"{folder.Name} is already empty."; return; }
        var go = await Confirm.AskBeforePermanentDeleteAsync(this, "Empty Folder",
            $"Permanently delete {total:N0} item{(total == 1 ? "" : "s")} from {folder.Name}?");
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

        ViewModels.FolderNode? pressed = null;

        folders.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(folders).Properties.IsRightButtonPressed) return;
            pressed = (e.Source as Control)?.DataContext as ViewModels.FolderNode;
        }, RoutingStrategies.Tunnel);

        // Built before it is shown, for the reason in RowMenu: a flyout filled from its own
        // Opening event has already been measured empty by the time the entries arrive, and the
        // popup keeps that size.
        void FillFolderContextMenu(MenuFlyout flyout)
        {
            flyout.Items.Clear();

            // An ordinary folder: the reference's menu over it. The node travels too, because
            // the same folder is drawn twice — in its account's tree and under Favourites —
            // and Move Up over a favourite must move the favourite, not rewrite the tree.
            if (pressed is not null && shell.FolderOf(pressed) is { } where)
            {
                FillFolderMenu(flyout, shell, where.Account, where.Folder,
                    pressed.Kind == ViewModels.FolderNodeKind.Favourite);
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
                shell.StatusRight = $"Search folder “{edited.Name}” updated.";
            };
            flyout.Items.Add(customize);

            var rename = new MenuItem { Header = "Rename Folder" };
            rename.Click += async (_, _) =>
            {
                var name = await Prompt.AskAsync(this, "Rename Folder", "New name:", search.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                account.Mail.UpdateSearchFolder(search.Id, name.Trim(), search.Query);
                shell.SelectSearchFolder(search.Id);
                shell.StatusRight = $"Search folder renamed to “{name.Trim()}”.";
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
                shell.StatusRight = $"Search folder “{search.Name}” deleted.";
            };
            flyout.Items.Add(delete);
        }

        folders.AddHandler(ContextRequestedEvent, (object? _, ContextRequestedEventArgs e) =>
        {
            if (e.Handled) return;

            var menu = _folderMenu = new MenuFlyout();
            FillFolderContextMenu(menu);
            MenuProbe.Show("the folder menu", menu, folders, atPointer: true);
            e.Handled = true;
        });
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

            // The reopened draft answers the same doors a new message does, which is the only way
            // to ask whether it came back the way it went in: the round trip is a claim about two
            // windows in two runs, and the second one is this.
            WirePhase5ADoors(compose);
            compose.Show(this);
            return;
        }

        var context = shell.SelectedMessage is { } row
            ? new OpenedMessageContext(row.Address, row.Id, row.FolderId)
            : null;

        // The warm window first: its engine is already alive, so the body is on screen for the
        // cost of a navigation rather than a process. Wiring and the close hold-back are still
        // attached from its first life; Replace is the same journey stepping makes.
        if (_warmMessageWindow is { } warm)
        {
            _warmMessageWindow = null;
            warm.ResetForReuse();
            warm.Replace(message, _openRaw, Verified(shell), context);
            warm.ShowWarmed(this);
            Log.Info("The warm message window takes the message.");
            return;
        }

        var window = new MessageWindow(
            App.Themes, () => shell.CurrentMail, message, _openRaw, Verified(shell), context);

        WireMessageWindow(shell, window);
        HoldWarmOnClose(window);
        window.Show(this);
    }

    /// <summary>The one message window kept alive and hidden between readings, engine and all.</summary>
    private MessageWindow? _warmMessageWindow;

    /// <summary>
    /// Turns this window's close into a hide, so the next open pays a navigation instead of an
    /// engine spawn.
    /// </summary>
    /// <remarks>
    /// Only a close the reader asked for is held back — a window dying because the application
    /// or its owner is shutting down must actually die, or the hold-back would block the
    /// shutdown it is cancelling. One window is kept: the second simultaneous window closes for
    /// real, since a pool of engines would cost a render process of memory apiece.
    /// </remarks>
    private void HoldWarmOnClose(MessageWindow window)
    {
        window.Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing || _warmMessageWindow is not null) return;

            e.Cancel = true;
            window.Hide();
            _warmMessageWindow = window;
            Log.Info("The message window is kept warm for the next open.");
        };

        // A pooled window can still die for real — the application's shutdown takes it — and
        // the pool must not go on pointing at the corpse: the next open would re-show a closed
        // window, which the framework refuses.
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_warmMessageWindow, window)) _warmMessageWindow = null;
        };
    }

    /// <summary>
    /// Builds the warm window before the first double-click asks for it, so even the first
    /// message opens onto a living engine.
    /// </summary>
    /// <remarks>
    /// The engine's life is gated on being shown, so the warm-up shows the window fully
    /// transparent and unactivated, gives the engine a moment to come up, and hides it into
    /// the pool. Idle priority and a posed-run gate keep it out of startup's way and out of
    /// captures — except when a pose asks for it by name, which is how it is verified.
    /// </remarks>
    private void PrewarmMessageWindow(ShellViewModel shell)
    {
        var wanted = (Environment.GetEnvironmentVariable("MAILBOX_STATE") ?? string.Empty)
            .Contains("warm-window", StringComparison.OrdinalIgnoreCase);
        if (Mailbox.App.Theming.WindowCapture.IsRequested && !wanted) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_warmMessageWindow is not null) return;

            var window = new MessageWindow(
                App.Themes, () => shell.CurrentMail, new MimeKit.MimeMessage(), raw: null);
            WireMessageWindow(shell, window);
            HoldWarmOnClose(window);
            window.ShowInvisible(this);

            DispatcherTimer.RunOnce(() =>
            {
                if (_warmMessageWindow is not null || !window.IsVisible)
                {
                    // A reader beat the warm-up to it, or something closed the window: let this
                    // one go rather than pool a window in an unknown state.
                    if (window.IsVisible) window.Close();
                    return;
                }

                window.Hide();
                _warmMessageWindow = window;
                Log.Info("A message window is warmed and waiting.");
            }, TimeSpan.FromSeconds(3));
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// What the message window cannot do alone: drafts, stepping and Quick Steps all live in
    /// the shell, so the window asks and the shell answers.
    /// </summary>
    private void WireMessageWindow(ShellViewModel shell, MessageWindow window)
    {
        // The owner's own flow, tested against the reference: Reply pressed in an open message
        // window takes you back to the app — the window closes and the reply grows inline, the
        // ribbon changing with it. The reply is addressed from the window's message, protected
        // fields and all, before the window goes; Pop Out is then the way back to a window.
        window.RespondRequested += (_, kind) =>
        {
            Respond(shell, kind, window.Current, covered: window.Covered);
            window.Close();
            Activate();
        };

        window.Changed += (_, _) => shell.Refresh();

        // An invitation answered in this window, which is the only place the bar appears when the
        // reading pane is off. The reply goes out on the shell's path — the same one the pane's
        // own bar uses — and the window says what happened, since the shell's status bar is
        // behind it.
        window.InvitationAnswered += (_, answer) =>
        {
            SendInvitationReply(shell, answer);
            window.Say(shell.StatusRight);
        };

        window.InvitationRemoved += (_, _) =>
        {
            AfterStoreChange(shell);
            window.Say("The meeting was removed from the calendar.");
        };

        // The QAT's arrows: step the shell's selection, then hand the window what the pane
        // loaded for it — the pane's own load path, so the window shows exactly what the shell
        // would. Posted at Background because the list re-asserts its selection as it lays out.
        window.StepRequested += (_, delta) =>
        {
            StepSelection(shell, delta);
            Dispatcher.UIThread.Post(() =>
            {
                if (_openMessage is not { } stepped) return;

                window.Replace(stepped, _openRaw, Verified(shell),
                    shell.SelectedMessage is { } row
                        ? new OpenedMessageContext(row.Address, row.Id, row.FolderId)
                        : null);
            }, DispatcherPriority.Background);
        };

        // A Quick Step is a sequence only the shell can run. It acts on the window's row when
        // the shell still shows it; a row that has moved out from under the list says so.
        window.QuickStepRequested += (_, id) =>
        {
            if (App.QuickSteps.FindByCommand(id) is not { } step) return;

            if (window.Context is { } context
                && shell.SelectedMessage is { } row
                && row.Id == context.MessageId
                && string.Equals(row.Address, context.Address, StringComparison.OrdinalIgnoreCase))
            {
                _ = RunQuickStepAsync(shell, step, [row]);
                return;
            }

            window.Say("Select this message in the main window to run a Quick Step on it.");
        };
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

        // The Advanced page's Export button closes Options and opens the Backstage page that
        // holds the exporters, which is the reference's own Import and Export door.
        if (dialog.ExportRequested)
        {
            ShowBackstage("openexport");
            return;
        }

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
    /// Gives each rail module a command that switches the window over.
    /// </summary>
    /// <remarks>
    /// The Calendar button once toggled the peek, because there was no module to switch
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
    /// The status bar's zoom percentage, which opens the Zoom dialog as the reference's does.
    /// </summary>
    /// <remarks>
    /// <see cref="ZoomDialog"/> already existed and was reachable only from the message window;
    /// the shell drew its own figure as a label, so the one place a reader would press to choose
    /// a zoom level did nothing. The slider and the ± buttons beside it were the only way.
    /// </remarks>
    private void ShowZoomDialog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) ShowZoomDialog(shell);
    }

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

                // The reference's default: beside the list. A pose, because the scratch settings
                // carry whatever the machine last chose and a capture must be able to say.
                case "reading-right": shell.ReadingPaneAtBottom = false; shell.ReadingPaneVisible = true; break;

                // The pane turned on mid-session, after the journey ordinary reading makes and
                // no startup pose can: the selection cleared (a folder switch does it, and it
                // hands the pane to the text fallback), a message selected through the real
                // dispatcher, and only then the pane shown — which must pick up the message it
                // was hidden and fallback-held for.
                case "reading-late":
                    Dispatcher.UIThread.Post(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(1000);
                        shell.SelectedMessage = null;
                        await System.Threading.Tasks.Task.Delay(400);
                        shell.SelectedMessage = shell.Messages.FirstOrDefault();
                        await System.Threading.Tasks.Task.Delay(400);
                        shell.ReadingPaneAtBottom = false;
                        shell.ReadingPaneVisible = true;
                        Log.Info("Harness: the reading pane was turned on late.");
                    }, DispatcherPriority.Background);
                    break;
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

        MenuProbe.Show("the change-view menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>
    /// The View tab's commands over the modules whose lists are not the message list. Honest
    /// throughout: what acts, acts on the module; what does not exist yet says so instead of
    /// opening mail's own surface.
    /// </summary>
    private bool RunModuleViewCommand(ShellViewModel shell, CommandId id)
    {
        if (shell.Module is not (MailboxModule.Notes or MailboxModule.Tasks or MailboxModule.Journal))
        {
            return false;
        }

        if (id == ViewCommands.ReverseSort.Id)
        {
            switch (shell.Module)
            {
                case MailboxModule.Notes:
                    var notes = EnsureNotes(shell);
                    notes.Reversed = !notes.Reversed;
                    shell.StatusRight = notes.Reversed ? "Oldest first." : "Newest first.";
                    return true;

                case MailboxModule.Tasks:
                    var tasks = EnsureTasks(shell);
                    tasks.Reversed = !tasks.Reversed;
                    shell.StatusRight = tasks.Reversed ? "Latest due first." : "Earliest due first.";
                    return true;

                default:
                    shell.StatusRight = "The timeline runs by date; turn it around from its own views.";
                    return true;
            }
        }

        if (id == ViewCommands.ChangeView.Id)
        {
            var flyout = new MenuFlyout();

            void Entry(string header, bool current, Action choose)
            {
                var item = new MenuItem { Header = header, Icon = current ? Tick() : null };
                item.Click += (_, _) => choose();
                flyout.Items.Add(item);
            }

            switch (shell.Module)
            {
                case MailboxModule.Notes:
                    var notes = EnsureNotes(shell);
                    Entry("Icon", notes.Arrangement == Mailbox.Scheduling.NoteArrangement.Icons, () => RunCommand(NoteCommands.IconsView.Id));
                    Entry("Notes List", notes.Arrangement == Mailbox.Scheduling.NoteArrangement.List, () => RunCommand(NoteCommands.NotesListView.Id));
                    Entry("Last 7 Days", notes.Arrangement == Mailbox.Scheduling.NoteArrangement.LastSevenDays, () => RunCommand(NoteCommands.LastSevenDaysView.Id));
                    break;

                case MailboxModule.Tasks:
                    var tasks = EnsureTasks(shell);
                    Entry("To-Do List", tasks.Kind == TaskViewKind.Todo, () => RunCommand(TaskCommands.TodoListView.Id));
                    Entry("Simple List", tasks.Kind == TaskViewKind.Simple, () => RunCommand(TaskCommands.SimpleListView.Id));
                    Entry("Detailed List", tasks.Kind == TaskViewKind.Detailed, () => RunCommand(TaskCommands.DetailedView.Id));
                    break;

                default:
                    shell.StatusRight = "The Journal's views are on its own ribbon tab.";
                    return true;
            }

            MenuProbe.Show("the change-view menu", flyout, _ribbon ?? (Control)this, atPointer: true);
            return true;
        }

        if (id == ViewCommands.ViewSettings.Id || id == ViewCommands.OpenViewSettings.Id
            || id == ViewCommands.ArrangeBy.Id)
        {
            shell.StatusRight = "This list has no view editor yet; the message list's would not fit it.";
            return true;
        }

        return false;
    }

    /// <summary>Current View: View Settings… and Reset View.</summary>
    private void ShowCurrentViewMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();
        var settings = new MenuItem { Header = ViewCommands.OpenViewSettings.Label + "…" };
        settings.Click += (_, _) => _ = ShowViewSettingsAsync(shell);
        var reset = new MenuItem { Header = ViewCommands.ResetView.Label };
        reset.Click += (_, _) => shell.ResetView();
        flyout.Items.Add(settings);
        flyout.Items.Add(reset);
        MenuProbe.Show("the current-view menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>Layout: the folder pane, the reading pane and the To-Do Bar, as the reference's menu has them.</summary>
    /// <remarks>
    /// The three are filled by the methods below rather than here, because the View tab gives
    /// each of them a button of its own and the two surfaces must offer the same entries.
    /// </remarks>
    private void ShowLayoutMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        MenuItem Sub(string header) { var item = new MenuItem { Header = header }; flyout.Items.Add(item); return item; }

        FillFolderPaneMenu(Sub("Folder Pane").Items, shell);
        FillReadingPaneMenu(Sub("Reading Pane").Items, shell);
        FillToDoBarMenu(Sub("To-Do Bar").Items, shell);

        MenuProbe.Show("the layout menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>One entry of a layout menu: a tick where it is the state, and what it sets.</summary>
    private static void LayoutEntry(ItemCollection into, string header, Action run, bool ticked)
    {
        var item = new MenuItem { Header = header, Icon = ticked ? new TextBlock { Text = "\u2713" } : null };
        item.Click += (_, _) => run();
        into.Add(item);
    }

    private static void FillFolderPaneMenu(ItemCollection into, ShellViewModel shell)
    {
        LayoutEntry(into, "Normal", () => shell.NavCollapsed = false, !shell.NavCollapsed);
        LayoutEntry(into, "Minimized", () => shell.NavCollapsed = true, shell.NavCollapsed);
    }

    private void FillReadingPaneMenu(ItemCollection into, ShellViewModel shell)
    {
        LayoutEntry(into, "Right", () => { shell.ReadingPaneAtBottom = false; shell.ReadingPaneVisible = true; }, shell.ReadingPaneVisible && !shell.ReadingPaneAtBottom);
        LayoutEntry(into, "Bottom", () => { shell.ReadingPaneAtBottom = true; shell.ReadingPaneVisible = true; }, shell.ReadingPaneVisible && shell.ReadingPaneAtBottom);
        LayoutEntry(into, "Off", () => shell.ReadingPaneVisible = false, !shell.ReadingPaneVisible);
        into.Add(new Separator());

        var options = new MenuItem { Header = "Options…" };
        options.Click += async (_, _) => await new ReadingPaneOptionsDialog(App.MailOptions).ShowDialog(this);
        into.Add(options);
    }

    /// <remarks>
    /// To-Do Bar · Calendar is the docked pane, not the popup: the menu's own tick reads "is the
    /// calendar docked", and it did not use to be what the entry set. Each entry is a section of
    /// the bar and switches only itself, as the reference's own three do.
    /// </remarks>
    private void FillToDoBarMenu(ItemCollection into, ShellViewModel shell)
    {
        LayoutEntry(into, "Calendar", () => { if (shell.IsCalendarDocked) UndockPeek(); else DockPeek(); }, shell.IsCalendarDocked);
        LayoutEntry(into, "Tasks", () => ShowToDoTasks(shell, !shell.AreTasksDocked), shell.AreTasksDocked);
        LayoutEntry(into, "People", () => ShowToDoPeople(shell, !shell.ArePeopleDocked), shell.ArePeopleDocked);
        LayoutEntry(into, "Off", () =>
        {
            shell.AreTasksDocked = false;
            shell.ArePeopleDocked = false;
            if (shell.IsCalendarDocked) UndockPeek();
            else RebuildToDoBar(shell);
        }, !shell.IsToDoBarVisible);
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
            await Confirm.SayAsync(this, "Save Current View", "That name is one of the views that ship; choose another.");
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

        // MAILBOX_QAT=flyout says what the customize menu holds and which entries are ticked;
        // flyout:<entry> presses one and says what the toolbar holds afterwards. Adding and
        // removing a button is what that menu is for, and neither the menu nor the result of
        // pressing it can be photographed.
        if (Environment.GetEnvironmentVariable("MAILBOX_QAT")?.Trim() is { Length: > 0 } pose
            && pose.StartsWith("flyout", StringComparison.OrdinalIgnoreCase))
        {
            var press = pose.Split(':', 2) is [_, var wanted] ? wanted.Trim() : null;
            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () => await PoseQuickAccessFlyoutAsync(customization, press),
                DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Harness only: reads the Quick Access Toolbar's customize menu, and presses one of its
    /// entries.
    /// </summary>
    /// <remarks>
    /// The real menu the chevron opens, filled the way opening it fills it — not a second list
    /// written for the harness. What the toolbar holds is logged before and after, because the
    /// question a tick answers is whether the command reached the bar, and the bar is rebuilt
    /// from the layout rather than from the menu.
    /// </remarks>
    private async Task PoseQuickAccessFlyoutAsync(QuickAccessLayout customization, string? press)
    {
        // The capture waits, as it does for every other popup pose: a popup takes real time to
        // reach the platform, and a probe that reads it in the same dispatcher turn measures a
        // menu that has not been presented yet.
        using var hold = WindowCapture.Hold();

        var chevron = this.FindControl<Button>(
            customization.Placement == QuickAccessPlacement.BelowRibbon
                ? "QuickAccessCustomizeBelow"
                : "QuickAccessCustomize");

        if (chevron?.Flyout is not MenuFlyout flyout)
        {
            Log.Warn("Harness: the toolbar's chevron carries no menu.");
            return;
        }

        // The button a reader actually clicks, before anything is asked of its menu: a chevron
        // with no size is a menu nobody can open, and that would read from a capture as a
        // toolbar that simply has no editor.
        Log.Info($"Harness: the toolbar's chevron is {chevron.Bounds.Width:0}x{chevron.Bounds.Height:0}, "
            + $"visible={chevron.IsEffectivelyVisible}, enabled={chevron.IsEffectivelyEnabled}.");

        // Really opened, so the presenter has a size: a popup's size is the claim, and IsOpen
        // alone proves nothing. Filled directly if the
        // presenter refuses to come up — an offscreen window still has to answer what the menu
        // holds, and a run that measured nothing would read as a toolbar with no editor.
        // Through the button's own Click first, which is what a reader does and what opens an
        // attached flyout; ShowAt after it, as the window-menu pose does, so a menu that the
        // click did not open is still asked to present itself.
        chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!flyout.IsOpen) flyout.ShowAt(chevron);
        if (!flyout.IsOpen) QuickAccessFlyout.Fill(flyout);

        Log.Info($"Harness: the toolbar holds {customization.Commands.Count}: "
            + string.Join(" | ", customization.Commands.Select(c => c.Value)));

        // Measured twice on purpose. This menu fills itself from its own Opening event, and
        // such a menu measures empty when it is read in the same
        // pass that opened it — the entries exist but are not yet in the popup's visual tree, so
        // there is nothing to take a size from. The second reading, a dispatcher turn later, is
        // the one that says whether a presenter really came up.
        await Task.Delay(400);

        // The size is the claim. This menu reported open with no presenter at all while a plain
        // menu shown at the same chevron in the same run presented 103x26 — which is how an
        // empty popup was told apart from a harness that cannot present one. It was empty
        // because its entries only ever arrived on Opening, after the popup had been built.
        Log.Info("Harness: " + FlyoutProbe.Describe("the toolbar's customize menu", flyout));

        foreach (var item in flyout.Items.OfType<MenuItem>())
        {
            var header = item.Header as string
                ?? (item.Header as TextBlock)?.Text
                ?? "(heading)";
            Log.Info($"Harness: QAT menu — “{header}”"
                + (item.Icon is not null ? "  [ticked]" : string.Empty)
                + (item.IsEnabled ? string.Empty : "  [greyed]"));
        }

        if (press is not { Length: > 0 }) return;

        // A command id names its own entry, so a pose list generated from QuickAccessCandidates
        // needs no second copy of every label. Anything else is matched as typed.
        var wanted = press.Contains('.', StringComparison.Ordinal)
                     && App.Commands.TryGet(new CommandId(press), out var named)
            ? named.Label
            : press;

        var entry = flyout.Items.OfType<MenuItem>().FirstOrDefault(
            i => (i.Header as string ?? string.Empty)
                .StartsWith(wanted, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Log.Warn($"Harness: the toolbar's menu has no entry “{wanted}”.");
            return;
        }

        Log.Info($"Harness: pressing “{entry.Header}”.");
        entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        flyout.Hide();

        Log.Info($"Harness: the toolbar now holds {customization.Commands.Count}: "
            + string.Join(" | ", customization.Commands.Select(c => c.Value)));
        Log.Info($"Harness: the toolbar is {(customization.IsVisible ? "shown" : "hidden")}, "
            + $"{(customization.Placement == QuickAccessPlacement.BelowRibbon ? "below" : "above")} the ribbon; "
            + $"the bar draws {shellButtons(this)} buttons.");

        // More Commands… opens the Options page and changes nothing about the toolbar, so the
        // counts above cannot tell it from an entry that did nothing at all.
        ReportBackstageAction($"the toolbar's “{entry.Header}”");

        static int shellButtons(MainWindow window)
            => window.DataContext is ShellViewModel s ? s.QuickAccess.Count : -1;
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
            onViewAccount: () => ShowBackstage(),
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
            // the hover that opens the peek is exercised — "tab:<tab>" the ribbon's tab strip,
            // "folder:<name>" a row of the folder pane, "dialog:<text>" a control inside
            // whatever window MAILBOX_CAPTURE_DIALOG is about to photograph, and anything else
            // is a caption button.
            //
            // The last three were added by the audit's door inventory, which is what a no-door
            // list is for: the tab strip and the folder pane each had a hover token every theme
            // defined and nothing read, and neither state could be photographed to settle it.
            if (hovered.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = hovered["tab:".Length..].Trim();
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        _ribbon.UpdateLayout();
                        Log.Info(_ribbon.ForceHoverTab(wanted)
                            ? $"Harness: hovering the {wanted} tab."
                            : $"Harness: no {wanted} tab — this bar shows {string.Join(", ", _ribbon.TabIds())}.");
                    },
                    DispatcherPriority.Loaded);
            }
            else if (hovered.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = hovered["folder:".Length..].Trim();
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (this.FindControl<ListBox>("FolderList") is not { } list)
                        {
                            Log.Info("Harness: no folder pane on this window.");
                            return;
                        }

                        list.UpdateLayout();

                        // The row's container rather than its data: the hover is a visual state
                        // on the ListBoxItem, and a folder pane scrolled past its recycling
                        // point has no container for a row nobody has looked at.
                        foreach (var item in list.GetRealizedContainers().OfType<ListBoxItem>())
                        {
                            if (item.DataContext is not { } row
                                || !row.ToString()!.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            ((IPseudoClasses)item.Classes).Add(":pointerover");
                            Log.Info($"Harness: hovering the folder row “{wanted}”.");
                            return;
                        }

                        Log.Info($"Harness: no realised folder row matches “{wanted}”.");
                    },
                    DispatcherPriority.Loaded);
            }
            else if (hovered.StartsWith("rail:", StringComparison.OrdinalIgnoreCase))
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
                        var icon = RailButton(module);
                        RailPointerEntered(icon, new PointerEventArgs(
                            PointerEnteredEvent, this, new Pointer(0, PointerType.Mouse, true),
                            null, default, 0, new PointerPointProperties(), KeyModifiers.None));

                        // And the visual state, which the handler does not set: a pointer over a
                        // button lights it as well as starting the dwell, and posing only the
                        // dwell meant the rail's own hover wash had never been photographed in
                        // any theme.
                        if (icon is not null) ((IPseudoClasses)icon.Classes).Add(":pointerover");

                        Log.Info($"Harness: hovering the rail's {module} icon"
                            + (icon is Button b ? $" — background {b.Background?.ToString() ?? "null"}." : " — no such icon."));
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
                var which = hovered.ToLowerInvariant();
                Opened += (_, _) => Dispatcher.UIThread.Post(
                    () => Log.Info(
                        caption.ForceHover(which)
                            ? $"Harness: hovering the {which} caption button — {caption.Describe(which)}."
                            : $"Harness: “{hovered}” is not a caption button — minimize, maximize or close."),
                    DispatcherPriority.Loaded);
            }
        }

        PoseCaption(caption);
        PoseLiveTheme();

        if (this.FindControl<Control>("TitleBar") is not { } bar) return;

        WindowFrame.Drags(this, bar);
    }

    /// <summary>
    /// <c>MAILBOX_CAPTION=hold:&lt;button&gt;</c> paints a caption button's held state, and
    /// <c>MAILBOX_CAPTION=press:&lt;button&gt;</c> clicks it. Both name minimize, maximize or
    /// close; a bare name is a press, and several separated by commas run in order — which is
    /// how restore is reached, <c>press:maximize,press:maximize</c> being the only way a window
    /// gets back to where it started through the button rather than through a property. A
    /// <c>wait:&lt;ms&gt;</c> step holds the next press that long, as <c>MAILBOX_RUN</c>'s
    /// <c>wait-</c> entries do — how a close reaches a shell whose idle-time machinery, the
    /// warm message window above all, has had time to exist.
    /// </summary>
    /// <remarks>
    /// Two doors the audit's inventory found missing on the same surface. Every built-in defines
    /// <c>titlebar.caption.pressed</c> and <c>titlebar.caption.close.pressed</c> and no capture
    /// could reach either, which is the shape of bug that made the close button's red wrong for
    /// two sessions; and maximize/restore could only be proven by assigning
    /// <see cref="Window.WindowState"/>, which proves nothing about the button. The press goes
    /// through the button's own <c>Click</c> event and reports the state either side of it, so
    /// the read-back is what the window did rather than what was asked for.
    /// </remarks>
    private void PoseCaption(CaptionButtons caption)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_CAPTION") is not { Length: > 0 } posed) return;

        var steps = posed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Opened += (_, _) => Dispatcher.UIThread.Post(
            async () =>
            {
                // Held across every step so the capture waits for the last one: the first press
                // of a maximize/restore pair is not finished when the second is queued, and a
                // picture taken between them is of neither state.
                using var hold = WindowCapture.Hold();

                foreach (var step in steps)
                {
                    var colon = step.IndexOf(':');
                    var verb = (colon > 0 ? step[..colon] : "press").Trim().ToLowerInvariant();
                    var which = (colon > 0 ? step[(colon + 1)..] : step).Trim().ToLowerInvariant();

                    if (verb is "wait")
                    {
                        await Task.Delay(int.TryParse(which, out var ms) ? ms : 1000);
                        continue;
                    }

                    if (verb is "hold" or "pressed")
                    {
                        Log.Info(caption.ForcePressed(which)
                            ? $"Harness: holding the {which} caption button — {caption.Describe(which)}."
                            : $"Harness: “{which}” is not a caption button.");
                        continue;
                    }

                    var before = WindowState;
                    var acted = caption.Press(which);

                    // Said at once, because close is one of the three: the window is gone a
                    // moment later and a line waiting on a delay would never be written, which
                    // would leave the one button whose effect cannot be photographed with no
                    // evidence at all.
                    Log.Info($"Harness: caption press {which} — {(acted ? "the button acted" : "no such button")}, "
                             + $"from {before}.");

                    // Then again once the windowing system has answered. A maximize is a request
                    // to the compositor, and reading the property in the same pass reports what
                    // it was before the reply came back: two presses read that way both saw
                    // Normal and both maximized, which looked like a restore that did nothing and
                    // was a measurement taken too early.
                    await Task.Delay(600);

                    Log.Info(
                        $"Harness: caption {which} settled — window {before} → {WindowState}, "
                        + $"{ClientSize.Width:0}x{ClientSize.Height:0}; "
                        + $"maximize tip “{caption.MaximizeTip}”.");
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <c>MAILBOX_THEME_SWITCH=&lt;id&gt;</c> applies a second theme once the window is up and
    /// laid out, so a capture shows a theme this window was *changed* to rather than one it
    /// started in.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_THEME</c> is a startup theme: it proves four themes render, and nothing about
    /// a live swap, which is a different claim — the resource dictionary is republished under a
    /// visual tree that already exists, and anything holding a brush rather than a
    /// <c>DynamicResource</c> keeps the old colour. Pair the two to photograph the swap:
    /// <c>MAILBOX_THEME=colorful MAILBOX_THEME_SWITCH=black</c>. The chrome tokens are logged
    /// after the swap so the read-back is a value rather than a picture.
    /// </remarks>
    private void PoseLiveTheme()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_THEME_SWITCH") is not { Length: > 0 } wanted) return;

        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                var was = App.Themes.ThemeId;
                if (App.Themes.Library.Canonical(wanted.Trim()) is not { } id)
                {
                    Log.Info($"Harness: no theme “{wanted}” — this build has {string.Join(", ", App.Themes.Library.Ids)}.");
                    return;
                }

                try
                {
                    App.Themes.Apply(id);
                }
                catch (Mailbox.Theming.Tokens.ThemeResolutionException ex)
                {
                    Log.Warn($"Harness: theme “{id}” would not apply: {ex.Message}");
                    return;
                }

                UpdateLayout();

                // Read out of the live resource dictionary rather than the token set: what the
                // window is painted from is the dictionary, and a swap that updated the service
                // and not the bridge would look identical in the tokens and wrong on screen.
                string Live(string key)
                {
                    if (Application.Current is not { } application) return "no application";

                    return application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value)
                           && value is not null
                        ? value.ToString() ?? "?"
                        : "unset";
                }

                Log.Info(
                    $"Harness: theme {was} → {App.Themes.ThemeId}; "
                    + $"titlebar.background {Live("titlebar.background")}, "
                    + $"rail.background {Live("rail.background")}, "
                    + $"ribbon.background {Live("ribbon.background")}, "
                    + $"titlebar.caption.hover {Live("titlebar.caption.hover")}.");
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Resolves what the ribbon raised to a catalogue command and hands it to
    /// <see cref="RunCommand"/>, which is where the behaviour lives. The two are separate so the
    /// round-trip from a click to a resolved command can be proven without a handler.
    /// </summary>
    private void OnRibbonCommand(object? sender, RibbonCommandEventArgs e)
    {
        // A collapsed ribbon rolls back up once it has been used, which is the whole bargain of
        // the mode: it is there when wanted and gone the rest of the time.
        _ribbon.CloseFloatingBody();

        // While an inline reply is open the strip carries both worlds: the Message tab's
        // commands act on the reply's surface, and the shell's own tabs stay the shell's — the
        // reader can file mail behind a reply without discarding it.
        if (_inlineCompose is { } surface
            && App.Commands.TryGet(e.Command, out var invoked)
            && (invoked.Surface == CommandSurface.Compose
                // Undo belongs to whatever is being written while a reply is open: somebody
                // with a caret in a reply means the sentence they just typed, not the message
                // they filed before opening it. The compose window answers it the same way.
                || e.Command == MailCommands.Undo.Id))
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

        // The box takes its place from the layout in front of it and then holds still — the
        // owner's rule, stated after watching it follow a splitter: resizing the email area
        // must never move it. What re-places it is a mode change, not a drag: the window
        // opening, and the inline reply opening or closing — the reply capture the size was
        // matched against shows the box ending on the reading-pane divider while a reply is
        // up, and home.png shows 511 (the token, now the cap) over the default list. So each
        // of those moments takes one measurement at the next settled layout, and between
        // them the box ignores the panes entirely.
        var placed = false;

        void PlaceOnce()
        {
            if (placed) return;
            if (list.TranslatePoint(default, this) is not { } origin) return;

            var left = Math.Round(origin.X);
            if (toolbar is { IsVisible: true } && toolbar.TranslatePoint(new Point(toolbar.Bounds.Width, 0), this) is { } end)
            {
                left = Math.Max(left, Math.Round(end.X) + 12);
            }

            var cap = this.TryFindResource("titlebar.search.width.value", out var t) && t is double d ? d : 511;
            var width = Math.Clamp(Math.Round(list.Bounds.Width), 180, cap);

            if (left <= 0 || list.Bounds.Width <= 0) return;

            search.Margin = new Thickness(left, search.Margin.Top, search.Margin.Right, search.Margin.Bottom);
            search.Width = width;
            placed = true;
        }

        _replaceSearchBox = () => placed = false;
        list.LayoutUpdated += (_, _) => PlaceOnce();
    }

    /// <summary>Asks the search box to take a fresh measurement at the next settled layout.</summary>
    private Action _replaceSearchBox = () => { };

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
    /// Full-screen mode: the caption and the ribbon body go, and the window fills the screen.
    /// </summary>
    /// <remarks>
    /// The reference's own reading of it — everything that is not the mail goes away, and one
    /// press of the same entry brings it all back. The ribbon collapses rather than disappears
    /// so the tab strip is still there to bring the body back for one command without leaving
    /// full screen, which is what makes the mode usable rather than a trap.
    /// <para>
    /// Escape leaves as well. A window with no caption buttons and no title bar has no other way
    /// out with the mouse, and a reader who cannot find one thinks the application has hung.
    /// </para>
    /// </remarks>
    private void ToggleFullScreen(ShellViewModel shell)
    {
        var going = WindowState != WindowState.FullScreen;

        if (going)
        {
            _ribbonBeforeFullScreen = _ribbon.DisplayMode;
            _ribbon.DisplayMode = RibbonDisplayMode.Collapsed;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = WindowState.Normal;
            _ribbon.DisplayMode = _ribbonBeforeFullScreen ?? RibbonDisplayMode.Simplified;
            _ribbonBeforeFullScreen = null;
        }

        _ribbon.IsFullScreen = going;
        if (this.FindControl<Border>("TitleBar") is { } bar) bar.IsVisible = !going;

        shell.StatusRight = going ? "Full-screen mode. Escape or the Ribbon Display Options menu leaves it." : string.Empty;
        Log.Info($"Full-screen mode {(going ? "on" : "off")}.");
    }

    private RibbonDisplayMode? _ribbonBeforeFullScreen;

    /// <summary>
    /// The single place a command arrives, whichever control raised it. Every route below is a
    /// real handler; a command with none falls through to the status line, which says what it is
    /// waiting for rather than pretending.
    /// </summary>
    private void RunCommand(CommandId id)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (!App.Commands.TryGet(id, out var command)) return;

        Log.Debug($"Command invoked: {command.Id}");

        if (id == MailCommands.SendReceiveAll.Id) { _ = SendReceiveAsync(shell); return; }
        if (id == ViewCommands.SendAll.Id) { _ = SendReceiveAsync(shell, mode: TransferMode.SendOnly); return; }
        if (id == ViewCommands.UpdateFolder.Id) { _ = UpdateFolderAsync(shell); return; }
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
        if (id == ViewCommands.Zoom.Id) { ShowZoomDialog(shell); return; }
        if (id == MailCommands.AdvancedFind.Id) { ShowAdvancedFind(shell); return; }
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

        // All Apps: the installed plugins' commands, and the way to the page that manages them.
        if (id == ViewCommands.Apps.Id)
        {
            var apps = AllAppsMenu.Build(RunCommand, () => _ = ShowOptions("addins"));
            MenuProbe.Show("All Apps", apps, _ribbon ?? (Control)this, atPointer: true);
            return;
        }

        // The View tab's first cluster: Change View, Current View, Arrange By, Layout, and the
        // entries behind them as commands of their own.
        // The View tab is one tab over nine modules, and these act on the module on screen:
        // pressed with Notes or Tasks up they used to flip and edit the message list nobody
        // could see, or open the mail list's own dialog under a mail view's name.
        if (RunModuleViewCommand(shell, id)) return;

        if (id == ViewCommands.ChangeView.Id) { ShowChangeViewMenu(shell); return; }
        if (id == ViewCommands.ViewSettings.Id) { ShowCurrentViewMenu(shell); return; }
        if (id == ViewCommands.ArrangeBy.Id) { MenuProbe.Show("the arrange-by menu", ArrangeFlyout(shell), _ribbon ?? (Control)this, atPointer: true); return; }
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
        if (id == MailCommands.Meeting.Id) { ReplyWithMeeting(shell); return; }
        if (id == MailCommands.MoreRespond.Id) { ShowMoreRespondMenu(shell); return; }

        if (RunCalendarCommand(shell, id)) return;
        if (RunPeopleCommand(shell, id)) return;
        if (RunTaskCommand(shell, id)) return;
        if (RunNoteCommand(shell, id)) return;
        if (RunJournalCommand(shell, id)) return;
        if (RunFeedCommand(shell, id)) return;
        if (RunOverSelection(shell, id)) return;
        if (RunViewCommand(shell, id)) return;
        if (RunViewTabCommand(shell, id)) return;
        if (RunFolderTabCommand(shell, id)) return;
        if (RunServerCommand(shell, id)) return;
        if (RunHelpCommand(shell, id)) return;

        // A plugin's command, found the way a Quick Step is: the host owns the handler, and a
        // handler that throws disables its plugin rather than the window.
        if (App.Plugins.TryRun(id)) return;

        // A command that acts on a selection, pressed with nothing selected. The button for it
        // is greyed, so this is the keyboard's way in — Delete in an empty folder — and the
        // answer is what is missing rather than a developer string about the command itself.
        if ((command.RequiresSelection || command.RequiresSingleSelection) && !IsCommandUsable(id))
        {
            shell.StatusRight = command.RequiresSingleSelection && SelectionCount() > 1
                ? $"{command.Label} works on one item at a time."
                : $"Nothing is selected, and {command.Label} acts on what is.";
            return;
        }

        // Everything left is a command with no handler, which is a defect rather than a state:
        // the status line names it so it can be found and wired.
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
    /// <param name="covered">
    /// The protected header fields of <paramref name="message"/>, when its own pane has them —
    /// a message window's reply must address from those exactly as the shell's does.
    /// </param>
    /// <summary>
    /// Reply with Meeting: the meeting window, already asking everyone the message was between.
    /// </summary>
    /// <remarks>
    /// Reply All's rule for who, not Reply's: a meeting about a conversation is a meeting for the
    /// people in it, which is what the reference's own button does. The reader's own addresses
    /// come out — nobody invites themselves — and the subject carries over, because the meeting
    /// is about the thing the message was about.
    /// </remarks>
    private void ReplyWithMeeting(ShellViewModel shell)
    {
        if (_openMessage is not { } original)
        {
            shell.StatusRight = "Select a message to meet about.";
            return;
        }

        var covered = _reading?.Protected;
        if (covered is not null) original = HeaderProtection.Addressed(original, covered, original.Body);

        var draft = Reply.Build(original, ReplyKind.ReplyAll, new ReplyOptions
        {
            OwnAddresses = [.. App.Accounts.All.Select(a => a.Account.Address)],
            Style = QuoteStyle.None,
        });

        var asked = draft.To.Concat(draft.Cc).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (asked.Count == 0)
        {
            shell.StatusRight = "That message names nobody to meet.";
            return;
        }

        var calendar = EnsureCalendar(shell);
        _ = NewAppointmentAsync(
            shell,
            calendar.Anchor.ToDateTime(StartFor(calendar.Anchor)),
            allDay: false,
            meeting: true,
            asked: asked,
            subject: original.Subject ?? string.Empty);

        Log.Info($"Reply with Meeting: asking {asked.Count} — {string.Join(", ", asked)}.");
    }

    /// <summary>
    /// The Respond group's "More" menu: the answers that are not one of the three big buttons.
    /// </summary>
    /// <remarks>
    /// The reference's own set. Reply with Meeting is on it as well as on the bar, because the
    /// bar sheds it first at narrow widths and a reader who has lost the button looks here.
    /// </remarks>
    private void ShowMoreRespondMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, string? icon, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = MenuIcon(icon), IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        var one = SelectedRows() is { Count: 1 };

        // The reference's own set, less the two that reach an instant-messaging service — Reply
        // with IM and Call. Absent rather than greyed, for the same reason Send to OneNote is:
        // a button that cannot do what it says is worse than one that is not there.
        Entry("Reply with _Meeting", "meeting", () => ReplyWithMeeting(shell), one);
        Entry("_Forward as Attachment", "forward", () => ForwardAsAttachment(shell), SelectedRows().Count > 0);

        _ribbon.OpenMenuUnder(MailCommands.MoreRespond.Id, flyout, this);
    }

    /// <summary>
    /// Forward as Attachment: the original as a <c>message/rfc822</c> part rather than quoted.
    /// </summary>
    /// <remarks>
    /// The bytes as they arrived, which is the point of the gesture — a quoted forward is a
    /// retyping of a message and an attached one is the message, headers, signatures and all.
    /// Somebody asked to look at a header, or at whether a signature verifies, needs the second.
    /// </remarks>
    private void ForwardAsAttachment(ShellViewModel shell)
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            shell.StatusRight = "Select a message to forward.";
            return;
        }

        var carried = new List<CarriedPart>();
        foreach (var row in rows)
        {
            if (shell.RawOf(row) is not { Length: > 0 } raw) continue;

            try
            {
                using var buffer = new MemoryStream(raw);
                var message = MimeKit.MimeMessage.Load(buffer);
                var subject = message.Subject is { Length: > 0 } s ? s : "(no subject)";
                carried.Add(new CarriedPart(
                    SafeName(subject, "message") + ".eml",
                    "message/rfc822",
                    new MimeKit.MessagePart { Message = message }));
            }
            catch (FormatException ex)
            {
                Log.Warn($"Message {row.Id} could not be attached.", ex);
            }
        }

        if (carried.Count == 0)
        {
            shell.StatusRight = "Those messages could not be read to attach.";
            return;
        }

        NewMessage(
            new ReplyDraft
            {
                Subject = "FW: " + (rows.Count == 1 ? rows[0].Subject : $"{rows.Count} messages"),
                Attachments = carried,
            },
            ReplyKind.Forward);

        shell.StatusRight = carried.Count == 1
            ? "The message is attached to a new one."
            : $"{carried.Count} messages are attached to a new one.";
        Log.Info($"Forward as Attachment: {carried.Count} message(s).");
    }

    private void Respond(ShellViewModel shell, ReplyKind kind, MimeKit.MimeMessage? message = null,
        IReadOnlyList<string>? to = null, ProtectedHeaders? covered = null)
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
        // content out in the clear.
        covered ??= ReferenceEquals(original, _openMessage) ? _reading?.Protected : null;
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

        // The reference embeds the reply in the app whatever the layout: with the reading pane
        // off, the wide list gives way and the reply takes that section (the owner's own setup
        // reads with the pane off, and the earlier fall-back-to-a-window here is what made
        // Reply open a window on their machine — the wrong reading of the capture). The Options
        // page's "Open replies and forwards in a new window" is the one switch that means a
        // window; Pop Out is the button for one reply at a time.
        //
        // Only in the mail module, though: the inline surface grows in the mail pane grid, and
        // any other module is covering that grid — a reply embedded from the to-do list swapped
        // the ribbon and gave the reader nowhere to type.
        if (App.MailOptions.OpenRepliesInNewWindow || shell.Module != MailboxModule.Mail)
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
    /// True while an inline reply borrows a reading-pane section the reader keeps hidden — put
    /// back the way it was when the reply closes or pops out.
    /// </summary>
    private bool _restoreHiddenPane;

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

        // With the reading pane off, the reply still embeds: the pane's section is shown for
        // the reply's lifetime — the wide list narrowing beside it, which is what "the other
        // emails disappear" looks like in the reference — and hidden again when the reply
        // closes or pops out.
        _restoreHiddenPane = !shell.ReadingPaneVisible;
        if (_restoreHiddenPane)
        {
            shell.ReadingPaneAtBottom = false;
            shell.ReadingPaneVisible = true;
        }

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

        // The strip keeps the shell's own tabs and gains Message, selected — the reference
        // changes the ribbon, not the window. A command routes by whose it is: the compose
        // window's commands ask the surface, everything else asks whatever the shell asked
        // before the reply opened.
        _savedRibbonEnabled = _ribbon.CommandEnabled;
        var saved = _savedRibbonEnabled;
        _ribbon.Layout = App.InlineReplyRibbon();
        _ribbon.ActiveTabId = "message";
        _ribbon.CommandEnabled = id =>
            App.Commands.TryGet(id, out var command) && command.Surface == CommandSurface.Compose
                ? surface.IsCommandEnabled(id)
                : saved?.Invoke(id) ?? true;
        _ribbon.RefreshEnablement();

        var host = this.FindControl<ContentControl>("ReadingComposeHost")!;
        host.Content = InlineComposeChrome(shell, surface);
        host.IsVisible = true;

        // A mode change, so the box re-measures once the reply's layout settles — the reply
        // capture shows it ending on the divider while a reply is up. Drags still move nothing.
        _replaceSearchBox();

        // A control made visible after layout does not grow its band until it is measured again
        // (the traps list) — without this a reply opened by the harness photographs as an empty
        // pane, and one opened at certain sizes draws late.
        host.InvalidateMeasure();
        host.UpdateLayout();

        // Formatting commands over the inline reply's editor — MAILBOX_COMPOSE_RUN — before the
        // send below, so what they did is what goes on the wire.
        PoseInlineComposeEditor();

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
        // The reference's order and dress: Discard with its bin, then Pop Out with its window,
        // both flat on the strip's own surface (clicking reply.png, top right of the pane).
        Button Flat(string icon, string label, string tip)
        {
            var glyph = new TextBlock
            {
                Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty(icon, 16),
                FontFamily = Mailbox.Theming.Icons.IconFont.Family,
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush"),
            };

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush"),
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("type.ui.size.small.value"),
            };

            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { glyph, text },
                },
                Padding = new Thickness(10, 4),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = default,
            };
            ToolTip.SetTip(button, tip);
            return button;
        }

        var discard = Flat("delete", "Discard", "Discard this reply");
        discard.Margin = new Thickness(0, 0, 6, 0);
        discard.Click += (_, _) => surface.Invoke(ComposeCommands.Discard.Id);

        var popOut = Flat("open-item", "Pop Out", "Open this reply in its own window");
        popOut.Click += (_, _) => PopOutInline(shell);

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
                Children = { discard, popOut },
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

        if (_restoreHiddenPane)
        {
            _restoreHiddenPane = false;
            shell.HideReadingPane.Execute(null);
        }

        _replaceSearchBox();
        compose.Show(this);
    }

    /// <summary>Dismisses the inline reply and puts the reading pane back to reading.</summary>
    private void CloseInlineCompose(ShellViewModel shell)
    {
        if (_inlineCompose is null) return;

        this.FindControl<ContentControl>("ReadingComposeHost")!.IsVisible = false;
        this.FindControl<ContentControl>("ReadingComposeHost")!.Content = null;
        _inlineCompose = null;

        if (_restoreHiddenPane)
        {
            _restoreHiddenPane = false;
            shell.HideReadingPane.Execute(null);
        }

        _replaceSearchBox();
        RestoreReadingRibbon();
        shell.Refresh();
    }

    private void RestoreReadingRibbon()
    {
        _ribbon.Layout = App.MailRibbon();

        // The Message tab the reply brought is gone with it; back to Home, not to a tab id
        // the strip no longer holds.
        _ribbon.ActiveTabId = "home";
        _ribbon.CommandEnabled = _savedRibbonEnabled;
        _ribbon.RefreshEnablement();
        _savedRibbonEnabled = null;
    }

    /// <summary>
    /// The Home tab's commands over the selected messages.
    /// </summary>
    /// <remarks>
    /// The shell has had every one of these operations for a long time — the Delete key, the
    /// hover actions and the shortcuts all call them — while the ribbon buttons for the same
    /// things reported "not wired" until an audit pressed them. They call the same operations
    /// now, so a thing done from the ribbon, the keyboard, a hover or the row's menu is one
    /// thing done four ways.
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
        // Not Junk belongs to the Junk folder, and the menu greys it everywhere else. The
        // catalogue lets any command be put on the toolbar or bound to a key, though, and there
        // nothing greys it: pressed on inbox mail it reached MarkJunk, which reads the folder to
        // decide which way to go, and marked the message *as* junk — a command doing the exact
        // opposite of its own label and description. It says so instead, in the words its own
        // menu entry uses.
        if (id == MailCommands.NotJunk.Id)
        {
            if (shell.CurrentFolderRole != FolderRole.Junk)
            {
                shell.StatusRight = "Not Junk is only for messages in the Junk Email folder.";
                return true;
            }

            shell.MarkJunk(rows);
            return true;
        }
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
            else shell.FlagForFollowUp(rows, QuickClickSettings.DueDate(App.QuickClick.Flag, Mailbox.Core.PosedClock.Now));
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
        // Undo and Redo. Undo is one of the two buttons on the shipped Quick Access Toolbar and
        // had no handler at all until now, which meant Ctrl+Z after deleting the wrong message
        // answered with "not wired yet" — the most reflexive gesture in a mail client reporting
        // a defect in the application.
        if (id == MailCommands.Undo.Id)
        {
            shell.StatusRight = shell.Undo.Undo() is { } undone
                ? $"{undone} undone."
                : "There is nothing to undo.";
            return true;
        }

        if (id == ViewCommands.Redo.Id)
        {
            shell.StatusRight = shell.Undo.Redo() is { } redone
                ? $"{redone} done again."
                : "There is nothing to redo.";
            return true;
        }

        // Two the shell places and cannot do, each saying what is absent rather than answering
        // with a developer string. The message window says the same two things in its own words
        // because it can say them in an InfoBar; here it is the status line.
        if (id == ViewCommands.ImmersiveReader.Id)
        {
            shell.StatusRight = "Immersive Reader is a second way of laying the document out, "
                + "and the reading pane lays it out one way.";
            return true;
        }

        if (id == ViewCommands.ReadAloud.Id)
        {
            shell.StatusRight = "There is no speech engine here, and nothing that reads a message "
                + "aloud without sending it off this machine.";
            return true;
        }

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
        => MenuProbe.Show("the follow-up menu", FollowUpMenu(shell, rows), _ribbon ?? (Control)this, atPointer: true);

    /// <summary>The Follow Up entries, for the bar's flyout and the row menu's submenu alike.</summary>
    private MenuFlyout FollowUpMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        if (rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Select a message first", IsEnabled = false });
            return flyout;
        }

        // PosedClock, as the Snooze menu's presets are and as the list's own grouping is: "Today"
        // and "This Week" have to mean the day the list is showing, or a capture of a flagged
        // message reads a date the run cannot be repeated to. The real clock when nothing is
        // pinned.
        var now = Mailbox.Core.PosedClock.Now;

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

        return flyout;
    }

    /// <summary>
    /// The Snooze menu: presets in the flag menu's shape — Later Today, Tomorrow, This
    /// Weekend, Next Week, Custom — and Unsnooze for a message that is snoozed. The presets
    /// are the reference's own times: four hours from now, and eight in the morning otherwise.
    /// </summary>
    private void ShowSnoozeMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        if (rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Select a message first", IsEnabled = false });
            MenuProbe.Show("the snooze menu", flyout, _ribbon ?? (Control)this, atPointer: true);
            return;
        }

        // The posed clock, not the machine's: the list groups by it, and a Snooze menu offering
        // "Later Today" four hours after a day the list is not showing writes captions no capture
        // can hold still and a return no run can travel to. Live in an ordinary run — PosedClock
        // is the real clock until MAILBOX_TODAY pins it.
        foreach (var (header, until) in Mailbox.Core.SnoozePresets.For(Mailbox.Core.PosedClock.Now))
        {
            var item = new MenuItem { Header = header };
            var when = until;
            item.Click += (_, _) => shell.Snooze(rows, when);
            flyout.Items.Add(item);
        }

        var custom = new MenuItem { Header = "Custom…" };
        custom.Click += async (_, _) =>
        {
            var now = Mailbox.Core.PosedClock.Now.LocalDateTime;
            var entered = await Prompt.AskAsync(this, "Snooze until", "Date and time (yyyy-MM-dd HH:mm):",
                now.AddHours(4).ToString("yyyy-MM-dd HH:mm"));
            if (entered is null) return;

            if (DateTime.TryParse(entered, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var when) && when > now)
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

        MenuProbe.Show("the snooze menu", flyout, _ribbon ?? (Control)this, atPointer: true);
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

        MenuProbe.Show("the clean-up menu", flyout, _ribbon ?? (Control)this, atPointer: true);
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

    // ---- Reminders ------------------------------------------------------------------------

    private RemindersWindow? _reminders;
    private readonly HashSet<(string, long)> _announced = [];

    /// <summary>
    /// What the minute timer asks: has any flag's reminder time come? The Reminders window shows
    /// what is due, a toast announces each item once, and the alarm sounds — each as the
    /// Options page allows.
    /// </summary>
    private void CheckReminders(ShellViewModel shell)
    {
        // The posed clock, not the machine's. Everything in this window is a sentence about the
        // distance between now and a due date — "Overdue by 14 days" — so a queue asked against
        // the real date under a pinned run reads a number nothing on screen agrees with, and
        // *which* items are in the list at all changes with the afternoon it was run. Live when
        // nothing is pinned, which is every ordinary session.
        var now = Mailbox.Core.PosedClock.UtcNow;
        var due = new List<DueReminder>();
        foreach (var account in App.Accounts.All)
        {
            foreach (var message in account.Mail.DueReminders(now)) due.Add(DueReminder.ForMessage(account, message));
        }

        // Appointments join the same queue: one reminders window over every module, with one
        // Dismiss All, rather than a second window for the calendar.
        foreach (var appointment in Mailbox.Scheduling.AppointmentReminders.Due(App.Pim, now, dismissPast: App.MailOptions.DismissPastReminders))
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

        if (App.MailOptions.PlayReminderSound) Notifications.Sounds.PlayReminder(App.MailOptions.ReminderSoundFile);

        if (App.MailOptions.DisplayDesktopAlert)
        {
            foreach (var item in fresh) _notifier.Notify(ReminderToast(shell, item));
        }
    }

    /// <summary>The desktop notification one due reminder raises.</summary>
    /// <remarks>
    /// A meeting and a task went out through the new-mail toast, with a message id of zero
    /// standing in for the message they do not have — so every reminder carried Reply, Delete
    /// and Mark Read, and all three landed on message zero of the account named by the empty
    /// string. Three buttons that could not act, on the two thirds of the queue that are not
    /// mail. A flagged message keeps them, because there they are the point; the other two get
    /// the one button that means anything for them, and it opens the item the reminder is about
    /// rather than the mail list.
    /// </remarks>
    private Notifications.Notification ReminderToast(ShellViewModel shell, DueReminder item)
    {
        var summary = "Reminder: " + item.Subject;
        var body = item.DueIn(Mailbox.Core.PosedClock.Now);

        if (item is { IsMessage: true, Account: { } account, Message: { } message })
        {
            return ToastFor(new NewMailToast(summary, body, account.Account.Address, message.Id));
        }

        return new Notifications.Notification(summary, body)
        {
            Actions = [new(Notifications.NotificationAction.Default, "Open")],

            // Kept in the server's history like the single-message toast, and for the same
            // reason: a button that outlives the popup is only useful while the toast is there
            // to press.
            Transient = false,
            Activated = action => Dispatcher.UIThread.Post(() =>
            {
                Log.Info($"Notification action: {action} for {(item.IsAppointment ? "an appointment" : "a task")}.");
                BringForward();
                if (item.Appointment is { } appointment) _ = OpenAppointmentByIdAsync(shell, appointment.ItemId);
                else if (item.Task is { } task) _ = OpenTaskByIdAsync(shell, task.ItemId);
            }),
        };
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

            // The reference has no Open button — a row is opened by double-clicking it — so this
            // is the row's only route to its item, and the item is of whichever of the three
            // kinds happens to be first in the queue.
            case "open":
                window.PressOpen(held);
                Dispatcher.UIThread.Post(
                    () => Log.Info($"Harness: opened “{held[0].Subject}”; windows: {OtherWindows()}"),
                    DispatcherPriority.ApplicationIdle);
                return;

            default:
                // press:<label> goes through the button itself rather than through the method
                // behind it, which is the only way to tell Dismiss from Dismiss All: the two
                // call the same method and differ in what they hand it — the selection, or
                // everything. A method call proves neither.
                if (!spec.StartsWith("press:", StringComparison.OrdinalIgnoreCase)) return;
                if (!PressLabelled(window, spec["press:".Length..].Trim(), "the Reminders window")) return;
                break;
        }

        var now = Mailbox.Core.PosedClock.UtcNow;
        var appointments = Mailbox.Scheduling.AppointmentReminders.Due(App.Pim, now, dismissPast: App.MailOptions.DismissPastReminders).Count;
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

            // The reference opens the item a reminder stands for — a message window here, the
            // way a task's reminder opens its task. Revealing the row alone showed nothing at
            // all with the reading pane off, which is how the owner reads.
            RevealMessage(shell, item.Address, item.MessageId);
            OpenMessageWindowById(shell, item.Address, item.MessageId);
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
        // MAILBOX_CATEGORIZE presses one of its entries, as it already does for the other
        // modules' menu. It reached ItemCategoryMenu only, which the mail module does not use —
        // so the mail Categorize menu had no door at all, and nothing could assign a category to
        // a message through the real path.
        if (Environment.GetEnvironmentVariable("MAILBOX_CATEGORIZE") is { Length: > 0 } posed)
        {
            PoseCategorizeMail(shell, rows, posed);
            return;
        }

        MenuProbe.Show("the categorize menu", CategorizeMenu(shell, rows), _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>The Categorize entries, shared by the bar and the row menu.</summary>
    private MenuFlyout CategorizeMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
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

        return flyout;
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
        try
        {
            await QuickClickAsync(shell, e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A handler body in all but signature — observed here, or the fault lands on the
            // dispatcher instead of in the log.
            Log.Warn("The quick-click column failed.", ex);
        }
    }

    private async Task QuickClickAsync(ShellViewModel shell, QuickClickEventArgs e)
    {
        IReadOnlyList<ViewModels.MessageRow> rows = [e.Row];

        if (e.Field == Mailbox.Core.Views.ViewFields.Flag)
        {
            if (e.Row.IsFlagged) shell.ClearFollowUpFlag(rows);
            else if (App.QuickClick.Flag == QuickFlag.Complete) shell.MarkFollowUpComplete(rows);
            else shell.FlagForFollowUp(rows, QuickClickSettings.DueDate(App.QuickClick.Flag, Mailbox.Core.PosedClock.Now));
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
        => MenuProbe.Show("the junk menu", JunkMenu(shell, rows), _ribbon ?? (Control)this, atPointer: true);

    /// <summary>The Junk entries, shared by the bar and the row menu.</summary>
    private MenuFlyout JunkMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
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

        return flyout;
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
        => MenuProbe.Show("the rules menu", RulesMenu(shell, rows), _ribbon ?? (Control)this, atPointer: true);

    /// <summary>The Rules entries, shared by the bar and the row menu.</summary>
    private MenuFlyout RulesMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var account = shell.CurrentAccountForCategories();
        var message = rows.Count == 1 ? _openMessage : null;

        // The reading pane may be off — the owner reads that way — and then nothing has opened
        // the selection, so the menu built from the open message alone greyed its own point:
        // the reference arms "Always Move Messages From:" from the selected row. The row knows
        // its store id, so the message is loaded rather than waited for.
        if (message is null && rows.Count == 1 && account?.Mail.LoadRaw(rows[0].Id) is { } raw)
        {
            try
            {
                message = MimeKit.MimeMessage.Load(new MemoryStream(raw));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the selected message for the rules menu.", ex);
            }
        }

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

        return flyout;
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
        => MenuProbe.Show("the move menu", MoveMenu(shell, rows), _ribbon ?? (Control)this, atPointer: true);

    /// <summary>The Move entries, shared by the bar and the row menu.</summary>
    private MenuFlyout MoveMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
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

        // The reference closes this menu with the two that reach past the list of folders: one
        // that asks for any folder, and one that copies rather than moves. The rule only earns
        // its place when there is a list above it to close.
        if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());

        var other = new MenuItem { Header = "_Other Folder…", IsEnabled = rows.Count > 0 };
        other.Click += (_, _) => _ = MoveToOtherFolderAsync(shell, rows);
        flyout.Items.Add(other);

        var copy = new MenuItem { Header = "_Copy to Folder…", IsEnabled = rows.Count > 0 };
        copy.Click += (_, _) => _ = CopyToFolderAsync(shell, rows);
        flyout.Items.Add(copy);

        return flyout;
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
        ToolTip.SetTip(form, "Custom forms are a plugin's business.");
        more.Items.Add(form);
        flyout.Items.Add(more);

        // New Items has its own button on the classic ribbon and hangs off the New chevron on the
        // Simplified bar — whichever module's New that is — so the menu falls back to whichever of
        // the two is on screen.
        var under = _ribbon.ControlFor(MailCommands.NewEmail.Id)
                    ?? _ribbon.ControlFor(PeopleCommands.NewContact.Id)
                    ?? _ribbon.ControlFor(TaskCommands.NewTask.Id)
                    ?? _ribbon.ControlFor(CalendarCommands.NewAppointment.Id)
                    ?? _ribbon.ControlFor(NoteCommands.NewNote.Id)
                    ?? _ribbon.ControlFor(JournalCommands.NewEntry.Id)
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

        MenuProbe.Show("the filter-email menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>
    /// The message row's right-click menu, in the reference's order.
    /// </summary>
    /// <remarks>
    /// Built when it opens rather than once: the ticks and the folder list change with the
    /// selection. Anything on it that has no command behind it yet is greyed with what it waits
    /// on in its tooltip, which is what the ribbon does for the same commands.
    /// </remarks>
    /// <summary>
    /// The menu over a message, transcribed from the reference's own.
    /// </summary>
    /// <remarks>
    /// Sixteen entries in five groups, in the reference's order, with its access keys and its
    /// submenus: Copy and Quick Print; the three responses; Mark as Unread, Categorize and
    /// Follow Up; Find Related on its own; Quick Steps, Rules and Move; then Ignore, Junk,
    /// Delete and Archive.
    /// <para>
    /// Every submenu is the flyout the bar's own button opens, hung off a menu item instead of
    /// shown at a control — one implementation each, so the menu and the ribbon cannot come to
    /// disagree about what Follow Up offers.
    /// </para>
    /// <para>
    /// Each icon is the command's own, tint included, which is how the ribbon draws it. Every
    /// entry does what it says: the reference's Send to OneNote reaches a product this
    /// application deliberately has no part of and is left out rather than drawn greyed,
    /// which is the reader's own instruction.
    /// </para>
    /// </remarks>
    private MenuFlyout RowMenu(ShellViewModel shell)
    {
        // Filled here rather than from the flyout's own Opening event, and this is the whole
        // reason the menu was invisible. A flyout creates its presenter once and binds it to the
        // item collection as it stood at that moment; a run that adds the entries from Opening
        // adds them after the popup has already measured, and the popup is sized from that
        // measurement — two pixels of border around nothing. It opened every time, and every
        // time it opened empty. Every other menu in this window was built before it was shown,
        // which is why the ribbon's dropdowns were never affected and this one always was.
        var flyout = new MenuFlyout();
        FillRowMenu(flyout, shell);

        // The trail that found it, kept. A flyout that is asked to open, fills, and then closes
        // again in the same breath looks exactly like one that never opened, and only the order
        // of these says which happened. The size is here because it is the thing that was wrong
        // and nothing on screen would have shown it.
        flyout.Opened += (_, _) => Log.Debug($"Row menu opened with {flyout.Items.Count} entries.");
        flyout.Closed += (_, _) => Log.Debug("Row menu closed.");
        return flyout;
    }

    /// <summary>The row menu while it is up, so the harness can ask what is in the one on screen.</summary>
    private MenuFlyout? _rowMenu;

    /// <summary>
    /// Builds the row menu into a flyout. Split from opening it so the harness can read it back:
    /// a popup never appears in a capture, so what a menu holds is checked by asking it.
    /// </summary>
    private void FillRowMenu(MenuFlyout flyout, ShellViewModel shell)
    {
        {
            flyout.Items.Clear();
            var rows = SelectedRows();
            var some = rows.Count > 0;

            void Rule() => flyout.Items.Add(new Separator());

            MenuItem Entry(string header, Control? icon, Action run, bool enabled = true)
            {
                var item = new MenuItem { Header = header, Icon = icon, IsEnabled = enabled };
                item.Click += (_, _) => run();
                flyout.Items.Add(item);
                return item;
            }

            void Command(string header, MailboxCommand command, bool enabled = true)
                => Entry(header, CommandIcon(command), () => RunCommand(command.Id), enabled);

            // A submenu built from the flyout the ribbon's own button opens: the items are moved
            // across, so both routes run the same handlers.
            void Submenu(string header, Control? icon, MenuFlyout source, bool enabled = true)
            {
                var item = new MenuItem { Header = header, Icon = icon, IsEnabled = enabled };
                foreach (var child in source.Items.Cast<Control>().ToList())
                {
                    source.Items.Remove(child);
                    item.Items.Add(child);
                }

                flyout.Items.Add(item);
            }

            Entry("_Copy", MenuIcon("copy"), () => _ = CopyRowsAsync(shell, rows), some);
            Command("_Quick Print", MailCommands.Print, some);
            Rule();

            Command("_Reply", MailCommands.Reply, some);
            Command("Reply _All", MailCommands.ReplyAll, some);
            Command("For_ward", MailCommands.Forward, some);
            Rule();

            // The reference names the state the press would move to, so a read message offers
            // to make it unread and an unread one offers the opposite.
            Command(rows.Any(r => r.IsUnread) ? "Mark as _Read" : "Mark as _Unread", MailCommands.Unread, some);
            Submenu("Cat_egorize", CategorizeArtwork(), CategorizeMenu(shell, rows), some);
            Submenu("Follow _Up", FlagArtwork(), FollowUpMenu(shell, rows), some);
            Rule();

            Submenu("_Find Related", MenuIcon("mail"), FindRelatedMenu(shell, rows), some);
            Rule();

            Submenu("_Quick Steps", MenuIcon("quicksteps"), QuickStepsMenu(shell, rows));
            Submenu("Rule_s", MenuIcon("rules"), RulesMenu(shell, rows), some);
            Submenu("_Move", MenuIcon("move"), MoveMenu(shell, rows), some);
            Rule();

            // Send to OneNote is the reference's next entry and is deliberately not here. It
            // reaches a product this application has no part of, and the reader's instruction
            // was to leave it out rather than draw it greyed: every entry in this menu does
            // what it says. Notes are a module of their own on the rail.

            Command(shell.IsIgnored(rows) ? "Stop Ignoring Conversation" : "Ignore", MailCommands.Ignore, some);
            Submenu("_Junk", CommandIcon(MailCommands.Junk), JunkMenu(shell, rows), some);
            Command("_Delete", MailCommands.Delete, some);
            Entry("_Archive…", CommandIcon(MailCommands.Archive), () => _ = ArchiveRowsAsync(shell, rows), some);
        }
    }

    /// <summary>
    /// Harness: every window open besides this one, by title.
    /// </summary>
    /// <remarks>
    /// Half the entries in a menu do their work by opening something — a dialog, or a compose
    /// window — and leave the status line untouched, so a read-back that only reports the status
    /// cannot tell "it opened what it should have" from "it did nothing at all".
    /// <para>
    /// Not the whole answer on its own: with the reading pane off, Reply and Forward embed their
    /// draft in the window rather than opening one, which is the reference's behaviour and the
    /// owner's setup. An empty list here is why the press read-back names the inline surface
    /// beside it — read on its own it says "nothing happened" about a reply that opened
    /// perfectly well.
    /// </para>
    /// </remarks>
    private string OtherWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return "not a desktop lifetime";
        }

        // Visible ones only: the answer is what a reader can see, and the warm message window
        // waiting hidden in its pool is machinery, not an answer.
        var others = desktop.Windows
            .Where(w => !ReferenceEquals(w, this) && w.IsVisible)
            .Select(w => string.IsNullOrWhiteSpace(w.Title) ? w.GetType().Name : w.Title)
            .ToList();

        return others.Count == 0 ? "none" : string.Join(", ", others.Select(t => $"\u201c{t}\u201d"));
    }

    /// <summary>
    /// Harness only: presses one of the Backstage's two plain buttons — the back arrow and Add
    /// Account — which raise events of their own rather than going through an action name.
    /// </summary>
    private static void PressBackstageButton(BackstageView view, string which)
    {
        var buttons = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<Button>();

        var button = which == "back"
            ? buttons.FirstOrDefault(b => ToolTip.GetTip(b) as string == "Back")
            : buttons.FirstOrDefault(
                b => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(b)
                    .OfType<TextBlock>()
                    .Any(t => t.Text == "Add Account"));

        if (button is null)
        {
            Log.Warn($"Harness: the Backstage has no “{which}” button.");
            return;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Harness only: what a Backstage action did, once it has had a moment to do it.
    /// </summary>
    /// <remarks>
    /// "The pose ran without throwing" is not "the button acts". Most of these actions open a
    /// window and say nothing, some only write the status line, and a few — Save As, the
    /// importers — hand over to the desktop's file dialog and open no window of this
    /// application's at all. So the read-back names all three: the windows that are up, the
    /// status line, and whether the Backstage put itself away, which is the tell that an action
    /// that closes it ran.
    /// <para>
    /// On a timer rather than another posted callback because a modal <c>ShowDialog</c> does not
    /// return: the dialog is up and pumping this same dispatcher, and a continuation queued
    /// behind the action would report from before it opened.
    /// </para>
    /// </remarks>
    private void ReportBackstageAction(string action)
        => Dispatcher.UIThread.Post(
            async () =>
            {
                // The capture waits for the read-back rather than racing it: the settle is 900ms
                // and this used to report at 500, which is a margin and not a guarantee.
                using var hold = WindowCapture.Hold();
                await Task.Delay(500);

                var host = this.FindControl<ContentControl>("BackstageHost");
                var status = (DataContext as ShellViewModel)?.StatusRight ?? string.Empty;

                // An action whose only result is a sentence may still be fetching it — Check for
                // Updates is the one, and reporting "Checking for updates…" says nothing about
                // what it found. Waited out, bounded, rather than lengthening every pose.
                for (var waited = 0; status.EndsWith('…') && waited < 4000; waited += 250)
                {
                    await Task.Delay(250);
                    status = (DataContext as ShellViewModel)?.StatusRight ?? string.Empty;
                }

                Log.Info($"Harness: {action} — windows: {OtherWindows()}; "
                    + $"status: “{status}”; "
                    + $"the Backstage is {(host?.IsVisible == true ? "still up" : "closed")}.");
            },
            DispatcherPriority.Background);

    /// <summary>What a harness press reports afterwards: the status line, once the press has run.</summary>
    private static Action? Pressed { get; set; }

    /// <summary>Harness only: presses a menu entry by name, descending into submenus.</summary>
    private static void PressMenuEntry(IEnumerable<Control> items, string[] path, int depth)
    {
        if (depth >= path.Length) return;

        var wanted = path[depth].Replace("_", string.Empty, StringComparison.Ordinal);

        foreach (var item in items.OfType<MenuItem>())
        {
            var header = (item.Header as string ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
            if (!header.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            if (depth == path.Length - 1)
            {
                Log.Info($"Harness: pressing “{header}”.");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Pressed?.Invoke();
                return;
            }

            PressMenuEntry(item.Items.Cast<Control>(), path, depth + 1);
            return;
        }

        Log.Warn($"Harness: the row menu has no entry “{wanted}”.");
    }

    /// <summary>
    /// Harness only: what the row menu holds, one line each, with its submenus opened out.
    /// </summary>
    /// <remarks>
    /// The reference's menu is a capture and this is the only way to compare ours with it
    /// entry by entry — a menu is a separate surface and never appears in a photograph of the
    /// window.
    /// </remarks>
    private void LogRowMenu(ShellViewModel shell)
    {
        // First the wiring, then the contents. A menu whose entries are right but which never
        // opens is the failure that reads as "there is no menu" — so the pose asks the list for
        // its context menu exactly as a right-click does, and says whether one appeared.
        if (List is { } list)
        {
            // A real right-click: press and release, the way a mouse does it. Raising
            // ContextRequested directly proves only that the flyout can open — the question is
            // whether the buttons a reader actually presses get that far.
            var pointer = new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, isPrimary: true);
            var at = new Point(24, 24);

            // On the row rather than on the list: a real right-click lands on the container and
            // bubbles, and a container that handles the press or the release is exactly how a
            // menu ends up never opening. On a message rather than on a group header — the first
            // container in a grouped list is the header, and a menu over one is a different
            // question from the menu the reader asked about.
            var target = list.ContainerFromIndex(0) as Control ?? list;
            for (var i = 0; i < list.ItemCount; i++)
            {
                if (list.ContainerFromIndex(i) is Control { DataContext: ViewModels.MessageRow } row)
                {
                    target = row;
                    break;
                }
            }

            Log.Info($"Harness: right-clicking a {target.GetType().Name}.");


            target.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
                target, pointer, list, at, 0,
                new Avalonia.Input.PointerPointProperties(
                    Avalonia.Input.RawInputModifiers.RightMouseButton,
                    Avalonia.Input.PointerUpdateKind.RightButtonPressed),
                Avalonia.Input.KeyModifiers.None));

            target.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(
                target, pointer, list, at, 0,
                new Avalonia.Input.PointerPointProperties(
                    Avalonia.Input.RawInputModifiers.None,
                    Avalonia.Input.PointerUpdateKind.RightButtonReleased),
                Avalonia.Input.KeyModifiers.None,
                Avalonia.Input.MouseButton.Right));

            // How many entries, not just whether it opened. A menu that opens holding nothing is
            // the failure this pose exists to catch: it was reported open for a fortnight while
            // the popup on screen was two pixels of border around an empty presenter, because
            // nothing here ever asked what was in the one the reader would see.
            Log.Info($"Harness: after a right-click the menu is open: {_rowMenu?.IsOpen == true}, "
                     + $"holding {_rowMenu?.Items.Count ?? 0} entries.");
            _rowMenu?.Hide();

            // And the same question asked the other way, in case the press never becomes a
            // context request at all.
            list.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            Log.Info($"Harness: after ContextRequested the menu is open: {_rowMenu?.IsOpen == true}, "
                     + $"holding {_rowMenu?.Items.Count ?? 0} entries.");
        }
        else
        {
            Log.Warn("Harness: the message list was not found, so the row menu cannot be checked.");
        }

        var flyout = new MenuFlyout();
        FillRowMenu(flyout, shell);

        // MAILBOX_ROWMENU=<entry> presses one of them — "Copy", or "Find Related/Messages from
        // Sender" for something inside a submenu. A menu item cannot be clicked by a capture,
        // and a menu whose entries are never pressed is a menu nobody has checked.
        if (Environment.GetEnvironmentVariable("MAILBOX_ROWMENU") is { Length: > 0 } press)
        {
            // The press is asynchronous where it opens a dialog, so the read-back is posted
            // below it rather than taken on the next line.
            // Held, and asked a moment later. A press that opens a window does not always open
            // it on the spot — a compose window builds its editor first — so a read-back taken
            // at the next idle moment reports "windows: none" for an entry that was on its way
            // to opening one, which reads exactly like an entry that did nothing. The hold stops
            // the capture ending the run before the answer is in.
            Pressed = () =>
            {
                var hold = WindowCapture.Hold();
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(900));
                        Log.Info($"Harness: status \u201c{shell.StatusRight}\u201d, "
                                 + $"search \u201c{shell.SearchText}\u201d, {shell.Messages.Count} row(s), "
                                 + $"windows: {OtherWindows()}, "
                                 + $"inline: {(_inlineCompose is null ? "none" : "a reply is embedded")}.");
                    }
                    finally
                    {
                        hold.Dispose();
                    }
                });
            };

            PressMenuEntry(flyout.Items.Cast<Control>(), press.Split('/', StringSplitOptions.TrimEntries), 0);
            return;
        }

        Log.Info($"Harness: the row menu over {SelectedRows().Count} selected message(s):");

        foreach (var child in flyout.Items.Cast<Control>())
        {
            if (child is Separator)
            {
                Log.Info("  ───");
                continue;
            }

            if (child is not MenuItem item) continue;

            var icon = item.Icon switch
            {
                Mailbox.Controls.Ribbon.RibbonArtwork art => $"drawn:{art.Drawing}",
                TextBlock => "glyph",
                Border => "swatch",
                _ => "none",
            };

            Log.Info($"  {item.Header}{(item.Items.Count > 0 ? " ▸" : string.Empty)}"
                     + $" [{icon}]{(item.IsEnabled ? string.Empty : " (greyed)")}");

            foreach (var sub in item.Items.Cast<Control>())
            {
                Log.Info(sub is Separator
                    ? "      ───"
                    : $"      {(sub as MenuItem)?.Header}{((sub as MenuItem)?.IsEnabled == false ? " (greyed)" : string.Empty)}");
            }
        }
    }

    /// <summary>
    /// A command's icon as the ribbon draws it: its own drawing where it has one, its own glyph
    /// otherwise, in its own tint.
    /// </summary>
    private static Control? CommandIcon(MailboxCommand command)
    {
        if (command.IconArtwork is { Length: > 0 } artwork)
        {
            return new Mailbox.Controls.Ribbon.RibbonArtwork(artwork, 16);
        }

        var icon = MenuIcon(command.Icon);

        if (icon is TextBlock glyph && command.IconTint is { Length: > 0 } tint)
        {
            // ".brush" on the end, as the ribbon's own BuildIcon does. A tint names a token,
            // and a token without the suffix resolves to the colour as text — which a Foreground
            // cannot be, and which threw the moment a menu had a visible glyph to draw. It never
            // did while the popup came out two pixels wide, so this rode along behind that.
            glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(tint + ".brush");
        }

        return icon;
    }

    /// <summary>
    /// Find Related: the reference's two searches — the rest of this conversation, and
    /// everything else from whoever sent it.
    /// </summary>
    /// <remarks>
    /// Both are the search box's own query language rather than a second search: what the reader
    /// gets is a search they can see, edit and clear, which is the search this application
    /// already has.
    /// </remarks>
    private MenuFlyout FindRelatedMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var row = rows.Count == 1 ? rows[0] : null;

        void Entry(string header, string query, bool enabled)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => shell.SearchText = query;
            flyout.Items.Add(item);
        }

        // The thread key is the subject with its Re: and Fw: already taken off, which is what
        // threads a conversation here — so the search finds the same set the list groups.
        var conversation = row?.ThreadKey is { Length: > 0 } key ? key : row?.Subject ?? string.Empty;
        var sender = row is null ? string.Empty : shell.SenderAddresses([row]).FirstOrDefault() ?? string.Empty;

        Entry("Messages in this _Conversation",
            conversation.Length > 0 ? $"subject:\"{conversation}\"" : string.Empty,
            conversation.Length > 0);

        Entry("Messages from _Sender",
            sender.Length > 0 ? $"from:{sender}" : string.Empty,
            sender.Length > 0);

        return flyout;
    }

    /// <summary>The Quick Steps the reader has, and the way to edit them.</summary>
    private MenuFlyout QuickStepsMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();

        foreach (var step in App.QuickSteps.All)
        {
            var item = new MenuItem
            {
                Header = step.Name,
                Icon = MenuIcon(step.Icon),
                IsEnabled = rows.Count > 0,
            };

            var chosen = step;
            item.Click += (_, _) => _ = RunQuickStepAsync(shell, chosen, rows);
            flyout.Items.Add(item);
        }

        if (App.QuickSteps.All.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No Quick Steps yet", IsEnabled = false });
        }

        flyout.Items.Add(new Separator());

        var manage = new MenuItem { Header = "_Manage Quick Steps…" };
        manage.Click += (_, _) => _ = ManageQuickStepsAsync(shell);
        flyout.Items.Add(manage);

        return flyout;
    }

    /// <summary>
    /// Move ▸ Other Folder…: any folder in any account, chosen from the picker.
    /// </summary>
    /// <remarks>
    /// The submenu above it lists the folders of the selection's own account, which is what the
    /// reference lists; this is the way past that — including into another account, where a move
    /// is a copy and a delete because two stores share no row.
    /// </remarks>
    private async Task MoveToOtherFolderAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        var dialog = FolderPicker("Move Items", "Move the selected items to the folder:", null, allowRoot: false);
        await dialog.ShowDialog(this);

        if (dialog.Result is not { Folder: { } folder } chosen) return;
        if (shell.NodeFor(chosen.Account, folder.Id) is { } node) shell.MoveToFolder([.. rows.Select(r => r.Id)], node);
        else shell.MoveToStoreFolder(rows, chosen.Account, folder);
    }

    /// <summary>Move ▸ Copy to Folder…: the same picker, leaving the originals where they are.</summary>
    private async Task CopyToFolderAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        var dialog = FolderPicker("Copy Items", "Copy the selected items to the folder:", null, allowRoot: false);
        await dialog.ShowDialog(this);

        if (dialog.Result is { Folder: { } folder }) shell.CopyTo(rows, folder);
    }

    /// <summary>
    /// Copy: the selected messages on the clipboard, as text.
    /// </summary>
    /// <remarks>
    /// The reference copies the items themselves, to be pasted into another folder — which is
    /// what Move ▸ Copy to Folder does here, and what a clipboard shared with every other
    /// application cannot carry. What it can carry is the message as somebody would paste it
    /// into a document: who it is from, what it is about, when it came, and what it says.
    /// </remarks>
    private async Task CopyRowsAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        var text = new System.Text.StringBuilder();

        foreach (var row in rows)
        {
            if (text.Length > 0) text.AppendLine().AppendLine("————————").AppendLine();

            text.AppendLine($"From: {row.From}");
            if (row.ToLine is { Length: > 0 }) text.AppendLine($"To: {row.ToLine}");
            text.AppendLine($"Sent: {row.Received.LocalDateTime:f}");
            text.AppendLine($"Subject: {row.Subject}");
            text.AppendLine();
            text.AppendLine(row.Body);
        }

        await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(
            clipboard, Avalonia.Input.DataFormat.Text, text.ToString());
        shell.StatusRight = $"{(rows.Count == 1 ? "Message" : $"{rows.Count} messages")} copied.";
    }

    /// <summary>
    /// Archive…: to the account's archive folder, asking which it is the first time.
    /// </summary>
    /// <remarks>
    /// The ellipsis in the reference's label is the ask: an account that has never been told
    /// where its archive is has nowhere to put the message, and the reference's own Set Archive
    /// Folder is what answers that. Once answered it never asks again.
    /// </remarks>
    private async Task ArchiveRowsAsync(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        if (!shell.HasArchiveFolder(rows))
        {
            await BackstageActions.RunAsync(BackstageContext(), "tools.archivefolder");
            if (!shell.HasArchiveFolder(rows)) return;
        }

        shell.MoveTo(rows, FolderRole.Archive);
    }

    /// <summary>A category's colour, as the square the reference draws beside its name.</summary>
    private static Control CategorySwatch(string token)
    {
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2) };
        swatch[!Border.BackgroundProperty] = new DynamicResourceExtension(token + ".brush");
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
        // PosedClock, so a run that has travelled forward with MAILBOX_TODAY sees the messages
        // whose time has come by *that* day — the only way the "it returns on time" claim can be
        // proven without waiting for the time to arrive. The real clock when nothing is pinned.
        var woken = shell.WakeSnoozed(Mailbox.Core.PosedClock.Now);
        if (woken.Count == 0) return;

        AnnounceArrival(woken.Count);
        if (!App.MailOptions.DisplayDesktopAlert) return;

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
            // Cancelled here, unwound off the dispatcher. Disposing in place waited up to two
            // seconds per watcher on the UI thread — a watcher mid-network-read cannot see the
            // cancellation until its read returns — so three IMAP accounts held a dispatcher
            // that could no longer run for six seconds after the window had gone, which reads
            // as a process that will not die. Cancelling all of them first lets them unwind at
            // the same time instead of one after another.
            var closing = _watchers.ToArray();
            _watchers.Clear();

            foreach (var watcher in closing) watcher.BeginStop();
            Task.Run(() => { foreach (var watcher in closing) watcher.Dispose(); });
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
        MenuProbe.Show("the send/receive groups menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>Which mailbox the taskbar is showing, so it is only set when it changes.</summary>
    private string? _taskbarArt;

    /// <summary>
    /// The icon the desktop draws for this application, which is the one the taskbar reads.
    /// </summary>
    /// <remarks>
    /// Handed the application's own embedded drawings; see <see cref="Mailbox.Core.Platform.PanelIcon"/>
    /// for why the taskbar cannot be reached any other way.
    /// </remarks>
    private readonly Mailbox.Core.Platform.PanelIcon _panelIcon = new(
        (art, size) => Avalonia.Platform.AssetLoader.Open(
            new Uri($"avares://mailbox/Assets/Icons/{art}-{size}.png")));

    /// <summary>
    /// Puts the full or the empty mailbox on the window and on the panel.
    /// </summary>
    /// <remarks>
    /// Both, because they are two different pictures on two different surfaces and only one of
    /// them is the window's. The window icon is what a taskbar draws on the desktops that read
    /// one, and it is set here. Plasma is not one of them — it draws the icon its desktop entry
    /// names — so the entry's icon is rewritten as well, which is what
    /// <see cref="Mailbox.Core.Platform.PanelIcon"/> does.
    /// <para>
    /// Only when it changes: setting a window icon goes to the window manager and rewriting the
    /// theme goes to the disk, and doing either on every count change would be work per message
    /// read. Off the UI thread, because the theme write ends in a process launch. Never during a
    /// capture — a screenshot run has no business rewriting the reader's icon theme.
    /// </para>
    /// <para>
    /// Logged when it happens, because an icon in a panel is the one part of this nobody can
    /// screenshot from here — the log is how a run says which mailbox it put up, and whether the
    /// desktop was told.
    /// </para>
    /// </remarks>
    private void ShowUnreadOnTaskbar(ShellViewModel shell)
    {
        var art = Mailbox.Core.Notifications.TrayArtwork.For(shell.TotalUnread);
        if (art == _taskbarArt) return;

        var unread = shell.TotalUnread;

        try
        {
            Icon = new WindowIcon(new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new Uri($"avares://mailbox/Assets/Icons/{art}-256.png"))));

            _taskbarArt = art;
            Log.Info($"Taskbar icon: {art} ({unread} unread).");
        }
        catch (Exception ex)
        {
            // The window keeps whatever icon it had; a panel drawing is not worth a crash.
            Log.Warn("The taskbar icon could not be set.", ex);
        }

        if (WindowCapture.IsRequested) return;

        Task.Run(() =>
        {
            try
            {
                var told = _panelIcon.Show(unread);
                Log.Info(told
                    ? $"Panel icon: the desktop entry's icon is now the {art} mailbox."
                    : $"Panel icon: unchanged ({art}).");
            }
            catch (Exception ex)
            {
                Log.Warn("The panel icon could not be written.", ex);
            }
        });
    }

    /// <summary>
    /// Says when an operation has been refused by a server often enough to stop being replayed.
    /// </summary>
    /// <remarks>
    /// A move to a folder that no longer exists, or a flag on a message whose server id will not
    /// parse, used to be replayed on every send/receive for ever: the attempts were counted, the
    /// error was kept, and nothing read either. The store stops offering one after five tries;
    /// this tells the reader, because a change that will never go is theirs to sort out.
    /// </remarks>
    private void ReportStuckOperations(ShellViewModel shell)
    {
        var stuck = 0;
        var first = string.Empty;

        foreach (var account in App.Accounts.All)
        {
            foreach (var op in account.Mail.StuckOps())
            {
                stuck++;
                if (first.Length == 0 && op.LastError is { Length: > 0 } why) first = why;

                Log.Warn($"{account.Account.Address}: the {op.Kind} operation has been refused "
                         + $"{op.Attempts} times and is no longer being replayed. {op.LastError}");
            }
        }

        if (stuck == 0) return;

        var detail = first.Length > 0 ? $" {first}" : string.Empty;
        shell.StatusRight = stuck == 1
            ? $"One change could not be sent to the server and is no longer being retried.{detail}"
            : $"{stuck} changes could not be sent to the server and are no longer being retried.{detail}";
    }

    /// <summary>
    /// Whether a command can act on what is selected right now.
    /// </summary>
    /// <remarks>
    /// <see cref="MailboxCommand.RequiresSelection"/> and its single-selection sibling are set on
    /// forty-one commands and were read by nobody: the ribbon dimmed only what the host's
    /// <c>CommandEnabled</c> said to, and the shell set that for the lifetime of an inline reply
    /// and at no other time. So Reply, Delete, Move, Categorize and the rest stayed lit with
    /// nothing selected and did nothing when pressed — the reference greys them, and a control
    /// that ships looking interactive while doing nothing unannounced is the thing this
    /// application is not supposed to have.
    /// <para>
    /// Selection means what it means in the module on screen: the rows chosen in the list, the
    /// appointment or the card or the row in the other five. A command that asks for neither is
    /// always usable, which is nearly all of them.
    /// </para>
    /// </remarks>
    private bool IsCommandUsable(CommandId id)
    {
        // Undo and Redo answer to the stack rather than to the selection, and the reference
        // greys them when it is empty.
        if (DataContext is ShellViewModel shell)
        {
            if (id == MailCommands.Undo.Id) return _inlineCompose is not null || shell.Undo.CanUndo;
            if (id == ViewCommands.Redo.Id) return shell.Undo.CanRedo;

            // Take Off Board acts on the board being read: with an article selected anywhere
            // else it used to light up and answer a press with an explanation, which is a black
            // button that does not do what it says.
            if (id.Value == "feeds.board.remove")
            {
                return _feedModule?.SelectedBoard is not null && _feedModule.SelectedArticle is not null;
            }

            // The Folder tab against the folder the pane has selected: the reference greys
            // Rename, Move and Delete on a folder the account cannot do without, and every
            // entry on the tab when nothing is selected at all.
            if (IsFolderTabCommand(id))
            {
                if (SelectedFolderFor(shell) is not var (_, folder)) return false;

                return id != MailCommands.RenameFolder.Id
                       && id != MailCommands.MoveFolder.Id
                       && id != MailCommands.DeleteFolder.Id
                    || folder.Role == FolderRole.None;
            }
        }

        if (!App.Commands.TryGet(id, out var command)) return true;
        if (!command.RequiresSelection && !command.RequiresSingleSelection) return true;

        var selected = SelectionCount();
        return command.RequiresSingleSelection ? selected == 1 : selected > 0;
    }

    /// <summary>How many items are selected in the module on screen.</summary>
    private int SelectionCount() => (DataContext as ShellViewModel)?.Module switch
    {
        MailboxModule.Mail => SelectedRows().Count,
        MailboxModule.Calendar => _calendar?.SelectedEntry is null ? 0 : 1,
        MailboxModule.People => _people?.Selected is null ? 0 : 1,
        MailboxModule.Tasks => _taskModule?.Selected is null ? 0 : 1,
        MailboxModule.Notes => _noteModule?.Selected is null ? 0 : 1,
        MailboxModule.Journal => _journalModule?.Selected is null ? 0 : 1,
        MailboxModule.Feeds => _feedModule?.SelectedArticle is null ? 0 : 1,
        _ => 0,
    };

    /// <summary>Re-reads enablement, for a selection or a module that has just changed.</summary>
    private void RefreshCommandEnablement()
    {
        _ribbon?.RefreshEnablement();
        _ribbon?.RefreshChecked();
    }

    /// <summary>
    /// Writes to the store, and says so when the write will not go.
    /// </summary>
    /// <remarks>
    /// The save paths in the five item modules had no error handling of their own, which left
    /// the crash handler to catch a failed write on the interface thread — and its whole job is
    /// to keep the window standing, so a locked database or a full disk became a line on a
    /// standard error stream that a desktop launch does not have. The window then closed as
    /// though the note had been saved. This is the missing half: the reader is told, the status
    /// line says it, and the exception carries on so that whatever was going to close on the
    /// strength of a successful save does not.
    /// </remarks>
    internal T Persisted<T>(string what, Func<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        try
        {
            return write();
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} could not be saved.", ex);

            if (DataContext is ShellViewModel shell) shell.StatusRight = $"{what} could not be saved.";

            // Fire and forget: this is on its way out through the crash handler, and awaiting a
            // dialog from a method that is about to throw would mean showing it after the throw.
            _ = Confirm.TellAsync(
                this,
                "Not saved",
                $"{what} could not be saved.\n\n{ex.Message}\n\nNothing you have written has been "
                + "lost — the window is still open. The log has the details.");

            throw;
        }
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
    /// <summary>
    /// The ribbon's Zoom button and the status bar's percentage share this one flow: the same
    /// dialog over the same <c>ZoomPercent</c>, so the two can never disagree about what the
    /// reading pane is doing. The button used to answer "not wired yet" while the figure below
    /// it worked, which the press sweep caught.
    /// </summary>
    /// <summary>
    /// Advanced Find: the dialog composes one query in the box's own grammar and the box runs
    /// it, so scope, highlighting and the results list are exactly the search the reader knows.
    /// </summary>
    private async void ShowAdvancedFind(ShellViewModel shell)
    {
        try
        {
            if (await MailAdvancedFindDialog.AskAsync(this) is not { Length: > 0 } query) return;

            shell.SearchText = query;
            Log.Info($"Harness: advanced find — “{query}”.");
        }
        catch (Exception ex)
        {
            // An async void handler: an exception here would land on the dispatcher unobserved.
            Log.Warn("The Advanced Find dialog failed.", ex);
        }
    }

    private async void ShowZoomDialog(ShellViewModel shell)
    {
        try
        {
            if (await ZoomDialog.AskAsync(this, shell.ZoomPercent) is not { } percent) return;

            shell.ZoomPercent = percent;
            Log.Info($"Harness: zoom {percent:0}%.");
        }
        catch (Exception ex)
        {
            // An async void handler: an exception here would land on the dispatcher unobserved.
            Log.Warn("The zoom dialog failed.", ex);
        }
    }

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
        ShellViewModel shell, SendReceiveGroup? group = null, bool retrying = false,
        TransferMode mode = TransferMode.SendAndReceive, string? folder = null)
    {
        if (_transferring) return;

        // Claimed here rather than after the accounts are gathered, because gathering them is
        // the first await: AccountConnectionsAsync reads a password per account out of the
        // keyring over D-Bus, which takes seconds and can put a prompt on the screen. Two
        // presses inside that window — F9 twice, F9 while the interval timer fires, the IDLE
        // watcher landing on a manual press — both used to get past a guard that had not been
        // set yet, and the second run would then overwrite _tasks and _cancellation and dispose
        // the token source the first was still using.
        _transferring = true;

        var accounts = InGroup(await AccountConnectionsAsync(), group);
        if (accounts.Count == 0)
        {
            // Released on the way out: everything below this point releases it in the finally,
            // and this is the one path that never reaches the try.
            _transferring = false;

            shell.StatusRight = group is null
                ? "No account is set up yet. File, Add Account."
                : $"No account in \u201c{group.Name}\u201d is set up.";
            return;
        }

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
            var filedBefore = App.Newsletters.Filed;

            var result = await Task.Run(() =>
                App.Transfer.RunAsync(accounts, DateTimeOffset.UtcNow, _cancellation.Token, mode, folder));

            _tasks.Finish(result);
            shell.StatusRight = result.Summary();
            shell.Refresh();
            ReportStuckOperations(shell);

            // A newsletter the arrival pipeline filed lands in its account's own RSS tree, and
            // the Feeds module reads a store of its own — so what this run filed is carried
            // across now, by the same copy-count-then-delete move that brought the tree over
            // originally, rather than waiting for the next launch.
            if (App.Newsletters.Filed > filedBefore && App.FeedStore?.Account is { } feedStore)
            {
                var carried = Mailbox.Store.FeedStoreMove.MoveAll(
                    feedStore, App.Accounts.All, Mailbox.Protocols.FeedReceiver.RootFolder);

                if (carried.DidAnything)
                {
                    Log.Info($"Feeds: {carried.Articles} filed newsletter issue(s) carried into the feeds store.");
                    _feedModule?.Reload();
                }
            }

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

            AnnounceArrival(result.Received);

            ShowRuleAlerts();

            // An account whose server-side rules could not be put on the server gets another try
            // now that the server has answered a poll.
            _ = SieveSync.RepublishStaleAsync();

            // Send/Receive is one button in the reference and it covers the calendars too, so the
            // DAV engine runs on the same press rather than on a second one. Send All and
            // Update Folder are narrower presses by definition and do not drag the whole world
            // along with them — a reader checking one folder did not ask for every calendar and
            // every feed as well.
            if (mode == TransferMode.SendAndReceive)
            {
                await SyncCalendarsAsync(shell, _cancellation.Token);

                // And the feeds, for the same reason: the reference checks a subscription once per
                // download interval, which is this press.
                await PollFeedsAsync(shell, _cancellation.Token);
            }
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
    /// Update Folder: check the folder in front of the reader, and nothing else.
    /// </summary>
    /// <remarks>
    /// Scoped to the folder's <em>own</em> account rather than every account, which is the whole
    /// point of the button — a reader looking at one folder and asking whether anything has
    /// arrived in it is asking about that folder. IMAP only: POP3 downloads into one folder and
    /// has no notion of any other, so the button says that rather than running a whole poll under
    /// a narrower name.
    /// </remarks>
    private async Task UpdateFolderAsync(ShellViewModel shell)
    {
        if (shell.CurrentFolder is not { } folder || shell.CurrentAccountForCategories() is not { } account)
        {
            shell.StatusRight = "Choose a folder to update.";
            return;
        }

        if (account.Account.Protocol != MailProtocol.Imap)
        {
            shell.StatusRight = $"{account.Account.Address} is a POP3 account, which has only its delivery folder — use Send/Receive.";
            Log.Info($"Update Folder: {account.Account.Address} is POP3; nothing folder-scoped to do.");
            return;
        }

        Log.Info($"Update Folder: {folder.Name} on {account.Account.Address}.");

        // A group of one, made here rather than looked up: this is "that account" rather than any
        // group the reader has defined, and the run's own filter already speaks that language.
        await SendReceiveAsync(
            shell,
            new SendReceiveGroup { Name = account.Account.Address, Accounts = [account.Account.Address] },
            mode: TransferMode.Folder,
            folder: folder.Name);
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

    private async Task BackstageActionAsync(string action)
    {
        // The exports act on what the shell is showing — the selection, the open folder — so
        // they route here rather than through the shared actions, which have no shell.
        if (DataContext is ShellViewModel shell)
        {
            switch (action)
            {
                // Print, from the page rather than the bar. Each closes the Backstage first,
                // because what they open is a window over the shell and the reference's page
                // closes behind its own Print button too.
                case "print.message": CloseBackstage(); RunCommand(MailCommands.Print.Id); return;
                case "print.list": CloseBackstage(); RunCommand(MailCommands.PrintList.Id); return;
                case "print.pdf": CloseBackstage(); RunCommand(MailCommands.PrintToPdf.Id); return;

                case "options.general": CloseBackstage(); await ShowOptions("general"); return;

                case "export.eml": CloseBackstage(); await ExportEmlAsync(shell); return;
                case "export.mbox": CloseBackstage(); await ExportMboxAsync(shell); return;
                case "export.ics": CloseBackstage(); await ExportIcsAsync(shell); return;
                case "export.vcf": CloseBackstage(); await ExportVcfAsync(shell); return;
            }
        }

        await BackstageActions.RunAsync(BackstageContext(), action);
    }

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
    /// These belong in the command catalogue so a shortcut editor can one day rebind them.
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

        // Esc leaves full-screen mode first: a window with no caption has no other way out with
        // the mouse, and that has to beat every other meaning Escape has.
        if (e.Key is Avalonia.Input.Key.Escape && WindowState == WindowState.FullScreen)
        {
            ToggleFullScreen(shell);
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

            // And the release, because a keystroke is two events and some of what the window
            // does happens on the second: Alt on its own opens the KeyTip traversal from
            // OnKeyUp — deliberately, so that reaching for Alt+Tab does not badge the ribbon —
            // so a pose that only pressed the key down could never open it at all.
            at.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent,
                Source = at,
                Key = pressed,
                KeyModifiers = Keystroke.Modifiers(chord.Modifiers),
            });
        }

        if (DataContext is not ShellViewModel shell) return;

        var row = shell.SelectedMessage;
        var after = row is null ? null : shell.SummaryOf(row);
        var focused = FocusManager?.GetFocusedElement() as Control;
        // The module belongs in this line because Ctrl+1..Ctrl+9 are the shell's own keys and
        // nothing else in a run says which module the press left the window in: the rail's mark
        // moves, the workspace changes and the ribbon is replaced, all of which are pictures.
        Log.Info($"Harness: after {key} — module {shell.Module}, "
            + $"focus on {focused?.Name ?? focused?.GetType().Name ?? "nothing"}, "
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
            pane.MaxWidth = RoomForList(grid);
        }
        else
        {
            pane.ClearValue(WidthProperty);
            pane.ClearValue(MaxWidthProperty);
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
    /// The narrowest the reading pane is left at before the list is the one that gives way.
    /// </summary>
    /// <remarks>
    /// Authored, and the number is what its own header needs: three reply buttons, the sender
    /// and a date on one line. Below it the pane is a stripe rather than a pane, and a stripe is
    /// worth less than the width it costs the list beside it.
    /// </remarks>
    private const double MinReadingPaneWidth = 220;

    /// <summary>
    /// The widest the list may be beside a reading pane: its own width while there is room for
    /// one, and a share of what is left when there is not.
    /// </summary>
    /// <remarks>
    /// The list's width is a fixed number from a token and the reading pane's column is the only
    /// star in the row, so as a window narrowed the reading pane took every pixel of the squeeze
    /// and then the To-Do Bar was pushed out of the window entirely — at 900 wide the bar's
    /// right-hand 86px, its close button among them, were past the edge, and at 760 (the
    /// window's own minimum) 226px were. A Grid does not shrink an Auto column to make an
    /// overflow fit, so the panes have to be told.
    /// <para>
    /// Everything except the two panes is measured rather than assumed — the folder pane can be
    /// collapsed, the bar can be off — so this is the room those two actually have, whatever
    /// else is on. Infinity before the first arrange, when there is nothing to measure yet; the
    /// pane grid's own SizeChanged brings it back.
    /// </para>
    /// </remarks>
    private static double RoomForList(Grid grid)
    {
        if (grid.Bounds.Width <= 0) return double.PositiveInfinity;

        var taken = 0.0;
        for (var column = 0; column < grid.ColumnDefinitions.Count; column++)
        {
            if (column is 3 or 5) continue;
            taken += grid.ColumnDefinitions[column].ActualWidth;
        }

        var shared = grid.Bounds.Width - taken;
        if (shared <= 0) return 0;

        // Half at the narrowest, so the floor above cannot itself be what pushes the list out.
        return Math.Max(0, shared - Math.Min(MinReadingPaneWidth, shared / 2));
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
        if (List is not { } list) return;

        // The list's width decides whether the Compact view draws the card or the line — the
        // reference's "use compact layout in widths smaller than N characters".
        list.SizeChanged += (_, e) => shell.ListWidth = e.NewSize.Width;
        shell.ListWidth = list.Bounds.Width;

        // What is selected decides what is usable, so the bar is re-read whenever it changes.
        // On the list rather than on the view model's SelectedRow: adding a second row to the
        // selection with Control leaves SelectedRow where it was, and the difference between
        // one row and two is exactly what the single-selection commands turn on.
        list.SelectionChanged += (_, _) => RefreshCommandEnablement();

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

        // And again whenever the panes have a different amount of room: how wide the list may be
        // depends on how much is left for the reading pane beside it, and that is a fact about
        // the window's width. Without this the fit is whatever it was when the window opened.
        //
        // The To-Do Bar is the second trigger and not an obvious one. It is built after the
        // first layout, so the fit made at startup was made in a window that did not yet have a
        // bar in it — it reserved room for a pane that was about to lose 255px to one, and the
        // bar went out of the window anyway. Putting the bar in does not change the grid's own
        // size, so only the bar's own can say it happened.
        if (this.FindControl<Grid>("PaneGrid") is { } paneGrid)
        {
            paneGrid.SizeChanged += (_, _) => FitListPane(shell);
        }

        if (this.FindControl<ContentControl>("DockHost") is { } dock)
        {
            dock.SizeChanged += (_, _) => FitListPane(shell);
        }

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

        // Why a right-click did or did not become a menu. Three things decide it, and none of
        // them can be seen from outside the event: which element the release landed on, whether
        // something had already handled it, and which button began the press — Avalonia raises
        // ContextRequested only from the element that is the source of an unhandled release
        // whose press was the right button. Debug level, so the diagnostics launcher has it and
        // an ordinary run does not.
        list.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            var point = e.GetCurrentPoint(list).Properties;
            Log.Debug($"List press: {e.Source?.GetType().Name ?? "nothing"}, "
                      + $"right: {point.IsRightButtonPressed}, handled: {e.Handled}.");
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        list.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            Log.Debug($"List release: {e.Source?.GetType().Name ?? "nothing"}, "
                      + $"began with {e.InitialPressMouseButton}, handled: {e.Handled}.");
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        list.AddHandler(ContextRequestedEvent, (object? _, ContextRequestedEventArgs e) =>
        {
            Log.Debug($"List context requested from {e.Source?.GetType().Name ?? "nothing"}, "
                      + $"handled: {e.Handled}.");
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // Right-click: the reference's menu over a message, in its order, over the selection.
        // Every entry runs the same command the ribbon button does, so a thing done from here
        // is the thing done from there. Entries whose command is not built yet say what they
        // wait for, the way the ribbon's do.
        //
        // Shown from the request rather than hung on ContextFlyout, because the menu is a
        // different menu every time — what is selected decides which entries are usable — and a
        // flyout that is filled after the popup has measured comes out empty. One menu per
        // right-click, built and then shown, which is the shape every other menu here has.
        list.AddHandler(ContextRequestedEvent, (object? _, ContextRequestedEventArgs e) =>
        {
            if (e.Handled) return;

            _rowMenu = RowMenu(shell);
            MenuProbe.Show("the row menu", _rowMenu, list, atPointer: true);
            e.Handled = true;
        });

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

    /// <summary>
    /// The list, found once and kept.
    /// </summary>
    /// <remarks>
    /// Enablement asks what is selected for every drawn control on the bar — a hundred of them —
    /// each time the selection changes, and a tree search per question is a hundred tree walks
    /// per keystroke in the list. Not the generated <c>MessageList</c> property: it resolves
    /// through a name scope and answered null from here, which made every command think nothing
    /// was selected.
    /// </remarks>
    private ListBox? _list;

    private ListBox? List => _list ??= this.FindControl<ListBox>("MessageList");

    /// <summary>What the list has highlighted, headers excluded.</summary>
    private IReadOnlyList<ViewModels.MessageRow> SelectedRows()
    {
        if (List?.SelectedItems is not { } selected) return [];

        return [.. selected.OfType<ViewModels.MessageRow>()];
    }

    private async Task ConfirmPermanentDeleteAsync(ShellViewModel shell,
        IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        var confirmed = await Confirm.AskBeforePermanentDeleteAsync(
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
        RefreshCommandChecked();
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
