using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;
using Mailbox.Security.OpenPgp;
using Mailbox.Store;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Bcpg.Sig;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Mailbox.App.Views;

/// <summary>
/// The doors onto the cryptographic surfaces: chain states, signature verdicts, the keyring's
/// failure modes, and where an OAuth token ends up.
/// </summary>
/// <remarks>
/// <b>Why a message has to be manufactured.</b> A verdict is a statement about a chain, and the
/// only way to see whether the statement matches the chain is to have one of each — a certificate
/// whose root this machine vouches for, one that had already run out, one nobody has vouched for,
/// and a message altered after it was signed. None of those can be seeded from a file: an expired
/// certificate is expired relative to the day the run happens, and a chain that is trusted is
/// trusted because <em>this</em> store was told to trust it. So the certificates are made in the
/// run, imported into the store the run actually opens, and the messages filed into the Inbox the
/// pose is about to look at. <c>MAILBOX_READING=dump</c> then reads the bar's own words out of the
/// visual tree, which is the verdict a reader sees rather than the one the verifier returned.
/// <para>
/// <b>Why the credential store gets a door of its own.</b> A capture run keeps its passwords in
/// memory on purpose — photographing a window is no reason to open a keyring — so nothing that
/// runs under the harness had ever spoken to the Secret Service, and "the password goes to the
/// keyring" was a sentence in a comment. This drives the real chooser, under an address in the
/// <c>.invalid</c> domain that cannot collide with anything a reader owns, and it clears up after
/// itself. The failure modes are the point: a machine with no <c>secret-tool</c>, and one where
/// the service does not answer.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The account nothing real can be filed under. Reserved by RFC 2606.</summary>
    private const string AuditAddress = "audit15b@example.invalid";

    /// <summary>Wires this lane's doors. Called once, from the constructor.</summary>
    private void WirePhase15BDoors()
    {
        // Before the folder pose picks a row, exactly as the remote-picture delivery is: a message
        // filed after the list has been built is a message no capture ever sees.
        if (Environment.GetEnvironmentVariable("MAILBOX_SMIME") is { Length: > 0 } smime)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DeliverSmime(smime), DispatcherPriority.Send);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_PGPMAIL") is { Length: > 0 } pgp)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DeliverPgp(pgp), DispatcherPriority.Send);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_KEYRING") is { Length: > 0 } keyring)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () => await RunKeyringAsync(keyring), DispatcherPriority.Background);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_OAUTH") is { Length: > 0 } oauth)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () => await RunOAuthAsync(oauth), DispatcherPriority.Background);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_TYPOGRAPHY") is "1" or "true")
        {
            // The hold keeps the capture — and the process — from exiting before the dialog this
            // reads has been laid out. Without it the peek that opened the dialog photographs and
            // leaves, and the typography read never runs; that was the whole failure mode here.
            var hold = Theming.WindowCapture.IsRequested ? Theming.WindowCapture.Hold() : null;
            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () => { try { await DescribeTypographyAsync(); } finally { hold?.Dispose(); } },
                DispatcherPriority.Background);
        }
    }

    // ---- What a dialog is actually painted and lettered with ------------------------------------

    /// <summary>
    /// Reads every piece of text on the newest window and says where its font and its colour came
    /// from: <c>MAILBOX_TYPOGRAPHY=1</c>.
    /// </summary>
    /// <remarks>
    /// <b>Why this is measured rather than read off the source.</b> The bug class that motivated the
    /// whole doors programme was a font named in the passphrase dialog's own code, which nobody
    /// could photograph and no test looked for. The source sweep now catches a family written as a
    /// literal — but it cannot catch a control that simply <em>inherits nothing</em>, because
    /// there is nothing there to find. So this asks the live control what it resolved to and holds
    /// the answer against the theme's own dictionary: a value that matches no token came from
    /// somewhere the theme cannot reach, whether or not a literal was ever typed.
    /// <para>
    /// Six hundred milliseconds, so it lands after the dialog has been laid out and before
    /// <c>CaptureNextWindow</c> photographs it and leaves.
    /// </para>
    /// </remarks>
    private async Task DescribeTypographyAsync()
    {
        await Task.Delay(600);

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.LastOrDefault(w => !ReferenceEquals(w, this));

        if (window is null)
        {
            Log.Warn("Harness: typography — no window other than the shell is open.");
            return;
        }

        window.UpdateLayout();

        Log.Info($"Harness: typography — “{window.Title}” under the {App.Themes.ThemeId} theme.");

        var index = 0;
        foreach (var control in window.GetVisualDescendants().OfType<Control>())
        {
            var (what, words) = control switch
            {
                TextBlock text => ("TextBlock", text.Text ?? string.Empty),
                TextBox box => ("TextBox", box.PasswordChar == '\0' ? box.Text ?? string.Empty : "(a password field)"),
                CheckBox tick => ("CheckBox", tick.Content?.ToString() ?? string.Empty),
                Button button when button.Classes.Count == 0 => ("Button", button.Content?.ToString() ?? string.Empty),
                _ => (string.Empty, string.Empty),
            };

            if (what.Length == 0) continue;

            Log.Info($"Harness: typography {index++} — {what} “{Shorten(words)}”: "
                     + $"family {Named(control.GetValue(TextElement.FontFamilyProperty))}, "
                     + $"size {control.GetValue(TextElement.FontSizeProperty)} {Token(control.GetValue(TextElement.FontSizeProperty))}, "
                     + $"ink {Paint(control.GetValue(TextElement.ForegroundProperty))}, "
                     + $"ground {Paint((control as TemplatedControl)?.Background)}.");
        }
    }

    private static string Shorten(string words)
        => words.Length <= 42 ? words : words[..40].TrimEnd() + "…";

    /// <summary>A family, and which typography token it is — or that it is neither.</summary>
    private static string Named(FontFamily? family)
    {
        if (family is null) return "unset";

        foreach (var key in new[] { "ui.fontfamily", "content.fontfamily", "mono.fontfamily" })
        {
            if (Resource(key) is FontFamily token && token.Name == family.Name) return $"{family.Name} ({key})";
        }

        return $"{family.Name} — MATCHES NO TOKEN";
    }

    /// <summary>Which size token a number is, if it is one.</summary>
    private static string Token(double size)
    {
        foreach (var key in new[]
        {
            "type.ui.size.value", "type.ui.size.small.value", "type.ui.size.large.value",
            "type.content.size.value",
        })
        {
            if (Resource(key) is double token && Math.Abs(token - size) < 0.01) return $"({key})";
        }

        return "— MATCHES NO TOKEN";
    }

    /// <summary>A brush, its colour, and the token whose colour that is.</summary>
    private static string Paint(IBrush? brush)
    {
        if (brush is not ISolidColorBrush solid) return brush is null ? "unset" : brush.GetType().Name;

        var colour = solid.Color;
        var matches = new List<string>();

        if (Application.Current is { } application)
        {
            foreach (var key in TokenKeysInPlay)
            {
                if (Resource(key) is ISolidColorBrush candidate && candidate.Color == colour) matches.Add(key);
            }
        }

        return $"{colour} {(matches.Count > 0 ? "(" + string.Join(", ", matches) + ")" : "— MATCHES NO TOKEN")}";
    }

    /// <summary>
    /// The brush tokens a dialog can legitimately be painted from.
    /// </summary>
    /// <remarks>
    /// Named rather than swept out of the dictionary because the dictionary holds hundreds and a
    /// colour that happens to equal one of them is not evidence it came from there. These are the
    /// families a dialog draws with; a colour outside them is a colour a theme cannot move.
    /// </remarks>
    private static readonly string[] TokenKeysInPlay =
    [
        "dialog.foreground.brush", "dialog.foreground.subtle.brush", "dialog.background.brush",
        "dialog.surface.brush", "dialog.surface.text.brush", "dialog.border.brush",
        "text.primary.brush", "text.secondary.brush", "text.disabled.brush",
        "surface.ground.brush", "border.subtle.brush", "border.strong.brush", "border.focus.brush",
        "status.warning.brush", "status.error.brush",
    ];

    private static object? Resource(string key)
        => Application.Current is { } application
           && application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value)
            ? value
            : null;

    // ---- S/MIME: one message per chain state ---------------------------------------------------

    /// <summary>
    /// Files one signed message per named chain state, having first put what each needs into the
    /// store the run opened.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_SMIME=good,expired,untrusted,tampered,encrypted</c>. Every subject is distinct so
    /// <c>MAILBOX_SELECT</c> can pick one; every certificate and key is made here and none of it
    /// outlives the run.
    /// </remarks>
    private void DeliverSmime(string states)
    {
        if (Inbox() is not { } box) return;

        try
        {
            using var store = CryptoStores.Certificates();

            var root = SelfSignedRoot("Mailbox Audit Root");
            var stranger = SelfSignedRoot("Mailbox Audit Unvouched-For Root");

            // The one decision that separates a good chain from an unvouched-for one, made
            // explicitly here so a run cannot pass by accident.
            if (store is DefaultSecureMimeContext database) database.Import(root.Certificate, trusted: true);

            Log.Info($"Harness: smime — store {store.GetType().Name}, "
                     + $"trusted root “{root.Certificate.SubjectDN}”.");

            foreach (var state in states.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (state.ToLowerInvariant())
                {
                    case "good":
                        FileMessage(box, Signed(
                            "S/MIME: a good chain", Leaf("a.person@example.com", root), "a.person@example.com"));
                        break;

                    case "expired":
                        FileMessage(box, Signed(
                            "S/MIME: an expired certificate",
                            Leaf("expired@example.com", root, notBefore: -800, notAfter: -400),
                            "expired@example.com"));
                        break;

                    case "untrusted":
                        FileMessage(box, Signed(
                            "S/MIME: a root nobody vouched for",
                            Leaf("stranger@example.org", stranger), "stranger@example.org"));
                        break;

                    case "tampered":
                        FileMessage(box, Tampered(Signed(
                            "S/MIME: changed after signing", Leaf("a.person@example.com", root),
                            "a.person@example.com")));
                        break;

                    case "encrypted":
                    {
                        // The reader's own certificate has to be in the store with its private key,
                        // or there is nothing to open the message with.
                        var mine = Leaf(box.Address, root);
                        Hold(store, mine);
                        FileMessage(box, Sealed("S/MIME: sealed to the reader", store, box.Address));
                        break;
                    }

                    default:
                        Log.Warn($"Harness: smime — no such state “{state}”.");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the S/MIME pose failed.", ex);
            return;
        }

        (DataContext as ShellViewModel)?.Refresh();
    }

    /// <summary>A message signed with a certificate the store need not hold.</summary>
    private static MimeMessage Signed(string subject, Identity signer, string from)
    {
        var message = Envelope(subject, from);

        // The sender signs through a context of their own: the overload that takes no context
        // builds MimeKit's default one, whose SQLite check is the very thing CertificateStore
        // exists to route around — it throws here, and the pose would log itself as failed.
        using var sender = new TemporarySecureMimeContext();
        message.Body = MultipartSigned.Create(
            sender, new CmsSigner(signer.Certificate, signer.Key), message.Body!);

        return message;
    }

    /// <summary>A message encrypted to one address through the store that holds its certificate.</summary>
    private static MimeMessage Sealed(string subject, SecureMimeContext store, string to)
    {
        var message = Envelope(subject, to);
        message.Body = ApplicationPkcs7Mime.Encrypt(
            store, [new MailboxAddress("A. Person", to)], message.Body!);

        return message;
    }

    /// <summary>
    /// The same message with a word changed inside the signed part, which is the only difference
    /// between "signed" and "signed, and then somebody edited it".
    /// </summary>
    private static MimeMessage Tampered(MimeMessage message)
    {
        if (message.Body is MultipartSigned signed && signed.Count > 0 && signed[0] is TextPart body)
        {
            body.Text = body.Text.Replace("figures", "invoices", StringComparison.Ordinal);
        }

        return message;
    }

    // ---- OpenPGP: one message per verdict -------------------------------------------------------

    /// <summary>
    /// Files one message per named OpenPGP verdict, importing only the keys that state calls for.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_PGPMAIL=good,tampered,unknown,expired,revoked</c>. The seed's own ring already
    /// holds a good pair; these are the four states no seed can carry, because each is a fact about
    /// a key rather than about a message — a key that had run out when it signed, one whose owner
    /// has since withdrawn it, and one this machine has never seen.
    /// </remarks>
    private void DeliverPgp(string states)
    {
        if (Inbox() is not { } box) return;

        try
        {
            using var ring = CryptoStores.KeyRing();
            Log.Info($"Harness: pgp — ring holds {KeyInventory.Read(ring).Count} key(s) to begin with.");

            foreach (var state in states.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (state.ToLowerInvariant())
                {
                    case "good":
                    {
                        var pair = MakeKey("A. Person", "good@example.com");
                        ring.Import(pair.Public);
                        FileMessage(box, PgpSigned("OpenPGP: a good signature", pair, "good@example.com"));
                        break;
                    }

                    case "tampered":
                    {
                        var pair = MakeKey("A. Person", "tampered@example.com");
                        ring.Import(pair.Public);
                        FileMessage(box, PgpTampered(
                            PgpSigned("OpenPGP: changed after signing", pair, "tampered@example.com")));
                        break;
                    }

                    case "unknown":
                    {
                        // Deliberately not imported. This is the message from somebody whose key
                        // this computer has never been given.
                        var pair = MakeKey("A. Stranger", "unknown@example.com");
                        FileMessage(box, PgpSigned("OpenPGP: a key this computer has not got", pair, "unknown@example.com"));
                        break;
                    }

                    case "expired":
                    {
                        // A message signed by a key that had already expired cannot be
                        // manufactured here: MimeKit refuses to sign with an expired key —
                        // itself the useful fact, since it means this application will never
                        // *send* with one. The receiving verdict (PgpVerification.Expired) is
                        // reached instead by a message from another client, and is covered by
                        // AnExpiredKeySignatureIsNotCalledSigned in PgpVerificationTests.
                        Log.Info("Harness: pgp — the expired-key case is a receive-only verdict; "
                                 + "MimeKit will not sign with an expired key. See the unit test.");
                        break;
                    }

                    case "revoked":
                    {
                        var pair = MakeKey("A. Person", "revoked@example.com");
                        var message = PgpSigned("OpenPGP: a key its owner withdrew", pair, "revoked@example.com");
                        ring.Import(Revoke(pair));
                        FileMessage(box, message);
                        break;
                    }

                    default:
                        Log.Warn($"Harness: pgp — no such state “{state}”.");
                        break;
                }
            }

            Log.Info($"Harness: pgp — ring holds {KeyInventory.Read(ring).Count} key(s) afterwards.");
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the OpenPGP pose failed.", ex);
            return;
        }

        (DataContext as ShellViewModel)?.Refresh();
    }

    /// <summary>
    /// Signs with a ring of the run's own, so the machine's ring can be told about the key or not
    /// told about it — which is the whole difference between "signed by" and "a key this computer
    /// has not got".
    /// </summary>
    private static MimeMessage PgpSigned(string subject, PgpIdentity pair, string from)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mailbox-15b-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var signer = new PgpContext(scratch, _ => string.Empty);
            signer.Import(pair.Public);
            signer.Import(pair.Secret);

            var message = Envelope(subject, from);
            message.Body = MultipartSigned.Create(
                signer, new MailboxAddress("A. Person", from), DigestAlgorithm.Sha256, message.Body!);

            return message;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { /* gone already */ }
        }
    }

    private static MimeMessage PgpTampered(MimeMessage message)
    {
        if (message.Body is MultipartSigned signed && signed.Count > 0 && signed[0] is TextPart body)
        {
            body.Text = body.Text.Replace("figures", "invoices", StringComparison.Ordinal);
        }

        return message;
    }

    // ---- The keyring, and what it does when it cannot answer ------------------------------------

    /// <summary>
    /// Drives the real credential chooser: <c>MAILBOX_KEYRING=best;save:incoming=…;load:incoming;delete:incoming</c>.
    /// </summary>
    /// <remarks>
    /// The chooser rather than a store built here, because the decision worth checking is the one
    /// it makes — whether a machine with no keyring is told so, or quietly given something that
    /// forgets. Everything is filed under <see cref="AuditAddress"/>, which is a domain reserved to
    /// never resolve, and <c>delete</c> takes it away again.
    /// <para>
    /// A secret is never written to the log. What is written is whether the store answered, and
    /// whether what came back was what went in.
    /// </para>
    /// </remarks>
    private static async Task RunKeyringAsync(string script)
    {
        ICredentialStore? store = null;

        foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':', StringComparison.Ordinal);
            var verb = (colon < 0 ? step : step[..colon]).Trim().ToLowerInvariant();
            var argument = colon < 0 ? string.Empty : step[(colon + 1)..];

            try
            {
                switch (verb)
                {
                    case "probe":
                    {
                        var keyring = new SecretServiceStore();
                        Log.Info($"Harness: keyring — secret-tool {(keyring.IsAvailable ? "answers" : "is not there")}.");
                        break;
                    }

                    case "best":
                        store = Credentials.Best();
                        Log.Info($"Harness: keyring — the chooser picked {store.GetType().Name}, "
                                 + $"described as “{store.Description}”.");
                        break;

                    case "save":
                    {
                        store ??= Credentials.Best();
                        var (purpose, secret) = Split(argument);
                        var saved = await store.SaveAsync(AuditAddress, purpose, secret);
                        Log.Info($"Harness: keyring — save {purpose} {(saved ? "succeeded" : "FAILED")}.");
                        break;
                    }

                    case "load":
                    {
                        store ??= Credentials.Best();
                        var (purpose, expected) = Split(argument);
                        var held = await store.LoadAsync(AuditAddress, purpose);
                        Log.Info($"Harness: keyring — load {purpose} came back "
                                 + (held is null ? "empty" : $"{held.Length} character(s), ")
                                 + (held is null ? string.Empty
                                     : expected.Length == 0 ? "nothing to compare"
                                     : held == expected ? "the same as was saved" : "DIFFERENT from what was saved")
                                 + ".");
                        break;
                    }

                    case "delete":
                    {
                        store ??= Credentials.Best();
                        var (purpose, _) = Split(argument);
                        var gone = await store.DeleteAsync(AuditAddress, purpose);
                        Log.Info($"Harness: keyring — delete {purpose} {(gone ? "succeeded" : "reported nothing to remove")}.");
                        break;
                    }

                    default:
                        Log.Warn($"Harness: keyring — no such step “{step}”.");
                        break;
                }
            }
            catch (Exception ex)
            {
                // A step that throws is the answer, not a reason to stop: the claim is that the
                // application degrades rather than falling over.
                Log.Warn($"Harness: keyring — the step “{step}” threw.", ex);
            }
        }
    }

    // ---- OAuth: which half of a sign-in is written down -----------------------------------------

    /// <summary>
    /// Renews a sign-in against a token endpoint of the run's own, and says where each token went.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_OAUTH=token:https://127.0.0.1:8899/token;seed:&lt;refresh&gt;;renew;report</c>.
    /// The endpoint is real HTTP over TLS — the flow refuses anything that is not https, which is
    /// the rule being relied on rather than worked around — and the certificate is the run's own,
    /// so the handler here is the one place that agrees to it.
    /// <para>
    /// The claim under test is the split: the refresh token is a long-lived credential and goes to
    /// the keyring; the access token lasts an hour, is bought again from the other, and is never
    /// written down anywhere. Proving the second half is a <c>grep</c> over the config directory,
    /// the state directory and the account stores after this has run — which is why the token
    /// values are given on the command line rather than invented here.
    /// </para>
    /// </remarks>
    private static async Task RunOAuthAsync(string script)
    {
        var endpoint = "https://127.0.0.1:8899/token";
        var refresh = string.Empty;
        var store = Credentials.Best();

        OAuthFlow? flow = null;
        OAuthTokenSource? source = null;

        try
        {
            foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = step.IndexOf(':', StringComparison.Ordinal);
                var verb = (colon < 0 ? step : step[..colon]).Trim().ToLowerInvariant();
                var argument = colon < 0 ? string.Empty : step[(colon + 1)..];

                switch (verb)
                {
                    case "token":
                        endpoint = argument;
                        break;

                    case "seed":
                        // What a completed sign-in would have left behind, put where the source
                        // will look for it: the keyring, under the audit's own address.
                        refresh = argument;
                        Log.Info($"Harness: oauth — seeding the refresh token into {store.Description} "
                                 + $"{(await store.SaveAsync(AuditAddress, Credentials.OAuthRefresh, refresh) ? "succeeded" : "FAILED")}.");
                        break;

                    case "renew":
                    {
                        var provider = new OAuthProvider(
                            "audit", "the audit's own", new Uri("https://127.0.0.1/authorize"),
                            new Uri(endpoint), "mail");

                        flow = new OAuthFlow(new LocalOnlyHandler());
                        source = new OAuthTokenSource(provider, "audit-client", AuditAddress, store, flow);

                        var access = await source.AccessTokenAsync();
                        Log.Info($"Harness: oauth — renewed; the access token is {access.Length} character(s) "
                                 + $"and expires {source.ExpiresAt:u}.");
                        break;
                    }

                    case "report":
                    {
                        var held = await store.LoadAsync(AuditAddress, Credentials.OAuthRefresh);
                        Log.Info($"Harness: oauth — {store.Description} holds "
                                 + (held is null
                                     ? "no refresh token"
                                     : $"a refresh token of {held.Length} character(s)"
                                       + (refresh.Length > 0
                                           ? held == refresh ? ", the one that was seeded" : ", a rotated one"
                                           : string.Empty))
                                 + ".");
                        break;
                    }

                    case "forget":
                        Log.Info("Harness: oauth — forgetting the sign-in "
                                 + $"{(await store.DeleteAsync(AuditAddress, Credentials.OAuthRefresh) ? "succeeded" : "found nothing")}.");
                        break;

                    default:
                        Log.Warn($"Harness: oauth — no such step “{step}”.");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the OAuth pose failed.", ex);
        }
        finally
        {
            source?.Dispose();
            flow?.Dispose();
        }
    }

    /// <summary>
    /// Talks to the run's own token endpoint on the loopback interface and to nothing else.
    /// </summary>
    /// <remarks>
    /// The certificate is made by the run and trusted by nobody, so it has to be agreed to
    /// somewhere. Here, narrowly: any other host is refused outright, so a pose that mistyped a URL
    /// cannot quietly reach the internet with certificate checking switched off.
    /// </remarks>
    private sealed class LocalOnlyHandler : DelegatingHandler
    {
        public LocalOnlyHandler()
            : base(new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            })
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;

            if (host is not ("127.0.0.1" or "localhost" or "::1"))
            {
                throw new HttpRequestException($"The audit's token handler refuses {host}.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // ---- Shared plumbing -------------------------------------------------------------------------

    /// <summary>The account and Inbox a delivered message should land in.</summary>
    private static InboxTarget? Inbox()
    {
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_FOLDER") ?? string.Empty;
        var slash = wanted.IndexOf('/', StringComparison.Ordinal);
        var address = slash > 0 ? wanted[..slash] : string.Empty;

        var account = App.Accounts.All.FirstOrDefault(a =>
                          string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase))
                      ?? App.Accounts.All.FirstOrDefault();

        if (account is null)
        {
            Log.Warn("Harness: phase 15b — no account is open to deliver into.");
            return null;
        }

        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (inbox is not null) return new InboxTarget(account.Account.Address, account.Mail, inbox.Id);

        Log.Warn($"Harness: phase 15b — {account.Account.Address} has no Inbox.");
        return null;
    }

    private sealed record InboxTarget(string Address, MailRepository Mail, long FolderId);

    /// <summary>An invented message with a body the tamper step can change one word of.</summary>
    /// <remarks>
    /// Dated now, not on the posed clock: a signature carries the moment it was made, and
    /// <c>MultipartSigned.Create</c> stamps that at real wall-clock time. A message dated
    /// 2026-08-16 but signed today disagrees with itself, and §19's signing-time check —
    /// rightly — calls that invalid. The good chain has to be a message whose sent time and
    /// signing time are the same, which is any message actually created now.
    /// </remarks>
    private static MimeMessage Envelope(string subject, string from)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
            MessageId = $"harness-15b-{Guid.NewGuid():N}@example.invalid",
        };

        message.From.Add(new MailboxAddress("A. Person", from));
        message.To.Add(new MailboxAddress("A. Reader", "you@example.com"));
        message.Body = new TextPart("plain") { Text = "The quarterly figures are settled." };

        return message;
    }

    /// <summary>Files a message into the Inbox with its real bytes beside the row.</summary>
    private static void FileMessage(InboxTarget box, MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var sender = message.From.Mailboxes.First();
        var summary = new MessageSummary(
            0, box.FolderId, message.MessageId, message.MessageId,
            sender.Name ?? string.Empty, sender.Address, message.Subject ?? string.Empty, "A posed message.",
            message.Date, message.Date, raw.Length,
            IsRead: false, IsFlagged: false, HasAttachment: false);

        var id = box.Mail.AddMessage(box.FolderId, summary, raw);

        Log.Info(id is null
            ? $"Harness: phase 15b — “{message.Subject}” was already in {box.Address}/Inbox."
            : $"Harness: phase 15b — filed “{message.Subject}” from {sender.Address} into "
              + $"{box.Address}/Inbox, {raw.Length} bytes, root {message.Body?.ContentType.MimeType}.");
    }

    private static (string Left, string Right) Split(string argument)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0
            ? (argument.Trim(), string.Empty)
            : (argument[..equals].Trim(), argument[(equals + 1)..]);
    }

    // ---- Certificates, made in the run ------------------------------------------------------------

    /// <summary>One certificate and the private key that goes with it.</summary>
    private sealed record Identity(string Address, X509Certificate Certificate, AsymmetricKeyParameter Key);

    private static Identity SelfSignedRoot(string name)
    {
        var random = new SecureRandom();
        var pair = Rsa(random);
        var dn = new X509Name($"CN={name}");

        var generator = Generator(random, dn, dn, pair.Public, -1000, 3000);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));

        return new Identity(
            string.Empty,
            generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", pair.Private, random)),
            pair.Private);
    }

    private static Identity Leaf(string address, Identity issuer, int notBefore = -30, int notAfter = 365)
    {
        var random = new SecureRandom();
        var pair = Rsa(random);

        var generator = Generator(
            random, issuer.Certificate.SubjectDN, new X509Name($"CN=A. Person, E={address}"),
            pair.Public, notBefore, notAfter);

        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        generator.AddExtension(
            X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment | KeyUsage.NonRepudiation));

        // §19 reads the address from the subject alternative name and from nowhere else.
        generator.AddExtension(
            X509Extensions.SubjectAlternativeName, false,
            new GeneralNames(new GeneralName(GeneralName.Rfc822Name, address)));

        generator.AddExtension(
            X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.id_kp_emailProtection));

        return new Identity(
            address,
            generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Key, random)),
            pair.Private);
    }

    /// <summary>Files a certificate and its private key, which is what decrypting needs.</summary>
    private static void Hold(SecureMimeContext store, Identity identity)
    {
        var pkcs12 = new Pkcs12StoreBuilder().Build();
        pkcs12.SetKeyEntry(
            identity.Address,
            new AsymmetricKeyEntry(identity.Key),
            [new X509CertificateEntry(identity.Certificate)]);

        using var stream = new MemoryStream();
        pkcs12.Save(stream, "harness".ToCharArray(), new SecureRandom());
        stream.Position = 0;
        store.Import(stream, "harness");
    }

    private static X509V3CertificateGenerator Generator(
        SecureRandom random, X509Name issuer, X509Name subject,
        AsymmetricKeyParameter publicKey, int notBefore, int notAfter)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, random));
        generator.SetIssuerDN(issuer);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(notBefore));
        generator.SetNotAfter(DateTime.UtcNow.AddDays(notAfter));
        generator.SetPublicKey(publicKey);
        return generator;
    }

    private static AsymmetricCipherKeyPair Rsa(SecureRandom random)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(random, 2048));
        return generator.GenerateKeyPair();
    }

    // ---- OpenPGP keys, made in the run -------------------------------------------------------------

    /// <summary>One OpenPGP identity: the ring to publish, and the secret half that signs.</summary>
    private sealed record PgpIdentity(PgpPublicKeyRing Public, PgpSecretKeyRing Secret);

    /// <summary>
    /// A single-key ring for one address, optionally made in the past with a life on it.
    /// </summary>
    /// <remarks>
    /// 2048 bits and one key rather than the two-key 3072 shape the Trust Center makes: a pose that
    /// generates five of these is generating them while a window waits, and what is being proved
    /// here is a verdict rather than a key policy. <paramref name="madeDaysAgo"/> with
    /// <paramref name="livesDays"/> is how a key comes to have already expired at the moment it
    /// signs, which no key made now can be.
    /// </remarks>
    private static PgpIdentity MakeKey(string name, string address, int madeDaysAgo = 1, int livesDays = 0)
    {
        var random = new SecureRandom();
        var made = DateTime.UtcNow.AddDays(-madeDaysAgo);
        var keys = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, Rsa(random), made);

        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetKeyFlags(false, KeyFlags.CertifyOther | KeyFlags.SignData);
        if (livesDays > 0) packets.SetKeyExpirationTime(false, (long)TimeSpan.FromDays(livesDays).TotalSeconds);

        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification, keys, $"{name} <{address}>",
            SymmetricKeyAlgorithmTag.Aes256, Array.Empty<char>(), useSha1: true,
            hashedPackets: packets.Generate(), unhashedPackets: null, rand: random);

        return new PgpIdentity(generator.GeneratePublicKeyRing(), generator.GenerateSecretKeyRing());
    }

    /// <summary>The same ring with its owner's withdrawal on it.</summary>
    /// <remarks>
    /// A revocation is a signature the key makes about itself, so it needs the secret half — which
    /// is exactly why revocation is the one kind of "this key is finished" that travels with the key
    /// and costs no round trip to check.
    /// </remarks>
    private static PgpPublicKeyRing Revoke(PgpIdentity pair)
    {
        var master = pair.Public.GetPublicKey();
        var secret = pair.Secret.GetSecretKey();

        var signer = new PgpSignatureGenerator(master.Algorithm, HashAlgorithmTag.Sha256);
        signer.InitSign(PgpSignature.KeyRevocation, secret.ExtractPrivateKey([]));

        return PgpPublicKeyRing.InsertPublicKey(
            pair.Public, PgpPublicKey.AddCertification(master, signer.GenerateCertification(master)));
    }

}
