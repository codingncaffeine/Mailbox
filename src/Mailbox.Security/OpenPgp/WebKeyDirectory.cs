using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mailbox.Core.Diagnostics;
using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>What looking somebody up in their domain's key directory came to.</summary>
/// <param name="Key">The key that was published for that address, or null when there was none.</param>
/// <param name="Detail">One sentence for the reader. Empty when a key came back.</param>
public sealed record KeyLookup(PgpPublicKeyRing? Key, string Detail)
{
    public static readonly KeyLookup Nothing = new(null, "No key is published for that address.");

    public bool Found => Key is not null;
}

/// <summary>
/// Finding somebody's public key at their own domain — the Web Key Directory.
/// </summary>
/// <remarks>
/// A key server is a place anybody may upload anybody's key to, which is why the field moved off
/// them. WKD asks the domain in the address instead: only whoever runs example.com can publish at
/// example.com, so a key found this way is at least the answer that domain gives about its own
/// people. That is not identity — it is the same trust as a mail server, which is the trust the
/// address already carried.
/// <para>
/// <b>Never called while a message is being drawn.</b> §19 allows nothing on the render path to
/// touch the network: no remote content, no revocation lookup, no key discovery. A fetch here is
/// something a reader asked for, and the address it asks about is one they already have — a
/// lookup driven by an arriving message would tell the sender exactly when it was opened.
/// </para>
/// <para>
/// The two methods are the standard's own. <b>Advanced</b> asks
/// <c>openpgpkey.example.com</c>, which lets a domain delegate publishing without touching its own
/// web root; <b>direct</b> asks <c>example.com</c> itself. Advanced is tried first, as the standard
/// says, and a domain that answers neither has published nothing.
/// </para>
/// </remarks>
public sealed class WebKeyDirectory : IDisposable
{
    /// <summary>The most key material one lookup will read.</summary>
    /// <remarks>
    /// A key ring is a few kilobytes and a hostile server can stream for ever. 256 KB is room for
    /// a key with a long history of signatures on it and nowhere near room for a denial of service.
    /// </remarks>
    public const int MostKeyBytes = 256 * 1024;

    /// <summary>z-base-32, which is what the standard hashes a local part into. Not RFC 4648's.</summary>
    private const string ZBase32 = "ybndrfg8ejkmcpqxot1uwisza345h769";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <param name="handler">
    /// Injectable so a fake directory is a handler and the whole of this can be tested without a
    /// domain, exactly as the DAV client is.
    /// </param>
    public WebKeyDirectory(HttpMessageHandler? handler = null)
    {
        _ownsClient = handler is null;

        handler ??= new HttpClientHandler
        {
            // A key directory that answers with a redirect is answering about somewhere else. The
            // standard's whole argument is that the domain in the address published this.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        _http = new HttpClient(handler, disposeHandler: _ownsClient)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>
    /// Asks an address's own domain for its key, advanced method first.
    /// </summary>
    /// <remarks>
    /// A key that comes back naming somebody else is discarded. Without that check a domain could
    /// answer every lookup with one key of its own and read everything its users were sent —
    /// which is the attack the directory exists to make harder, not one to reintroduce here.
    /// </remarks>
    public async Task<KeyLookup> FindAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!MailboxAddress.TryParse(address, out var mailbox) || mailbox.Address is not { Length: > 0 } parsed)
        {
            return new KeyLookup(null, "That is not an e-mail address.");
        }

        var at = parsed.LastIndexOf('@');
        if (at <= 0 || at == parsed.Length - 1) return new KeyLookup(null, "That is not an e-mail address.");

        var local = parsed[..at];
        var domain = parsed[(at + 1)..].ToLowerInvariant();
        var hashed = Hash(local);
        var escaped = Uri.EscapeDataString(local);

        foreach (var url in new[]
        {
            $"https://openpgpkey.{domain}/.well-known/openpgpkey/{domain}/hu/{hashed}?l={escaped}",
            $"https://{domain}/.well-known/openpgpkey/hu/{hashed}?l={escaped}",
        })
        {
            if (await FetchAsync(url, cancellationToken).ConfigureAwait(false) is not { } bytes) continue;

            if (Read(bytes) is not { } ring)
            {
                Log.Warn($"A key directory at {domain} answered with something that is not a key.");
                continue;
            }

            if (!Names(ring, parsed))
            {
                Log.Warn($"A key directory at {domain} answered with a key for somebody else.");
                return new KeyLookup(
                    null, $"The key {domain} published does not belong to {parsed}, so it was discarded.");
            }

            return new KeyLookup(ring, string.Empty);
        }

        return KeyLookup.Nothing;
    }

    /// <summary>The bytes at one URL, or null when there is nothing usable there.</summary>
    private async Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            // Read to the cap and no further, and refuse what says up front it is bigger: a length
            // header is the server's claim and the copy below is what actually holds it to it.
            if (response.Content.Headers.ContentLength is > MostKeyBytes) return null;

            await using var body = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            var chunk = new byte[8 * 1024];
            int read;

            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MostKeyBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // A domain that publishes nothing does not answer, and that is the ordinary case rather
            // than an error worth showing anybody.
            return null;
        }
    }

    /// <summary>
    /// The answer as a key ring — binary as the standard asks, or armoured because somebody did it
    /// anyway.
    /// </summary>
    private static PgpPublicKeyRing? Read(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(stream));

            PgpObject? packet;
            while ((packet = factory.NextPgpObject()) is not null)
            {
                if (packet is PgpPublicKeyRing ring) return ring;
            }

            return null;
        }
        catch (Exception ex) when (ex is PgpException or IOException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether one of the ring's user IDs is the address that was asked about.</summary>
    private static bool Names(PgpPublicKeyRing ring, string address)
    {
        foreach (var key in ring.GetPublicKeys())
        {
            foreach (var id in key.GetUserIds())
            {
                if (MailboxAddress.TryParse(id, out var mailbox)
                    && string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The local part as the directory names it: SHA-1, lower-cased, in z-base-32.
    /// </summary>
    /// <remarks>
    /// SHA-1 because the standard says SHA-1, and it is not being used as a signature here — the
    /// hash is a file name, and finding a second local part that collides with somebody's would
    /// win an attacker a chance to publish a key their own domain would have let them publish
    /// anyway. z-base-32 is Zooko's alphabet rather than RFC 4648's, and the difference is not
    /// cosmetic: the wrong one asks for a URL that is not there.
    /// </remarks>
    internal static string Hash(string local)
    {
#pragma warning disable CA5350 // The standard names SHA-1, and this is a file name rather than a signature.
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(local.ToLower(CultureInfo.InvariantCulture)));
#pragma warning restore CA5350

        // 20 bytes at five bits each is 32 characters exactly, so there is no padding to think about.
        var text = new StringBuilder(32);
        var bits = 0;
        var accumulator = 0;

        foreach (var b in digest)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                text.Append(ZBase32[(accumulator >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return text.ToString();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
