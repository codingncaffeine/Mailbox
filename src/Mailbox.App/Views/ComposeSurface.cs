using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Controls.Ribbon;
using Mailbox.Editor;
using Mailbox.Rendering;
using Mailbox.Contacts;
using Mailbox.Core.Commands;
using Mailbox.Core.Compose;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Security;
using Mailbox.Store;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Icons;
using MimeKit;
using MimeKit.Utils;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;

namespace Mailbox.App.Views;

/// <summary>
/// The message-composition surface: address fields, Send, the body editor, and everything the
/// compose ribbon acts on — with no chrome of its own.
/// </summary>
/// <remarks>
/// Host-neutral on purpose. <see cref="ComposeWindow"/> wraps it in a window with a title bar
/// and its own ribbon; the reading pane embeds the same control as an inline reply strip with
/// the shell's ribbon driving it. Everything a host differs on is an event or a resolved
/// <see cref="Owner"/> rather than a hard-coded window: a command comes in through
/// <see cref="Invoke"/>, enablement goes out through <see cref="EnablementChanged"/>, and
/// closing is a request the host answers.
/// <para>
/// The body is a real document. §7.3's survey found a GPL-3-compatible editor that carries the
/// document model that section planned to build, so what is in-house is the serializer — and
/// that is the half mail fidelity rests on. Send writes both an HTML body, through
/// <see cref="EmailHtml"/>, and the plain text alternative, off the same document so the two
/// cannot disagree.
/// <para>
/// The editor rather than its packaged view: the view brings a toolbar and a status bar of its
/// own, and a host already has a ribbon. What each ribbon button does today is recorded once in
/// <see cref="ComposeAvailability"/> and read from there, so a button still waiting on something
/// says what, rather than "not wired yet".
/// </para>
/// </remarks>
public sealed class ComposeSurface : UserControl
{
    /// <summary>
    /// The window this surface is hosted in, for the modal dialogs and the file picker that
    /// need an owner. Resolved from the visual tree, so it is the compose window when the
    /// surface is that window's content and the main window when it is an inline reply strip.
    /// </summary>
    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The host window, for a modal dialog that must have an owner. Throws if detached — a
    /// picker is only ever reached through a command, and a command means the surface is live.</summary>
    private Window Host => Owner ?? throw new InvalidOperationException("The compose surface is not hosted in a window.");
    /// <summary>
    /// What this message is written in: Personal Stationery's font for new mail, for a reply or
    /// forward, or for plain text — Calibri 11 unless the reader has chosen otherwise, which is
    /// the reference's own default and the one name §6 cares most about getting onto the wire
    /// correctly. The family here is the wire name; the editor draws its substitute.
    /// </summary>
    private MessageFont _font = MessageFont.Default;

    /// <summary>The Format Text tab, shared with every other window that writes rich text.</summary>
    private readonly EditorCommands _editorCommands;

    /// <summary>The document measures in device-independent pixels; mail talks in points.</summary>
    private const double PointsPerPixel = 0.75;

    /// <summary>
    /// Writes in a stationery font: the editor's default face and size become the font's
    /// substitute at its size, and its weight, slant and colour are put on the empty document
    /// so what is typed comes out in them — the runs then carry them, and the wire says what
    /// the screen shows. Only for a body nothing has been typed into; a draft keeps its own.
    /// </summary>
    private void UseFont(MessageFont font)
    {
        _font = font;
        _body.DefaultFontFamily = Mailbox.Theming.Fonts.BundledFonts.FamilyFor(App.Fonts.Resolve(font.Family).Rendered);
        _body.DefaultFontSize = font.Points / PointsPerPixel;

        if (!string.IsNullOrWhiteSpace(_body.GetPlainText())) return;
        if (font.Bold) _body.ToggleBold();
        if (font.Italic) _body.ToggleItalic();
        if (font.Colour is { } hex && Avalonia.Media.Color.TryParse(hex, out var colour))
        {
            _body.SetForeground(new Avalonia.Media.SolidColorBrush(colour));
        }
    }

    private readonly CommandCatalog _catalog;
    private readonly AccountStores? _accounts;

    /// <summary>The address book, which the recipient lines offer beside what has been written to before.</summary>
    private readonly Mailbox.Contacts.ContactBook? _contacts;

    // Named for a screen reader: the labels beside these are buttons that open the address
    // book, so a reader tabbing into the field heard "edit" and nothing else.
    private readonly TextBox _to = Field("To");
    private readonly TextBox _cc = Field("Cc");
    private readonly TextBox _bcc = Field("Bcc");
    private readonly TextBox _subject = Field("Subject");
    private readonly ComposeEditor _body;
    /// <summary>
    /// The sending account, shown as plain text beside the From button.
    /// </summary>
    /// <remarks>
    /// Not a combo. The reference puts a <c>From ⌄</c> button in the label column and the
    /// address as text in the field column, which is why a full-width picker looked wrong: the
    /// choosing happens in the button's menu, and the field only reports.
    /// </remarks>
    private readonly TextBlock _fromAddress = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string? _sendingAddress;
    private readonly TextBlock _status = new();
    private readonly TextBlock _attachmentStrip = new() { TextWrapping = TextWrapping.Wrap };

    // Each address row is one control, so showing or hiding it is one visibility change on one
    // thing rather than two halves that have to agree.
    private Control _bccRow = null!;
    private Control _fromRow = null!;
    private Border _attachmentRow = null!;

    private readonly List<IStorageFile> _attachments = [];

    /// <summary>
    /// Attachments that came from a message rather than a file: a forward's, or an attached
    /// original. Already MIME, so they go into the message as they are.
    /// </summary>
    private readonly List<CarriedPart> _carried = [];

    /// <summary>The threading headers a reply carries, so the recipient's client can join it up.</summary>
    private string? _inReplyTo;
    private IReadOnlyList<string> _references = [];

    private MessageImportance _importance = MessageImportance.Normal;
    private bool _wantsReadReceipt;
    private bool _wantsDeliveryReceipt;

    /// <summary>
    /// Whether this message is signed, sealed, both or neither when it goes.
    /// </summary>
    /// <remarks>
    /// Per message, from the two buttons on the Options tab — not a setting, because the reference's
    /// are per message and because signing everything and encrypting nothing are both reasonable
    /// habits that only the writer knows they have. Which algorithm carries it is not asked here;
    /// see <see cref="MessageProtection"/> for why that is the application's decision.
    /// </remarks>
    private Protection _protection = Protection.None;

    /// <summary>
    /// The header fields the message this one answers kept off its own outside. Usually empty.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §6.1's half of the reply rules — see <see cref="Answering"/>. Held rather than
    /// derived, because by the time Send is pressed the message being answered is not in hand.
    /// </remarks>
    private IReadOnlyList<string> _confidential = [];
    private DateTimeOffset? _notBefore;
    private string? _replyTo;
    private bool _sent;

    /// <summary>
    /// Whether this message goes out as plain text only.
    /// </summary>
    /// <remarks>
    /// The body is the same document either way — the mode decides what Send writes, not what
    /// the writer sees. Plain text sends the text half alone; HTML sends both. Starts from the
    /// Options page and is switched per message from the Format Text tab, as the reference does.
    /// </remarks>
    private bool _plainText;

    /// <summary>
    /// The title the host shows, which is where the reference says what format a message is in.
    /// The window puts it in its caption; an inline strip ignores it.
    /// </summary>
    public string Title { get; private set; } = "Untitled - Message (HTML)";

    /// <summary>Raised when <see cref="Title"/> changes — the subject typed, or the format switched.</summary>
    public event EventHandler? TitleChanged;

    private void UpdateTitle()
    {
        var format = _plainText ? "Plain Text" : "HTML";
        Title = string.IsNullOrWhiteSpace(_subject.Text)
            ? $"Untitled - Message ({format})"
            : $"{_subject.Text} - Message ({format})";
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Drafts row this window is editing, so saving again replaces it.</summary>
    private long? _draftId;

    /// <summary>Saves a message being written, on the interval the Options page sets.</summary>
    private DispatcherTimer? _autosave;

    /// <summary>Whether anything has changed since the last save, so autosave has a reason to.</summary>
    private bool _dirty;

    /// <summary>
    /// Loaded the first time spelling is asked for, not at startup.
    /// </summary>
    /// <remarks>
    /// A dictionary is a few megabytes of word list and parsing one is felt. Most messages are
    /// sent without the button ever being pressed, so paying for it on every compose window
    /// would be paying for it almost always in vain.
    /// </remarks>
    private SpellCheck? _spelling;

    /// <summary>
    /// Raised when a message went to the outbox and is meant to go as soon as it can — under
    /// Undo Send's hold if that is on, at once if it is not.
    /// </summary>
    /// <remarks>
    /// An event rather than the surface putting up its own toast, because the surface asks to be
    /// closed the instant it fires — a message offering to undo something has to outlive the
    /// thing that did it, so the shell owns it.
    /// </remarks>
    public event EventHandler<QueuedMessageEventArgs>? Queued;

    /// <summary>
    /// Raised when the surface is done and asks its host to dismiss it — the message sent or
    /// discarded. The window closes; an inline strip collapses. The host decides whether an
    /// unsaved message is worth a prompt first, because the host knows what dismissing means.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when a command's enabled state may have changed, so a host ribbon can refresh.</summary>
    public event EventHandler? EnablementChanged;

    /// <summary>True once the message has gone out, so there is nothing left for a host to keep.</summary>
    /// <remarks>
    /// Sent, not saved. Saving a draft used to set this too, on the reasoning that a written
    /// draft needs no keeping — but the flag never went back down, so the first save silenced
    /// the close prompt and stopped the autosave timer for the life of the window, and
    /// everything typed afterwards was dropped on close without a word. Whether there is
    /// anything worth keeping is <see cref="IsDirty"/>'s question, and it answers it per
    /// keystroke.
    /// </remarks>
    public bool IsSent => _sent;

    /// <summary>True while there are changes this window has not written to Drafts.</summary>
    /// <remarks>
    /// False on a message that was only ever populated — a reply's quoted body, a signature —
    /// so closing one nobody typed in asks nothing, and false again after every save.
    /// </remarks>
    public bool IsDirty => _dirty;

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseEnablementChanged() => EnablementChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Not a word anybody would suggest, so it cannot collide with one.</summary>
    private const string AddToDictionary = "\u0000add";
    private const string DeleteRepeated = "\u0000delete-repeated";
    private const string EditSignatures = "\u0000signatures";

    public ComposeSurface(CommandCatalog catalog, AccountStores? accounts, Mailbox.Contacts.ContactBook? contacts = null)
    {
        _catalog = catalog;
        _accounts = accounts;
        _contacts = contacts;

        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);

        // The editor itself rather than its own view: that wrapper brings a toolbar and a
        // status bar of its own, and a host already has a ribbon. Two bars disagreeing about the
        // same document is the mistake the compose window made once already with its caption
        // buttons.
        _body = new ComposeEditor
        {
            [Avalonia.Automation.AutomationProperties.NameProperty] = "Message body",
            AllowRemoteImagesOnPaste = false,
            AllowLocalFileImages = false,
            AutoLinkOnType = true,
        };

        // The body is a document page, not a pane — white even in Dark Gray, where the reading
        // pane is not. The page is painted by the border around it; these are the marks drawn
        // on top of it, which have to come from tokens like everything else.
        Bind(_body, RichEditor.SelectionBrushProperty, "state.selected.brush");
        Bind(_body, RichEditor.CaretBrushProperty, "compose.body.text.brush");

        _editorCommands = new EditorCommands(_body, () => Host, Report, RaiseEnablementChanged)
        {
            BaseFont = () => _font,
        };

        // A fresh editor has no document until something makes one, and until then InsertText,
        // InsertHtml and a keystroke are all silent no-ops. Clear() is what makes one — a single
        // empty paragraph with the caret in it — and it is called here so that Insert Symbol, a
        // link, a signature or the automatic one all work before anything has been typed. Found
        // by asking the editor rather than the window: the harness poses text through a path
        // that happened to call Clear() first, so every capture looked fine.
        _body.Clear();

        // The left of the Message tab is pale on an empty message and darkens as soon as there
        // is something to format. That is enablement, and it has to track every keystroke; the
        // host's ribbon listens to EnablementChanged.
        _body.TextChanged += (_, _) => RaiseEnablementChanged();

        // What the Options page says a new message starts as: its importance, whether it asks
        // for receipts, and its format. Read here rather than at Send, so what the status line
        // shows while writing is what will go.
        _importance = App.MailOptions.DefaultImportanceIndex switch
        {
            1 => MessageImportance.Low,
            2 => MessageImportance.High,
            _ => MessageImportance.Normal,
        };
        _wantsDeliveryReceipt = App.MailOptions.RequestDeliveryReceipt;
        _wantsReadReceipt = App.MailOptions.RequestReadReceipt;
        _plainText = App.MailOptions.ComposeFormat == ComposeFormat.PlainText;
        UseFont(App.Stationery.Get(_plainText ? StationeryUse.PlainText : StationeryUse.NewMessages));

        ApplyAutocorrect();

        Content = BuildSurface();
        Focusable = true;
        UpdateStatus();
        UpdateTitle();

        // The Auto-Complete List under each recipient line. The list is per account file and
        // the window can send from any account, so what is offered is the union, weighted by
        // use across the lot; forgetting an entry forgets it everywhere.
        foreach (var field in new[] { _to, _cc, _bcc })
        {
            _completions.Add(RecipientAutocomplete.Attach(
                field,
                SuggestRecipients,
                ForgetRecipient,
                () => App.MailOptions.UseAutoCompleteList,
                () => App.MailOptions.CommasSeparateRecipients));
        }

        // The signature this account signs new mail with, if it has chosen one. After the tree
        // is built, because it goes into the document and the document is part of that tree.
        InsertDefaultSignature();

        ApplyAutosaveInterval();

        // The timer runs while the surface is in the tree and stops when it leaves — so a closed
        // window or a dismissed inline strip does not keep one alive, and a surface popped out of
        // the reading pane into a window (where it briefly detaches and re-attaches) keeps saving.
        AttachedToVisualTree += (_, _) => { if (!_sent) _autosave?.Start(); };
        DetachedFromVisualTree += (_, _) => _autosave?.Stop();

        // The AutoCorrect dialog writes as it goes, and what it writes has to reach a message
        // already being written — the reference's switches take effect on the next word, not
        // the next message. Attached to the tree for the same reason the timer is: a surface
        // that has been closed must not still be listening to the settings store.
        AttachedToVisualTree += (_, _) => App.Settings.Changed += OnSettingChanged;
        DetachedFromVisualTree += (_, _) => App.Settings.Changed -= OnSettingChanged;

        _body.TextChanged += (_, _) => _dirty = true;
        foreach (var field in new[] { _to, _cc, _bcc, _subject }) field.TextChanged += (_, _) => _dirty = true;

        _to.AttachedToVisualTree += (_, _) => _to.Focus();
    }

