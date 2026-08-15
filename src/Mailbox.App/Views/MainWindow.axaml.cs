using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
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
using Mailbox.Security;
using Mailbox.Store;

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
                if (module == MailboxModule.Calendar) TogglePeek();
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
    private static void ApplyHarnessState(ShellViewModel shell)
    {
        // Which message the reading pane is showing. The bars above it only appear for certain
        // mail — one with a tracking pixel, one pretending to be someone else — and a capture
        // cannot click a row to find one.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } subject)
        {
            var match = shell.Messages.FirstOrDefault(
                m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase));

            if (match is not null) shell.SelectedMessage = match;
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

        shell.StatusRight = $"{command.Label} — not wired yet ({command.Id})";
    }

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
