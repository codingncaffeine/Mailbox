using Mailbox.Core.Feeds;

namespace Mailbox.Tests;

/// <summary>
/// Reading the article out of a publisher's page, for the many feeds that publish a teaser.
/// </summary>
/// <remarks>
/// The cases here are the shapes real pages come in, not tidy ones: an article buried in nested
/// layout divs, navigation that is longer than the article, comments under it, and a page with
/// no article at all — where getting the wrong answer means replacing a publisher's own summary
/// with a column of menu items.
/// </remarks>
public class ArticleTextTests
{
    /// <summary>A page shaped like the ones this actually runs on.</summary>
    private static string Page(string article, string extra = "") =>
        $$"""
        <!doctype html><html><head><title>A page</title>
          <meta property="og:image" content="/img/hero.jpg">
        </head><body>
          <header><a href="/">Home</a><a href="/news">News</a><a href="/about">About</a></header>
          <nav><ul><li><a href="/a">Section A</a></li><li><a href="/b">Section B</a></li></ul></nav>
          <div class="wrap"><div class="col"><div class="inner">
            {{article}}
          </div></div></div>
          {{extra}}
          <aside><p>Related: one thing, another thing, a third thing you might also like a lot.</p></aside>
          <footer><p>Copyright somebody. All rights reserved. Terms and conditions apply here.</p></footer>
          <script>var tracking = 1; if (a < b) { document.write("<p>not an article</p>"); }</script>
        </body></html>
        """;

    private const string Body = """
        <p>The first paragraph of the article, which is long enough to be worth reading and says
        something a reader would actually want to know about the subject at hand.</p>
        <p>The second paragraph continues the thought, adding detail and a <a href="/x">link</a>
        that does not make the paragraph into navigation because it is one word among many.</p>
        <p>A third paragraph, so the run is unmistakably the densest thing on the page and there
        can be no argument about which part of it is the article somebody came to read.</p>
        """;

    [Fact]
    public void TheArticleComesOutAndTheFurnitureDoesNot()
    {
        var body = ArticleText.Extract(Page(Body), "https://example.com/story");

        Assert.True(body.Found);
        Assert.Contains("first paragraph of the article", body.Text, StringComparison.Ordinal);
        Assert.Contains("third paragraph", body.Text, StringComparison.Ordinal);

        // Everything around it: the menu, the sidebar, the footer, and the script that writes
        // markup — a page's script is code, and a scan that treated it as text would find an
        // article in it.
        Assert.DoesNotContain("Section A", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Related:", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("All rights reserved", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not an article", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APageWithNoArticleInItGivesNothing()
    {
        // The important half. A page of links that came back as "an article" would replace the
        // publisher's own summary with a column of menu items, which is worse than the teaser.
        var body = ArticleText.Extract(Page("<p>Sorry.</p>"), "https://example.com/404");

        Assert.False(body.Found);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public void AParagraphThatIsMostlyLinksIsNavigation()
    {
        var links = string.Concat(Enumerable.Range(0, 12)
            .Select(n => $"<a href=\"/section-{n}\">A section of the site number {n}</a> "));

        var body = ArticleText.Extract(Page($"<div><p>{links}</p></div>"), "https://example.com/x");

        Assert.False(body.Found);
    }

    [Fact]
    public void CommentsUnderTheArticleDoNotJoinIt()
    {
        // A long run of short comments can outweigh a short article by count. It is measured by
        // how much text is in the run, not by how many paragraphs, so the article still wins.
        var comments = string.Concat(Enumerable.Range(0, 30)
            .Select(n => $"<p>Comment {n}. Short.</p>"));

        var body = ArticleText.Extract(Page(Body, $"<section>{comments}</section>"), "https://example.com/story");

        Assert.True(body.Found);
        Assert.Contains("first paragraph", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Comment 17", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadingsAndListsInsideTheArticleAreKept()
    {
        var withStructure = Body + """
            <h2>A heading in the middle of it</h2>
            <ul><li>The first of three points made in a list</li><li>The second point</li></ul>
            """;

        var body = ArticleText.Extract(Page(withStructure), "https://example.com/story");

        Assert.Contains("<h2>", body.Html, StringComparison.Ordinal);
        Assert.Contains("<li>", body.Html, StringComparison.Ordinal);
        Assert.Contains("A heading in the middle", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressesInTheArticleComeOutAbsolute()
    {
        // The article is going into a message and read there; a relative href resolves against
        // about:blank in the reading pane, which is to say nowhere.
        var body = ArticleText.Extract(Page(Body), "https://example.com/section/story");

        Assert.Contains("https://example.com/x", body.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/x\"", body.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ALazilyLoadedPictureKeepsItsRealAddress()
    {
        // What the whole web does now: a placeholder in src and the real one in data-src. Reading
        // src alone gets a one-pixel spacer on every picture in the article.
        var withPicture = Body.Replace(
            "<p>A third paragraph",
            "<p><img src=\"data:image/gif;base64,R0lGOD\" data-src=\"/img/real.jpg\" alt=\"A picture\"></p><p>A third paragraph",
            StringComparison.Ordinal);

        var body = ArticleText.Extract(Page(withPicture), "https://example.com/story");

        Assert.Contains("https://example.com/img/real.jpg", body.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", body.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptAndStyleAreNeverRead()
    {
        var hostile = """
            <style>p { content: "The first paragraph of an article that is not one, at length. " }</style>
            <script>
              var s = "<p>A paragraph inside a script, long enough to look like an article body.</p>";
              if (x < 1 && y > 2) { s += s; }
            </script>
            """;

        var body = ArticleText.Extract(Page(Body, hostile), "https://example.com/story");

        Assert.DoesNotContain("inside a script", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not one, at length", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclosedNavDoesNotSwallowTheArticleUnderIt()
    {
        // Malformed markup is the normal case. Skipping to a close tag that never comes would
        // blank the rest of the page, and the article with it.
        var body = ArticleText.Extract(
            $"<html><body><nav><a href=\"/\">Home</a><div class=\"main\">{Body}</div></body></html>",
            "https://example.com/story");

        Assert.True(body.Found);
        Assert.Contains("first paragraph", body.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not html at all")]
    [InlineData("<html><body></body></html>")]
    [InlineData("<p>")]
    [InlineData("<<<>>><p><p><p>")]
    public void NothingUsableGivesNothingRatherThanThrowing(string html)
        => Assert.False(ArticleText.Extract(html, "https://example.com/x").Found);
}
