using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Security.OpenPgp;

namespace Mailbox.App.Views;

/// <summary>
/// Makes an OpenPGP key here, so the Trust Center's switch can mean something for a reader whose
/// answer to "where is your key?" is "what key?". Import covers everyone else.
/// </summary>
/// <remarks>
/// The reference has no such dialog — OpenPGP is one of the design's deliberate additions — so this is
/// an ordinary Mailbox dialog rather than a cloned surface. The work runs off the UI thread,
/// because RSA 3072 takes seconds and the one standing rule about the UI thread is that nothing
/// waits on it; the buttons disable, the caption says what is happening, and Cancel stays
/// honest — it closes the window, and a generation already past its last cancellation point
/// finishes into the ring, which costs nothing but a key nobody uses.
/// </remarks>
public static class NewKeyDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Asks, makes, and returns the new key's entry — or null when the reader declined.</summary>
    public static Task<KeyEntry?> MakeAsync(
        Window owner, PgpContext ring, PassphraseVault vault, string prefillName, string prefillAddress)
        => RunAsync(owner, ring, vault, prefillName, prefillAddress, harness: null);

    /// <summary>
    /// The harness's way of pressing the dialog's own Make button: fills the boxes, presses, and
    /// returns what the press produced. Typing is noticed on the next dispatcher pass, so the
    /// press yields between filling and pressing exactly as a person does.
    /// </summary>
    internal static Task<KeyEntry?> HarnessAsync(
        Window owner, PgpContext ring, PassphraseVault vault, string name, string address, string passphrase)
        => RunAsync(owner, ring, vault, name, address, (name, address, passphrase));

    private static async Task<KeyEntry?> RunAsync(
        Window owner,
        PgpContext ring,
        PassphraseVault vault,
        string prefillName,
        string prefillAddress,
        (string Name, string Address, string Passphrase)? harness)
    {
        KeyEntry? made = null;

        var caption = new TextBlock
        {
            Text = "Make an OpenPGP key for signing and encrypting your mail. It is kept in " +
                   "Mailbox's own keyring, beside your data.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var name = new TextBox { Width = 380, Text = prefillName, PlaceholderText = "Name", Name = "KeyName" };
        var address = new TextBox { Width = 380, Text = prefillAddress, PlaceholderText = "Address", Name = "KeyAddress" };

        var passphrase = new TextBox
        {
            Width = 380,
            PasswordChar = '●',
            PlaceholderText = "Passphrase",
            Name = "KeyPassphrase",
        };

        var confirm = new TextBox
        {
            Width = 380,
            PasswordChar = '●',
            PlaceholderText = "Passphrase again",
            Name = "KeyConfirm",
        };

        var aboutPassphrase = new TextBlock
        {
            Text = "The passphrase locks the key on disk. Left empty, the key opens without " +
                   "one — anybody who can read your files can use it.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        Bind(aboutPassphrase, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(aboutPassphrase, TextBlock.FontSizeProperty, "type.ui.size.small.value");

        // Out of the way until there is something to say, as the passphrase prompt's own
        // wrong-answer line is.
        var trouble = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            IsVisible = false,
        };
        Bind(trouble, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var window = new Window
        {
            Title = "New Key",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var make = new Button { Content = "Make Key", IsDefault = true };

        async Task PressAsync()
        {
            trouble.IsVisible = false;

            var whoName = (name.Text ?? string.Empty).Trim();
            var whoAddress = (address.Text ?? string.Empty).Trim();
            var pass = passphrase.Text ?? string.Empty;

            if (whoName.Length == 0 || whoAddress.Length == 0)
            {
                trouble.Text = "The key needs a name and an address.";
                trouble.IsVisible = true;
                return;
            }

            if (!MimeKit.MailboxAddress.TryParse(whoAddress, out var parsed)
                || parsed.Address != whoAddress)
            {
                trouble.Text = $"Could not read '{whoAddress}' as a plain address.";
                trouble.IsVisible = true;
                return;
            }

            if (pass != (confirm.Text ?? string.Empty))
            {
                trouble.Text = "The two passphrases do not agree.";
                trouble.IsVisible = true;
                confirm.SelectAll();
                confirm.Focus();
                return;
            }

            make.IsEnabled = false;
            cancel.IsEnabled = false;
            trouble.Text = "Making the key — this takes a few seconds.";
            trouble.IsVisible = true;

            try
            {
                // Off the UI thread: two RSA 3072 generations are seconds of arithmetic, and
                // the window should say so rather than freeze through it.
                made = await Task.Run(() => KeyGeneration.Make(ring, whoName, whoAddress, pass));

                // A protected key's passphrase is offered to the vault now, so the first use
                // does not immediately ask for what was typed a moment ago. In memory only,
                // like every answer the vault holds.
                if (pass.Length > 0 && SecretKeyIdOf(ring, made) is { } keyId)
                {
                    vault.Remember(keyId, pass);
                }

                Log.Info($"Trust Center: made key {made.ShortId} for {whoAddress}.");
                window.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("Making a key failed.", ex);
                trouble.Text = $"The key could not be made: {ex.Message}";
                trouble.IsVisible = true;
                make.IsEnabled = true;
                cancel.IsEnabled = true;
            }
        }

        make.Click += async (_, _) => await PressAsync();

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                name,
                address,
                passphrase,
                confirm,
                aboutPassphrase,
                trouble,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, make },
                },
            },
        };

        DialogChrome.Apply(window, body);

        if (harness is { } posed)
        {
            window.Opened += async (_, _) =>
            {
                try
                {
                    name.Text = posed.Name;
                    address.Text = posed.Address;
                    passphrase.Text = posed.Passphrase;
                    confirm.Text = posed.Passphrase;

                    // Setting Text raises TextChanged on a later pass; pressing in the same one
                    // presses a button whose state still belongs to the empty boxes.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await PressAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the new-key pose failed.", ex);
                    window.Close();
                }
            };
        }
        else
        {
            window.Opened += (_, _) => name.Focus();
        }

        await window.ShowDialog(owner);
        return made;
    }

    /// <summary>The secret primary's id for a made key, which is what the vault files answers under.</summary>
    private static long? SecretKeyIdOf(PgpContext ring, KeyEntry made)
    {
        foreach (var secrets in ring.SecretRings())
        {
            var fingerprint = Convert.ToHexString(secrets.GetPublicKey().GetFingerprint());
            if (fingerprint.Equals(made.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return secrets.GetSecretKey().KeyId;
            }
        }

        return null;
    }
}