    /// <summary>
    /// The surface's own keys, so Ctrl+Enter, Ctrl+S and Escape work whether it is a window's
    /// content or an inline strip — the compose window used to override the Window's OnKeyDown,
    /// which an embedded control has no equivalent of.
    /// </summary>
    /// <remarks>
    /// Those three first, because two of them answer to a setting and the third closes rather
    /// than commands. Everything else goes through the key map, asked for this window's own
    /// commands — Ctrl+B, Ctrl+K, F7 — so a shortcut rebound in Customize Keyboard is rebound
    /// here too, and a plain keystroke stays the reader typing.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            // The Options page's "CTRL+ENTER sends a message". Off is for the person who has
            // sent one too many half-written messages that way, and it is a real setting.
            case Key.Enter when control && App.MailOptions.CtrlEnterSends:
                Invoke(ComposeCommands.Send.Id);
                break;

            case Key.S when control:
                Invoke(ComposeCommands.SaveDraft.Id);
                break;

            case Key.Escape:
                RequestClose();
                break;

            default:
                if (Keystroke.Of(e) is not { } chord || Keystroke.IsTyping(chord)) return;
                if (App.Keys.CommandFor(chord, CommandSurface.Compose) is not { } id) return;
                Invoke(id);
                break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Commands that need something in the body before they mean anything — either because they
    /// format text that is not there, or because they insert into a document that is empty.
    /// </summary>
    private static readonly HashSet<CommandId> InsertsIntoBody =
    [
        ComposeCommands.Table.Id, ComposeCommands.Pictures.Id, ComposeCommands.StockImages.Id,
        ComposeCommands.OnlinePictures.Id, ComposeCommands.Shapes.Id, ComposeCommands.Icons.Id,
        ComposeCommands.Models3D.Id, ComposeCommands.SmartArt.Id, ComposeCommands.Chart.Id,
        ComposeCommands.Equation.Id, ComposeCommands.Symbol.Id, ComposeCommands.Link.Id,
        ComposeCommands.Styles.Id, ComposeCommands.ChangeStyles.Id, ComposeCommands.PageColor.Id,
    ];

    /// <summary>
    /// Whether a command is usable right now.
    /// </summary>
    /// <remarks>
    /// Paste is the exception among the formatting commands: there is always somewhere to paste
    /// to, even in an empty message. Everything else in that run needs text to act on.
    /// </remarks>
    public bool IsCommandEnabled(CommandId id)
    {
        if (id == ComposeCommands.Paste.Id) return true;
        if (!_catalog.TryGet(id, out var command)) return true;
        if (!command.NeutralIcon && !InsertsIntoBody.Contains(id)) return true;

        return !string.IsNullOrEmpty(_body.GetPlainText());
    }

    /// <summary>
    /// Fills the window from a message, for one that has been pulled back out of the outbox.
    /// </summary>
    /// <remarks>
    /// Undo Send (§12). What comes back is the message as it was queued, so the reader gets
    /// their words rather than a blank window and an apology — which is the whole point of
    /// pressing Undo rather than letting it go and writing a correction.
    /// </remarks>
    public void Restore(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _to.Text = string.Join("; ", message.To.Mailboxes.Select(m => m.Address));
        _cc.Text = string.Join("; ", message.Cc.Mailboxes.Select(m => m.Address));
        _bcc.Text = string.Join("; ", message.Bcc.Mailboxes.Select(m => m.Address));
        _subject.Text = message.Subject ?? string.Empty;

        if (message.From.Mailboxes.FirstOrDefault()?.Address is { Length: > 0 } from)
        {
            SendFrom(from);
        }

        // The document as it was written. The HTML half where there is one, because that is
        // what carried the formatting; the text half otherwise. Through the renderer rather
        // than straight into the editor: an image the writer put in went out as a related part
        // and a cid: reference, and the editor cannot resolve cid: — it drops the picture. The
        // renderer already turns cid: into data: for the reading pane, and this is our own
        // conservative markup, so what it lets through is everything that was there.
        _body.Clear();

        if (message.HtmlBody is { Length: > 0 })
        {
            _body.LoadHtml(Mailbox.Rendering.MessageRenderer.Render(message).Html);
        }
        else
        {
            _body.InsertText(message.TextBody ?? string.Empty);
        }

        _importance = message.Importance switch
        {
            MessageImportance.High => MessageImportance.High,
            MessageImportance.Low => MessageImportance.Low,
            _ => MessageImportance.Normal,
        };

        UpdateStatus();
        RaiseEnablementChanged();
    }

    /// <summary>
    /// Opens on a reply or a forward: recipients, subject, threading headers, the reply
    /// signature, and the original quoted below the caret.
    /// </summary>
    /// <remarks>
    /// The caret goes at the top and the quote below, which is the reference's arrangement and
    /// the one every recipient reading top-down expects. LoadHtml rather than InsertHtml, for
    /// the same reason the automatic signature uses it: it is what leaves the caret at the top.
    /// </remarks>
    public void Prefill(ReplyDraft draft, ReplyKind kind)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // A reply or a forward is written in Personal Stationery's font for replies, which may
        // differ from the one new mail starts in.
        UseFont(App.Stationery.Get(_plainText ? StationeryUse.PlainText : StationeryUse.Replies));

        _to.Text = string.Join("; ", draft.To);
        _cc.Text = string.Join("; ", draft.Cc);
        _subject.Text = draft.Subject;
        _inReplyTo = draft.InReplyTo;
        _references = draft.References;

        _carried.AddRange(draft.Attachments);
        if (_carried.Count > 0)
        {
            _attachmentStrip.Text = "Attached: " + string.Join(", ", _carried.Select(c => c.Name));
            _attachmentRow.IsVisible = true;
        }

        // The signature for a reply, if the account has one, then the quote — with two blank
        // lines above the lot for the answer to go in.
        var signature = SendingAccount() is { } account
            ? App.Signatures.ForReply(account.Account.Address)
            : null;

        var html = new StringBuilder("<p>&nbsp;</p><p>&nbsp;</p>");
        if (signature is { IsEmpty: false }) html.Append(signature.Html);

        if (_plainText || draft.QuotedHtml.Length == 0)
        {
            html.Append(SignatureEditor.AsHtml(draft.QuotedText));
        }
        else
        {
            html.Append(draft.QuotedHtml);
        }

        _body.LoadHtml(html.ToString());
        _dirty = false;

        UpdateTitle();
        UpdateStatus();
        RaiseEnablementChanged();

