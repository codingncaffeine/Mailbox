using System.Net.Http;
using Avalonia.Controls;
using Mailbox.App.Views;
using Mailbox.Core;
using Mailbox.Core.Compose;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Platform;

namespace Mailbox.App;

/// <summary>
/// Acts on a message's <see cref="UnsubscribeOffer"/>: the RFC 8058 one-click POST when the
/// list supports it, a pre-addressed message for a mailto entry, and the list's own page in
/// the browser when a link is all there is.
/// </summary>
/// <remarks>
/// In that order, because it is the order of how little the reader has to do — and how little
/// gets leaked doing it. The POST is asked about first and goes through a client on the same
/// terms as the image fetcher's: no cookies, a short timeout, and a User-Agent that says what
/// it is. The mailto lane opens the message rather than sending it — pressing Send is the
/// reader's own confirmation, and a dialog in front of a window that already waits for one
/// would ask twice. Hunting the footer for an unsubscribe link is how tracking links get
/// clicked; this is the standard way out, surfaced.
/// </remarks>
public static class Unsubscriber
{
    private static readonly HttpClient Client = Build();

    private static HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/0.1");
        return client;
    }

    /// <summary>
    /// Acts on the offer and says what happened — a sentence for whichever channel the caller
    /// draws, and null for a one-click the reader thought better of.
    /// </summary>
    public static async Task<string?> ActAsync(Window owner, UnsubscribeOffer offer, string sender)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.OneClick is { } target)
        {
            var agreed = await Confirm.AskAsync(owner, "Unsubscribe",
                $"Ask {(sender.Length > 0 ? sender : "this mailing list")} to stop sending this mail?\n"
                + "The list is told directly; nothing opens.", "Unsubscribe", destructive: false);
            if (!agreed) return null;

            try
            {
                using var response = await Client.PostAsync(target,
                    new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]));

                if (response.IsSuccessStatusCode)
                {
                    Log.Info($"Unsubscribe: one-click accepted by {target.Host}.");
                    return "Unsubscribed — the list was told directly.";
                }

                Log.Warn($"Unsubscribe: {target.Host} answered {(int)response.StatusCode}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Unsubscribe: the one-click request failed: {ex.Message}");
            }

            // The POST lane failed; whatever else the message offered still stands, and
            // falling through beats a dead end the reader has to diagnose.
        }

        if (offer.Mailto.FirstOrDefault() is { } mailto
            && MailtoLink.Parse(mailto.AbsoluteUri) is { } link)
        {
            var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
            compose.ComposeFromMailto(link);
            compose.Show(owner);
            return "An unsubscribe message is ready — press Send to go through with it.";
        }

        if (offer.Web.FirstOrDefault() is { } page)
        {
            DesktopOpen.Open(page.AbsoluteUri);
            return "The list's unsubscribe page was opened in the browser.";
        }

        return "The message offers no unsubscribe route this application can take.";
    }
}
