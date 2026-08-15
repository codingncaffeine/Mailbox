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
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
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

        _ribbon = new RibbonView(App.Commands, layout)
        {
            DisplayMode = Environment.GetEnvironmentVariable("MAILBOX_RIBBON")?.ToLowerInvariant()
                switch
                {
                    "classic" => RibbonDisplayMode.Classic,
                    "collapsed" or "revealed" => RibbonDisplayMode.Collapsed,
                    _ => RibbonDisplayMode.Simplified,
                },
        };
        _ribbon.CommandInvoked += OnRibbonCommand;
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        this.FindControl<ContentControl>("RibbonHost")!.Content = _ribbon;

        // The rendering diagnostics the text investigation needs go to the log, not the status
        // bar. In the reference the bar carries the item counts and nothing else.
        Log.Info($"Text rendering: {TextRendering.Describe()}");
        Log.Info($"UI font: {App.Fonts.Resolve("Segoe UI").Rendered}");
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
        WireRail(shell);
        WireWindowMenu();
        WireToolbarCommands(shell);
        WireAccountButton(shell);
        WireArrangeMenu(shell);
        WireListInteraction(shell);
        WireReadingPane(shell);
        this.FindControl<ContentControl>("UndoSendHost")!.Content = _undoSend;
        WireSchedule(shell);
        DataContext = shell;

        ApplyHarnessState(shell);

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

        // Which folder is open. Set after the window opens rather than with the rest of the
        // posed state: the folder pane's list pushes its own selection back as it binds, so a
        // folder chosen in the constructor is overwritten the moment it lays out.
        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDER") is { Length: > 0 } wanted)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is not ShellViewModel s) return;

                    var match = s.Folders.FirstOrDefault(
                        f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

                    Log.Info(match is null
                        ? $"No folder matching '{wanted}' in: {string.Join(", ", s.Folders.Select(f => f.Name))}"
                        : $"Harness: opening the {match.Name} folder.");

                    if (match is not null) s.SelectedFolder = match;
                },
                DispatcherPriority.Loaded);
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

            var dialog = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)
                ?.Windows.FirstOrDefault(w => !ReferenceEquals(w, this));

            if (dialog is not null)
            {
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
            case "backstage": Opened += (_, _) => ShowBackstage(); break;

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

            // The dialogs behind the Backstage's menus, which otherwise take three clicks to
            // reach and so have never been photographed.
            case "accounts":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new AccountSettingsDialog().ShowDialog(this);
                };
                break;

            case "cleanup":
                Opened += async (_, _) =>
                {
                    CaptureNextWindow();
                    await new MailboxCleanupDialog().ShowDialog(this);
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
                    await new RulesAndAlertsDialog().ShowDialog(this);
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
                Opened += (_, _) =>
                {
                    var tasks = new SendReceiveTasks(["you@example.com", "other@example.com"]);
                    tasks.Report(new PollProgress("you@example.com", 0, 0, "Sending"));
                    tasks.Report(new PollProgress("you@example.com", 0, 0, "Connecting"));
                    tasks.Report(new PollProgress("you@example.com", 3, 8, "Downloading"));
                    tasks.Report(new PollProgress("other@example.com", 0, 0, "Sending"));

                    CaptureNextWindow();
                    new SendReceiveProgressDialog(tasks, App.Settings, () => { }).Show(this);
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
                var compose = new ComposeWindow(App.Commands, App.Accounts);
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
                    }, DispatcherPriority.Background);
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
    private void NewMessage()
    {
        var compose = new ComposeWindow(App.Commands, App.Accounts);

        // "Always use the default account when composing new messages" is off by default, and
        // off means what the reference means: a message written while looking at the work
        // account's inbox comes from the work account.
        if (!App.MailOptions.AlwaysUseDefaultAccount
            && DataContext is ShellViewModel current
            && current.CurrentAddress is { Length: > 0 } address)
        {
            compose.SendFromAccount(address);
        }

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

            var compose = new ComposeWindow(App.Commands, App.Accounts);
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

    private void ShowSelectedMessage(ShellViewModel shell)
    {
        if (_reading is null) return;

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

        _attachments.Show(message);
        _reading.Show(message, shell.SelectedMessage?.Body ?? string.Empty, Verified(shell));
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
            var compose = new ComposeWindow(App.Commands, App.Accounts);
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

    private CalendarPeek? _floatingPeek;

    /// <summary>
    /// Gives each rail module a command. Calendar toggles the peek; the rest are inert until
    /// their modules exist.
    /// </summary>
    private void WireRail(ShellViewModel shell)
    {
        foreach (var tab in shell.Modules)
        {
            var module = tab.Module;
            tab.Activate = new RelayCommand(() =>
            {
                // Mail is where this is; Calendar has its peek. The rest are whole modules in
                // Part IV, and a button that says so is better than one that does nothing.
                switch (module)
                {
                    case MailboxModule.Mail: break;
                    case MailboxModule.Calendar: TogglePeek(); break;
                    default:
                        shell.StatusRight = module switch
                        {
                            MailboxModule.People => "People arrives with Phase 12.",
                            MailboxModule.Tasks or MailboxModule.Notes or MailboxModule.Journal
                                => $"{module} arrives with Phase 13.",
                            _ => $"{module} is Phase 14, with the rest of the shell.",
                        };
                        break;
                }
            });
        }
    }

    private void TogglePeek()
    {
        if (_floatingPeek is not null)
        {
            ClosePeek();
            return;
        }

        var peek = new CalendarPeek(DateTime.Now, docked: false);
        peek.DockRequested += (_, _) => DockPeek();

        // Anchored just right of the rail, near the icon that opened it — the position
        // the reference application uses so the peek reads as belonging to that module.
        Canvas.SetLeft(peek, 52);
        Canvas.SetTop(peek, 8);

        this.FindControl<Canvas>("PeekLayer")!.Children.Add(peek);
        _floatingPeek = peek;
    }

    private void ClosePeek()
    {
        if (_floatingPeek is null) return;
        this.FindControl<Canvas>("PeekLayer")!.Children.Remove(_floatingPeek);
        _floatingPeek = null;
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
    /// The little corner button: the floating peek becomes a docked panel down the right edge,
    /// where it takes the reading pane's place until closed.
    /// </summary>
    private void DockPeek()
    {
        ClosePeek();
        if (DataContext is not ShellViewModel shell) return;

        var docked = new CalendarPeek(DateTime.Now, docked: true);
        docked.CloseRequested += (_, _) => UndockPeek();

        this.FindControl<ContentControl>("DockHost")!.Content = docked;
        shell.IsCalendarDocked = true;
    }

    private void UndockPeek()
    {
        if (DataContext is not ShellViewModel shell) return;
        this.FindControl<ContentControl>("DockHost")!.Content = null;
        shell.IsCalendarDocked = false;
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
        // Which message the reading pane is showing. The bars above it only appear for certain
        // mail — one with a tracking pixel, one pretending to be someone else — and a capture
        // cannot click a row to find one.
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

        var wanted = Environment.GetEnvironmentVariable("MAILBOX_STATE");
        if (string.IsNullOrWhiteSpace(wanted)) return;

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
                case "zoom-in": shell.ZoomIn.Execute(null); break;
                case "zoom-out": shell.ZoomOut.Execute(null); break;
                default: Log.Warn($"Unknown MAILBOX_STATE: {state}"); break;
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
        button.Flyout = flyout;
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
            Opened += (_, _) => caption.ForceHover(hovered.ToLowerInvariant());
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
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_floatingRibbon is null || e.Source is not Visual source) return;
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
        if (id == MailCommands.WorkOffline.Id) { ToggleWorkOffline(shell); return; }
        if (id == MailCommands.NewEmail.Id) { NewMessage(); return; }
        if (id == ViewCommands.ShowProgress.Id) { ShowProgressDialog(shell); return; }
        if (id == MailCommands.ViewSource.Id) { ShowMessageSource(shell); return; }
        if (id == MailCommands.TrackerReport.Id) { _ = _reading?.ShowTrackerReportAsync(); return; }
        if (id == MailCommands.AuthenticationDetails.Id) { _ = _reading?.ShowAuthenticationAsync(); return; }
        if (id == MailCommands.Print.Id) { PrintMessage(shell); return; }
        if (id == MailCommands.PrintToPdf.Id) { _ = PrintToPdfAsync(shell); return; }
        if (id == MailCommands.PrintList.Id) { PrintList(shell); return; }
        if (id == ViewCommands.CancelAll.Id) { CancelTransfer(); return; }
        if (id == ViewCommands.SendReceiveGroups.Id) { ShowGroupsMenu(shell); return; }

        if (id == MailCommands.Reply.Id) { Respond(shell, ReplyKind.Reply); return; }
        if (id == MailCommands.ReplyAll.Id) { Respond(shell, ReplyKind.ReplyAll); return; }
        if (id == MailCommands.Forward.Id) { Respond(shell, ReplyKind.Forward); return; }

        if (RunOverSelection(shell, id)) return;
        if (RunViewCommand(shell, id)) return;

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
    private void Respond(ShellViewModel shell, ReplyKind kind)
    {
        if (_openMessage is not { } original)
        {
            shell.StatusRight = "Select a message to reply to.";
            return;
        }

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

        // The account the message arrived in, which is what a reply means.
        var address = shell.CurrentAddress;

        // The reference grows the reply where the message is; the Options page's "Open replies
        // and forwards in a new window" is the switch back to a separate window. An inline reply
        // is already open — a reply to a reply — reuses the strip rather than stacking a second.
        if (App.MailOptions.OpenRepliesInNewWindow)
        {
            OpenReplyWindow(shell, draft, kind, address);
        }
        else
        {
            OpenInlineReply(shell, draft, kind, address);
        }
    }

    private void OpenReplyWindow(ShellViewModel shell, ReplyDraft draft, ReplyKind kind, string? address)
    {
        var compose = new ComposeWindow(App.Commands, App.Accounts);

        if (address is { Length: > 0 }) compose.SendFromAccount(address);

        compose.Prefill(draft, kind);
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
    private void OpenInlineReply(ShellViewModel shell, ReplyDraft draft, ReplyKind kind, string? address)
    {
        // A reply already open — a reply to a reply, or Forward pressed twice — is dismissed
        // first, so there is one inline surface at a time rather than a stack nobody asked for.
        if (_inlineCompose is not null) CloseInlineCompose(shell);

        var surface = new ComposeSurface(App.Commands, App.Accounts);
        if (address is { Length: > 0 }) surface.SendFromAccount(address);
        surface.Prefill(draft, kind);

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
        if (id == MailCommands.Junk.Id) { shell.MarkJunk(rows); return true; }

        // The reference's Unread/Read button toggles: unread if the selection is all read, read
        // otherwise. Same for the flag.
        if (id == MailCommands.Unread.Id) { shell.SetRead(rows, read: rows.Any(r => r.IsUnread)); return true; }
        if (id == MailCommands.FollowUp.Id) { shell.SetFlagged(rows, flagged: rows.Any(r => !r.IsFlagged)); return true; }

        if (id == MailCommands.Categorize.Id) { ShowCategorizeMenu(shell, rows); return true; }
        if (id == MailCommands.MoveTo.Id || id == ViewCommands.MoveToQuick.Id) { ShowMoveMenu(shell, rows); return true; }
        if (id == MailCommands.Rules.Id) { _ = new RulesAndAlertsDialog().ShowDialog(this); return true; }
        if (id == MailCommands.NewItems.Id) { ShowNewItemsMenu(); return true; }
        if (id == MailCommands.FilterEmail.Id) { ShowFilterMenu(shell); return true; }

        return false;
    }

    /// <summary>The View tab's toggles that have state behind them.</summary>
    private static bool RunViewCommand(ShellViewModel shell, CommandId id)
    {
        if (id == ViewCommands.ReverseSort.Id) { shell.SortDescending = !shell.SortDescending; return true; }
        if (id == ViewCommands.TighterSpacing.Id) { shell.CompactRows = !shell.CompactRows; return true; }

        return false;
    }

    /// <summary>The Categorize menu: the account's six, ticked where the whole selection has one.</summary>
    private void ShowCategorizeMenu(ShellViewModel shell, IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var flyout = new MenuFlyout();
        var categories = shell.Categories();

        if (categories.Count == 0 || rows.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = rows.Count == 0 ? "Select a message first" : "No categories are defined",
                IsEnabled = false,
            });
        }

        foreach (var category in categories)
        {
            var item = new MenuItem
            {
                Header = category.Name,
                Icon = CategorySwatch(category.ColourToken),
            };

            if (shell.AllHave(rows, category)) item.Icon = Tick();

            var chosen = category;
            item.Click += (_, _) => shell.ToggleCategory(rows, chosen);
            flyout.Items.Add(item);
        }

        if (categories.Count > 0 && rows.Count > 0)
        {
            flyout.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear All Categories" };
            clear.Click += (_, _) => shell.ClearCategories(rows);
            flyout.Items.Add(clear);
        }

        // Creating, renaming and recolouring one is Phase 8, and the entry says so rather than
        // being missing — it is where the reference puts the way in.
        flyout.Items.Add(new Separator());
        var all = new MenuItem { Header = "All Categories…", IsEnabled = false };
        ToolTip.SetTip(all, "Managing categories is Phase 8.");
        flyout.Items.Add(all);

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
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
        var flyout = new MenuFlyout();

        var mail = new MenuItem { Header = "Email Message" };
        mail.Click += (_, _) => NewMessage();
        flyout.Items.Add(mail);
        flyout.Items.Add(new Separator());

        foreach (var (label, phase) in new[]
                 {
                     ("Appointment", "Phase 11"), ("Meeting", "Phase 11"),
                     ("Contact", "Phase 12"), ("Task", "Phase 13"), ("Note", "Phase 13"),
                 })
        {
            var item = new MenuItem { Header = label, IsEnabled = false };
            ToolTip.SetTip(item, $"{label}s arrive with {phase}.");
            flyout.Items.Add(item);
        }

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Filter Email: the one filter the list has, and the rest named for what they wait on.</summary>
    private void ShowFilterMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        var unread = new MenuItem { Header = "Unread", Icon = shell.UnreadOnly ? Tick() : null };
        unread.Click += (_, _) => shell.UnreadOnly = !shell.UnreadOnly;
        flyout.Items.Add(unread);

        foreach (var label in new[] { "Has Attachments", "Flagged", "Important", "Categorized", "This Week" })
        {
            var item = new MenuItem { Header = label, IsEnabled = false };
            ToolTip.SetTip(item, "Phase 8 — the search refiners.");
            flyout.Items.Add(item);
        }

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
            flyout.Items.Add(new Separator());

            Command("Rules…", MailCommands.Rules.Id);
            Command("Move…", MailCommands.MoveTo.Id);
            Command("Ignore", MailCommands.Ignore.Id, works: false, "Phase 8 — Ignore Conversation");
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

        Opened += (_, _) =>
        {
            foreach (var target in AccountConnections()
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
    private async Task SendReceiveAsync(ShellViewModel shell, SendReceiveGroup? group = null)
    {
        if (_transferring) return;

        var accounts = InGroup(AccountConnections(), group);
        if (accounts.Count == 0)
        {
            shell.StatusRight = group is null
                ? "No account is set up yet. File, Add Account."
                : $"No account in \u201c{group.Name}\u201d is set up.";
            return;
        }

        _transferring = true;

        _tasks = new SendReceiveTasks(accounts.Select(a => a.Connection.Address));
        _cancellation = new CancellationTokenSource();
        ShowProgressDialog();

        void OnProgress(object? _, PollProgress p) => Dispatcher.UIThread.Post(() =>
        {
            shell.StatusRight = $"{p.Stage} {p.Account}…";
            _tasks?.Report(p);
            _progress?.Refresh();
        });

        App.Transfer.Progress += OnProgress;

        try
        {
            var result = await Task.Run(() =>
                App.Transfer.RunAsync(accounts, DateTimeOffset.UtcNow, _cancellation.Token));

            _tasks.Finish(result);
            shell.StatusRight = result.Summary();
            shell.Refresh();
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

            _progress?.Refresh();

            // A run that worked has nothing left to say, so the dialog goes when it does. One
            // that did not is the reason the dialog has an Errors tab, and stays.
            if (_tasks.Errors.Count == 0) CloseProgressDialog();

            _cancellation.Dispose();
            _cancellation = null;
        }
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

        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ShellViewModel shell) return;

        var control = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        var rows = SelectedRows();

        switch (e.Key)
        {
            case Avalonia.Input.Key.F9:
                _ = SendReceiveAsync(shell);
                break;

            case Avalonia.Input.Key.F5:
                shell.Refresh();
                break;

            // Delete goes to Deleted Items; Shift+Delete asks first, because with POP3 the
            // store may be the only copy left.
            case Avalonia.Input.Key.Delete when shift:
                _ = ConfirmPermanentDeleteAsync(shell, rows);
                break;

            case Avalonia.Input.Key.Delete:
                shell.Delete(rows, permanently: false);
                break;

            case Avalonia.Input.Key.Q when control:
                shell.SetRead(rows, read: true);
                break;

            case Avalonia.Input.Key.U when control:
                shell.SetRead(rows, read: false);
                break;

            case Avalonia.Input.Key.G when control && shift:
                shell.SetFlagged(rows, flagged: rows.Any(r => !r.IsFlagged));
                break;

            case Avalonia.Input.Key.I when control && shift:
                shell.GoTo(FolderRole.Inbox);
                break;

            case Avalonia.Input.Key.O when control && shift:
                shell.GoTo(FolderRole.Outbox);
                break;

            default:
                return;
        }

        e.Handled = true;
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

    private void ToggleWorkOffline(ShellViewModel shell)
    {
        App.Transfer.SetWorkOffline(!App.Transfer.WorkOffline, AccountConnections());
        shell.StatusRight = App.Transfer.WorkOffline ? "Working offline." : "Working online.";
    }

    /// <summary>
    /// Turns the open accounts into something the transfer service can use, pulling each
    /// password out of the keyring as late as possible. An account whose servers were never
    /// filled in is skipped rather than attempted against an empty hostname.
    /// </summary>
    private static List<TransferTarget> AccountConnections()
    {
        var targets = new List<TransferTarget>();

        foreach (var open in App.Accounts.All)
        {
            var settings = AccountSettings.Load(App.Settings, open.Account.Address);
            if (settings is null) continue;

            targets.Add(new TransferTarget(
                settings.ToConnection(open.Account, App.Secrets), open.Mail));
        }

        return targets;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