        // A forward wants a recipient; a reply has one already and wants the words. Focus
        // follows, which is what stops the first keystroke going into the wrong box. Keyed on
        // attachment to the tree rather than a window's Opened, so it works inline too.
        if (kind == ReplyKind.Forward) _to.AttachedToVisualTree += (_, _) => _to.Focus();
        else _body.AttachedToVisualTree += (_, _) => _body.Focus();
    }

    /// <summary>
    /// Says which of the answered message's header fields were confidential, and asks for the same.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §6.1, a MUST: a value that was kept off the outside of the message being answered
    /// must not go out in the clear in the answer, <em>even where this writer's own policy would not
    /// have hidden it</em>. Two things follow, and the first is the one that makes the second rare:
    /// <list type="bullet">
    /// <item><description>Encrypt goes down by itself. A reply to a message somebody took the
    /// trouble to encrypt is a reply that wants encrypting, and once it is encrypted the subject is
    /// obscured again by the same policy that obscured theirs.</description></item>
    /// <item><description>If the writer takes it back off, the send is <b>refused</b> and says which
    /// field it would have exposed. There is no partial state here for the same reason there is none
    /// anywhere else in this file: sending it anyway and mentioning it afterwards is doing the one
    /// thing the writer was trying to prevent.</description></item>
    /// </list>
    /// </remarks>
    public void Answering(IReadOnlyList<string> confidential)
    {
        ArgumentNullException.ThrowIfNull(confidential);

        _confidential = confidential;
        if (confidential.Count == 0) return;

        // Nothing to encrypt with means nothing to promise (§14). The refusal on Send is what stops
        // the leak in that case, and it says why.
        if (!App.Security.Smime && !App.Security.OpenPgp) return;

        _protection |= Protection.Encrypt;
        Report("This message will be encrypted: the one it answers kept its "
            + Names(confidential) + " out of the clear.");
    }

    /// <summary>
    /// Opens a draft for more writing, so saving or sending it acts on that draft.
    /// </summary>
    /// <remarks>
    /// Without this a draft was a message that could be looked at and never resumed — Save wrote
    /// to Drafts and nothing read from it — which is not what a draft is.
    /// </remarks>
    public void EditDraft(long messageId, MimeMessage message)
    {
        Restore(message);
        _draftId = messageId;
        _dirty = false;
    }

    /// <summary>Puts text in the body, so a capture can show the ribbon in its enabled state.</summary>
    public void PoseBodyText(string text)
    {
        _body.Clear();
        _body.InsertText(text);
        RaiseEnablementChanged();
    }

    /// <summary>
    /// A body with one of everything the serializer handles, for the harness to send.
    /// </summary>
    /// <remarks>
    /// The picture is a real 1×1 PNG rather than a header, because the editor drops what it
    /// cannot decode — which is right, and which made an earlier probe look like a bug in the
    /// path rather than in the fixture.
    /// </remarks>
    public void PoseRichBody()
    {
        _body.Clear();
        _body.InsertHtml(
            "<p>Plain, then <b>bold</b>, then <i>italic</i>, then a "
            + "<a href=\"https://example.com/\">link</a>.</p>"
            + "<ul><li>One</li><li>Two</li></ul>"
            + "<p><img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
            + "AAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==\" width=\"10\" height=\"10\" /></p>"
            + "<p>After the picture.</p>");
        RaiseEnablementChanged();
    }

    /// <summary>
    /// Types into the body one character at a time, as a person would, for the harness.
    /// </summary>
    /// <remarks>
    /// Through the editor's own input events rather than through <see cref="RichEditor.InsertText"/>,
    /// because what is being checked is what happens <em>while</em> somebody types: autocorrect
    /// fires on a keystroke and nothing else. A newline is Return, for the rules that answer to
    /// it. The claim a run makes is the body read back afterwards.
    /// </remarks>
    public void PoseBodyTyping(string text)
    {
        _body.Focus();

        foreach (var ch in text)
        {
            if (ch is '\n')
            {
                _body.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.Enter,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = _body,
                });

                continue;
            }

            _body.RaiseEvent(new TextInputEventArgs
            {
                Text = ch.ToString(),
                RoutedEvent = InputElement.TextInputEvent,
                Source = _body,
            });
        }

        RaiseEnablementChanged();
    }

    /// <summary>The body as text, for a run that has to read back what typing did to it.</summary>
    public string BodyText => _body.GetPlainText();

    /// <summary>
    /// The body as markup, so a run can see the formatting a correction carried — a bold word
    /// is not something plain text can show.
    /// </summary>
    public string BodyHtml => _body.ToHtml();

    /// <summary>Presses Send, for the harness.</summary>
    public void PressSend() => Invoke(ComposeCommands.Send.Id);

    /// <summary>
    /// Presses Sign, Encrypt or both, for the harness — through the dispatcher, as a pointer would.
    /// </summary>
    /// <remarks>
    /// Pressed rather than assigned: what is being checked is that the buttons do it, and setting
    /// the field directly would pass over a bar whose entries were wired to nothing.
    /// </remarks>
    public void PressProtection(string what)
    {
        if (what.Contains("sign", StringComparison.OrdinalIgnoreCase)
            || what.Contains("both", StringComparison.OrdinalIgnoreCase))
        {
            Invoke(ComposeCommands.Sign.Id);
        }

        if (what.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
            || what.Contains("both", StringComparison.OrdinalIgnoreCase))
        {
            Invoke(ComposeCommands.Encrypt.Id);
        }

        Log.Info($"Harness: protection — the buttons leave this message {_protection}.");
    }

    /// <summary>Poses the optional address fields, so a capture can show them.</summary>
    public void ShowOptionalFields()
    {
        // Set rather than toggled. Bcc is the only row that is off to begin with — From is
        // shown, as the reference shows it — and a toggle turned that one back off the moment
        // the default changed, which photographs as a From button that does not exist.
        _bccRow.IsVisible = true;
        _fromRow.IsVisible = true;
    }

    /// <summary>
    /// Fills a new message from a <c>mailto:</c> link — the desktop asking Mailbox, as the system
    /// mail client, to write to someone.
    /// </summary>
    public void FillFromMailto(Mailbox.Core.Compose.MailtoLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        _to.Text = string.Join("; ", link.To);
        _cc.Text = string.Join("; ", link.Cc);
        _bcc.Text = string.Join("; ", link.Bcc);
        _subject.Text = link.Subject;

        // Show Bcc only when the link set one — otherwise the row stays hidden, as on a new message.
        if (link.Bcc.Count > 0) _bccRow.IsVisible = true;

        if (link.Body.Length > 0)
        {
            _body.Clear();
            _body.InsertText(link.Body);
        }

        UpdateTitle();
        UpdateStatus();
        RaiseEnablementChanged();

        // Focus the first empty field: the body when there is a recipient and a subject already,
        // else the To line — the same rule a reply uses.
        if (link.To.Count > 0) _body.AttachedToVisualTree += (_, _) => _body.Focus();
        else _to.AttachedToVisualTree += (_, _) => _to.Focus();
    }

    /// <summary>Fills the header, so a capture can be measured against the reference.</summary>
    public void PoseHeader(string to, string cc, string subject)
    {
        _to.Text = to;
        _cc.Text = cc;
        if (subject.Length > 0 && !subject.Contains(" - Message (", StringComparison.Ordinal)) _subject.Text = subject;
    }

    // ----------------------------------------------------------------------------------
    // Composition
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Title bar, ribbon and address header stacked from the top, status bar pinned to the
    /// bottom, and the body taking what is left.
    /// </summary>
    /// <remarks>
    /// A DockPanel rather than a Grid of auto rows, deliberately. Showing Bcc or From after the
    /// first layout pass did not grow an auto row — the field appeared and Subject was clipped
    /// off the bottom — and invalidating the grid and its border did not fix it. Docking sizes
    /// each band to its content every pass, so a field that appears simply makes the band taller.
    /// </remarks>
    private Control BuildSurface()
    {
        var root = new DockPanel { LastChildFill = true };

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var info = BuildInfoBar();
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);

        // The body is inset 5px left and right, and the chrome shows through as a gutter —
        // measured: the white starts at x=18 in a window whose content starts at 13, and ends
        // at 1647 with five more before the frame. It runs to the bottom; there is no status
        // bar in the reference, and the format the window is in is already in its title.
        var body = new Border { Child = _body, Padding = new Thickness(12, 8) };
        Bind(body, Border.BackgroundProperty, "compose.body.background.brush");

        var bodyHost = new Border
        {
            Child = body,
            Padding = new Thickness(BodyGutter, 0, BodyGutter, 0),
        };
        Bind(bodyHost, Border.BackgroundProperty, "compose.header.background.brush");
        root.Children.Add(bodyHost);

        // The title follows the subject as it is typed, so the host's caption stays current.
        _subject.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            UpdateTitle();
        };

        return root;
    }

    /// <summary>
    /// Send beside the address fields, as the reference has it: the button that finishes the
    /// job is not on the ribbon, because it is not a formatting choice.
    /// </summary>
    private Control BuildHeader()
    {
        // A stack, not a grid of auto rows. A row becoming visible after the first layout pass
        // has to make the header taller, and an auto row did not do that: Bcc appeared, the
        // header kept its old height, and Subject was painted over by the body below. A stack's
        // height is the sum of what is visible, recomputed every pass.
        // No spacing: each row already carries its own gap as a bottom margin, and adding
        // both put the rows on a 42px pitch where the reference measures 40.
        // Known gap, measured rather than assumed: the rules land on a fractional origin
        // inherited from the chrome above, so a 1px hairline antialiases across two rows on
        // some of them where the reference's are crisp. UseLayoutRounding here does not fix it
        // — the same thing the zoom slider's track found — and the real answer is ZoomSlider's:
        // translate to the TopLevel and correct by the fractional part. Not done yet.
        var rows = new StackPanel();

        _fromRow = AddressRow(rows, "From", _fromAddress, opensAddressBook: false, picksAccount: true);
        AddressRow(rows, "To", _to);
        AddressRow(rows, "Cc", _cc);
        _bccRow = AddressRow(rows, "Bcc", _bcc);
        AddressRow(rows, "Subject", _subject, opensAddressBook: false);

        _attachmentStrip.Text = string.Empty;
        // Sits in the header, so it takes the header's ink like everything else there.
        Bind(_attachmentStrip, TextBlock.ForegroundProperty, "compose.header.label.brush");
        _attachmentRow = new Border
        {
            Child = _attachmentStrip,
            Padding = new Thickness(76, 6, 0, 0),
            IsVisible = false,
        };
        rows.Children.Add(_attachmentRow);

        var send = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Children =
                {
                    IconBlock("send", 22),
                    new TextBlock { Text = "Send", HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
            Width = SendWidth,
            Height = SendHeight,
            Margin = new Thickness(SendInset, 0, LabelInset, 0),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
        };
        Bind(send, TemplatedControl.BackgroundProperty, "surface.raised.brush");
        Bind(send, TemplatedControl.BorderBrushProperty, "border.strong.brush");
        ToolTip.SetTip(send, "Send this message  (Ctrl+Enter)");
        send.Click += (_, _) => Invoke(ComposeCommands.Send.Id);

        // Bcc is off until asked for, which is what the Options tab's toggle is for.
        //
        // From is not. The reference shows it on a new message — its own capture has From above
        // To with the sending address beside it — and hiding it was wrong twice over: a message
        // goes out from exactly one account, and with the row hidden there was nothing on screen
        // saying which, and no way to change it without knowing to go and turn a toggle on first.
        _bccRow.IsVisible = false;
        PopulateAccounts();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, HeaderTopInset, 14, HeaderTopInset),
        };
        Grid.SetColumn(send, 0);
        grid.Children.Add(send);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(rows);

        var host = new Border { Child = grid };
        Bind(host, Border.BackgroundProperty, "compose.header.background.brush");
        return host;
    }

    // Measured off the compose capture, all of it. Horizontally: the header's background runs
    // from x=13, Send is a filled button at x=34–93, the From/To/Cc buttons start at x=110, and
    // the field rules begin at x=202. Vertically: the header background starts at y=136 and the
    // first button at y=161, so the block is inset 25; buttons are 31 tall on a 40 pitch; and
    // the rule under a field sits at 197 — five below its button, not level with it, which is
    // why the field is taller than the button beside it. Send runs 161–236.
    private const double SendInset = 21;
    private const double SendWidth = 58;
    private const double SendHeight = 75;
    private const double LabelInset = 17;
    private const double LabelWidth = 80;
    private const double FieldInset = 12;
    private const double HeaderTopInset = 25;
    private const double RowPitch = 40;
    private const double RowHeight = 31;
    private const double FieldHeight = 36;

    /// <summary>
    /// One address row — its label and its field as a single control, so the pair is shown and
    /// hidden together and cannot get out of step.
    /// </summary>
    private Control AddressRow(
        StackPanel rows, string label, Control field,
        bool opensAddressBook = true, bool picksAccount = false)
    {
        // The row is the pitch, and everything in it is centred in that pitch: the button, the
        // label and the field's own text. Measured off the reference, where a row spans 40px
        // and the address it carries sits dead centre of it — ours had the text 7px high, which
        // reads as the writing floating above its line rather than sitting on it.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Height = RowPitch,
        };

        Control caption;
        if (picksAccount)
        {
            // From carries a chevron and opens the account list, rather than acting on the
            // address book like the recipient rows.
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock
                        {
                            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                            FontFamily = IconFont.Family,
                            FontSize = 9,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Width = LabelWidth,
                Height = RowHeight,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };
            Bind(button, TemplatedControl.BackgroundProperty, "surface.raised.brush");
            Bind(button, TemplatedControl.BorderBrushProperty, "border.strong.brush");
            ToolTip.SetTip(button, "Send this message from a different account");

            // Built full and then shown, never filled from its own Opening: the presenter is
            // created and measured before that event is raised, so a menu populated there is
            // measured with nothing in it and opens as a window the size of its own border.
            // Which is what this was doing — the click worked, the menu was empty.
            button.Click += (_, _) =>
            {
                var flyout = new MenuFlyout();
                foreach (var item in AccountMenuItems())
                {
                    flyout.Items.Add(item);
                }

                flyout.ShowAt(button, showAtPointer: false);
            };

            caption = button;
        }
        else if (opensAddressBook)
        {
            var button = new Button
            {
                Content = label,
                Width = LabelWidth,
                Height = RowHeight,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };
            Bind(button, TemplatedControl.BackgroundProperty, "surface.raised.brush");
            Bind(button, TemplatedControl.BorderBrushProperty, "border.strong.brush");
            ToolTip.SetTip(button, $"Choose {label} recipients from the address book");
            button.Click += (_, _) => Invoke(MailCommands.AddressBook.Id);
            caption = button;
        }
        else
        {
            // Subject is a plain label rather than a button: it opens nothing.
            var text = new TextBlock
            {
                Text = label,
                Width = LabelWidth,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            Bind(text, TextBlock.ForegroundProperty, "compose.header.label.brush");
            caption = text;
        }

        Grid.SetColumn(caption, 0);
        row.Children.Add(caption);

        // The fields carry no box. The reference draws a single rule under each one and nothing
        // else, so the address block reads as writing lines rather than as a form.
        // The rule belongs to the row, not to whatever control sits in it. From shows an
        // address as text rather than an input, and it is still underlined in the reference —
        // hanging the rule off the TextBox left that one row without one.
        if (field is TextBox box)
        {
            box.BorderThickness = default;
            box.CornerRadius = default;
            box.Background = Brushes.Transparent;
            box.Padding = default;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            Bind(box, TemplatedControl.ForegroundProperty, "compose.header.text.brush");
        }

        if (field is TextBlock plain)
        {
            plain.VerticalAlignment = VerticalAlignment.Center;
            Bind(plain, TextBlock.ForegroundProperty, "compose.header.text.brush");
        }

        // Fills the row rather than sitting 36px of it, so the rule is the row's own bottom edge
        // and the text centres against the same 40px the button does. The reference leaves 14px
        // between the bottom of an address and the rule under it; centring in the full pitch is
        // what produces that, and it goes on producing it at another type size.
        var underlined = new Border
        {
            Child = field,
            Margin = new Thickness(FieldInset, 0, 0, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(underlined, Border.BorderBrushProperty, "compose.field.rule.brush");

        Grid.SetColumn(underlined, 1);
        row.Children.Add(underlined);

        rows.Children.Add(row);
        return row;
    }

    /// <summary>
    /// The strip that carries a message, between the header and the body.
    /// </summary>
    /// <remarks>
    /// Where the reference puts an InfoBar, and hidden until there is something to say — the
    /// compose window has no status bar, so a permanent strip would be a band the reference
    /// does not have. It carries two things: what a button that cannot act yet is waiting for,
    /// and the state worth stating, which is importance and a delayed send.
    /// </remarks>
    private Control BuildInfoBar()
    {
        Bind(_status, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Bind(_status, TextBlock.FontSizeProperty, "type.ui.size.value");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;

        _infoBar = new Border
        {
            Child = _status,
            Padding = new Thickness(BodyGutter + 12, 7),
            IsVisible = false,
        };
        Bind(_infoBar, Border.BackgroundProperty, "reading.infobar.background.brush");
        return _infoBar;
    }

    private Border _infoBar = null!;

    /// <summary>The body's inset from the window edge, measured at 5px either side.</summary>
    private const double BodyGutter = 5;

    private static TextBox Field(string? spoken = null)
    {
        var box = new TextBox { MinWidth = 200 };
        if (spoken is { Length: > 0 }) Avalonia.Automation.AutomationProperties.SetName(box, spoken);
        return box;
    }

    private readonly List<RecipientAutocomplete> _completions = [];

    /// <summary>
    /// What the Auto-Complete List offers for what has been typed: the addresses written to
    /// before, merged across every account, and then the address book.
    /// </summary>
    /// <remarks>
    /// Both, in that order, which is the order they are worth: an address written to last week is
    /// a better guess than one of two thousand contacts, and a contact nobody has written to yet
    /// would otherwise be unreachable without opening the address book. A contact whose address
    /// is already remembered stays one entry and gains the contact's name where the cache had
    /// none.
    /// </remarks>
    internal IReadOnlyList<RecipientSuggestion> SuggestRecipients(string typed)
    {
        var merged = new Dictionary<string, RecipientSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in _accounts?.All ?? [])
        {
            foreach (var entry in account.Mail.SuggestRecipients(typed))
            {
                merged[entry.Address] = merged.TryGetValue(entry.Address, out var seen)
                    ? seen with
                    {
                        Weight = seen.Weight + entry.Weight,
                        DisplayName = seen.DisplayName.Length > 0 ? seen.DisplayName : entry.DisplayName,
                        LastUsed = entry.LastUsed > seen.LastUsed ? entry.LastUsed : seen.LastUsed,
                    }
                    : new RecipientSuggestion(
                        entry.Address, entry.DisplayName, entry.Address, entry.Formatted,
                        Weight: entry.Weight, LastUsed: entry.LastUsed);
            }
        }

        foreach (var suggestion in _contacts is { } book ? ContactSuggestions.For(book, typed) : [])
        {
            if (merged.TryGetValue(suggestion.Key, out var seen))
            {
                // Already remembered: one entry, which now knows it is somebody in the book.
                merged[suggestion.Key] = seen with
                {
                    DisplayName = seen.DisplayName.Length > 0 ? seen.DisplayName : suggestion.DisplayName,
                    Detail = suggestion.Detail,
                };
                continue;
            }

            merged[suggestion.Key] = suggestion;
        }

        return
        [
            .. merged.Values
                .OrderByDescending(e => e.Weight)
                .ThenByDescending(e => e.LastUsed)
                .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Address, StringComparer.OrdinalIgnoreCase)
                .Take(8),
        ];
    }

    private void ForgetRecipient(string address)
    {
        if (_accounts is null) return;
        foreach (var account in _accounts.All) account.Mail.ForgetRecipient(address);
    }

    /// <summary>
    /// Feeds the list from a message that has just been queued: every recipient, under the
    /// name it was addressed with. Here rather than in the sender because the list is about
    /// what the writer chose to type, and the sender sees only the message.
    /// </summary>
    private static void RememberRecipients(OpenAccount account, MimeMessage message)
    {
        var recipients = message.To.Mailboxes
            .Concat(message.Cc.Mailboxes)
            .Concat(message.Bcc.Mailboxes)
            .Select(m => (m.Address, (string?)m.Name));

        account.Mail.RecordRecipients(recipients, DateTimeOffset.UtcNow);
    }

    /// <summary>Types into the To line as a person would, caret at the end, for the harness.</summary>
    public void PoseTyping(string text)
    {
        _to.Focus();
        _to.Text = text;
        _to.CaretIndex = text.Length;
        if (_completions.Count > 0) _completions[0].Refresh();
    }

    /// <summary>What the Auto-Complete List last offered on the To line, for the harness.</summary>
    public (bool IsOpen, int Offered) ToLineCompletion =>
        _completions.Count > 0 ? (_completions[0].IsOpen, _completions[0].Offered) : (false, 0);

    /// <summary>What the To line is offering, one line each, for the harness.</summary>
    public IReadOnlyList<string> ToLineSuggestions => _completions.Count > 0 ? _completions[0].Describe() : [];

    private static Control IconBlock(string icon, double size)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 24),
            FontFamily = IconFont.Family,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("accent.rest.brush");
        return glyph;
    }

    private void PopulateAccounts()
    {
        _sendingAddress = _accounts?.Default?.Account.Address;
        _fromAddress.Text = _sendingAddress ?? string.Empty;
    }

    /// <summary>
    /// The From button's menu: one entry per account, ticked for the one being sent from.
    /// </summary>
    /// <remarks>
    /// Filled when it opens rather than when the window is built. An account added while a
    /// message is being written — from the wizard, which this window can reach through the
    /// Backstage — would otherwise be missing from a list captured before it existed, and the
    /// only way to see it would be to close the message and start again.
    /// <para>
    /// The tick is the point of the menu as much as the choosing is. A message goes out from
    /// exactly one account and which one is not otherwise visible from here, so a list that
    /// offers four addresses and marks none of them leaves the reader counting on the field
    /// beside the button to be telling the truth.
    /// </para>
    /// </remarks>
    private List<MenuItem> AccountMenuItems()
    {
        var accounts = _accounts?.All ?? [];

        if (accounts.Count == 0)
        {
            return [new MenuItem { Header = "No account is set up yet", IsEnabled = false }];
        }

        return [.. accounts.Select(account =>
        {
            var address = account.Account.Address;
            var name = account.Account.DisplayName;

            var item = new MenuItem
            {
                // The name and the address, because two accounts at one provider are told apart
                // by the name and two names at one address by nothing at all.
                Header = string.IsNullOrWhiteSpace(name) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
                    ? address
                    : $"{name}  ({address})",
                Icon = string.Equals(address, _sendingAddress, StringComparison.OrdinalIgnoreCase)
                    ? Tick()
                    : null,
            };

            item.Click += (_, _) => SendFrom(address);
            return item;
        })];
    }

    /// <summary>The same tick the Quick Access flyout draws, from the same glyph.</summary>
    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };

    /// <summary>Starts the message from this account, for the shell to say which folder was open.</summary>
    public void SendFromAccount(string address)
    {
        if (_accounts?.Find(address) is null) return;
        _sendingAddress = address;
        _fromAddress.Text = address;

        // The signature is the account's, so a different account means a different one — or
        // none. Only while nothing has been written, which is the only time this is called.
        if (!_dirty)
        {
            InsertDefaultSignature();
            _dirty = false;
        }
    }

    /// <summary>Sends from this account, and says so where it is being read from.</summary>
    private void SendFrom(string address)
    {
        _sendingAddress = address;
        _fromAddress.Text = address;
        _dirty = true;

        Report($"This message will be sent from {address}.");
    }

    // ----------------------------------------------------------------------------------
    // Commands
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Runs a compose command. The one entry point a host's ribbon (or the Send button) routes
    /// through, so the same command from a window's ribbon, the shell's ribbon or a keystroke
    /// arrives at one place.
    /// </summary>
    /// <summary>Manage Add-ins… — the Options window is the host's, not this surface's.</summary>
    public event EventHandler? ManageAddInsRequested;

    public void Invoke(CommandId id)
    {
        if (!_catalog.TryGet(id, out var command))
        {
            Report($"No command with id '{id}'.");
            return;
        }

        // Everything that works is handled here. Everything else falls through to the status
        // line, which says what it is waiting for rather than that it is not wired.
        if (Handle(id)) return;

        var status = ComposeAvailability.For(id);
        Report(status is null
            ? $"{command.Label} — no recorded status. That is a bug in ComposeAvailability."
            : $"{command.Label} — {status.Note}");
    }

    private bool Handle(CommandId id)
    {
        if (id == ComposeCommands.Send.Id) { _ = SendAsync(); return true; }
        if (id == ComposeCommands.SaveDraft.Id) { _ = SaveDraftAsync(); return true; }
        if (id == ComposeCommands.Discard.Id) { RequestClose(); return true; }

        // Paste goes through the editor, which reads the clipboard's HTML flavour and keeps
        // the formatting. Cut, Copy and Select All are its own key handling, pressed into it.
        if (id == ComposeCommands.Paste.Id) { _ = _body.PasteFromClipboardAsync(); return true; }

        if (id == ComposeCommands.Cut.Id || id == ComposeCommands.Copy.Id
            || id == ComposeCommands.SelectAll.Id)
        {
            // Pressed into the editor rather than announced: it answers these as keys and has
            // no method for them, and a button that names its own shortcut instead of doing
            // what its label says is a button that does not work.
            _body.Focus();

            if (id == ComposeCommands.Cut.Id) _body.Cut();
            else if (id == ComposeCommands.Copy.Id) _body.Copy();
            else _body.SelectAll();
            return true;
        }

        if (id == ComposeCommands.ShowBcc.Id) { Toggle(_bccRow); return true; }
        if (id == ComposeCommands.ShowFrom.Id) { Toggle(_fromRow); return true; }

        if (id == ComposeCommands.HighImportance.Id) { SetImportance(MessageImportance.High); return true; }
        if (id == ComposeCommands.LowImportance.Id) { SetImportance(MessageImportance.Low); return true; }

        if (id == ComposeCommands.DeliveryReceipt.Id)
        {
            _wantsDeliveryReceipt = !_wantsDeliveryReceipt;
            Report($"Delivery receipt {(_wantsDeliveryReceipt ? "requested" : "not requested")}.");
            return true;
        }

        if (id == ComposeCommands.ReadReceipt.Id)
        {
            _wantsReadReceipt = !_wantsReadReceipt;
            Report($"Read receipt {(_wantsReadReceipt ? "requested" : "not requested")}.");
            return true;
        }

        if (id == ComposeCommands.Sign.Id) { Want(Protection.Sign, "signed"); return true; }
        if (id == ComposeCommands.Encrypt.Id) { Want(Protection.Encrypt, "encrypted"); return true; }

        if (id == ComposeCommands.WordCount.Id) { _ = ShowWordCountAsync(); return true; }
        if (id == ComposeCommands.Zoom.Id) { StepZoom(); return true; }
        if (id == ComposeCommands.AttachFile.Id) { _ = AttachAsync(); return true; }
        if (id == ComposeCommands.CheckNames.Id) { CheckNames(); return true; }
        if (id == MailCommands.AddressBook.Id) { _ = PickNamesAsync(); return true; }

        // All Apps opens what is really installed, through this window's own dispatcher — the
        // same menu the shell's button opens, so a plugin's command is one command whichever
        // window ran it. Manage Add-ins… belongs to the shell, which owns the Options window.
        if (id == ViewCommands.Apps.Id)
        {
            AllAppsMenu.Build(Invoke, () => ManageAddInsRequested?.Invoke(this, EventArgs.Empty))
                .ShowAt(this, showAtPointer: true);
            return true;
        }

        if (id == ComposeCommands.Find.Id || id == ComposeCommands.Replace.Id)
        {
            _ = FindAsync(replace: id == ComposeCommands.Replace.Id);
            return true;
        }

        if (HandleFormatting(id)) return true;
        if (HandleInsert(id)) return true;

        if (id == ComposeCommands.Symbol.Id) { _ = InsertSymbolAsync(); return true; }
        if (id == ComposeCommands.DelayDelivery.Id) { _ = DelayAsync(); return true; }
        if (id == ComposeCommands.DirectRepliesTo.Id) { _ = DirectRepliesAsync(); return true; }

        return false;
    }

    /// <summary>
    /// The Format Text tab, and the formatting half of Message.
    /// </summary>
    /// <remarks>
    /// Every one of these acts on the selection, which is the editor's business rather than
    /// this window's — so each is one call. What is not here is what the editor does not do:
    /// sub- and superscript, clearing formatting, paragraph marks, borders, shading and sort.
    /// Those stay recorded as blocked in <see cref="ComposeAvailability"/> with what they want,
    /// rather than being faked.
    /// <para>
    /// The choosers are the compromise. A ribbon control reports which command was pressed and
    /// never a value with it, so a font, a size, a colour or an alignment has to be asked for
    /// before the command can act. The reference uses live-previewing galleries; replacing these
    /// with those is ribbon work, and it is recorded as work still to do rather than pretended
    /// away.
    /// </para>
    /// </remarks>
    private bool HandleFormatting(CommandId id)
    {
        // Everything that is only about a document and a selection is EditorCommands' — the
        // contact window's notes are rich text too, and bold is bold in both windows.
        if (_editorCommands.Handle(id)) return true;

        if (id == ComposeCommands.FormatHtml.Id)
        {
            _plainText = false;
            ApplyAutocorrect();
            UpdateTitle();
            Report("This message will be sent as HTML.");
            return true;
        }

        if (id == ComposeCommands.FormatPlainText.Id)
        {
            // The document keeps its formatting on screen; what changes is what leaves. Saying
            // so matters, because a writer who bolded a word and sees it still bold would
            // otherwise assume it is going out that way.
            _plainText = true;
            ApplyAutocorrect();
            UpdateTitle();
            Report("This message will be sent as plain text. Formatting stays on screen and "
                + "is not sent.");
            return true;
        }

        return false;
    }

    /// <summary>The Insert tab, for the things the document model can actually hold.</summary>
    private bool HandleInsert(CommandId id)
    {
        if (id == ComposeCommands.Signature.Id) { _ = ChooseSignatureAsync(); return true; }

        if (id == ComposeCommands.Spelling.Id || id == ComposeCommands.Editor.Id)
        {
            _ = CheckSpellingAsync();
            return true;
        }

        if (_editorCommands.HandleInsert(id)) return true;
        if (id == ComposeCommands.Pictures.Id) { _ = InsertPictureAsync(); return true; }

        return false;
    }

    /// <summary>
    /// Applies something to the selection and puts the caret back where it was.
    /// </summary>
    /// <remarks>
    /// The focus is the point. Pressing a ribbon button moves focus to the button, and a second
    /// press with the caret no longer in the document formats nothing — which reads as the
    /// button having stopped working.
    /// </remarks>
    private bool Format(Action apply)
    {
        apply();
        _body.Focus();
        RaiseEnablementChanged();
        return true;
    }

    /// <summary>
    /// Shows or hides an address row, and settles the layout before returning.
    /// </summary>
    /// <remarks>
    /// The forced pass is load-bearing. Making a row visible invalidates measure, but the band
    /// it sits in keeps its old height until a layout pass runs — and the header is docked, so
    /// the body below simply paints over whatever overflows. Bcc appeared and Subject vanished
    /// underneath it.
    /// </remarks>
    private void Toggle(Control row)
    {
        row.IsVisible = !row.IsVisible;
        UpdateLayout();
    }

    private void SetImportance(MessageImportance wanted)
    {
        _importance = _importance == wanted ? MessageImportance.Normal : wanted;

        // Dirty like a keystroke: this goes out on the message, so a window that has been given
        // it has something a saved draft does not yet have.
        _dirty = true;
        UpdateStatus();
    }

    private void StepZoom()
    {
        var next = _body.DefaultFontSize + 2;
        _body.DefaultFontSize = next > 28 ? 12 : next;
        Report($"Zoom {_body.DefaultFontSize * PointsPerPixel:0}pt.");
    }

    /// <summary>
    /// Check Names: what is not an address is looked up in the address book, and replaced where
    /// exactly one contact answers to it.
    /// </summary>
    /// <remarks>
    /// One match is resolved; several are left alone and named, because picking between two
    /// people called Person is a decision the reader makes and not one a button makes for them.
    /// </remarks>
    private void CheckNames()
    {
        var resolved = 0;
        var ambiguous = new List<string>();

        if (_contacts is { } book)
        {
            foreach (var box in new[] { _to, _cc, _bcc })
            {
                var entries = Split(box.Text).ToList();
                if (entries.Count == 0) continue;

                var rewritten = new List<string>(entries.Count);
                foreach (var entry in entries)
                {
                    if (MailboxAddress.TryParse(entry, out _))
                    {
                        rewritten.Add(entry);
                        continue;
                    }

                    var found = ContactSuggestions.For(book, entry, limit: 4);
                    if (found.Count == 1)
                    {
                        rewritten.Add(found[0].Insert);
                        resolved++;
                        continue;
                    }

                    if (found.Count > 1) ambiguous.Add(entry);
                    rewritten.Add(entry);
                }

                var joined = string.Join("; ", rewritten);
                if (!string.Equals(joined, box.Text?.Trim(), StringComparison.Ordinal)) box.Text = joined;
            }
        }

        var bad = BadAddresses();

        Report(
            bad.Count > 0 ? "Could not read: " + string.Join("; ", bad)
            : ambiguous.Count > 0 ? "More than one contact answers to: " + string.Join("; ", ambiguous)
            : resolved > 0 ? $"{resolved} name(s) resolved against the address book."
            : "Every address parses.");
    }

    /// <summary>Every recipient entry that is not an address, named by its field.</summary>
    /// <summary>
    /// The Address Book, filling whichever lines names were put on. The window is the reference's
    /// own Select Names, which is why it has all three rather than only To.
    /// </summary>
    private async Task PickNamesAsync()
    {
        if (_contacts is not { } book || TopLevel.GetTopLevel(this) is not Window owner) return;

        var picked = await AddressBookDialog.PickAsync(owner, book);
        if (picked is null || picked.IsEmpty) return;

        Append(_to, picked.To);
        Append(_cc, picked.Cc);
        Append(_bcc, picked.Bcc);
        UpdateStatus();

        static void Append(TextBox box, IReadOnlyList<string> addresses)
        {
            if (addresses.Count == 0) return;
            var already = box.Text is { Length: > 0 } text ? text.TrimEnd().TrimEnd(';') + "; " : string.Empty;
            box.Text = already + string.Join("; ", addresses);
        }
    }

    private List<string> BadAddresses()
    {
        var bad = new List<string>();

        foreach (var (label, box) in new[] { ("To", _to), ("Cc", _cc), ("Bcc", _bcc) })
        {
            foreach (var entry in Split(box.Text))
            {
                if (!MailboxAddress.TryParse(entry, out _)) bad.Add($"{label}: {entry}");
            }
        }

        return bad;
    }

    /// <summary>
    /// The addresses in a field. Semicolons always separate; commas only when the Options page
    /// says so, because a display name can carry one — "Person, A." — and a reader who has
    /// turned commas off is a reader whose address book is written that way.
    /// </summary>
    private static IEnumerable<string> Split(string? value)
        => (value ?? string.Empty)
            .Split(App.MailOptions.CommasSeparateRecipients ? [',', ';'] : [';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Restates whatever is worth stating about the message, or hides the bar when nothing is.
    /// </summary>
    /// <remarks>
    /// The format is not among them: the title bar already reads "Message (HTML)", which is
    /// exactly where the reference says it.
    /// </remarks>
    private void UpdateStatus()
    {
        var bits = new List<string>();

        if (_importance != MessageImportance.Normal)
        {
            bits.Add($"This message will be sent with {_importance} importance.");
        }

        if (_notBefore is { } when)
        {
            bits.Add($"It will not be delivered before {when.LocalDateTime:g}.");
        }

        if (bits.Count == 0)
        {
            _status.Text = string.Empty;
            _infoBar.IsVisible = false;
            return;
        }

        Report(string.Join("  ", bits));
    }

    private void Report(string message)
    {
        _status.Text = message;
        _infoBar.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    // ----------------------------------------------------------------------------------
    // Dialogs and pickers
    // ----------------------------------------------------------------------------------

    private async Task AttachAsync()
    {
        if (Owner is not { StorageProvider: { } storage }) return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
        });

        if (picked.Count == 0) return;

        _attachments.AddRange(picked);
        _attachmentStrip.Text = "Attached: " +
            string.Join(", ", _attachments.Select(f => f.Name));
        _attachmentRow.IsVisible = true;

        // Attaching is a change like typing is. Only the text fields marked the surface dirty,
        // so a message whose only content was a file it had just been given looked to the close
        // prompt exactly like one nobody had touched.
        _dirty = true;
        UpdateStatus();
    }

    private async Task ShowWordCountAsync()
    {
        var text = _body.GetPlainText();
        var words = text.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        await Message("Word Count",
            $"Words: {words}\n" +
            $"Characters (with spaces): {text.Length}\n" +
            $"Characters (no spaces): {text.Count(c => !char.IsWhiteSpace(c))}\n" +
            $"Paragraphs: {paragraphs}");
    }

    private async Task FindAsync(bool replace)
    {
        var needle = await Prompt(replace ? "Replace" : "Find", "Find what:");
        if (string.IsNullOrEmpty(needle)) return;

        // The editor's own, which searches the document rather than a flattened copy of it —
        // so a match spanning two differently-formatted runs is still a match, and replacing
        // one keeps the formatting around it.
        if (!replace)
        {
            _body.Focus();

            Report(_body.FindNext(needle, matchCase: false)
                ? $"Found '{needle}'."
                : $"'{needle}' is not in the message.");

            return;
        }

        var with = await Prompt("Replace", "Replace with:") ?? string.Empty;
        var replaced = _body.ReplaceAll(needle, with, matchCase: false);

        Report(replaced == 0
            ? $"'{needle}' is not in the message."
            : $"Replaced {replaced}.");
    }

    /// <summary>
    /// The font picker, which §6 says lists the Microsoft names.
    /// </summary>
    /// <remarks>
    /// Because that is what people expect to choose, and because it is what goes on the wire:
    /// a message composed in Calibri names Calibri first, so a Windows reader gets the real
    /// font. What is rendered here is the metric-compatible substitute, and the entry says so —
    /// a reader choosing a face is entitled to know whether their recipient will see it and
    /// whether the layout will hold.
    /// </remarks>
    /// <summary>
    /// The Signature dropdown: pick one to insert, or go and edit them.
    /// </summary>
    /// <remarks>
    /// Inserted at the caret rather than appended, because that is where the reference puts it
    /// and because a reply wants it above the quoted half rather than below it.
    /// </remarks>
    private async Task ChooseSignatureAsync()
    {
        var signatures = App.Signatures.All;

        var choices = signatures
            .Select(sig => new Choice(sig.Name, sig.Name, First(sig)))
            .ToList();

        choices.Add(new Choice("Signatures…", EditSignatures, "add, change or remove one"));

        if (await Chooser.AskAsync(Host, "Signature", "Insert:", choices) is not { } chosen) return;

        if (chosen == EditSignatures)
        {
            await EditSignaturesAsync();
            return;
        }

        if (App.Signatures.Find(chosen) is { } signature) Insert(signature);
    }

    /// <summary>
    /// Signs a new message, where the account has said to.
    /// </summary>
    /// <remarks>
    /// Nothing happens unless somebody chose one — a client that puts a block of text on the
    /// first message you ever write is one you have to go and find a setting to stop.
    /// </remarks>
    private void InsertDefaultSignature()
    {
        if (SendingAccount() is not { } account) return;

        if (App.Signatures.ForNew(account.Account.Address) is not { IsEmpty: false } signature)
        {
            // No signature for this account: an empty document, which also undoes the one
            // that was put in for the account this window started from.
            _body.Clear();
            return;
        }

        // Two blank lines above it, as every mail client does. LoadHtml rather than InsertHtml,
        // and the difference is where the caret ends up: InsertHtml leaves it after what it
        // inserted, so the writer would type below their own signature. LoadHtml starts a fresh
        // document with the caret at the top, which is where the reply goes.
        _body.LoadHtml("<p>&nbsp;</p><p>&nbsp;</p>" + signature.Html);
    }

    /// <summary>The first line of a signature, so the list says which one it is.</summary>
    private static string First(Signature signature)
    {
        var text = signature.Text is { Length: > 0 } t ? t : signature.Html;

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                   ?? string.Empty;

        return line.Length > 60 ? line[..60] + "…" : line;
    }

    /// <summary>Puts a signature into the document, and remembers it went in.</summary>
    private void Insert(Signature signature)
    {
        if (signature.IsEmpty) return;

        // The markup where there is any, so a formatted signature stays formatted. The text is
        // what the plain half of the message will carry, and the serializer produces that from
        // the document — so inserting the HTML is enough for both.
        if (signature.Html is { Length: > 0 } html) _body.InsertHtml(html);
        else _body.InsertText(signature.Text);

        _body.Focus();
        Report($"Inserted the {signature.Name} signature.");
    }

    /// <summary>The shared editor, over this window and its sending account.</summary>
    private Task EditSignaturesAsync()
        => new StationeryDialog(App.Signatures, App.Stationery, App.Accounts.All, SendingAccount()?.Account.Address, tab: 0).ShowDialog(Host);

    /// <summary>
    /// Spelling, over the whole message.
    /// </summary>
    /// <remarks>
    /// A pass rather than squiggles as you type, which is what §7.3 asks for and what the editor
    /// cannot do: underlining a word as it is typed needs the editor to draw on its own text run,
    /// and it exposes nothing for that. This is the reference's F7 — walk what is not in the
    /// dictionary, offer what is, and let a word be kept.
    /// <para>
    /// The dictionary is the desktop's own, and there may be none. Saying so once is the whole
    /// of the handling that needs: a mail client that nags about a missing word list is worse
    /// than one that quietly cannot check.
    /// </para>
    /// </remarks>
    /// <param name="quietWhenClean">
    /// Say nothing when there is nothing to say — for the pass Send runs, where a dialog
    /// reporting no misspellings on the way out of every message would be a nag.
    /// </param>
    private async Task CheckSpellingAsync(bool quietWhenClean = false)
    {
        await EnsureSpellingAsync();

        if (_spelling is null || !_spelling.IsAvailable)
        {
            if (quietWhenClean) return;

            await Message("Spelling",
                "No dictionary is installed, so spelling cannot be checked.\n\n"
                + "Install a Hunspell dictionary — hunspell-en_gb, hunspell-en_us or the one for "
                + "your language — and it will be found next time.");
            return;
        }

        var text = _body.GetPlainText();
        var found = _spelling.Check(text);

        if (found.Count == 0)
        {
            if (quietWhenClean) return;

            await Message("Spelling",
                $"The spelling check is complete. Nothing was found, against {_spelling.Language}.");
            return;
        }

        // One word at a time, in order, as the reference does — and stopping the moment the
        // reader dismisses, because a dialog per word is a thing to be able to get out of.
        var corrected = 0;

        foreach (var word in found.DistinctBy(w => (w.Word, w.IsRepeated)))
        {
            var choices = new List<Choice> { new("Ignore", string.Empty, "leave it as written") };

            if (word.IsRepeated)
            {
                // "the the": the reference offers to delete the second one.
                choices.Add(new Choice("Delete repeated word", DeleteRepeated, "keep one of the two"));
            }
            else
            {
                choices.AddRange(_spelling.Suggest(word.Word)
                    .Select(s => new Choice(s, s, "replace every one in this message")));

                choices.Add(new Choice("Add to dictionary", AddToDictionary,
                    "keep it, and stop asking about it"));
            }

            var answer = await Chooser.AskAsync(
                Host, "Spelling", word.IsRepeated ? $"Repeated word: {word.Word} {word.Word}" : $"Not in the dictionary: {word.Word}", choices);

            if (answer is null) break;

            if (answer == AddToDictionary)
            {
                _spelling.Add(word.Word);
                continue;
            }

            if (answer == DeleteRepeated)
            {
                // Each "word word" becomes "word": the doubled form replaced by the single, every
                // time it occurs, which is what the reader asked for on seeing the first.
                corrected += _body.ReplaceAll($"{word.Word} {word.Word}", word.Word, matchCase: false);
                continue;
            }

            if (answer.Length == 0) continue;

            corrected += _body.ReplaceAll(word.Word, answer, matchCase: true);
        }

        _body.Focus();

        Report(corrected == 0
            ? "The spelling check is complete."
            : $"The spelling check is complete. {corrected} replaced.");
    }

    /// <summary>
    /// Loads the dictionary once, for the F7 pass and for autocorrect's suggestions.
    /// </summary>
    /// <remarks>
    /// One checker rather than two: they would each hold a few megabytes of word list, and the
    /// words taught to one would be unknown to the other.
    /// </remarks>
    private async Task EnsureSpellingAsync()
    {
        _spelling ??= await SpellCheck.LoadAsync(personalPath: PersonalDictionaryPath());

        // The Proofing switches, read each time so an Options change shows on the next word.
        _spelling.Options = new SpellCheckOptions(
            App.MailOptions.SpellingIgnoresUppercase,
            App.MailOptions.SpellingIgnoresNumbers,
            App.MailOptions.SpellingIgnoresAddresses,
            App.MailOptions.SpellingFlagsRepeated);
    }

    /// <summary>
    /// The AutoCorrect dialog's switches, its table and its exceptions, as the body's corrector.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than patched whenever any of them changes: the table and the two exception
    /// lists are read from JSON, which is cheap next to how rarely somebody presses OK in that
    /// dialog, and one construction path means the switches cannot drift out of step with the
    /// lists they act on.
    /// <para>
    /// The suggestion rule reads the same checker the F7 pass uses, through a delegate rather
    /// than a reference, so that a machine with no dictionary — or one where it has not finished
    /// loading — simply never fires that rule.
    /// </para>
    /// </remarks>
    private void ApplyAutocorrect()
    {
        var mail = App.MailOptions;

        var options = new AutocorrectOptions
        {
            ReplaceAsYouType = mail.AutocorrectReplaces,
            TwoInitialCapitals = mail.AutocorrectTwoInitials,
            CapitalizeSentences = mail.AutocorrectSentences,
            CapitalizeTableCells = mail.AutocorrectTableCells,
            CapitalizeDays = mail.AutocorrectDays,
            CapsLock = mail.AutocorrectCapsLock,
            UseSpellingSuggestions = mail.AutocorrectSuggestions,
            MathReplacements = mail.AutocorrectMath,
            SmartQuotes = mail.AutoformatQuotes,
            Fractions = mail.AutoformatFractions,
            Dashes = mail.AutoformatDashes,
            BoldAndItalic = mail.AutoformatEmphasis,
            Hyperlinks = mail.AutoformatHyperlinks,
            BulletedLists = mail.AutoformatBullets,
            NumberedLists = mail.AutoformatNumbering,
            BorderLines = mail.AutoformatBorders,
        };

        _body.Autocorrect = new Autocorrect(
            options,
            AutocorrectTable.FromJson(mail.AutocorrectTable),
            AutocorrectExceptions.FromJson(mail.AutocorrectExceptions),
            word => _spelling?.IsCorrect(word) ?? true,
            word => _spelling?.Suggest(word) ?? []);

        // Formatting a correction carries is only ever formatting this message can send: in
        // plain text the stars stay as stars, which is what the recipient would have seen.
        _body.AllowFormatting = !_plainText;

        // "Internet and network paths with hyperlinks" is the editor's own switch rather than a
        // rule of ours, so the dialog's checkbox is passed straight through to it.
        _body.AutoLinkOnType = options.Hyperlinks;

        // The dictionary, in the background: one of the rules is the checker's own suggestion,
        // and waiting for the first F7 to load it would mean that rule never fires while the
        // first message is being written.
        if (options.UseSpellingSuggestions && _spelling is null) _ = EnsureSpellingAsync();
    }

    /// <summary>
    /// Autosave, on the Options page's interval. Zero is off.
    /// </summary>
    /// <remarks>
    /// Only when something has changed since the last save, so an idle surface does not rewrite
    /// its draft every few minutes for nothing. Rebuilt rather than read once: the interval was
    /// taken at construction, so changing it on the Options page reached the next window and no
    /// window already open — including the one somebody had just been told to change it for.
    /// </remarks>
    private void ApplyAutosaveInterval()
    {
        var minutes = App.MailOptions.AutosaveMinutes;

        _autosave?.Stop();
        _autosave = null;

        if (minutes <= 0) return;

        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _autosave.Tick += (_, _) => { if (_dirty && !_sent && HasContent()) _ = SaveDraftAsync(); };

        // Started here when the surface is already on screen: the attach handler that usually
        // starts it has long since run.
        if (!_sent && IsAttachedToVisualTree()) _autosave.Start();
    }

    /// <summary>Whether this surface is on screen, which is when its timer should be running.</summary>
    private bool IsAttachedToVisualTree() => VisualRoot is not null;

    /// <summary>A settings change this surface has to hear about.</summary>
    private void OnSettingChanged(object? sender, string key)
    {
        if (key.StartsWith("mail.autocorrect", StringComparison.Ordinal)
            || key.StartsWith("mail.autoformat", StringComparison.Ordinal)
            || key.StartsWith("mail.spelling", StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(ApplyAutocorrect);
        }

        if (key == MailOptions.AutosaveMinutesKey) Dispatcher.UIThread.Post(ApplyAutosaveInterval);
    }

    /// <summary>Beside the mail, not in the system dictionary, which is not ours to edit.</summary>
    internal static string PersonalDictionaryPath()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(data, "mailbox", "personal.dic");
    }

    /// <summary>
    /// A picture from a file, which is the only source the sender's own machine offers.
    /// </summary>
    /// <remarks>
    /// It becomes a related part and a <c>cid:</c> reference when the message is built — the
    /// serializer asks for that — so the recipient gets the image with the mail rather than a
    /// request back to somewhere.
    /// </remarks>
    private async Task InsertPictureAsync()
    {
        if (Owner is not { StorageProvider: { } storage }) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert Picture",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        if (files.FirstOrDefault() is not { } file) return;

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            _body.InsertImageBytes(buffer.ToArray());
            _body.Focus();
            Report($"Inserted {file.Name}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not insert a picture.", ex);
            Report("That picture could not be read.");
        }
    }

    private async Task InsertSymbolAsync()
    {
        var symbol = await Prompt("Symbol", "Character to insert:");
        if (string.IsNullOrEmpty(symbol)) return;

        Insert(symbol);
    }

    private void Insert(string text)
    {
        _body.InsertText(text);
        _body.Focus();
    }

    private async Task DelayAsync()
    {
        var entered = await Prompt("Delay Delivery",
            "Do not deliver before (yyyy-MM-dd HH:mm), or blank to clear:");

        if (entered is null) return;

        if (string.IsNullOrWhiteSpace(entered))
        {
            _notBefore = null;
            _dirty = true;
            UpdateStatus();
            return;
        }

        if (!DateTime.TryParse(entered, CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal, out var when))
        {
            Report($"Could not read '{entered}' as a date and time.");
            return;
        }

        _notBefore = new DateTimeOffset(when);
        _dirty = true;
        UpdateStatus();
    }

    private async Task DirectRepliesAsync()
    {
        var entered = await Prompt("Direct Replies To", "Send replies to:");
        if (entered is null) return;

        if (string.IsNullOrWhiteSpace(entered))
        {
            _replyTo = null;
            _dirty = true;
            Report("Replies go to the sending account.");
            return;
        }

        if (!MailboxAddress.TryParse(entered, out _))
        {
            Report($"Could not read '{entered}' as an address.");
            return;
        }

        _replyTo = entered;
        _dirty = true;
        Report($"Replies will go to {entered}.");
    }

    // ----------------------------------------------------------------------------------
    // Send and save
    // ----------------------------------------------------------------------------------

    private OpenAccount? SendingAccount()
    {
        if (_accounts is null) return null;

        return _sendingAddress is null
            ? _accounts.Default
            : _accounts.Find(_sendingAddress) ?? _accounts.Default;
    }

    private async Task SendAsync()
    {
        if (SendingAccount() is not { } account)
        {
            Report("No account is set up yet. Add one under File in the main window.");
            return;
        }

        if (Split(_to.Text).Concat(Split(_cc.Text)).Concat(Split(_bcc.Text)).Any() is false)
        {
            Report("Add at least one recipient.");
            return;
        }

        // "Automatic name checking": an address that will not parse stops the send here, with
        // the field named, rather than failing at the server with a message about a mailbox.
        if (App.MailOptions.AutomaticNameChecking && BadAddresses() is { Count: > 0 } bad)
        {
            Report("Could not read: " + string.Join("; ", bad));
            return;
        }

        // "Always check spelling before sending": the same pass the button runs, and then the
        // send goes ahead — a spelling check is a chance to fix things, not a gate.
        if (App.MailOptions.CheckSpellingBeforeSend) await CheckSpellingAsync(quietWhenClean: true);

        MimeMessage message;
        try
        {
            message = await BuildMessageAsync(account);
        }
        catch (Exception ex)
        {
            Report($"Could not build the message: {ex.Message}");
            return;
        }

        // A plugin may stop a send (§13), and it is asked before the cryptography for the same
        // reason the cryptography runs last: a message stopped after signing was signed for
        // nothing. The refusal names the plugin, so the writer knows what stood in the way.
        if (App.Plugins.BeforeSend(account.Account.Address, message) is { } stopped)
        {
            Report($"{stopped.PluginName} stopped the send: {stopped.Reason}");
            return;
        }

        // Signed and sealed here and nowhere else: immediately before it goes, over the message as
        // it will actually be sent, once the writer has decided to send it (§19). A refusal stops
        // the send with the message intact rather than sending it in the clear.
        if (!await ProtectAsync(message)) return;

        try
        {
            var sender = new SmtpSender(account.Mail);
            var outboxId = sender.Queue(account.Account.Id, message);
            RememberRecipients(account, message);

            // Delayed delivery is the reader's own choice about this message, so it wins over
            // the few seconds Undo Send holds everything for — asking to send on Thursday and
            // getting a five-second grace period instead would be the wrong way round.
            var undo = _notBefore is null
                ? App.UndoSend.HoldUntil(DateTimeOffset.UtcNow)
                : null;

            if (_notBefore is { } when) account.Mail.ScheduleOutbox(outboxId, when);
            else if (undo is { } until) account.Mail.ScheduleOutbox(outboxId, until);

            _sent = true;
            _autosave?.Stop();

            // A draft that has been sent is no longer a draft.
            if (_draftId is { } draft) account.Mail.DeleteMessage(draft);

            Report(_notBefore is { } held
                ? $"Queued, held until {held.LocalDateTime:g}."
                : App.MailOptions.SendImmediately
                    ? "Sending."
                    : "Queued in the Outbox. It goes out on the next send/receive.");

            // The window is about to close, so whoever opened it is who offers the way back —
            // and who sends it when the hold is up. Not raised for a message the writer asked to
            // hold until a chosen time: that one goes on the schedule, as asked, and offering
            // to undo Thursday's message today would be odd.
            if (_notBefore is null)
            {
                Queued?.Invoke(this, new QueuedMessageEventArgs(
                    account.Account.Address, outboxId, undo ?? DateTimeOffset.UtcNow,
                    message.Subject ?? string.Empty));
            }

            RequestClose();
        }
        catch (Exception ex)
        {
            Report($"Could not queue the message: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------------------
    // Signing and encrypting (Phase 15)
    // ----------------------------------------------------------------------------------

    /// <summary>Puts one of the two buttons down or up, and says what it now means.</summary>
    private void Want(Protection what, string word)
    {
        // Neither algorithm switched on means nothing to do it with, and a button that goes down
        // over an empty Trust Center is a promise the send would have to break (§14).
        if (!App.Security.Smime && !App.Security.OpenPgp)
        {
            Report("Turn on S/MIME or OpenPGP under File · Options · Trust Center first.");
            return;
        }

        _protection ^= what;
        _dirty = true;

        Report(_protection.HasFlag(what)
            ? $"This message will be {word}."
            : $"This message will not be {word}.");
    }

    /// <summary>
    /// Applies what the two buttons asked for, and says why if it cannot. False stops the send.
    /// </summary>
    /// <remarks>
    /// A locked key is the one refusal worth acting on rather than reporting: the material is here
    /// and nobody has said what opens it, so the reader is asked and the whole thing runs again —
    /// exactly once, because a second refusal after a passphrase that was accepted is not about the
    /// passphrase.
    /// </remarks>
    private async Task<bool> ProtectAsync(MimeMessage message)
    {
        // Before the toggles are even looked at, because the message this answers gets a say: a
        // field it kept back does not go out in the clear here (§6.1). See Answering.
        if (Exposed() is { Count: > 0 } exposed)
        {
            Report("This message answers one that kept its " + Names(exposed)
                + " out of the clear, so it cannot be sent unencrypted. "
                + "Press Encrypt, or take that out of this message.");

            Log.Info($"Harness: protection — refused, would expose {string.Join(", ", exposed)}.");
            return false;
        }

        if (_protection == Protection.None) return true;

        // So the list of keys to ask about is this attempt's own rather than a previous send's.
        CryptoStores.Passphrases.Clear();

        var report = await Task.Run(() => Protect(message, draft: false));

        if (report.State == ProtectionState.Locked && await UnlockAsync())
        {
            report = await Task.Run(() => Protect(message, draft: false));
        }

        // What the message actually became, which is the only way to check the claim: a shape here
        // is what the reading pane will be handed at the other end.
        Log.Info(
            $"Harness: protection — asked for {_protection}, came to {report.State} "
            + $"(body {message.Body?.ContentType.MimeType ?? "none"}). {report.Detail}");

        if (report.MaySend) return true;

        Report(report.Detail);
        return false;
    }

    /// <summary>
    /// Opens both stores, applies what was asked for, and closes them again.
    /// </summary>
    /// <remarks>
    /// Off the UI thread for a send — signing is a public-key operation over the whole message and
    /// the attachments go through it. A null store is an algorithm the reader has not turned on,
    /// which is a different answer from one that has no keys.
    /// </remarks>
    private ProtectionReport Protect(MimeMessage message, bool draft)
    {
        using var certificates = CryptoStores.CertificatesIfEnabled();
        using var keys = CryptoStores.KeyRingIfEnabled();

        return draft
            ? MessageProtection.ApplyToDraft(message, _protection, certificates, keys)
            : MessageProtection.Apply(message, _protection, certificates, keys);
    }

    /// <summary>
    /// Which of the answered message's confidential fields this one would send in the clear.
    /// </summary>
    /// <remarks>
    /// A field is safe when this message is going out encrypted <em>and</em> the policy that reduces
    /// its outer header section hides that field too — which is why the question is asked of the
    /// policy rather than answered from a list here: the two have to agree, and only one of them can
    /// be right about what it does.
    /// </remarks>
    private List<string> Exposed()
    {
        if (_confidential.Count == 0) return [];
        if (!_protection.HasFlag(Protection.Encrypt)) return [.. _confidential];

        return [.. _confidential.Where(name => !HeaderConfidentiality.Baseline.Hides(name))];
    }

    /// <summary>Header field names as a sentence: "subject", or "subject and keywords".</summary>
    private static string Names(IReadOnlyList<string> fields)
    {
        var words = fields.Select(f => f.ToLowerInvariant()).ToList();

        return words.Count switch
        {
            0 => "header fields",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
        };
    }

    /// <summary>Asks for whatever the last attempt could not open. False if the reader declines.</summary>
    private async Task<bool> UnlockAsync()
    {
        var wanted = CryptoStores.Passphrases.Wanted;
        if (wanted.Count == 0) return false;

        using var keys = CryptoStores.KeyRingIfEnabled();
        if (keys is null) return false;

        return await PassphraseDialog.UnlockAsync(Host, keys, CryptoStores.Passphrases, wanted);
    }

    private async Task<MimeMessage> BuildMessageAsync(OpenAccount account)
    {
        var address = account.Account.Address;
        var domain = address.Contains('@', StringComparison.Ordinal)
            ? address[(address.LastIndexOf('@') + 1)..]
            : "localhost";

        var message = new MimeMessage
        {
            // Under the sender's own domain, as every client does. Left alone, MimeKit stamps
            // the machine's hostname into it — and into every cid: below — which is a name a
            // recipient has no business learning from a message header. §19.
            MessageId = MimeUtils.GenerateMessageId(domain),
        };

        // A display name only when there is one. The seed, and an account added with nothing
        // but an address, hold the address in that slot too — and "you@example.com"
        // <you@example.com> is what a mail client writes when nobody was looking.
        var name = account.Account.DisplayName;
        message.From.Add(string.IsNullOrWhiteSpace(name) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
            ? new MailboxAddress(string.Empty, address)
            : new MailboxAddress(name, address));

        foreach (var entry in Split(_to.Text)) message.To.Add(MailboxAddress.Parse(entry));
        foreach (var entry in Split(_cc.Text)) message.Cc.Add(MailboxAddress.Parse(entry));
        foreach (var entry in Split(_bcc.Text)) message.Bcc.Add(MailboxAddress.Parse(entry));

        if (_replyTo is { } reply) message.ReplyTo.Add(MailboxAddress.Parse(reply));

        message.Subject = _subject.Text ?? string.Empty;
        message.Importance = _importance;
        message.Priority = _importance switch
        {
            MessageImportance.High => MessagePriority.Urgent,
            MessageImportance.Low => MessagePriority.NonUrgent,
            _ => MessagePriority.Normal,
        };

        if (_wantsReadReceipt)
        {
            message.Headers.Add("Disposition-Notification-To", account.Account.Address);
        }

        if (_wantsDeliveryReceipt)
        {
            message.Headers.Add("Return-Receipt-To", account.Account.Address);
        }

        // The Options page's default sensitivity. Normal is the header's absence.
        if (App.MailOptions.DefaultSensitivityHeader is { } sensitivity)
        {
            message.Headers.Add("Sensitivity", sensitivity);
        }

        // What lets the recipient's client put a reply under the message it answers.
        if (_inReplyTo is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
            foreach (var reference in _references) message.References.Add(reference);
        }

        // Both halves, always. A recipient whose client shows plain text — or who has told it
        // to — gets a readable message rather than a page of markup, and the two are the same
        // message rather than two that can disagree, because both come off one document.
        //
        // U+FFFC is what the editor puts in its plain text where a picture is. It is a
        // placeholder for a rendering system, not a character for a person, and a text-only
        // reader would see it as a box. The picture is simply absent from the text half, which
        // is what every other client does.
        var builder = new BodyBuilder
        {
            TextBody = _body.GetPlainText().Replace("\uFFFC", string.Empty, StringComparison.Ordinal),
        };

        // Ours, not the editor's: §6's wire/render split and the narrow set of elements mail
        // clients actually render. See Mailbox.Editor.EmailHtml for why that is the half worth
        // keeping in-house. Unless this message is going as plain text, in which case the text
        // half is the message and there is no other.
        if (!_plainText) builder.HtmlBody = EmailHtml.Serialize(_body.Document ?? new FlowDocument(), new EmailHtmlOptions
        {
            BaseFontFamily = _font.Family,
            BaseFontPoints = _font.Points,
            BaseColour = _font.Colour,

            // An image the writer put in the body becomes a related part and a cid: reference,
            // which is how mail carries one. Several large clients drop a data: image outright.
            RegisterImage = (bytes, type) =>
            {
                var extension = type.Split('/').ElementAtOrDefault(1) ?? "png";
                var part = builder.LinkedResources.Add(
                    $"image-{builder.LinkedResources.Count + 1}.{extension}", bytes);

                part.ContentId = MimeUtils.GenerateMessageId(domain);
                return $"cid:{part.ContentId}";
            },
        });

        foreach (var file in _attachments)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            builder.Attachments.Add(file.Name, buffer.ToArray());
        }

        // A forward's attachments, or an attached original — already MIME, carried as they are
        // rather than decoded and re-encoded, which would be a lossy trip for no reason.
        foreach (var carried in _carried) builder.Attachments.Add(carried.Entity);

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>Saves the message to Drafts, replacing the row it is editing. Public so a host can save on close.</summary>
    /// <remarks>
    /// Awaited rather than blocked on. <see cref="BuildMessageAsync"/> reads every attachment off
    /// the disk, and its continuations come back to the dispatcher; pulling the result out with
    /// <c>GetAwaiter().GetResult()</c> on the UI thread deadlocked the application outright the
    /// moment a message carried an attachment large enough for the copy to go asynchronous —
    /// including from the autosave timer, which needs nobody to press anything.
    /// </remarks>
    public async Task SaveDraftAsync()
    {
        if (SendingAccount() is not { } account)
        {
            Report("No account is set up yet, so there is no Drafts folder to save into.");
            return;
        }

        try
        {
            var drafts = account.Mail.Folders(account.Account.Id)
                .FirstOrDefault(f => f.Role == FolderRole.Drafts);

            if (drafts is null)
            {
                Report("This account has no Drafts folder.");
                return;
            }

            var message = await BuildMessageAsync(account);

            // A draft is never signed and is encrypted to its author alone (§19) — the recipient
            // fields are the part a mailto: link gets to choose, and a signature is a statement made
            // when somebody decides to send something, not every few minutes by an autosave. A draft
            // that cannot be encrypted is not saved in the clear instead: the writer is told, and
            // what they typed stays in the window where they can still see it.
            var report = Protect(message, draft: true);
            if (!report.MaySend)
            {
                Report(report.Detail);
                return;
            }

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            var summary = MessageMapper.ToSummary(message, null, raw.Length, DateTimeOffset.UtcNow)
                with { IsRead = true };

            // Replaces rather than adds. Saving twice used to leave two drafts, and autosave on
            // top of that would have left one every few minutes; the row this window is editing
            // is the one that goes, and the new one takes its place.
            if (_draftId is { } previous) account.Mail.DeleteMessage(previous);
            _draftId = account.Mail.AddMessage(drafts.Id, summary, raw);

            // Not _sent — the message has not gone anywhere. Clearing _dirty is the whole of
            // what a save means: there is nothing unwritten now, and the next keystroke says
            // there is again.
            _dirty = false;
            Report("Saved to Drafts.");
        }
        catch (Exception ex)
        {
            Report($"Could not save the draft: {ex.Message}");
        }
    }

    /// <summary>
    /// True when there is anything worth keeping — a recipient, a subject, a body, an
    /// attachment. The host asks this before dismissing, to decide whether to offer a save.
    /// </summary>
    public bool HasContent()
        => !string.IsNullOrWhiteSpace(_to.Text)
           || !string.IsNullOrWhiteSpace(_cc.Text)
           || !string.IsNullOrWhiteSpace(_bcc.Text)
           || !string.IsNullOrWhiteSpace(_subject.Text)
           || !string.IsNullOrWhiteSpace(_body.GetPlainText())
           || _attachments.Count > 0;

    // ----------------------------------------------------------------------------------
    // Small dialogs
    // ----------------------------------------------------------------------------------

    private async Task Message(string title, string body)
    {
        var dialog = SmallDialog(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            out var panel);

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);

        await dialog.ShowDialog(Host);
    }

    private async Task<string?> Prompt(string title, string label, string value = "")
    {
        var input = new TextBox
        {
            MinWidth = 320,
            Text = value,

            // A signature is several lines, and so is anything else worth offering a starting
            // value for. One that cannot hold a second line would be a box for a name only.
            AcceptsReturn = true,
            MaxHeight = 220,
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = label }, input },
        };

        var dialog = SmallDialog(title, content, out var panel);
        string? answer = null;

        var ok = new Button { Content = "OK" };
        ok.Click += (_, _) => { answer = input.Text ?? string.Empty; dialog.Close(); };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close();

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        });

        input.AttachedToVisualTree += (_, _) => input.Focus();
        await dialog.ShowDialog(Host);
        return answer;
    }

    /// <remarks>
    /// Chromed through <see cref="DialogChrome"/> like every other dialog. Setting the window's
    /// <c>Content</c> here instead left these seven — Word Count, Find,
    /// Replace, the two spelling reports, Symbol, Delay Delivery and Direct Replies To — wearing
    /// the desktop's title bar and its close button in the middle of a themed compose window.
    /// The caller keeps adding buttons to <paramref name="panel"/> afterwards, which still works:
    /// the panel is the same instance, now inside the frame rather than directly in the window.
    /// </remarks>
    private static Window SmallDialog(string title, Control content, out StackPanel panel)
    {
        panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(content);

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(dialog, panel);
        return dialog;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
