using System.DirectoryServices.Protocols;
using System.Net;

namespace Mailbox.Contacts.Directory;

/// <summary>What a directory search came back with, including the ways it did not work.</summary>
/// <param name="People">Who was found, in the order the server gave them.</param>
/// <param name="Refusal">
/// Why nothing was found, in a sentence a reader can act on, or empty when the search worked —
/// including when it worked and matched nobody.
/// </param>
/// <param name="Truncated">
/// Whether the server had more to say than was asked for. Worth showing: "no results" and "the
/// first hundred of far too many" are different answers and look identical in a list.
/// </param>
public sealed record DirectoryResult(
    IReadOnlyList<Contact> People,
    string Refusal = "",
    bool Truncated = false)
{
    public bool Worked => Refusal.Length == 0;

    public static DirectoryResult Failed(string why) => new([], why);
}

/// <summary>
/// Looks people up in an LDAP directory.
/// </summary>
/// <remarks>
/// A connection per search rather than one held open. A directory is asked a question every few
/// keystrokes and then not for an hour, and a pooled connection would have to survive sleep, a
/// changed network and a server that closes idle sessions — three failure modes in exchange for
/// the milliseconds a bind costs on a network fast enough to be worth searching over.
/// <para>
/// Every failure comes back as a sentence rather than an exception. This runs behind a text box
/// somebody is typing into: a directory that is unreachable, misconfigured or refusing the
/// password must leave the local results standing and say what is wrong somewhere the reader can
/// see it, never interrupt what they are doing. The one thing that is not caught is being
/// cancelled.
/// </para>
/// <para>
/// <b>The library is loaded lazily.</b> System.DirectoryServices.Protocols opens the platform's
/// own LDAP library by name at run time, so a machine without it throws on the first call rather
/// than at start-up — which is why the packages ask for it weakly and why <see cref="IsAvailable"/>
/// exists: a directory that cannot be searched because the machine has no LDAP library is a
/// different thing from one that is refusing to answer, and telling the reader the second when it
/// is the first sends them to look at the wrong end.
/// </para>
/// </remarks>
public static class LdapDirectorySearch
{
    /// <summary>
    /// The encrypted session was refused — carried as its own type so the sentence about it
    /// survives the layer that turns every other connection failure into "did not answer".
    /// </summary>
    private sealed class TlsFailed(string message) : Exception(message);

    /// <summary>Whether this machine has the LDAP library the search needs.</summary>
    /// <remarks>
    /// Asked by building a connection and throwing it away, which is the only honest test: the
    /// library is opened on first use, and nothing else here can tell whether it is there.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var probe = new LdapConnection(new LdapDirectoryIdentifier("localhost", 389));
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException
                                           or PlatformNotSupportedException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>What to say when it is not.</summary>
    public const string Missing =
        "This machine has no LDAP library, so directories cannot be searched. "
        + "Install OpenLDAP (libldap) and restart.";

    /// <summary>
    /// Searches one directory. Never throws for anything the directory did.
    /// </summary>
    /// <param name="directory">Where to look.</param>
    /// <param name="filter">The filter, from <see cref="LdapFilter"/>.</param>
    /// <param name="password">The bind password, or null for an anonymous bind.</param>
    public static DirectoryResult Search(LdapDirectory directory, string filter, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!directory.IsUsable) return DirectoryResult.Failed("This directory has no server or no search base.");
        if (string.IsNullOrWhiteSpace(filter)) return new DirectoryResult([]);

