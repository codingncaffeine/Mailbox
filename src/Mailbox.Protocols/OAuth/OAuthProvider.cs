namespace Mailbox.Protocols.OAuth;

/// <summary>
/// One authorization server, and what this application asks it for.
/// </summary>
/// <remarks>
/// A record rather than a class hierarchy because the providers differ only in strings. What
/// they do not differ in is the flow: authorization code with PKCE, a loopback redirect, and no
/// client secret anywhere — RFC 8252's shape for a native application, which is the only shape a
/// desktop program can implement honestly.
/// </remarks>
public sealed record OAuthProvider(
    string Id,
    string Name,
    Uri Authorization,
    Uri Token,
    string Scopes)
{
    /// <summary>
    /// The client registration this project ships, or empty when there is none and the user has
    /// to bring their own.
    /// </summary>
    /// <remarks>
    /// Empty is not a placeholder for a secret. A native application's client ID is public by
    /// construction — it travels in a URL the browser shows — so shipping one is a question of
    /// whose registration it is, not of whether it can be kept. Where it is empty, either the
    /// provider's terms forbid distributing one (Google) or nobody has registered it yet.
    /// </remarks>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Extra query parameters this provider wants on the authorization request.
    /// </summary>
    /// <remarks>
    /// Google issues a refresh token only when asked (<c>access_type=offline</c>), and only on
    /// the first consent unless told to ask again — an account re-added after a reinstall would
    /// otherwise come back with an access token good for an hour and no way to renew it.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ExtraParameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>What to tell someone who has to register their own client.</summary>
    public string? OwnClientGuidance { get; init; }

    /// <summary>True when this provider can be used without the user registering anything.</summary>
    public bool WorksOutOfTheBox => ClientId.Length > 0;
}

/// <summary>The providers this application knows how to sign in to.</summary>
public static class OAuthProviders
{
    /// <summary>
    /// Microsoft's consumer and work accounts, over the endpoints its hosted mail service uses.
    /// </summary>
    /// <remarks>
    /// <c>offline_access</c> is what makes a refresh token come back; the three service scopes are
    /// what Exchange Online checks when the SASL exchange happens, and asking for the wrong one
    /// authenticates and then fails at the first command. <c>common</c> as the tenant covers a
    /// personal account and a work one with the same registration.
    /// <para>
    /// The registration is free, takes no security assessment and holds no secret, so Microsoft's
    /// side of the stance is a public client and nothing more. It is a project action rather than a code
    /// one: until an application is registered and its ID put in <see cref="ClientIds"/>, this
    /// provider works through a client the user brings.
    /// </para>
    /// </remarks>
    public static readonly OAuthProvider Microsoft = new(
        "microsoft",
        "Microsoft",
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        "offline_access "
        + "https://outlook.office.com/IMAP.AccessAsUser.All "
        + "https://outlook.office.com/POP.AccessAsUser.All "
        + "https://outlook.office.com/SMTP.Send")
    {
        ClientId = ClientIds.Microsoft,
        OwnClientGuidance =
            "Register a free application at the Azure portal (Microsoft Entra ID, App "
            + "registrations), choose \"Public client/native\", add the redirect URI "
            + "http://localhost, and paste the Application (client) ID here.",
    };

    /// <summary>
    /// Google, for the one service it does not expose over a standard protocol.
    /// </summary>
    /// <remarks>
    /// Mail, calendar and contacts all reach Google over IMAP, SMTP, CalDAV and CardDAV with an
    /// app password, which is the default path and needs none of this. Tasks has no such door:
    /// it is a REST API and OAuth-only, so it is the one place a Google sign-in is required.
    /// <para>
    /// The client ID is deliberately empty and stays empty. Google's API Terms prohibit shipping
    /// one in an open-source application, and the mail scope is "restricted" — an annual paid
    /// security assessment, which the no-hosted-services stance rules out. A user's own free Cloud project sidesteps both,
    /// because the credential is then theirs.
    /// </para>
    /// </remarks>
    public static readonly OAuthProvider Google = new(
        "google",
        "Google",
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        "https://www.googleapis.com/auth/tasks")
    {
        ExtraParameters = new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        },
        OwnClientGuidance =
            "Google does not allow an open-source application to ship a sign-in credential, so "
            + "this one is yours: create a free project at console.cloud.google.com, enable the "
            + "Tasks API, create an OAuth client of type \"Desktop app\", and paste its client "
            + "ID here.",
    };

    public static readonly IReadOnlyList<OAuthProvider> All = [Microsoft, Google];

    public static OAuthProvider? ById(string id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The provider that signs in to an address's mail, or null for one that needs none.</summary>
    public static OAuthProvider? ForMail(string address)
    {
        var domain = Autoconfig.DomainOf(address);
        return domain.ToLowerInvariant() switch
        {
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" or "passport.com" => Microsoft,
            _ => null,
        };
    }
}

/// <summary>
/// The client registrations this build ships. One place, so there is one thing to look at.
/// </summary>
/// <remarks>
/// These are identifiers, not secrets, and the file is checked in on purpose: a build that
/// carried a credential nobody could see would be exactly the arrangement the stance rejects. Empty
/// means no registration exists yet, and the sign-in offers to take one from the user instead of
/// failing.
/// </remarks>
public static class ClientIds
{
    /// <summary>
    /// Azure application (client) ID. Empty until the project registers one — see
    /// <see cref="OAuthProviders.Microsoft"/> for what that involves and why nothing here can do
    /// it for itself.
    /// </summary>
    public const string Microsoft = "";
}
