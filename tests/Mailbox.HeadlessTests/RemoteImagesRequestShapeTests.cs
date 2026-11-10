using System.Net;
using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The shape of the request a picture is fetched with. The CDNs in front of many senders'
/// pictures refuse an HTTP/1.1 request, or one naming no language, with 403 whatever the
/// sender's trust status, so the shape is a claim the pictures depend on.
/// </summary>
public class RemoteImagesRequestShapeTests
{
    [Fact]
    public void AsksForHttp2WithALanguageAndItsOwnName()
    {
        var client = RemoteImages.ClientForTests;

        Assert.Equal(HttpVersion.Version20, client.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.DefaultVersionPolicy);
        Assert.NotEmpty(client.DefaultRequestHeaders.AcceptLanguage);
        Assert.Contains(client.DefaultRequestHeaders.Accept, a => a.MediaType == "image/*");
        Assert.Contains(client.DefaultRequestHeaders.UserAgent, u => u.Product?.Name == "Mailbox");
        Assert.DoesNotContain(client.DefaultRequestHeaders.UserAgent, u => u.Product?.Name == "Mozilla");
    }
}
