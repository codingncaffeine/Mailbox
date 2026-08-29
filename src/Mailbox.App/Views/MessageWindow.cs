using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Rendering;
using Mailbox.Security;
using Mailbox.Store;
using Mailbox.Theming;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>Which stored row an opened message is, so the window's buttons can act on it.</summary>
public sealed record OpenedMessageContext(string Address, long MessageId, long FolderId);

/// <summary>
/// One message in a window of its own, as double-clicking a row opens it: the read ribbon over
/// the reference's own header, the attachment strip, and the same body the reading pane renders.
/// </summary>
/// <remarks>
/// The third ribbon host, after the shell's and the compose window's, carrying
/// <see cref="MessageRibbonLayout"/> — transcribed from the reference's message window. The
/// window acts on its own stored row (<see cref="OpenedMessageContext"/>), never on the shell's
/// selection: the reader can select something else behind this window, and Delete here must not
/// delete that. What needs the shell — replying, stepping to the next message, running a Quick
/// Step — is raised as an event, because those build drafts and sequences only the shell owns.
/// </remarks>
public sealed class MessageWindow : Window
{
    private readonly ReadingPaneBody _body;
    private readonly AttachmentStrip _attachments = new();
    private readonly Func<MailRepository?> _mail;
    private readonly RibbonView _ribbon;
    private Button? _moreButton;

    private MimeMessage _message;
    private byte[]? _raw;
    private OpenedMessageContext? _context;

    private TextBlock _caption = null!;
    private ContentControl _backstage = null!;
    private Canvas _floatLayer = null!;
    private Control? _floatingRibbon;

    private readonly TextBlock _subject;
    private readonly TextBlock _from;
    private readonly TextBlock _to;
    private readonly TextBlock _sent;
    private readonly TextBlock _initial;

    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private DispatcherTimer? _noticeTimer;

    /// <summary>The message's own zoom, applied to the body and nothing around it.</summary>
    private double _zoomPercent = 100;

    /// <summary>Reply, Reply All or Forward pressed here: the shell builds the draft.</summary>
    public event EventHandler<ReplyKind>? RespondRequested;

    /// <summary>The QAT's arrows: step to the previous (-1) or next (+1) message.</summary>
    public event EventHandler<int>? StepRequested;

    /// <summary>Something here changed the store, so the shell's list should look again.</summary>
    public event EventHandler? Changed;

    /// <summary>A Quick Step pressed here, by the command it is placed as.</summary>
    public event EventHandler<CommandId>? QuickStepRequested;

    /// <summary>The message on show — after stepping, not necessarily the one it opened on.</summary>
    public MimeMessage Current => _message;

    /// <summary>The stored row on show, or null for a message with no row behind it.</summary>
    public OpenedMessageContext? Context => _context;

    /// <summary>
    /// The header fields this message carries inside its cryptography, when it does — what a
    /// reply must be addressed from (RFC 9788), and only this window's pane has opened it.
    /// </summary>
    public ProtectedHeaders? Covered => _body.Protected;

    /// <summary>The shell's way to say something in this window's own notice strip.</summary>
    public void Say(string text) => Notice(text);

    /// <summary>
    /// Accept, Tentative or Decline pressed on this window's invitation bar.
    /// </summary>
    /// <remarks>
    /// Forwarded rather than handled, for the reason Reply is: the reply to an invitation is a
    /// message and a window does not own an outbox. Only the shell's own pane used to be
    /// listened to, so answering a meeting request in a window wrote the appointment and queued
    /// nothing — and with the reading pane off, a window is the only place the bar appears at
    /// all, which made it the ordinary way to answer rather than an unusual one.
    /// </remarks>
    public event EventHandler<InvitationBar.Answer>? InvitationAnswered;

    /// <summary>The bar's Remove from Calendar, pressed in this window.</summary>
    public event EventHandler? InvitationRemoved;

