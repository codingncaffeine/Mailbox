using System.Security.Cryptography;
using System.Text;

namespace Mailbox.Protocols.OAuth;

/// <summary>
/// The proof that the client redeeming an authorization code is the one that asked for it.
/// </summary>
/// <remarks>
/// RFC 7636. A native application's redirect is a loopback URL, and any program on the machine
/// may register to answer one — so the code arrives somewhere an attacker can also listen. PKCE
/// is what makes an intercepted code worthless: the token request has to carry the verifier whose
/// hash was sent with the authorization request, and only the client that made it has that.
/// <para>
/// <c>S256</c> only. The specification also defines <c>plain</c>, where the challenge is the
/// verifier — which protects against nothing at all if the authorization request can be observed,
/// and is offered here as no option because a fallback nobody chose would be the one an
/// authorization server could negotiate us down to.
/// </para>
/// </remarks>
public sealed record PkceChallenge(string Verifier, string Challenge)
{
    /// <summary>The one method this application implements.</summary>
    public const string Method = "S256";

    /// <summary>
    /// A fresh verifier and its challenge. 64 bytes of randomness rather than the minimum 32,
    /// the cost being a longer URL nobody reads.
    /// </summary>
    public static PkceChallenge Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new PkceChallenge(verifier, Hash(verifier));
    }

    /// <summary>The challenge for a verifier: base64url of its SHA-256, unpadded.</summary>
    public static string Hash(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// base64url without padding, which is what every parameter in these flows is encoded as.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Convert.ToBase64String(byte[])"/> with replacements bolted on afterwards:
    /// the padding has to go as well as the two characters, and a trailing '=' in a query
    /// parameter is the sort of thing that works against one authorization server and not the
    /// next.
    /// </remarks>
    internal static string Base64Url(byte[] bytes) => Base64UrlTextEncoder(bytes);

    private static string Base64UrlTextEncoder(byte[] bytes)
    {
        var text = Convert.ToBase64String(bytes);
        var end = text.Length;
        while (end > 0 && text[end - 1] == '=') end--;

        var builder = new StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            builder.Append(text[i] switch { '+' => '-', '/' => '_', var c => c });
        }

        return builder.ToString();
    }
}
