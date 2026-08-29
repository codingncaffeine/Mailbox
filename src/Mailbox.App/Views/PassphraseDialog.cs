using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Security.OpenPgp;

namespace Mailbox.App.Views;

/// <summary>
/// Asks what unlocks an OpenPGP secret key, and refuses an answer that does not.
/// </summary>
/// <remarks>
/// <b>Why this is a dialog and not a callback.</b> The library asks for a passphrase synchronously,
/// from inside the cryptography, on whatever thread is doing the work — and a window is asynchronous
/// and belongs to the UI thread. So nothing is asked from in there: an operation that meets a locked
/// key refuses and records which key it wanted (see <see cref="PassphraseVault"/>), the caller opens
/// this, and the operation runs again. A reader who cancels has answered, and the message stays as
/// it was.
/// <para>
/// <b>The answer is checked here.</b> A passphrase that does not open the key is refused in the box
/// that took it, rather than being filed and turning into a send that fails several seconds later
/// for reasons the writer cannot see.
/// </para>
/// </remarks>
public static class PassphraseDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>
    /// Asks for every key an operation could not open, in turn.
    /// </summary>
    /// <returns>
    /// True when every one of them was unlocked, so the operation is worth running again. False the
    /// moment a reader declines one — there is no point asking for the rest of a message that is not
    /// going to go.
    /// </returns>
    public static async Task<bool> UnlockAsync(
        Window owner, PgpContext context, PassphraseVault vault, IReadOnlyList<PassphraseRequest> wanted)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(wanted);

        if (wanted.Count == 0) return false;

        foreach (var request in wanted)
        {
            // The key itself, so the answer can be tried before it is kept. A key that has gone
            // since the operation asked for it cannot be unlocked and cannot be asked about either.
            if (context.SecretKey(request.KeyId) is not { } key) return false;
            if (!await AskAsync(owner, vault, request, key)) return false;
        }

        return true;
    }

    /// <summary>One key, one dialog. True when it was unlocked.</summary>
    private static async Task<bool> AskAsync(
        Window owner, PassphraseVault vault, PassphraseRequest request, Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKey key)
    {
        var unlocked = false;

        var caption = new TextBlock
        {
            Text = request.Address is { Length: > 0 } address
                ? $"Enter the passphrase for the OpenPGP key belonging to {address}."
                : "Enter the passphrase for this OpenPGP key.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        // The fingerprint, because it is the part a reader who checks anything checks — an address
        // is what an attacker can also write on a key. Monospaced so the groups line up, from the
        // theme's own family rather than from a name in this file: a family asked for by its bare
        // name skips both the theme and the metric-compatible substitution, and one Mailbox bundles
        // is not found at all.
        var fingerprint = new TextBlock
        {
            Text = request.Fingerprint,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        Bind(fingerprint, TextBlock.FontFamilyProperty, "mono.fontfamily");
        Bind(fingerprint, TextBlock.FontSizeProperty, "type.ui.size.small.value");
        Bind(fingerprint, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var input = new TextBox
        {
            Width = 380,
            PasswordChar = '●',
            Name = "Passphrase",
        };

        var remember = new CheckBox
        {
            Content = "Remember this until Mailbox is closed",
            IsChecked = true,
        };
        Bind(remember, CheckBox.ForegroundProperty, "dialog.foreground.brush");

        // Kept out of the way until there is something to say, so the dialog does not open already
        // telling the reader they got it wrong.
        var wrong = new TextBlock
        {
            Text = "That passphrase does not open this key.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            IsVisible = false,
        };
        Bind(wrong, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var window = new Window
        {
            Title = "Unlock Key",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,

            // The window's own font, so every control inherits it — the way MessageWindow and
            // AppointmentWindow do. Without it the CheckBox's content presenter fell to the
            // toolkit default (Inter), a shade off the token font every sibling drew, which the
            // typography read-back caught: the exact class the doors programme was built for.
            FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default),
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var unlock = new Button { Content = "Unlock", IsDefault = true };
        unlock.Click += (_, _) =>
        {
            var answer = input.Text ?? string.Empty;

            if (!PassphraseVault.Opens(key, answer))
            {
                wrong.IsVisible = true;
                input.SelectAll();
                input.Focus();
                return;
            }

            // Remembered for the session, or spent on the next operation and gone. Either way it is
            // in memory only — this is the one secret that does not go to the keyring, the keyring
            // being what it opens.
            if (remember.IsChecked == true) vault.Remember(request.KeyId, answer);
            else vault.Once(request.KeyId, answer);

            unlocked = true;
            window.Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                fingerprint,
                input,
                wrong,
                remember,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, unlock },
                },
            },
        };

        DialogChrome.Apply(window, body);

        window.Opened += (_, _) => input.Focus();

        await window.ShowDialog(owner);
        return unlocked;
    }
}