    public MessageWindow(
        ThemeService themes, Func<MailRepository?> mail, MimeMessage message, byte[]? raw,
        DkimResult? verified = null, OpenedMessageContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        _mail = mail;
        _message = message;
        _raw = raw;
        _context = context;

        Title = TitleOf(message.Subject);
        Width = 1160;
        Height = 800;
        MinWidth = 640;
        MinHeight = 420;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowFrame.Apply(this);
        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);
        TextRendering.Apply(this);

        _body = new ReadingPaneBody(themes, mail);

        _body.InvitationAnswered += (_, answer) => InvitationAnswered?.Invoke(this, answer);
        _body.InvitationRemoved += (_, _) => InvitationRemoved?.Invoke(this, EventArgs.Empty);

        // The engine goes when the window does. Reading mail by double-clicking is the ordinary
        // gesture, so without this a morning's reading is a morning's worth of web processes.
        Closed += (_, _) => _body.Dispose();
        _subject = Line(20, "text.primary.brush");
        _from = Line(null, "text.primary.brush");
        _to = Line(null, "text.secondary.brush");
        _sent = Line(null, "text.secondary.brush");
        _initial = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 15,
        };
        Bind(_initial, TextBlock.ForegroundProperty, "avatar.foreground.brush");

        _noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Bind(_noticeText, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        _notice = new Border
        {
            Child = _noticeText,
            Padding = new Thickness(12, 6),
            IsVisible = false,
        };
        Bind(_notice, Border.BackgroundProperty, "reading.infobar.background.brush");

        // The read ribbon, with the Quick Steps gallery filled exactly as the shell's is.
        _ribbon = new RibbonView(App.Commands, QuickStepsRibbon.Inject(MessageRibbonLayout.Layout, App.QuickSteps.All))
        {
            CommandEnabled = IsCommandEnabled,
        };
        RibbonDisplayMemory.Wire(_ribbon, RibbonWindow.Message, Environment.GetEnvironmentVariable("MAILBOX_RIBBON"));
        _ribbon.CommandInvoked += (_, e) =>
        {
            _ribbon.CloseFloatingBody();
            OnCommand(e.Command, e.FromChevron);
        };
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();
        _ribbon.MenuOpened += (id, menu) => MenuProbe.Record($"the menu under {id.Value}", menu);
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Content = BuildRoot();

        _body.HeaderChanged += (_, _) => Correct();

        ShowMessage();
    }

    /// <summary>
    /// Puts another message in this window — the QAT's Previous and Next arrows, which step
    /// through the folder without opening a window per press.
    /// </summary>
    public void Replace(MimeMessage message, byte[]? raw, DkimResult? verified, OpenedMessageContext? context)
    {
        ArgumentNullException.ThrowIfNull(message);

        _message = message;
        _raw = raw;
        _context = context;
        ShowMessage(verified);
    }

    private void ShowMessage(DkimResult? verified = null)
    {
        Title = TitleOf(_message.Subject);
        _caption.Text = Title;

        _subject.Text = _message.Subject ?? string.Empty;
        _from.Text = Sender();
        _to.Text = "To " + _message.To;
        _sent.Text = (_message.Date == default ? DateTimeOffset.Now : _message.Date.ToLocalTime())
            .ToString("ddd M/d/yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        _initial.Text = Initial();

        // The row identity, for a plugin's info-bar provider; the envelope alone otherwise.
        _body.PluginSummary = _context is { } c
            ? new Mailbox.Plugins.Api.PluginMessageSummary(
                c.Address, c.MessageId, c.FolderId, _message.Subject ?? string.Empty,
                Sender(), _message.Date, IsRead: true)
            : null;

        _body.MessageFontSize = 14.5 * (_zoomPercent / 100d);
        _attachments.Show(_message);
        _body.Show(_message, _message.TextBody ?? string.Empty, verified);
        _ = _body.ApplySenderPolicyAsync();
    }

    private static string TitleOf(string? subject)
        => (string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject) + " - Message (HTML)";

    private string Sender()
        => _message.From.Mailboxes.FirstOrDefault() is { } box
            ? box.Address
            : _message.From.ToString();

    private string Initial()
    {
        var name = _message.From.Mailboxes.FirstOrDefault()?.Name is { Length: > 0 } display
            ? display
            : Sender();
        return name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
    }

    /// <summary>Takes the pane's word for the subject and the sender, when it has one (RFC 9788).</summary>
    private void Correct()
    {
        if (_body.HeaderSubject is { } subject)
        {
            _subject.Text = subject;
            Title = TitleOf(subject);
            _caption.Text = Title;
        }

        if (_body.HeaderFrom is { } from) _from.Text = from;
    }

    // ---- Commands ------------------------------------------------------------------------------

    /// <summary>Acting on a message needs one; only the row-free announcements do not.</summary>
    private bool IsCommandEnabled(CommandId id)
        => _context is not null
           || id == ViewCommands.Zoom.Id
           || id == ViewCommands.FindInMessage.Id
           || id == ViewCommands.ReadAloud.Id
           || id == ViewCommands.ImmersiveReader.Id
           || id == ViewCommands.Translate.Id
           || id == ViewCommands.Apps.Id;

    /// <summary>Presses a ribbon command by id, exactly as a click does. For the harness.</summary>
    internal void Press(CommandId id) => OnCommand(id, fromChevron: false);

    /// <summary>Presses a command's chevron half, which is what opens its menu. For the harness.</summary>
    internal void PressChevron(CommandId id) => OnCommand(id, fromChevron: true);

    /// <summary>Opens the quick strip's "…" menu, exactly as a click does. For the harness.</summary>
    internal void PressMore() => _moreButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private void OnCommand(CommandId id, bool fromChevron)
    {
        Log.Debug($"Message window command: {id}{(fromChevron ? " (chevron)" : string.Empty)}");

        if (id == MailCommands.Reply.Id) { RespondRequested?.Invoke(this, ReplyKind.Reply); return; }
        if (id == MailCommands.ReplyAll.Id) { RespondRequested?.Invoke(this, ReplyKind.ReplyAll); return; }
        if (id == MailCommands.Forward.Id) { RespondRequested?.Invoke(this, ReplyKind.Forward); return; }

        if (id == MailCommands.PreviousMessage.Id) { StepRequested?.Invoke(this, -1); return; }
        if (id == MailCommands.NextMessage.Id) { StepRequested?.Invoke(this, +1); return; }

        if (id == MailCommands.Undo.Id || id == ViewCommands.Redo.Id)
        {
            Notice("Nothing here to undo — an open message is read, not edited.");
            return;
        }

        if (id == ViewCommands.Zoom.Id) { _ = ZoomAsync(); return; }
        // What each of these waits on is what is absent, not a date. A note naming a phase tells
        // the reader to wait for something that may already have arrived — which is exactly what
        // these four did — and a phase is not a reason anyway.
        if (id == ViewCommands.FindInMessage.Id) { Notice("The reading pane renders the message in a web engine, and nothing here can drive that engine's own find."); return; }
        if (id == ViewCommands.ReadAloud.Id) { Notice("There is no speech engine here, and nothing that reads a message aloud without sending it off this machine."); return; }
        if (id == ViewCommands.ImmersiveReader.Id) { Notice("Immersive Reader is a second way of laying the document out, and the reading pane lays it out one way."); return; }
        if (id == ViewCommands.Translate.Id) { Notice("Translating means sending the message to a translation service, and no service has been chosen."); return; }

        if (id == ViewCommands.Apps.Id)
        {
            MenuProbe.Show(
                "All Apps",
                AllAppsMenu.Build(
                    pressed => OnCommand(pressed, fromChevron: false),
                    () => _ = new OptionsWindow(App.Themes, "addins").ShowDialog<bool>(this)),
                this,
                atPointer: true);
            return;
        }

        if (id == MailCommands.ViewSource.Id) { ShowSource(); return; }

        // Everything below acts on the stored row.
        if (_context is not { } context || Repo() is not { } mail)
        {
            Notice("This message is not in a folder this window can act on.");
            return;
        }

        if (id == MailCommands.Delete.Id && fromChevron) { DeleteMenu(context, mail); return; }
        if (id == MailCommands.Delete.Id) { Delete(context, mail); return; }

        if (id == MailCommands.Archive.Id)
        {
            if (mail.FolderWithRole(AccountId(mail), FolderRole.Archive) is not { } archive)
            {
                Notice("This account has no Archive folder.");
                return;
            }

            mail.MoveMessage(context.MessageId, archive.Id);
            Log.Info($"Message window: {context.MessageId} archived.");
            Changed?.Invoke(this, EventArgs.Empty);
            Close();
            return;
        }

        if (id == MailCommands.MoveTo.Id) { MoveMenu(context, mail); return; }

        if (id == MailCommands.Unread.Id || id == MailCommands.MarkAsUnread.Id)
        {
            mail.SetRead(context.MessageId, read: false);
            Log.Info($"Message window: {context.MessageId} marked unread.");
            Changed?.Invoke(this, EventArgs.Empty);
            Notice("Marked unread.");
            return;
        }

        if (id == MailCommands.Categorize.Id) { CategorizeMenu(context, mail); return; }
        if (id == MailCommands.FollowUp.Id) { FollowUpMenu(context, mail); return; }

        // A Quick Step, by the command it is placed as — the sequences live in the shell.
        if (App.QuickSteps.FindByCommand(id) is not null)
        {
            QuickStepRequested?.Invoke(this, id);
            return;
        }

        // A plugin's command is app-global and runs from any host, this window included.
        if (App.Plugins.TryRun(id)) return;

        if (App.Commands.TryGet(id, out var command))
        {
            Notice($"{command.Label} — not wired here yet ({command.Id}).");
        }
    }

    private MailRepository? Repo()
    {
        // The window acts on the store the message came from, which is not always the one the
        // shell is showing now.
        if (_context is { } context)
        {
            foreach (var (address, mail) in App.Mailboxes())
            {
                if (string.Equals(address, context.Address, StringComparison.OrdinalIgnoreCase)) return mail;
            }
        }

        return _mail();
    }

    private static long AccountId(MailRepository mail) => mail.Accounts().FirstOrDefault()?.Id ?? 0;

    private void Delete(OpenedMessageContext context, MailRepository mail)
    {
        mail.DeleteMessage(context.MessageId);
        Log.Info($"Message window: {context.MessageId} deleted.");
        Changed?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>The chevron half's menu. Authored — no capture shows the reference's open.</summary>
    private void DeleteMenu(OpenedMessageContext context, MailRepository mail)
    {
        var flyout = new MenuFlyout();

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => Delete(context, mail);
        flyout.Items.Add(delete);

        var junk = new MenuItem { Header = "Move to Junk Email" };
        junk.IsEnabled = mail.FolderWithRole(AccountId(mail), FolderRole.Junk) is not null;
        junk.Click += (_, _) =>
        {
            if (mail.FolderWithRole(AccountId(mail), FolderRole.Junk) is not { } folder) return;
            mail.MoveMessage(context.MessageId, folder.Id);
            Log.Info($"Message window: {context.MessageId} moved to Junk Email.");
            Changed?.Invoke(this, EventArgs.Empty);
            Close();
        };
        flyout.Items.Add(junk);

        MenuProbe.Show("the message window's delete menu", flyout, this, atPointer: true);
    }

    private void MoveMenu(OpenedMessageContext context, MailRepository mail)
    {
        var flyout = new MenuFlyout();

        foreach (var folder in mail.Folders(AccountId(mail))
                     .Where(f => f.Id != context.FolderId && f.Role != FolderRole.Outbox)
                     .OrderBy(f => f.Role == FolderRole.None)
                     .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var entry = new MenuItem { Header = folder.Name };
            entry.Click += (_, _) =>
            {
                mail.MoveMessage(context.MessageId, folder.Id);
                Log.Info($"Message window: {context.MessageId} moved to “{folder.Name}”.");
                Changed?.Invoke(this, EventArgs.Empty);
                Close();
            };
            flyout.Items.Add(entry);
        }

        MenuProbe.Show("the message window's move menu", flyout, this, atPointer: true);
    }

    private void CategorizeMenu(OpenedMessageContext context, MailRepository mail)
    {
        var flyout = new MenuFlyout();
        var carried = mail.CategoriesFor([context.MessageId])
            .GetValueOrDefault(context.MessageId, [])
            .Select(c => c.Id)
            .ToHashSet();

        foreach (var category in mail.Categories())
        {
            var has = carried.Contains(category.Id);
            var entry = new MenuItem { Header = (has ? "✓ " : string.Empty) + category.Name };
            entry.Click += (_, _) =>
            {
                if (has) mail.Unassign([context.MessageId], category.Id);
                else mail.Assign([context.MessageId], category.Id);
                Log.Info($"Message window: {context.MessageId} {(has ? "lost" : "took")} “{category.Name}”.");
                Changed?.Invoke(this, EventArgs.Empty);
            };
            flyout.Items.Add(entry);
        }

        MenuProbe.Show("the message window's categorize menu", flyout, this, atPointer: true);
    }

    private void FollowUpMenu(OpenedMessageContext context, MailRepository mail)
    {
        var flyout = new MenuFlyout();
        var today = DateTimeOffset.Now.Date;

        void Entry(string header, Action press)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                press();
                Log.Info($"Message window: {context.MessageId} follow-up — {header}.");
                Changed?.Invoke(this, EventArgs.Empty);
            };
            flyout.Items.Add(item);
        }

        Entry("Today", () => mail.SetFollowUp([context.MessageId], today.AddHours(17)));
        Entry("Tomorrow", () => mail.SetFollowUp([context.MessageId], today.AddDays(1).AddHours(17)));
        Entry("This Week", () => mail.SetFollowUp([context.MessageId], EndOfWeek(today)));
        Entry("Next Week", () => mail.SetFollowUp([context.MessageId], EndOfWeek(today.AddDays(7))));
        Entry("No Date", () => mail.SetFollowUp([context.MessageId], null));
        Entry("Mark Complete", () => mail.CompleteFollowUp([context.MessageId]));
        Entry("Clear Flag", () => mail.ClearFollowUp([context.MessageId]));

        MenuProbe.Show("the message window's follow-up menu", flyout, this, atPointer: true);

        static DateTimeOffset EndOfWeek(DateTime from)
        {
            var days = ((int)DayOfWeek.Friday - (int)from.DayOfWeek + 7) % 7;
            return from.AddDays(days).AddHours(17);
        }
    }

    private async Task ZoomAsync()
    {
        var chosen = await ZoomDialog.AskAsync(this, _zoomPercent);
        if (chosen is not { } percent) return;

        _zoomPercent = percent;
        _body.MessageFontSize = 14.5 * (percent / 100d);
        Log.Info($"Message window: zoom {percent:0}%.");
    }

    private void ShowSource()
    {
        if (_raw is { Length: > 0 })
        {
            new MessageSourceWindow(_message.Subject ?? string.Empty, _raw).Show(this);
        }
        else
        {
            Notice("This message's stored bytes are not to hand.");
        }
    }

    /// <summary>
    /// The window's own word for a command that has nothing to do yet — it has no status bar,
    /// the reference's message window not carrying one, so the word is a strip that says its
    /// piece and goes.
    /// </summary>
    private void Notice(string text)
    {
        _noticeText.Text = text;
        _notice.IsVisible = true;
        Log.Info($"Message window: {text}");

        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) =>
        {
            _notice.IsVisible = false;
            _noticeTimer?.Stop();
        };
        _noticeTimer.Start();
    }

    // ---- Chrome --------------------------------------------------------------------------------

    private Control BuildRoot()
    {
        var layered = new Grid();
        var root = new DockPanel { LastChildFill = true };

        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        // The same 8px inset the compose window gives its ribbon: no rail in this window either.
        var ribbonHost = new Border
        {
            Child = _ribbon,
            ZIndex = 2,
            Padding = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(ribbonHost, Dock.Top);
        root.Children.Add(ribbonHost);

        DockPanel.SetDock(_notice, Dock.Top);
        root.Children.Add(_notice);

        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_attachments, Dock.Top);
        root.Children.Add(_attachments);

        var workspace = new Grid();
        workspace.Children.Add(_body);
        _floatLayer = new Canvas { IsHitTestVisible = true, ZIndex = 1 };
        workspace.Children.Add(_floatLayer);
        root.Children.Add(workspace);

        layered.Children.Add(root);

        _backstage = new ContentControl { ZIndex = 10, IsVisible = false };
        layered.Children.Add(_backstage);

        return WindowFrame.Rounded(layered);
    }

    /// <summary>
    /// The reference's message-window header: the subject over the sender's disc and address,
    /// the date at the right, and the header's own Reply, Reply All and Forward — the same
    /// commands the ribbon carries, drawn where the reference draws them twice.
    /// </summary>
    private Control Header()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        _subject.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_subject, 0);
        Grid.SetColumn(_subject, 0);
        grid.Children.Add(_subject);

        var quick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top,
        };
        quick.Children.Add(HeaderButton(MailCommands.Reply));
        quick.Children.Add(HeaderButton(MailCommands.ReplyAll));
        quick.Children.Add(HeaderButton(MailCommands.Forward));
        quick.Children.Add(MoreButton());
        Grid.SetRow(quick, 0);
        Grid.SetColumn(quick, 1);
        grid.Children.Add(quick);

        var disc = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(0, 2, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _initial,
        };
        Bind(disc, Border.BackgroundProperty, "avatar.background.brush");

        _from.FontWeight = FontWeight.SemiBold;

        var who = new StackPanel { Spacing = 2, Children = { _from, _to } };

        var senderRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { disc, who },
        };
        Grid.SetRow(senderRow, 1);
        Grid.SetColumn(senderRow, 0);
        grid.Children.Add(senderRow);

        _sent.VerticalAlignment = VerticalAlignment.Bottom;
        _sent.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(_sent, 1);
        Grid.SetColumn(_sent, 1);
        grid.Children.Add(_sent);

        var header = new Border
        {
            Child = grid,
            Padding = new Thickness(20, 14, 20, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(header, Border.BorderBrushProperty, "border.subtle.brush");
        Bind(header, Border.BackgroundProperty, "reading.header.background.brush");

        return header;
    }

    /// <summary>One of the header's own respond buttons: the command's glyph beside its label.</summary>
    private Button HeaderButton(MailboxCommand command)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(command.Icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty,
            command.IconTint is { Length: > 0 } tint ? tint + ".brush" : "text.primary.brush");

        var label = new TextBlock
        {
            Text = command.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");

        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { glyph, label },
            },
            Padding = new Thickness(10, 4),
            BorderThickness = new Thickness(1),
        };
        Bind(button, Button.BorderBrushProperty, "border.subtle.brush");
        Bind(button, Button.BackgroundProperty, "surface.raised.brush");
        ToolTip.SetTip(button, command.Description);
        button.Click += (_, _) => OnCommand(command.Id, fromChevron: false);
        return button;
    }

    /// <summary>The header's "…": the actions the reference keeps off the three buttons.</summary>
    private Button MoreButton()
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = "…",
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(1),
        };
        Bind(button, Button.BorderBrushProperty, "border.subtle.brush");
        Bind(button, Button.BackgroundProperty, "surface.raised.brush");
        Bind((TextBlock)button.Content!, TextBlock.ForegroundProperty, "text.primary.brush");
        ToolTip.SetTip(button, "More actions");
        _moreButton = button;

        button.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();

            void Entry(string header, CommandId id)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => OnCommand(id, fromChevron: false);
                flyout.Items.Add(item);
            }

            Entry("Mark Unread", MailCommands.Unread.Id);
            Entry("Follow Up", MailCommands.FollowUp.Id);
            Entry("Categorize", MailCommands.Categorize.Id);
            flyout.Items.Add(new Separator());
            Entry("View Source", MailCommands.ViewSource.Id);

            MenuProbe.Show("the message window's more menu", flyout, button);
        };

        return button;
    }

    private Control BuildTitleBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        leading.Children.Add(new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(ComposeWindow.AssetLoaderIcon()),
            Width = 16,
            Height = 16,
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        foreach (var id in MessageRibbonLayout.Layout.QuickAccess)
        {
            if (!App.Commands.TryGet(id, out var command)) continue;
            leading.Children.Add(QuickAccessButton(command));
        }

        var chevron = new Button
        {
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                FontFamily = IconFont.Family,
                FontSize = 11,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("titlebar.foreground.brush"),
            },
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = default,
        };
        ToolTip.SetTip(chevron, "Customize Quick Access Toolbar");
        leading.Children.Add(chevron);

        _caption = new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Bind(_caption, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        Bind(_caption, TextBlock.FontSizeProperty, "type.ui.size.value");
        leading.Children.Add(_caption);

        Grid.SetColumn(leading, 0);
        grid.Children.Add(leading);

        var buttons = new CaptionButtons(this) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var host = new Border { Child = grid, Height = 40 };
        Bind(host, Border.BackgroundProperty, "titlebar.background.brush");
        WindowFrame.Drags(this, host);
        return host;
    }

    private Button QuickAccessButton(MailboxCommand command)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(command.Icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "titlebar.foreground.brush");

        var button = new Button
        {
            Content = glyph,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = default,
        };
        ToolTip.SetTip(button, command.Label);
        button.Click += (_, _) => OnCommand(command.Id, fromChevron: false);
        return button;
    }

    private void ShowFloatingRibbon(Control? body)
    {
        if (_floatingRibbon is not null)
        {
            _floatLayer.Children.Remove(_floatingRibbon);
            _floatingRibbon = null;
        }

        if (body is null) return;

        body.Width = _floatLayer.Bounds.Width > 0 ? _floatLayer.Bounds.Width : Width;
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);

        _floatLayer.Children.Add(body);
        _floatingRibbon = body;
    }

    private void OnWindowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_floatingRibbon is null || e.Source is not Visual source) return;
        if (IsWithin(source, _floatingRibbon) || IsWithin(source, _ribbon)) return;

        _ribbon.CloseFloatingBody();
    }

    private static bool IsWithin(Visual node, Visual? ancestor)
        => ancestor is not null
           && (ReferenceEquals(node, ancestor) || node.GetVisualAncestors().Contains(ancestor));

    /// <summary>File over this window: the same Backstage the shell and the compose window show.</summary>
    private void ShowBackstage()
    {
        var host = new BackstageHost(this, _ => { }, () => { }, CloseBackstage);

        var backstage = new BackstageView();
        backstage.OptionsRequested += async (_, _) =>
            await new OptionsWindow(App.Themes).ShowDialog<bool>(this);
        backstage.AddAccountRequested += async (_, _) =>
            await BackstageActions.AddAccountAsync(host);
        backstage.ActionRequested += async (_, action) =>
            await BackstageActions.RunAsync(host, action);
        backstage.CloseRequested += (_, _) => CloseBackstage();

        _backstage.Content = backstage;
        _backstage.IsVisible = true;
    }

    private void CloseBackstage()
    {
        _backstage.IsVisible = false;
        _backstage.Content = null;
    }

    private static TextBlock Line(double? size, string ink)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (size is { } points) line.FontSize = points;

        Bind(line, TextBlock.ForegroundProperty, ink);
        return line;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
