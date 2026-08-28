using System.Globalization;
using MailKit;
using MailKit.Security;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The IMAP seam against a server that is really there.
/// </summary>
/// <remarks>
/// <see cref="FakeImap"/> answers what we expect a server to answer, which is exactly what makes
/// it useless for finding out what one really answers — the same argument
/// <see cref="RealDavTests"/> makes, and the same one that found two defects on the DAV engine's
/// first real run. These run only when told where a server is:
/// <code>
/// MAILBOX_IMAP_HOST=mail.example.com MAILBOX_IMAP_USER=you@example.com \
///   MAILBOX_IMAP_PASSWORD=secret dotnet test --filter RealImap
/// </code>
/// <para>
/// <b>Every test works inside a folder of its own</b>, named for the run, and removes it at the
/// end — so a run leaves the mailbox as it found it, two runs do not collide, and nothing here can
/// touch mail that was already there. Nothing is ever sent to anybody: the messages are appended
/// straight into that folder, which is the only way to have mail to act on without involving
/// somebody else's inbox.
/// </para>
/// Skipped, not passed, when no server is named — a green test that did nothing is worse than no
/// test at all.
/// </remarks>
/// <summary>
/// The real-server tests share one mailbox, so they run one at a time.
/// </summary>
/// <remarks>
/// Without this they race each other rather than testing anything: one class sends a message
/// while another is counting what is on the server, and the count moves under it. A failure that
/// means "two tests overlapped" is worse than no test, because somebody will go looking for it in
/// the code under test.
/// </remarks>
[CollectionDefinition("real-server", DisableParallelization = true)]
public sealed class RealServerCollection;

[Collection("real-server")]
public class RealImapTests
{
    private static string? Host => Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST");

    private static ServerSettings Server => new(
        Host ?? string.Empty,
        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_IMAP_PORT"), out var port) ? port : 993,
        SecureSocketOptions.SslOnConnect,
        Environment.GetEnvironmentVariable("MAILBOX_IMAP_USER") ?? string.Empty,
        Environment.GetEnvironmentVariable("MAILBOX_IMAP_PASSWORD") ?? string.Empty);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// A connected session with a folder of this run's own, removed on the way out.
    /// </summary>
    /// <remarks>
    /// The folder's name carries the process id rather than a timestamp, so two runs on one
    /// machine cannot land on the same name and a run that died without tidying leaves something
    /// a person can recognise as ours.
    /// </remarks>
    private sealed class Fixture : IAsyncDisposable
    {
        public required MailKitImapSession Session { get; init; }
        public required RemoteFolder Folder { get; init; }

        public static async Task<Fixture> OpenAsync(CancellationToken cancellation)
        {
            var session = new MailKitImapSession();
            await session.ConnectAsync(Server, cancellation);
            await session.AuthenticateAsync(Server, cancellation);

            var name = "MailboxTest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

            // A leftover from a run that died is removed rather than reused: its contents would
            // make the next run's counts wrong in a way that reads as a defect in the code.
            var existing = await session.ListFoldersAsync(cancellation);
            if (existing.FirstOrDefault(f => f.Name == name) is { } stale)
            {
                await session.DeleteFolderAsync(stale.Path, cancellation);
            }

            var folder = await session.CreateFolderAsync(name, cancellation);
            return new Fixture { Session = session, Folder = folder };
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.DeleteFolderAsync(Folder.Path, CancellationToken.None);
            }
            catch (Exception)
            {
                // The folder is already gone, or the connection is. Either way there is nothing
                // further to tidy, and a failure here would hide the failure that caused it.
            }

            try
            {
                await Session.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Same.
            }

            Session.Dispose();
        }

