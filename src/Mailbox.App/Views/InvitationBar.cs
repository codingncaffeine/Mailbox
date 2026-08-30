using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The invitation strip above the reading pane: what the meeting is, and Accept, Tentative and
/// Decline.
/// </summary>
/// <remarks>
/// This is what makes the calendar useful with no scheduling server anywhere — iMIP carries the
/// whole exchange over ordinary mail, and the reference's own bar is the one people know. It
/// writes into the calendar through the same path a typed appointment takes, and hands the reply
/// back to the shell to send, because a bar cannot own an outbox.
/// </remarks>
public sealed class InvitationBar : Border
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The <c>text/calendar</c> part of a message, or null when there is none.</summary>
    public static ItipMessage? Read(MimeMessage? message)
    {
        if (message is null) return null;

        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            if (!part.ContentType.IsMimeType("text", "calendar")) continue;
            if (Imip.Read(part.Text) is { } invitation) return invitation;
        }

        // Some senders attach the invitation as application/ics rather than as a text part.
        foreach (var attachment in message.Attachments.OfType<MimePart>())
        {
            if (attachment.FileName is not { Length: > 0 } name || !name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) continue;
            if (attachment.Content is null) continue;
            using var reader = new StreamReader(attachment.Content.Open());
            if (Imip.Read(reader.ReadToEnd()) is { } invitation) return invitation;
        }

        return null;
    }

    /// <summary>What was answered, so the shell can send the reply and refresh the calendar.</summary>
    public sealed record Answer(ItipMessage Invitation, ItipResponse Response, bool SendReply, string Payload, PimItem? Stored);

    public event EventHandler<Answer>? Answered;

    /// <summary>A cancellation's Remove from Calendar was pressed and the rows are gone.</summary>
    /// <remarks>
    /// Raised so the shell can say so and reload the calendar: without it a workspace already
    /// built kept drawing a meeting the store no longer held, and nothing said anything.
    /// </remarks>
    public event EventHandler? Removed;

    private readonly ItipMessage _invitation;
    private readonly string _address;
    private readonly PimRepository _repository;
    private readonly CheckBox _sendReply = new() { Content = "Send the reply now", IsChecked = true };

    public InvitationBar(ItipMessage invitation, string address, PimRepository repository, string? organizerName = null)
    {
        _invitation = invitation ?? throw new ArgumentNullException(nameof(invitation));
        _address = address ?? string.Empty;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        Padding = new Thickness(14, 8);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Bind(this, BackgroundProperty, "reading.infobar.background.brush");
        Bind(this, BorderBrushProperty, "border.subtle.brush");

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("meeting", 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");

        var headline = new TextBlock
        {
            Text = Imip.Headline(invitation, organizerName),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        Bind(headline, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        // On the reader's own clock, not the organizer's: accepting writes the instant, and a bar
        // that states the organizer's wall time disagrees with the block it is about to draw.
        var detail = new TextBlock
        {
            Text = Imip.Describe(invitation, reader: TimeZoneInfo.Local),
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(detail, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        var lines = new StackPanel { Spacing = 3, Children = { headline, detail } };

        // Only a message that asks for an answer gets buttons. A cancellation and a reply are
        // told, not asked — the reference shows the same bar without the three.
        if (invitation.WantsReply)
        {
            Bind(_sendReply, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "reading.infobar.text.brush");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
                Children =
                {
                    Button("Accept", ItipResponse.Accepted),
                    Button("Tentative", ItipResponse.Tentative),
                    Button("Decline", ItipResponse.Declined),
                    _sendReply,
                },
            };
            lines.Children.Add(buttons);
        }
        else if (invitation.Method == ItipMethod.Cancel)
        {
            var remove = new Button { Content = "Remove from Calendar", Margin = new Thickness(0, 8, 0, 0) };
            remove.Click += (_, _) => Cancel();
            lines.Children.Add(remove);
        }
        else if (invitation.Method == ItipMethod.Reply)
        {
            // An answer to a meeting this reader organises: applied to the meeting the moment
            // the reply is read, which is what fills the Tracking tab in. Imip.Apply's Reply
            // branch was correct, unit-tested, and called by nothing — an ACCEPTED reply left
            // its attendee NEEDS-ACTION for ever.
            ApplyReply();
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        Child = row;
    }

    private Button Button(string label, ItipResponse response)
    {
        var button = new Button { Content = label, MinWidth = 84 };
        button.Click += (_, _) => Respond(response);
        return button;
    }

    /// <summary>
    /// Writes the meeting into the calendar and reports the reply. The store is the authority
    ///: accepting is a write here, and the mail that goes out is a consequence of it.
    /// </summary>
    public void Respond(ItipResponse response)
    {
        var calendar = _repository.DefaultCalendar();
        var existing = _repository.ItemsByUid(calendar.Id, _invitation.Event.Uid).FirstOrDefault(i => !i.IsOverride);
        var current = existing is null ? null : PimEventCodec.FromItem(existing);

        PimItem? stored = null;
        if (Imip.Apply(_invitation, current, response, _address) is { } updated)
        {
            var row = PimEventCodec.ToItem(updated, calendar.Id, existing);
            stored = existing is null ? _repository.AddItem(row) : Save(row);
            App.PimSync.QueuePut(stored);
        }

        var payload = Imip.Reply(_invitation, _address, response);
        Log.Info($"Invitation: {response}, item {stored?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}.");
        Log.Debug($"Invitation: the event is “{_invitation.Event.Summary}”.");
        Answered?.Invoke(this, new Answer(_invitation, response, _sendReply.IsChecked == true, payload, stored));
        IsVisible = false;
    }

    private PimItem Save(PimItem row)
    {
        _repository.UpdateItem(row);
        return row;
    }

    /// <summary>Writes an arriving answer onto the meeting it answers, wherever it is filed.</summary>
    private void ApplyReply()
    {
        foreach (var calendar in _repository.Collections(Mailbox.Store.Pim.CollectionKind.Events))
        {
            foreach (var item in _repository.ItemsByUid(calendar.Id, _invitation.Event.Uid))
            {
                var meeting = PimEventCodec.FromItem(item);
                if (Imip.Apply(_invitation, meeting) is not { } updated || updated.Equals(meeting)) continue;

                var row = item with { RawPayload = PimEventCodec.ToItem(updated, item.CollectionId).RawPayload };
                _repository.UpdateItem(row);
                App.PimSync.QueuePut(row);
                Log.Info($"Invitation: an answer to “{meeting.Summary}” was written onto the meeting.");
            }
        }
    }

    private void Cancel()
    {
        var calendar = _repository.DefaultCalendar();
        foreach (var item in _repository.ItemsByUid(calendar.Id, _invitation.Event.Uid))
        {
            App.PimSync.Remove(item);
        }

        Log.Info("Invitation: the event was removed after a cancellation.");
        IsVisible = false;
        Removed?.Invoke(this, EventArgs.Empty);
    }
}
