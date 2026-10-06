using Mailbox.Security.Dns;

namespace Mailbox.App.Views;

/// <summary>
/// MX answers a capture run is given instead of asking the network.
/// </summary>
/// <remarks>
/// A photograph of the account wizard has to come out the same on every machine, and a real
/// lookup would make it depend on the resolver and on whatever the domain publishes that day.
/// <c>MAILBOX_MX</c> holds <c>domain=host host;domain=host</c>; a domain it does not name has no
/// records, which is the answer a lookup that found nothing gives.
/// </remarks>
internal sealed class PosedMx(IReadOnlyDictionary<string, string[]> answers) : IMxLookup
{
    public const string Variable = "MAILBOX_MX";

    public static PosedMx FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    internal static PosedMx Parse(string? text)
    {
        var answers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Split('=', 2) is [var domain, var hosts])
            {
                answers[domain.Trim()] = hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return new PosedMx(answers);
    }

    public Task<DnsAnswer> MxAsync(string domain, CancellationToken cancellation = default)
    {
        var found = answers.TryGetValue(domain, out var hosts)
            ? new DnsAnswer(DnsResponseCode.NoError, hosts, 300)
            : new DnsAnswer(DnsResponseCode.NameError, [], 0);

        Core.Diagnostics.Log.Info($"Harness: posed MX for {domain} — {(hosts is null ? "none" : string.Join(", ", hosts))}.");
        return Task.FromResult(found);
    }
}