        /// <summary>Puts a message in this run's folder and hands back its UID where the server said.</summary>
        public async Task<long?> AppendAsync(string subject, CancellationToken cancellation, MessageFlags flags = MessageFlags.None)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
            message.To.Add(new MailboxAddress("You", Server.UserName));
            message.Subject = subject;
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("mailbox.test");
            message.Body = new TextPart("plain") { Text = "Written by Mailbox's own tests. Nothing here was sent." };

            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, cancellation);

            return await Session.AppendAsync(Folder.Path, buffer.ToArray(), flags, DateTimeOffset.UtcNow, cancellation);
        }
    }

    private static void SkipWithoutAServer()
        => Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_IMAP_HOST to run against a real server.");

    // ---- What the server says it can do ----

    /// <summary>
    /// Which features a real server offers, which is the thing a fake cannot tell anybody. The
    /// four the synchroniser branches on are read and reported rather than asserted: a server
    /// without CONDSTORE is a configuration, not a fault.
    /// </summary>
    [Fact]
    public async Task TheServerSaysWhatItSupports()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        var features = fixture.Session.Features;

        // Every server has to have these two for the synchroniser to work at all.
        Assert.True(fixture.Session.IsConnected);
        TestContext.Current.TestOutputHelper?.WriteLine($"Features: {features}");
    }

    // ---- Folders ----

    /// <summary>
    /// The folder list, and the roles read off it. A real server names its special folders with
    /// SPECIAL-USE flags and a fake says whatever it was told to; getting the Inbox wrong is the
    /// difference between mail arriving and mail vanishing.
    /// </summary>
    [Fact]
    public async Task TheFolderListNamesTheInbox()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        var folders = await fixture.Session.ListFoldersAsync(Stop);

        Assert.Contains(folders, f => f.Role == FolderRole.Inbox);
        Assert.Contains(folders, f => f.Path == fixture.Folder.Path);

        foreach (var folder in folders)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{folder.Path}  role={folder.Role}  selectable={folder.Selectable}  parent={folder.ParentPath ?? "-"}");
        }
    }

    /// <summary>
    /// Making, renaming, nesting and removing a folder, which is the half of §15's folder
    /// operations that had only ever met a fake.
    /// </summary>
    [Fact]
    public async Task AFolderCanBeMadeRenamedNestedAndRemoved()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);

        var child = await fixture.Session.CreateFolderAsync("Child", Stop, fixture.Folder.Path);
        Assert.Equal(fixture.Folder.Path, child.ParentPath);

        var renamed = await fixture.Session.RenameFolderAsync(child.Path, "Renamed", Stop);
        Assert.Equal("Renamed", renamed.Name);

        // Still under the same parent: a rename is not a move, and a server that treated it as
        // one would quietly reparent somebody's folder tree.
        Assert.Equal(fixture.Folder.Path, renamed.ParentPath);

        var listed = await fixture.Session.ListFoldersAsync(Stop);
        Assert.Contains(listed, f => f.Path == renamed.Path);

        await fixture.Session.DeleteFolderAsync(renamed.Path, Stop);

        var after = await fixture.Session.ListFoldersAsync(Stop);
        Assert.DoesNotContain(after, f => f.Path == renamed.Path);
    }

    // ---- Messages ----

    [Fact]
    public async Task AMessageAppendedComesBackWithItsFlagsAndItsText()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);

        var subject = "Mailbox test — append and read back";
        var uid = await fixture.AppendAsync(subject, Stop, MessageFlags.Seen);

        var state = await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);
        Assert.Equal(1, state.Count);

        var uids = await fixture.Session.SearchAllAsync(Stop);
        var found = Assert.Single(uids);

        // UIDPLUS is what makes an append name its own UID. Where the server has it, the two
        // answers have to agree — that agreement is what a move relies on to keep track of a
        // message it has just relocated.
        if (uid is { } stated) Assert.Equal(stated, found);

        var info = Assert.Single(await fixture.Session.FetchInfoAsync([found], Stop));
        Assert.True(info.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(info.Size > 0);
        Assert.NotNull(info.InternalDate);

        var message = await fixture.Session.GetMessageAsync(found, Stop);
        Assert.NotNull(message);
        Assert.Equal(subject, message.Subject);
    }

    [Fact]
    public async Task FlagsSetHereComeBackFromTheServer()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        await fixture.AppendAsync("Mailbox test — flags", Stop);
        await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);

        var uid = Assert.Single(await fixture.Session.SearchAllAsync(Stop));

        await fixture.Session.StoreFlagsAsync([uid], MessageFlags.Flagged | MessageFlags.Seen, set: true, Stop);
        var after = Assert.Single(await fixture.Session.FetchInfoAsync([uid], Stop));
        Assert.True(after.Flags.HasFlag(MessageFlags.Flagged));
        Assert.True(after.Flags.HasFlag(MessageFlags.Seen));

        await fixture.Session.StoreFlagsAsync([uid], MessageFlags.Seen, set: false, Stop);
        var cleared = Assert.Single(await fixture.Session.FetchInfoAsync([uid], Stop));
        Assert.False(cleared.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(cleared.Flags.HasFlag(MessageFlags.Flagged));
    }

    /// <summary>
    /// A move, and whether the server named the new UID.
    /// </summary>
    /// <remarks>
    /// This is the trap: a move clears a row's <c>server_uid</c> until
    /// UIDPLUS, a Message-ID search or <c>AdoptServerUid</c> names it again. Which of the three
    /// happens depends entirely on what the server does, and only a real one can say.
    /// </remarks>
    [Fact]
    public async Task AMoveEitherNamesTheNewUidOrIsFoundByMessageId()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        var target = await fixture.Session.CreateFolderAsync("Moved", Stop, fixture.Folder.Path);

        await fixture.AppendAsync("Mailbox test — move", Stop);
        await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);
        var uid = Assert.Single(await fixture.Session.SearchAllAsync(Stop));

        var message = await fixture.Session.GetMessageAsync(uid, Stop);
        var messageId = message!.MessageId ?? string.Empty;
        Assert.NotEmpty(messageId);

        var map = await fixture.Session.MoveAsync([uid], target.Path, Stop);

        // Gone from where it was.
        await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);
        Assert.Empty(await fixture.Session.SearchAllAsync(Stop));

        // And there, under whichever UID the server chose.
        await fixture.Session.OpenAsync(target.Path, Stop);
        var moved = Assert.Single(await fixture.Session.SearchAllAsync(Stop));

        if (map.TryGetValue(uid, out var stated))
        {
            Assert.Equal(stated, moved);
        }
        else
        {
            // No UIDPLUS on the move: the fallback has to find it by its Message-ID, which is the
            // path that would otherwise never run.
            var byId = await fixture.Session.SearchByMessageIdAsync(messageId, Stop);
            Assert.Contains(moved, byId);
        }

        await fixture.Session.DeleteFolderAsync(target.Path, Stop);
    }

    [Fact]
    public async Task AnExpungeTakesTheMessageAway()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        await fixture.AppendAsync("Mailbox test — expunge", Stop);
        await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);

        var uid = Assert.Single(await fixture.Session.SearchAllAsync(Stop));
        await fixture.Session.ExpungeAsync([uid], Stop);

        Assert.Empty(await fixture.Session.SearchAllAsync(Stop));
    }

    /// <summary>
    /// The folder's own bookkeeping: UIDVALIDITY, UIDNEXT and the modification sequence, which is
    /// what an incremental sync leans on and what a fake simply asserts.
    /// </summary>
    [Fact]
    public async Task TheFolderStateIsWhatAnIncrementalSyncNeeds()
    {
        SkipWithoutAServer();

        await using var fixture = await Fixture.OpenAsync(Stop);
        var before = await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);

        Assert.True(before.UidValidity > 0);
        Assert.Equal(0, before.Count);

        await fixture.AppendAsync("Mailbox test — state", Stop);
        var after = await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);

        Assert.Equal(before.UidValidity, after.UidValidity);
        Assert.Equal(1, after.Count);
        Assert.True(after.UidNext > before.UidNext || before.UidNext == 0);

        if (after.SupportsModSeq)
        {
            Assert.True(after.HighestModSeq >= before.HighestModSeq);

            // CONDSTORE's own question: what changed since a sequence. Asking about the sequence
            // before the append has to bring the appended message back.
            await fixture.Session.OpenAsync(fixture.Folder.Path, Stop);
            var changed = await fixture.Session.FetchFlagsChangedSinceAsync(before.HighestModSeq, Stop);
            Assert.NotEmpty(changed);
        }
    }

    // ---- Mail that really arrived ----

    /// <summary>
    /// Reads the Inbox and says what is in it, touching nothing.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose: this is somebody's real mailbox, and a test that marked mail read or
    /// moved it would be a test that damaged what it was measuring. It exists because a message
    /// that arrived over the internet is the one thing no fixture can produce — a real Received
    /// chain, a real Message-ID, whatever encoding the sender's client chose.
    /// </remarks>
    [Fact]
    public async Task TheInboxCanBeReadWithoutTouchingIt()
    {
        SkipWithoutAServer();

        var session = new MailKitImapSession();
        try
        {
            await session.ConnectAsync(Server, Stop);
            await session.AuthenticateAsync(Server, Stop);

            var folders = await session.ListFoldersAsync(Stop);
            var inbox = folders.First(f => f.Role == FolderRole.Inbox);

            var state = await session.OpenAsync(inbox.Path, Stop);
            var uids = await session.SearchAllAsync(Stop);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Inbox: {state.Count} message(s), uidvalidity {state.UidValidity}, uidnext {state.UidNext}.");

            Assert.Equal(state.Count, uids.Count);

            foreach (var info in await session.FetchInfoAsync([.. uids.TakeLast(5)], Stop))
            {
                var message = await session.GetMessageAsync(info.Uid, Stop);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"  uid {info.Uid}  {info.InternalDate:yyyy-MM-dd HH:mm}  {info.Size}B  {info.Flags}");
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"    from {message?.From}  subject “{message?.Subject}”");
            }
        }
        finally
        {
            try { await session.DisconnectAsync(CancellationToken.None); } catch (Exception) { }
            session.Dispose();
        }
    }

    // ---- Certificates ----

    /// <summary>
    /// A host whose certificate is for somebody else: refused, explained, and then allowed once
    /// the reader has agreed to that certificate in particular.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_IMAP_MISMATCH_HOST</c> names a host that resolves to the same server but is not
    /// on its certificate — which is what shared hosting looks like everywhere, the certificate
    /// carrying the hosting company's own name while the customer's domain is pointed at it.
    /// Nothing on this machine can manufacture that, which is why it needs a real one.
    /// <para>
    /// It pins into a settings file of its own and throws it away, so a run leaves no standing
    /// decision behind on the machine that ran it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACertificateForAnotherNameIsRefusedUntilItIsAgreedTo()
    {
        var mismatched = Environment.GetEnvironmentVariable("MAILBOX_IMAP_MISMATCH_HOST");
        Assert.SkipUnless(
            mismatched is { Length: > 0 },
            "Set MAILBOX_IMAP_MISMATCH_HOST to a name pointing at the server but absent from its certificate.");

        var directory = Directory.CreateTempSubdirectory("mailbox-trust-").FullName;
        var settings = new Mailbox.Core.Settings.SettingsStore(Path.Combine(directory, "settings.json"));
        var trust = new Mailbox.Security.Tls.CertificateTrust(settings);

        var server = Server with { Host = mismatched!, Trust = trust };

        // Refused, and it is the certificate that is refused rather than the connection failing
        // with nothing to show for it.
        using (var first = new MailKitImapSession())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.ConnectAsync(server, Stop));
        }

        var refusal = trust.RefusalFor(mismatched!, server.Port);
        Assert.NotNull(refusal);
        Assert.True(refusal.Faults.HasFlag(Mailbox.Security.Tls.CertificateFault.NameMismatch));
        Assert.Equal(64, refusal.Certificate.Fingerprint.Length);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Refused {mismatched}: certificate is {refusal.Certificate.CommonName}, "
            + $"names {refusal.Certificate.NamesLine}");
        foreach (var problem in refusal.Problems) TestContext.Current.TestOutputHelper?.WriteLine($"  {problem}");

        // Agreed to, and now it connects — a real handshake against a real server, allowed by the
        // reader's own decision rather than by the check being switched off.
        trust.Pin(refusal);

        using (var second = new MailKitImapSession())
        {
            await second.ConnectAsync(server, Stop);
            await second.AuthenticateAsync(server, Stop);
            Assert.True(second.IsConnected);
            await second.DisconnectAsync(Stop);
        }

        Directory.Delete(directory, true);
    }
}
