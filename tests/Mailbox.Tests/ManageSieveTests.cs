using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Security;
using Mailbox.Core.Rules;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// A ManageSieve server on the loopback, speaking RFC 5804 the way Dovecot does: the greeting,
/// PLAIN authentication, LISTSCRIPTS, PUTSCRIPT with a literal, SETACTIVE, DELETESCRIPT,
/// GETSCRIPT with a literal reply, LOGOUT. It keeps the scripts it is given so the tests can
/// look at what the client put there.
/// </summary>
internal sealed class FakeSieveServer : IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly TcpListener _listener;
    private readonly Task _serving;
    private readonly CancellationTokenSource _stop = new();

    public FakeSieveServer(string? presetActive = null, string? presetScript = null)
    {
        if (presetActive is not null) { Scripts[presetActive] = presetScript ?? "keep;\r\n"; Active = presetActive; }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serving = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Dictionary<string, string> Scripts { get; } = new(StringComparer.Ordinal);

    public string? Active { get; private set; }

    public List<string> Commands { get; } = [];

    /// <summary>The user name and password the client sent, decoded.</summary>
    public (string User, string Password)? Credentials { get; private set; }

    /// <summary>What to answer PUTSCRIPT with instead of OK — for the "server refuses" test.</summary>
    public string? RefusePutWith { get; set; }

    public string Extensions { get; set; } = "fileinto reject envelope comparator-i;ascii-numeric relational imap4flags copy include variables body";

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await SessionAsync(client.GetStream());
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Lines and literals off the socket in bytes — a literal's length is a byte count, and a StreamReader would count chars.</summary>
    private sealed class ByteReader(Stream stream)
    {
        private readonly List<byte> _pending = [];

        public async Task<string?> ReadLineAsync()
        {
            while (true)
            {
                var newline = _pending.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var end = newline > 0 && _pending[newline - 1] == (byte)'\r' ? newline - 1 : newline;
                    var line = Utf8.GetString(_pending.Take(end).ToArray());
                    _pending.RemoveRange(0, newline + 1);
                    return line;
                }

                if (!await FillAsync()) return null;
            }
        }

        public async Task<string> ReadExactAsync(int length)
        {
            while (_pending.Count < length)
            {
                if (!await FillAsync()) throw new IOException("closed inside a literal");
            }

            var text = Utf8.GetString(_pending.Take(length).ToArray());
            _pending.RemoveRange(0, length);
            return text;
        }

        private async Task<bool> FillAsync()
        {
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) return false;
            _pending.AddRange(buffer.Take(read));
            return true;
        }
    }

    private async Task SessionAsync(NetworkStream stream)
    {
        var reader = new ByteReader(stream);
        var writer = new StreamWriter(stream, Utf8, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("\"IMPLEMENTATION\" \"Fake Pigeonhole\"");
        await writer.WriteLineAsync($"\"SIEVE\" \"{Extensions}\"");
        await writer.WriteLineAsync("\"NOTIFY\" \"mailto\"");
        await writer.WriteLineAsync("\"SASL\" \"PLAIN LOGIN\"");
        await writer.WriteLineAsync("\"VERSION\" \"1.0\"");
        await writer.WriteLineAsync("OK \"Fake ready.\"");

        while (await reader.ReadLineAsync() is { } line)
        {
            Commands.Add(line);
            var upper = line.ToUpperInvariant();

            if (upper.StartsWith("AUTHENTICATE \"PLAIN\""))
            {
                var (b64, _) = ManageSieveClient.ReadQuoted(line["AUTHENTICATE \"PLAIN\" ".Length..]);
                var parts = Utf8.GetString(Convert.FromBase64String(b64 ?? string.Empty)).Split('\0');
                Credentials = (parts.ElementAtOrDefault(1) ?? string.Empty, parts.ElementAtOrDefault(2) ?? string.Empty);
                await writer.WriteLineAsync(Credentials.Value.Password == "secret" ? "OK \"Logged in.\"" : "NO (AUTH-TOO-WEAK) \"Authentication failed.\"");
            }
            else if (upper == "LISTSCRIPTS")
            {
                foreach (var name in Scripts.Keys)
                {
                    await writer.WriteLineAsync($"{ManageSieveClient.Quote(name)}{(name == Active ? " ACTIVE" : string.Empty)}");
                }

                await writer.WriteLineAsync("OK \"Listscripts completed.\"");
            }
            else if (upper.StartsWith("PUTSCRIPT "))
            {
                var (name, rest) = ManageSieveClient.ReadQuoted(line["PUTSCRIPT ".Length..]);
                var length = int.Parse(rest.Trim().TrimStart('{').TrimEnd('}', '+'));
                var text = await reader.ReadExactAsync(length);
                await reader.ReadLineAsync(); // the CRLF after the literal
                if (RefusePutWith is { } refuse)
                {
                    await writer.WriteLineAsync($"NO {{{Utf8.GetByteCount(refuse)}}}");
                    await writer.WriteAsync(refuse);
                    await writer.WriteLineAsync();
                    continue;
                }

                Scripts[name!] = text;
                await writer.WriteLineAsync("OK \"Putscript completed.\"");
            }
            else if (upper.StartsWith("SETACTIVE "))
            {
                var (name, _) = ManageSieveClient.ReadQuoted(line["SETACTIVE ".Length..]);
                if (name is { Length: > 0 } && !Scripts.ContainsKey(name))
                {
                    await writer.WriteLineAsync("NO (NONEXISTENT) \"There is no script by that name\"");
                    continue;
                }

                Active = name is { Length: > 0 } ? name : null;
                await writer.WriteLineAsync("OK \"Setactive completed.\"");
            }
            else if (upper.StartsWith("DELETESCRIPT "))
            {
                var (name, _) = ManageSieveClient.ReadQuoted(line["DELETESCRIPT ".Length..]);
                if (name == Active) { await writer.WriteLineAsync("NO (ACTIVE) \"You may not delete an active script\""); continue; }
                Scripts.Remove(name!);
                await writer.WriteLineAsync("OK \"Deletescript completed.\"");
            }
            else if (upper.StartsWith("GETSCRIPT "))
            {
                var (name, _) = ManageSieveClient.ReadQuoted(line["GETSCRIPT ".Length..]);
                if (!Scripts.TryGetValue(name!, out var script)) { await writer.WriteLineAsync("NO (NONEXISTENT) \"No such script\""); continue; }
                await writer.WriteLineAsync($"{{{Utf8.GetByteCount(script)}}}");
                await writer.WriteAsync(script);
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("OK \"Getscript completed.\"");
            }
            else if (upper == "LOGOUT")
            {
                await writer.WriteLineAsync("OK \"Logout completed.\"");
                return;
            }
            else
            {
                await writer.WriteLineAsync("NO \"Unknown command\"");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _serving; } catch { /* stopping */ }
    }
}

public class ManageSieveTests
{
    private static ServerSettings Server(FakeSieveServer fake, string password = "secret")
        => new("127.0.0.1", fake.Port, SecureSocketOptions.None, "you@example.com", password);

    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- The client on the wire --------------------------------------------------------------

    [Fact]
    public async Task ReadsTheGreetingAndSignsInWithPlain()
    {
        await using var fake = new FakeSieveServer();
        await using var client = new ManageSieveClient();

        await client.ConnectAsync(Server(fake), TestContext.Current.CancellationToken);
        Assert.Equal("Fake Pigeonhole", client.Capabilities.Implementation);
        Assert.Contains("fileinto", client.Capabilities.Extensions);
        Assert.Contains("comparator-i;ascii-numeric", client.Capabilities.Extensions);
        Assert.Contains("PLAIN", client.Capabilities.Mechanisms);
        Assert.False(client.Capabilities.StartTls);

        await client.AuthenticateAsync("you@example.com", "secret", TestContext.Current.CancellationToken);
        Assert.Equal(("you@example.com", "secret"), fake.Credentials);
        await client.LogoutAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARefusedPasswordIsAnExceptionWithTheServersWords()
    {
        await using var fake = new FakeSieveServer();
        await using var client = new ManageSieveClient();
        await client.ConnectAsync(Server(fake), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ManageSieveException>(() => client.AuthenticateAsync("you@example.com", "wrong", TestContext.Current.CancellationToken));
        Assert.Equal("AUTH-TOO-WEAK", ex.Code);
        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task PutsListsActivatesGetsAndDeletesScripts()
    {
        await using var fake = new FakeSieveServer();
        await using var client = new ManageSieveClient();
        await client.ConnectAsync(Server(fake), TestContext.Current.CancellationToken);
        await client.AuthenticateAsync("you@example.com", "secret", TestContext.Current.CancellationToken);

        Assert.Empty(await client.ListScriptsAsync(TestContext.Current.CancellationToken));

        const string script = "require [\"fileinto\"];\r\nif header :contains \"subject\" \"héllo\" {\r\n    fileinto \"Archive\";\r\n}\r\n";
        await client.PutScriptAsync("mailbox", script, TestContext.Current.CancellationToken);
        Assert.Equal(script, fake.Scripts["mailbox"]);

        await client.SetActiveAsync("mailbox", TestContext.Current.CancellationToken);
        var listed = await client.ListScriptsAsync(TestContext.Current.CancellationToken);
        Assert.Equal([new SieveScriptInfo("mailbox", true)], listed);

        // A literal reply, with a non-ASCII byte in it, comes back whole.
        Assert.Equal(script, await client.GetScriptAsync("mailbox", TestContext.Current.CancellationToken));

        var active = await Assert.ThrowsAsync<ManageSieveException>(() => client.DeleteScriptAsync("mailbox", TestContext.Current.CancellationToken));
        Assert.Equal("ACTIVE", active.Code);

        await client.SetActiveAsync(string.Empty, TestContext.Current.CancellationToken);
        await client.DeleteScriptAsync("mailbox", TestContext.Current.CancellationToken);
        Assert.Empty(fake.Scripts);
        await client.LogoutAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AServersNoWithALiteralMessageIsReadWhole()
    {
        await using var fake = new FakeSieveServer { RefusePutWith = "line 3: unknown command 'fileinfo'\r\nerror: validation failed." };
        await using var client = new ManageSieveClient();
        await client.ConnectAsync(Server(fake), TestContext.Current.CancellationToken);
        await client.AuthenticateAsync("you@example.com", "secret", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ManageSieveException>(() => client.PutScriptAsync("mailbox", "fileinfo \"x\";", TestContext.Current.CancellationToken));
        Assert.Contains("line 3", ex.Message);
        Assert.Contains("validation failed", ex.Message);
    }

    // ---- The publisher over a store ---------------------------------------------------------

    private static (MailStore Store, MailRepository Repo, long AccountId, Folder Receipts) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Imap);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
        repo.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var deleted = repo.FolderWithRole(account.Id, FolderRole.Deleted)!;
        repo.MapFolder(deleted.Id, "Trash", "Deleted Items", null);
        var receipts = repo.AddFolder(account.Id, "Receipts", parentId: inbox.Id);
        repo.MapFolder(receipts.Id, "INBOX/Receipts", "Receipts", inbox.Id);
        return (store, repo, account.Id, receipts);
    }

    private static MailRule MoveRule(Folder receipts, bool serverSide = true) => new()
    {
        Name = "Receipts",
        ServerSide = serverSide,
        Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@shop.example"] }],
        Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = receipts.Id, FolderName = receipts.Name }, new RuleAction(RuleActionKind.StopProcessing)],
    };

    [Fact]
    public async Task PublishPutsTheCompiledRulesOnTheServerAndTheStoreKnows()
    {
        var (store, repo, accountId, receipts) = Fresh();
        using var _ = store;
        await using var fake = new FakeSieveServer();

        repo.AddRule(MoveRule(receipts), T0);
        repo.AddRule(new MailRule
        {
            Name = "Alert me",
            ServerSide = false,
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["urgent"] }],
            Actions = [new RuleAction(RuleActionKind.DesktopAlert)],
        }, T0);

        Assert.False(repo.ServerRulesCurrent());

        var outcome = await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, outcome.RulesOnServer);

        var script = fake.Scripts["mailbox"];
        Assert.Equal("mailbox", fake.Active);
        Assert.Contains("require [\"fileinto\"];", script);
        Assert.Contains("address :domain :is \"from\" \"shop.example\"", script);
        Assert.Contains("fileinto \"INBOX/Receipts\";", script);
        Assert.Contains("stop;", script);
        Assert.DoesNotContain("urgent", script);

        var state = repo.SieveState();
        Assert.NotNull(state);
        Assert.Equal(script, state.Script);
        Assert.False(state.Stale);
        Assert.True(repo.ServerRulesCurrent());

        // Editing a server-side rule leaves the server behind until the next publish.
        var rule = repo.Rules().First(r => r.Name == "Receipts");
        repo.UpdateRule(rule with { Name = "Shop receipts" });
        Assert.False(repo.ServerRulesCurrent());

        // Renaming the folder on the server does too.
        repo.MapFolder(receipts.Id, "INBOX/Shopping", "Shopping", null);
        Assert.False(repo.ServerRulesCurrent());

        outcome = await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok);
        Assert.Contains("fileinto \"INBOX/Shopping\";", fake.Scripts["mailbox"]);
        Assert.True(repo.ServerRulesCurrent());
    }

    [Fact]
    public async Task AnActiveScriptFromElsewhereIsIncludedFirstAndRestoredWhenOursGoes()
    {
        var (store, repo, accountId, receipts) = Fresh();
        using var _ = store;
        await using var fake = new FakeSieveServer(presetActive: "roundcube", presetScript: "keep;\r\n");

        var stored = repo.AddRule(MoveRule(receipts), T0);
        var outcome = await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("\"roundcube\" still runs first", outcome.Message);
        Assert.Contains("include :personal \"roundcube\";", fake.Scripts["mailbox"]);
        Assert.Contains("\"include\"", fake.Scripts["mailbox"]);
        Assert.Equal("mailbox", fake.Active);
        Assert.Equal("roundcube", repo.SieveState()!.Include);

        // The last server-side rule goes: the server gets its old script back and ours is gone.
        repo.DeleteRule(stored.Id);
        outcome = await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("roundcube", fake.Active);
        Assert.False(fake.Scripts.ContainsKey("mailbox"));
        Assert.Null(repo.SieveState());
    }

    [Fact]
    public async Task AFailedPublishLeavesTheServerMarkedBehindAndTheRulesRunningHere()
    {
        var (store, repo, accountId, receipts) = Fresh();
        using var _ = store;
        await using var fake = new FakeSieveServer();

        repo.AddRule(MoveRule(receipts), T0);
        Assert.True((await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken)).Ok);
        Assert.True(repo.ServerRulesCurrent());

        // A rule changed; the server now refuses everything.
        repo.SetRuleEnabled(repo.Rules()[0].Id, false);
        fake.RefusePutWith = "quota exceeded";
        repo.AddRule(MoveRule(receipts) with { Name = "Second" }, T0);
        var outcome = await SievePublisher.PublishAsync(Server(fake), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.False(outcome.Ok);
        Assert.Contains("quota exceeded", outcome.Message);
        Assert.False(repo.ServerRulesCurrent());

        // A wrong password is a failure with the server's words, not a crash.
        outcome = await SievePublisher.PublishAsync(Server(fake, password: "nope"), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.False(outcome.Ok);
        Assert.Contains("Authentication failed", outcome.Message);

        // Nothing listening: the same.
        await using var closed = new FakeSieveServer();
        var port = closed.Port;
        await closed.DisposeAsync();
        outcome = await SievePublisher.PublishAsync(new ServerSettings("127.0.0.1", port, SecureSocketOptions.None, "you@example.com", "secret"), repo, accountId, ["you@example.com"], () => T0, cancellation: TestContext.Current.CancellationToken);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public async Task ProbeReportsWhatTheServerCanDo()
    {
        await using var fake = new FakeSieveServer { Extensions = "fileinto imap4flags" };
        var capabilities = await SievePublisher.ProbeAsync(Server(fake), TestContext.Current.CancellationToken);
        Assert.Equal(new HashSet<string> { "fileinto", "imap4flags" }, capabilities.Extensions.ToHashSet());
        Assert.Contains(fake.Commands, c => c == "LOGOUT");
    }

    // ---- The compiler ----------------------------------------------------------------------------

    private static readonly SieveContext Dovecot = new()
    {
        OwnAddresses = ["you@example.com"],
        FolderPath = id => id switch { 10 => "INBOX/Receipts", 20 => "Archive", _ => null },
        DeletedItemsPath = "Trash",
        Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fileinto", "body", "copy", "imap4flags", "relational", "comparator-i;ascii-numeric", "include", "variables" },
    };

    [Fact]
    public void CompilesTheWizardsClausesToSieve()
    {
        var rule = new MailRule
        {
            Name = "Everything at once",
            Conditions =
            [
                new RuleCondition(RuleConditionKind.From) { Values = ["Alice <alice@example.org>", "@shop.example", "Bob"] },
                new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["invoice", "receipt"] },
                new RuleCondition(RuleConditionKind.BodyContains) { Values = ["total due"] },
                new RuleCondition(RuleConditionKind.SentOnlyToMe),
                new RuleCondition(RuleConditionKind.MyNameInToOrCc),
                new RuleCondition(RuleConditionKind.Importance) { Level = 2 },
                new RuleCondition(RuleConditionKind.SizeBetween) { Min = 10, Max = 500 },
            ],
            Exceptions = [new RuleCondition(RuleConditionKind.SenderAddressContains) { Values = ["noreply"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.CopyToFolder) { FolderId = 20, FolderName = "Archive" },
                new RuleAction(RuleActionKind.MarkAsRead),
                new RuleAction(RuleActionKind.FlagForFollowUp),
                new RuleAction(RuleActionKind.ForwardTo) { Values = ["Carol <carol@example.net>"] },
                new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 10, FolderName = "Receipts" },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
        };

        var compiled = SieveCompiler.Compile(rule, Dovecot);
        Assert.True(compiled.Compiles, string.Join("; ", compiled.Reasons));
        var block = compiled.Block!;

        Assert.StartsWith("# Rule: Everything at once\nif allof(", block);
        Assert.Contains("anyof(anyof(address :all :is \"from\" \"alice@example.org\", header :contains \"from\" \"Alice\"), address :domain :is \"from\" \"shop.example\", header :contains \"from\" \"Bob\")", block);
        Assert.Contains("header :contains \"subject\" [\"invoice\", \"receipt\"]", block);
        Assert.Contains("body :text :contains \"total due\"", block);
        Assert.Contains("address :count \"eq\" :comparator \"i;ascii-numeric\" [\"to\", \"cc\"] \"1\"", block);
        Assert.Contains("address :all :is [\"to\", \"cc\"] \"you@example.com\"", block);
        Assert.Contains("header :matches \"x-priority\" [\"1*\", \"2*\"]", block);
        Assert.Contains("allof(size :over 10239, size :under 512001)", block);
        Assert.Contains("not anyof(address :all :contains \"from\" \"noreply\")", block);
        Assert.Contains("    fileinto :copy \"Archive\";\n", block);
        Assert.Contains("    addflag \"\\\\Seen\";\n", block);
        Assert.Contains("    addflag \"\\\\Flagged\";\n", block);
        Assert.Contains("    redirect :copy \"carol@example.net\";\n", block);
        Assert.Contains("    fileinto \"INBOX/Receipts\";\n    stop;\n}\n", block);
        Assert.Equal(new HashSet<string> { "fileinto", "copy", "imap4flags", "body", "relational", "comparator-i;ascii-numeric" }, compiled.Requires.ToHashSet());
    }

    [Fact]
    public void SaysWhyARuleStaysOnThisComputer()
    {
        var alert = new MailRule
        {
            Name = "Alert",
            Conditions = [new RuleCondition(RuleConditionKind.HasAttachment)],
            Actions = [new RuleAction(RuleActionKind.DesktopAlert), new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 99, FolderName = "Local only" }],
        };

        var compiled = SieveCompiler.Compile(alert, Dovecot);
        Assert.False(compiled.Compiles);
        Assert.Equal(
        [
            "'which has an attachment' can't be tested on the server",
            "'display a Desktop Alert' happens on this computer",
            "the folder \"Local only\" isn't on the server",
        ], compiled.Reasons);

        // A server without the body extension cannot search text.
        var plain = Dovecot with { Extensions = new HashSet<string> { "fileinto" } };
        var body = new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.BodyContains) { Values = ["x"] }],
            Actions = [new RuleAction(RuleActionKind.MarkAsRead)],
        };
        var reasons = SieveCompiler.Compile(body, plain).Reasons;
        Assert.Contains(reasons, r => r.Contains("'body' extension"));
        Assert.Contains(reasons, r => r.Contains("'imap4flags' extension"));
    }

    [Fact]
    public void TheScriptDeclaresWhatItNeedsAndKeepsRuleOrder()
    {
        var first = new MailRule
        {
            Name = "Delete it",
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["spam \"quoted\""] }],
            Actions = [new RuleAction(RuleActionKind.Delete)],
        };
        var second = new MailRule
        {
            Name = "Gone",
            Conditions = [new RuleCondition(RuleConditionKind.SentTo) { Values = ["list@example.org"] }],
            Actions = [new RuleAction(RuleActionKind.PermanentlyDelete)],
        };
        var skipped = new MailRule { Name = "Local", Conditions = [new RuleCondition(RuleConditionKind.Flagged)], Actions = [new RuleAction(RuleActionKind.MarkAsRead)] };

        var script = SieveCompiler.Script([first, skipped, second], Dovecot, include: "roundcube");
        Assert.StartsWith("# Written by Mailbox,", script);
        Assert.Contains("require [\"fileinto\", \"include\"];", script);
        Assert.Contains("include :personal \"roundcube\";", script);
        Assert.Contains("header :contains \"subject\" \"spam \\\"quoted\\\"\"", script);
        Assert.Contains("fileinto \"Trash\";", script);
        Assert.Contains("address :all :is [\"to\", \"cc\"] \"list@example.org\"", script);
        Assert.Contains("discard;", script);
        Assert.DoesNotContain("# Rule: Local", script);
        Assert.True(script.IndexOf("# Rule: Delete it", StringComparison.Ordinal) < script.IndexOf("# Rule: Gone", StringComparison.Ordinal));
        Assert.Equal(16, SieveCompiler.Hash(script).Length);
    }

    // ---- The automatic reply ---------------------------------------------------------------

    private static readonly AwayMessage Away = new()
    {
        Enabled = true,
        Subject = "Away until Monday",
        Body = "I am away and will answer when I am back.\n\n. and a line that starts with a stop.",
        Days = 3,
        Addresses = ["me@alias.example"],
    };

    /// <summary>
    /// The whole point of holding the reply on the server: the dates are the server's to keep, so
    /// the window is a currentdate test and not something this application has to be running for.
    /// </summary>
    [Fact]
    public void TheAutomaticReplyCompilesToVacationInsideItsDates()
    {
        var context = Dovecot with
        {
            Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vacation", "date", "relational", "fileinto" },
        };

        var compiled = SieveCompiler.Vacation(
            Away with { From = new DateOnly(2026, 9, 7), Until = new DateOnly(2026, 9, 14) }, context);

        Assert.True(compiled.Compiles);
        Assert.Empty(compiled.Reasons);
        Assert.Equal(new HashSet<string> { "vacation", "date", "relational" }, compiled.Requires.ToHashSet());

        var block = compiled.Block!;
        Assert.Contains("if allof(currentdate :value \"ge\" \"date\" \"2026-09-07\", currentdate :value \"le\" \"date\" \"2026-09-14\") {", block);
        Assert.Contains("vacation :days 3 :subject \"Away until Monday\"", block);

        // Both addresses, the account's own included, or a message to an alias is not "to me" as
        // far as the server is concerned and goes unanswered.
        Assert.Contains(":addresses [\"me@alias.example\", \"you@example.com\"]", block);

        // The reply is a multi-line literal, and none of it is indented — every character between
        // text: and the closing stop is the message.
        Assert.Contains("text:\nI am away and will answer when I am back.\n\n.. and a line that starts with a stop.\n.\n;", block);
    }

    /// <summary>
    /// A server without the date extensions still sends the reply, and says so. Refusing to
    /// publish at all would leave somebody away with nothing answering.
    /// </summary>
    [Fact]
    public void WithoutTheDateExtensionsTheReplyRunsAndSaysSo()
    {
        var context = Dovecot with
        {
            Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vacation" },
        };

        var compiled = SieveCompiler.Vacation(Away with { Until = new DateOnly(2026, 9, 14) }, context);

        Assert.True(compiled.Compiles);
        Assert.DoesNotContain("currentdate", compiled.Block!);
        Assert.Contains("switched off", string.Join(" ", compiled.Reasons));
    }

    [Fact]
    public void AServerWithNoVacationActionIsToldAboutRatherThanWorkedAround()
    {
        var refused = SieveCompiler.Vacation(Away, Dovecot with { Extensions = new HashSet<string> { "fileinto" } });
        Assert.False(refused.Compiles);
        Assert.Contains("does not offer", string.Join(" ", refused.Reasons));

        var empty = SieveCompiler.Vacation(
            Away with { Body = "   " },
            Dovecot with { Extensions = new HashSet<string> { "vacation" } });
        Assert.False(empty.Compiles);
        Assert.Contains("nothing to say", string.Join(" ", empty.Reasons));
    }

    /// <summary>
    /// The reply goes before the rules. A rule that stops processing would otherwise skip it, and
    /// somebody away is away for every message, not only the ones no rule matched.
    /// </summary>
    [Fact]
    public void TheReplyIsPublishedAheadOfTheRules()
    {
        var context = Dovecot with
        {
            Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vacation", "fileinto" },
        };

        var rule = new MailRule
        {
            Name = "Receipts",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@shop.example"] }],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 10, FolderName = "Receipts" }, new RuleAction(RuleActionKind.StopProcessing)],
        };

        var script = SieveCompiler.Script([rule], context, include: null, away: Away);

        Assert.Contains("require [\"fileinto\", \"vacation\"];", script);
        Assert.True(
            script.IndexOf("vacation :days", StringComparison.Ordinal) < script.IndexOf("# Rule: Receipts", StringComparison.Ordinal),
            "the reply has to be reachable before a rule can stop processing");
    }

    /// <summary>An automatic reply on its own is reason enough to have a script at all.</summary>
    [Fact]
    public async Task TheReplyIsPublishedWithNoRulesAtAll()
    {
        var (store, repo, accountId, _) = Fresh();
        using var _keep = store;
        await using var fake = new FakeSieveServer { Extensions = "fileinto vacation date relational" };

        var outcome = await SievePublisher.PublishAsync(
            Server(fake), repo, accountId, ["you@example.com"], () => T0,
            Away with { Until = new DateOnly(2026, 9, 14) },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("mailbox", fake.Active);
        Assert.Contains("vacation :days 3", fake.Scripts["mailbox"]);
        Assert.Contains("automatic reply", outcome.Message);
        Assert.True(repo.ServerRulesCurrent());

        // And switching it off takes the script down again, which is what stops a server
        // answering for somebody who came back.
        outcome = await SievePublisher.PublishAsync(
            Server(fake), repo, accountId, ["you@example.com"], () => T0,
            Away with { Enabled = false },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.DoesNotContain("mailbox", fake.Scripts.Keys);
    }

    /// <summary>
    /// A server that cannot reply at all: the rules still go up, and the outcome says the reply
    /// did not — a reader who is told nothing believes their mail is being answered.
    /// </summary>
    [Fact]
    public async Task AServerWithoutVacationPublishesTheRulesAndSaysWhatItCouldNotDo()
    {
        var (store, repo, accountId, receipts) = Fresh();
        using var _keep = store;
        await using var fake = new FakeSieveServer { Extensions = "fileinto" };

        repo.AddRule(MoveRule(receipts), T0);
        var outcome = await SievePublisher.PublishAsync(
            Server(fake), repo, accountId, ["you@example.com"], () => T0, Away,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("The automatic reply is not", outcome.Message);
        Assert.DoesNotContain("vacation", fake.Scripts["mailbox"]);
    }
}
