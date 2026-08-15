using System.Net.Sockets;
using System.Security.Authentication;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>What a publish did, in words for the status bar, and what the server can do.</summary>
public sealed record SievePublishOutcome(bool Ok, string Message, SieveCapabilities? Capabilities = null)
{
    /// <summary>How many rules are on the server now.</summary>
    public int RulesOnServer { get; init; }
}

/// <summary>
/// Puts the account's server-side rules on its mail server as one Sieve script, and takes them
/// down again when the last of them goes.
/// </summary>
/// <remarks>
/// The script is <see cref="SieveCompiler.ScriptName"/>. If another script was active the first
/// time — a host's own rules, or ones written elsewhere — and the server supports
/// <c>include</c>, Mailbox's script includes it first so it keeps running; the store remembers
/// which so that taking Mailbox's script down makes it active again. Every publish is whole:
/// the enabled server-side rules compiled fresh from the store, so a renamed folder or an
/// edited rule is a new script, and the store's <see cref="MailRepository.SieveState"/> says
/// whether the server has it. Failures leave that state stale, which is what makes
/// <see cref="RulesHandler"/> run the rules here in the meantime.
/// </remarks>
public static class SievePublisher
{
    /// <summary>Connects and signs in, and reports what the server can do — for the wizard's checkbox.</summary>
    public static async Task<SieveCapabilities> ProbeAsync(ServerSettings server, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(server);

        await using var client = new ManageSieveClient();
        await client.ConnectAsync(server, cancellation).ConfigureAwait(false);
        await client.AuthenticateAsync(server.UserName, server.Password, cancellation).ConfigureAwait(false);
        var capabilities = client.Capabilities;
        await client.LogoutAsync(cancellation).ConfigureAwait(false);
        return capabilities;
    }

    /// <summary>
    /// Compiles the account's enabled server-side rules and puts them on the server as the
    /// active script; with none left, takes Mailbox's script down. Never throws for the server's
    /// sake — the outcome says what happened, and the store is marked stale on failure.
    /// </summary>
    public static async Task<SievePublishOutcome> PublishAsync(
        ServerSettings server, MailRepository mail, long accountId, IReadOnlyList<string> ownAddresses,
        Func<DateTimeOffset>? now = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        var clock = now ?? (() => DateTimeOffset.UtcNow);
        var rules = mail.Rules().Where(r => r.Enabled && r.ServerSide).ToList();
        var state = mail.SieveState();

        if (rules.Count == 0 && state is null)
        {
            return new SievePublishOutcome(true, "No rules run on the server.");
        }

        try
        {
            await using var client = new ManageSieveClient();
            await client.ConnectAsync(server, cancellation).ConfigureAwait(false);
            await client.AuthenticateAsync(server.UserName, server.Password, cancellation).ConfigureAwait(false);
            var capabilities = client.Capabilities;

            var scripts = await client.ListScriptsAsync(cancellation).ConfigureAwait(false);
            var active = scripts.FirstOrDefault(s => s.Active)?.Name;
            var ours = scripts.Any(s => string.Equals(s.Name, SieveCompiler.ScriptName, StringComparison.Ordinal));

            if (rules.Count == 0)
            {
                // The last server-side rule went: hand the server back what it had.
                if (string.Equals(active, SieveCompiler.ScriptName, StringComparison.Ordinal))
                {
                    await client.SetActiveAsync(state?.Include ?? string.Empty, cancellation).ConfigureAwait(false);
                }

                if (ours) await client.DeleteScriptAsync(SieveCompiler.ScriptName, cancellation).ConfigureAwait(false);
                await client.LogoutAsync(cancellation).ConfigureAwait(false);

                mail.ClearSieveState();
                Log.Info($"Sieve: script removed from {server.Host}.");
                return new SievePublishOutcome(true,
                    state?.Include is { Length: > 0 } restored ? $"Rules removed from the server; \"{restored}\" is active again." : "Rules removed from the server.",
                    capabilities);
            }

            // Which script to include first: the one remembered, else the one active now when
            // it is not ours and the server can include.
            var include = state?.Include;
            var replaced = (string?)null;
            if (include is null && active is { Length: > 0 } && !string.Equals(active, SieveCompiler.ScriptName, StringComparison.Ordinal))
            {
                if (capabilities.Extensions.Contains("include")) include = active;
                else replaced = active;
            }

            var context = new SieveContext
            {
                OwnAddresses = ownAddresses,
                FolderPath = id => mail.GetFolder(id)?.ImapPath,
                DeletedItemsPath = mail.FolderWithRole(accountId, FolderRole.Deleted)?.ImapPath,
                Extensions = capabilities.Extensions,
            };

            var compiled = rules.Select(r => (Rule: r, Sieve: SieveCompiler.Compile(r, context))).ToList();
            var left = compiled.Where(c => !c.Sieve.Compiles).Select(c => c.Rule.Name).ToList();
            var script = SieveCompiler.Script(compiled.Where(c => c.Sieve.Compiles).Select(c => c.Rule), context, include);

            // A rule the server cannot run — a folder that lost its server name, an extension this
            // server lacks — goes back to running here, rather than nowhere.
            foreach (var (rule, _) in compiled.Where(c => !c.Sieve.Compiles))
            {
                mail.SetRuleServerSide(rule.Id, false);
                Log.Warn($"Sieve: rule \"{rule.Name}\" runs here after all: {string.Join("; ", SieveCompiler.Compile(rule, context).Reasons)}.");
            }

            await client.PutScriptAsync(SieveCompiler.ScriptName, script, cancellation).ConfigureAwait(false);
            await client.SetActiveAsync(SieveCompiler.ScriptName, cancellation).ConfigureAwait(false);
            await client.LogoutAsync(cancellation).ConfigureAwait(false);

            mail.SetSieveState(script, include, clock());
            var count = compiled.Count - left.Count;
            Log.Info($"Sieve: {count} rule{(count == 1 ? "" : "s")} published to {server.Host}.");

            var message = $"{count} rule{(count == 1 ? "" : "s")} on the server.";
            if (include is { Length: > 0 }) message += $" \"{include}\" still runs first.";
            if (replaced is { Length: > 0 }) message += $" The server's script \"{replaced}\" no longer runs: the server can't include it.";
            if (left.Count > 0) message += $" Left on this computer: {string.Join(", ", left)}.";
            return new SievePublishOutcome(true, message, capabilities) { RulesOnServer = count };
        }
        catch (Exception ex) when (ex is ManageSieveException or IOException or SocketException or AuthenticationException or OperationCanceledException)
        {
            if (state is not null) mail.MarkSieveStale();
            Log.Warn($"Sieve: publish to {server.Host} failed.", ex);
            return new SievePublishOutcome(false, $"The rules could not be put on the server: {ex.Message} They run on this computer until they can be.");
        }
    }
}
