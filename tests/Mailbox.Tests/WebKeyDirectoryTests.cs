using System.Net;
using Mailbox.Security.OpenPgp;

namespace Mailbox.Tests;

/// <summary>
/// Looking a correspondent up at their own domain, and the two things that must not happen while
/// doing it: taking a key for somebody else, and reading for ever.
/// </summary>
public class WebKeyDirectoryTests
{
    [Fact]
    public void TheLocalPartIsHashedTheWayTheStandardHashesIt()
    {
        // The worked example in the Web Key Directory draft: "Joe.Doe" hashes to this, lower-cased
        // first and in Zooko's alphabet rather than RFC 4648's. Getting the alphabet wrong asks for
        // a URL that is not there, and nothing about the failure says why — which is why the one
        // published vector is worth a test of its own.
        Assert.Equal("iy9q119eutrkn8s1mk4r39qejnbu3n5q", WebKeyDirectory.Hash("Joe.Doe"));
    }

    [Fact]
    public void TheHashDoesNotCareAboutCase()
        => Assert.Equal(WebKeyDirectory.Hash("A.Person"), WebKeyDirectory.Hash("a.person"));

    [Fact]
    public async Task TheAdvancedMethodIsAskedFirstAndTheDirectOneAfterIt()
    {
        var asked = new List<string>();
        using var directory = new WebKeyDirectory(new FakeDirectory(asked, _ => null));

        var found = await directory.FindAsync("a.person@example.com", TestContext.Current.CancellationToken);

        Assert.False(found.Found);
        Assert.Equal(
            [
                "https://openpgpkey.example.com/.well-known/openpgpkey/example.com/hu/"
                + WebKeyDirectory.Hash("a.person") + "?l=a.person",
                "https://example.com/.well-known/openpgpkey/hu/"
                + WebKeyDirectory.Hash("a.person") + "?l=a.person",
            ],
            asked);
    }

    [Fact]
    public async Task AKeyPublishedForTheAddressComesBack()
    {
        var key = PgpKeys.Sender.Public.GetEncoded();
        using var directory = new WebKeyDirectory(new FakeDirectory([], _ => key));

        var found = await directory.FindAsync(PgpKeys.Sender.Address, TestContext.Current.CancellationToken);

        Assert.True(found.Found, found.Detail);
        Assert.Equal(PgpKeys.Sender.Public.GetPublicKey().KeyId, found.Key!.GetPublicKey().KeyId);
    }

    [Fact]
    public async Task AKeyForSomebodyElseIsDiscarded()
    {
        // A domain answering every lookup with one key of its own would read everything its users
        // are sent, which is the attack the directory exists to make harder — so what comes back
        // has to name who was asked about.
        var key = PgpKeys.Other.Public.GetEncoded();
        using var directory = new WebKeyDirectory(new FakeDirectory([], _ => key));

        var found = await directory.FindAsync(PgpKeys.Sender.Address, TestContext.Current.CancellationToken);

        Assert.False(found.Found);
        Assert.Null(found.Key);
        Assert.Contains("does not belong to", found.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryThatStreamsForEverIsCutOff()
    {
        using var directory = new WebKeyDirectory(
            new FakeDirectory([], _ => new byte[WebKeyDirectory.MostKeyBytes + 1]));

        var found = await directory.FindAsync(PgpKeys.Sender.Address, TestContext.Current.CancellationToken);

        Assert.False(found.Found);
    }

    [Fact]
    public async Task SomethingThatIsNotAnAddressIsNotLookedUp()
    {
        var asked = new List<string>();
        using var directory = new WebKeyDirectory(new FakeDirectory(asked, _ => null));

        Assert.False((await directory.FindAsync("not-an-address", TestContext.Current.CancellationToken)).Found);
        Assert.Empty(asked);
    }

    /// <summary>A key directory that answers whatever the test says, and records what was asked.</summary>
    private sealed class FakeDirectory(List<string> asked, Func<string, byte[]?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            asked.Add(url);

            var bytes = answer(url);
            return Task.FromResult(bytes is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