        try
        {
            using var connection = Connect(directory, password);

            var request = new SearchRequest(
                directory.BaseDn,
                filter,
                directory.Scope == DirectoryScope.OneLevel ? SearchScope.OneLevel : SearchScope.Subtree,
                [.. LdapEntries.Attributes]);

            // The server's own limit as well as ours, so a directory that would have streamed
            // thousands stops at the source rather than over the network.
            request.SizeLimit = directory.MaxResults;
            request.TimeLimit = TimeSpan.FromSeconds(directory.TimeoutSeconds);

            var response = (SearchResponse)connection.SendRequest(request);
            return Read(response, directory.MaxResults);
        }
        catch (DirectoryOperationException ex)
        {
            // SizeLimitExceeded is the server saying "that is as many as you may have", which is
            // an answer rather than a failure: the entries it did send are in the response.
            if (ex.Response is SearchResponse partial
                && ex.Response.ResultCode is ResultCode.SizeLimitExceeded or ResultCode.TimeLimitExceeded)
            {
                return Read(partial, directory.MaxResults) with { Truncated = true };
            }

            // A base that names nothing arrives here rather than as an LdapException, and it is
            // the mistake somebody setting one of these up makes most often — so it gets the
            // sentence that says where to look rather than "the object does not exist".
            if (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                return DirectoryResult.Failed($"The search base “{directory.BaseDn}” does not exist on this server.");
            }

            return DirectoryResult.Failed($"The directory refused the search: {Plain(ex.Message)}");
        }
        catch (TlsFailed ex)
        {
            return DirectoryResult.Failed(ex.Message);
        }
        catch (LdapException ex)
        {
            return DirectoryResult.Failed(Explain(ex, directory));
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException
                                       or PlatformNotSupportedException or EntryPointNotFoundException)
        {
            return DirectoryResult.Failed(Missing);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return DirectoryResult.Failed($"The directory could not be searched: {Plain(ex.Message)}");
        }
    }

    /// <summary>
    /// Binds and lets go, which is what a Test Settings button asks.
    /// </summary>
    /// <returns>Empty when it worked, or why it did not.</returns>
    public static string Check(LdapDirectory directory, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!directory.IsUsable) return "Fill in the server and the search base first.";

        // A search rather than a bare bind: an anonymous bind succeeds against almost anything,
        // including a server whose search base is wrong, so binding alone would report a working
        // directory that finds nobody. One entry is enough to prove the base exists.
        var probe = directory with { MaxResults = 1 };
        var result = Search(probe, LdapFilter.Everyone(), password);
        return result.Worked ? string.Empty : result.Refusal;
    }

    private static LdapConnection Connect(LdapDirectory directory, string? password)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(directory.Host, directory.Port))
        {
            AuthType = string.IsNullOrWhiteSpace(directory.BindDn) ? AuthType.Anonymous : AuthType.Basic,
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.Timeout = TimeSpan.FromSeconds(directory.TimeoutSeconds);

        if (directory.UseTls)
        {
            // 636 is wrapped from the first byte; every other port negotiates on the plain one.
            // Set before the bind either way, so no password is ever sent in the clear.
            if (directory.Port == 636)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else
            {
                try
                {
                    connection.SessionOptions.StartTransportLayerSecurity(null);
                }
                catch (Exception ex) when (ex is DirectoryOperationException or LdapException)
                {
                    // A server with no TLS refuses the negotiation, and every failure after that
                    // point looks like the connection failing — which sent the reader to check
                    // the host and the port when the answer is this tick box. Said plainly, and
                    // the connection is dropped rather than continued in the clear: the password
                    // was going to be sent over it.
                    connection.Dispose();
                    throw new TlsFailed(
                        $"{directory.Host}:{directory.Port} would not start an encrypted session. "
                        + "Untick encryption, or use port 636.");
                }
            }
        }

        connection.Bind(
            string.IsNullOrWhiteSpace(directory.BindDn)
                ? null
                : new NetworkCredential(directory.BindDn, password ?? string.Empty));

        return connection;
    }

    private static DirectoryResult Read(SearchResponse response, int limit)
    {
        var people = new List<Contact>();
        foreach (SearchResultEntry entry in response.Entries)
        {
            if (people.Count >= limit) return new DirectoryResult(people, string.Empty, Truncated: true);

            var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in entry.Attributes.AttributeNames)
            {
                attributes[name] = [.. entry.Attributes[name].GetValues(typeof(string)).Cast<string>()];
            }

            if (LdapEntries.ToContact(entry.DistinguishedName, attributes) is { } person) people.Add(person);
        }

        return new DirectoryResult(people);
    }

    /// <summary>
    /// The failures worth telling apart, because each sends the reader somewhere different.
    /// </summary>
    private static string Explain(LdapException ex, LdapDirectory directory) => ex.ErrorCode switch
    {
        // 49: the password or the bind DN. 48: the server wants credentials and got none.
        49 => "The directory refused those credentials.",
        48 => "This directory will not accept an anonymous connection; give it a user to sign in as.",

        // 32: no such object — the base DN names nothing on this server, which is the mistake
        // somebody setting one of these up makes most often and the one an error code hides.
        32 => $"The search base “{directory.BaseDn}” does not exist on this server.",
        34 => $"“{directory.BaseDn}” is not a valid search base.",

        // 81: the server could not be reached at all — a wrong host, a wrong port, or nothing
        // listening. 91: connect failed for the same reasons a socket does.
        81 or 91 => $"{directory.Host}:{directory.Port} did not answer.",
        _ => $"The directory could not be reached: {Plain(ex.Message)}",
    };

    /// <summary>One line, no trailing full stop doubled up.</summary>
    private static string Plain(string message)
        => message.ReplaceLineEndings(" ").Trim().TrimEnd('.');
}
