using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using MimeKit;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// One identity: the name and address a message goes out as, where its replies should go, and
/// the organization it is written from.
/// </summary>
/// <remarks>
/// Four fields and no more. Everything else an identity could carry is already chosen per
/// address elsewhere and would be a second place to say it: the signature is picked in the
/// Signatures window, which has been keyed by address since it was written, and the outgoing
/// server belongs to the account, because an identity is a From line rather than a second
/// connection.
/// </remarks>
public sealed class IdentityDialog : Window
{
    private readonly TextBox _name = Field();
    private readonly TextBox _address = Field();
    private readonly TextBox _replyTo = Field("Leave empty to receive replies at the address above");
    private readonly TextBox _organization = Field();
    private readonly TextBlock _error = Label(string.Empty);
    private readonly IReadOnlyList<string> _taken;

    /// <summary>What was entered, or null when the window was cancelled.</summary>
    public Identity? Result { get; private set; }

    public IdentityDialog(Identity identity, IReadOnlyList<string> taken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(taken);

        _taken = taken;
        Title = identity.Address.Length > 0 ? "Change Identity" : "New Identity";
        Width = 452;
        Height = 244;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = identity.DisplayName;
        _address.Text = identity.Address;
        _replyTo.Text = identity.ReplyTo;
        _organization.Text = identity.Organization;

        // The system dialogs' own red, from the same family the drawn icons take theirs from —
        // one mark, one token family, so this window stays light in every theme like its
        // siblings rather than borrowing an ink from the shell.
        SystemDialogKit.Bind(_error, TextBlock.ForegroundProperty, "systemdialog.icon.red.brush");
        _error.Margin = new Thickness(12, 4, 12, 0);

        var grid = new Grid
        {
            Margin = new Thickness(12, 12, 12, 0),
            ColumnDefinitions = new ColumnDefinitions("118,*"),
            RowDefinitions = new RowDefinitions("28,28,28,28"),
        };

        void Row(int row, string label, TextBox field, string automationName)
        {
            var caption = Label(label);
            caption.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(caption, row);
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);

            field.VerticalAlignment = VerticalAlignment.Center;
            Avalonia.Automation.AutomationProperties.SetName(field, automationName);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        Row(0, "Your Name:", _name, "Your Name");
        Row(1, "E-mail Address:", _address, "E-mail Address");
        Row(2, "Reply-To Address:", _replyTo, "Reply-To Address");
        Row(3, "Organization:", _organization, "Organization");

        var ok = PushButton("OK", Save);
        ok.IsDefault = true;
        var cancel = PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 7, 9),
            Children = { ok, cancel },
        };

        var stack = new StackPanel { Children = { grid, _error } };

        SystemDialogChrome.Apply(this, new Panel { Children = { stack, buttons } });

        _address.AttachedToVisualTree += (_, _) => _address.Focus();
    }

    /// <summary>
    /// Checks what was typed and keeps it, or says what is wrong and stays open.
    /// </summary>
    /// <remarks>
    /// In the window rather than at send time: an address that will not parse is a message that
    /// cannot go out, and finding that out days later, on the one message that mattered, is the
    /// failure this dialog exists to prevent.
    /// </remarks>
    private void Save()
    {
        if (Validate() is not { } kept) return;

        Result = kept;
        Close();
    }

    /// <summary>What the fields say, or null with <see cref="_error"/> filled in.</summary>
    private Identity? Validate()
    {
        var address = (_address.Text ?? string.Empty).Trim();
        var replyTo = (_replyTo.Text ?? string.Empty).Trim();

        if (address.Length == 0) return Fail("An identity needs an e-mail address.", _address);

        // Parsed and then looked at, which is two checks because MimeKit's parser is the
        // permissive one RFC 5322 asks for: it reads "not-an-address" as a mailbox with no
        // domain and hands it back happily. The wizard's own test is what tells a typed address
        // from a typed word, and it is the one the account beside this one was added under.
        if (!MailboxAddress.TryParse(address, out var parsed)
            || !Autoconfig.LooksLikeAnAddress(parsed.Address))
        {
            return Fail($"Could not read '{address}' as an e-mail address.", _address);
        }

        if (_taken.Contains(parsed.Address, StringComparer.OrdinalIgnoreCase))
        {
            return Fail($"This account already sends as {parsed.Address}.", _address);
        }

        var reply = string.Empty;

        if (replyTo.Length > 0)
        {
            if (!MailboxAddress.TryParse(replyTo, out var parsedReply)
                || !Autoconfig.LooksLikeAnAddress(parsedReply.Address))
            {
                return Fail($"Could not read '{replyTo}' as an e-mail address.", _replyTo);
            }

            reply = parsedReply.Address;
        }

        _error.Text = string.Empty;

        // What was parsed rather than what was typed. Somebody who pastes
        // "Sales <sales@example.com>" into the address box means the address, and the name they
        // pasted with it is the one they would have typed in the box above had it been empty.
        var typedName = (_name.Text ?? string.Empty).Trim();

        return new Identity
        {
            Address = parsed.Address,
            DisplayName = typedName.Length > 0 ? typedName : parsed.Name ?? string.Empty,
            ReplyTo = reply,
            Organization = (_organization.Text ?? string.Empty).Trim(),
        };
    }

    private Identity? Fail(string message, TextBox field)
    {
        _error.Text = message;
        field.Focus();
        return null;
    }

    /// <summary>
    /// Types the four fields and presses OK, for the harness.
    /// </summary>
    /// <remarks>
    /// Through the boxes and the same check a press runs, so a posed identity meets the address
    /// parsing and the duplicate refusal rather than going round them — which is the whole point
    /// of posing it here instead of writing the setting directly. The window is never shown, so
    /// it validates rather than pressing OK: a <c>Close</c> on a window that was never opened is
    /// not a thing to ask for.
    /// </remarks>
    /// <param name="fields"><c>address|name|replyTo|organization</c>, any trailing part omitted.</param>
    internal void Harness(string fields)
    {
        var parts = fields.Split('|');

        _address.Text = parts.ElementAtOrDefault(0) ?? string.Empty;
        _name.Text = parts.ElementAtOrDefault(1) ?? string.Empty;
        _replyTo.Text = parts.ElementAtOrDefault(2) ?? string.Empty;
        _organization.Text = parts.ElementAtOrDefault(3) ?? string.Empty;

        Result = Validate();

        Mailbox.Core.Diagnostics.Log.Info(Result is { } kept
            ? $"Harness: identity accepted — {kept.Label}"
              + (kept.ReplyTo.Length > 0 ? $", replies to {kept.ReplyTo}" : string.Empty)
              + (kept.Organization.Length > 0 ? $", organization '{kept.Organization}'" : string.Empty) + "."
            : $"Harness: identity refused — {_error.Text}");
    }
}
