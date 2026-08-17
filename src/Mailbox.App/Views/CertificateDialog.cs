using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Security.Tls;

namespace Mailbox.App.Views;

/// <summary>
/// The certificate a server offered, what is wrong with it, and whether to go on.
/// </summary>
/// <remarks>
/// This exists because refusing was all Mailbox could do. A server whose certificate does not
/// match its hostname — which is what shared hosting looks like nearly everywhere, the
/// certificate carrying the hosting company's name while the customer's domain points at it —
/// produced a connection failure with nothing the reader could act on. Every other mail client
/// shows the certificate and lets the reader decide, and so does this one now.
/// <para>
/// <b>It shows the fingerprint, and it says what agreeing means.</b> A reader being asked to
/// vouch for a stranger's key is owed the thing that identifies it, in the form every other tool
/// prints, so it can be compared against what the server's owner says it should be. And the
/// button says "Trust This Certificate" rather than "Continue", because that is what pressing it
/// does: the decision covers the key that is on screen and nothing else, and the day it changes
/// the question is asked again.
/// </para>
/// </remarks>
public static class CertificateDialog
{
    /// <summary>Shows the certificate and asks. True when the reader agreed to it.</summary>
    public static async Task<bool> AskAsync(Window owner, CertificateRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var window = new Window
        {
            Title = "Server certificate",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var agreed = false;

        var trustIt = new Button { Content = "Trust This Certificate" };
        trustIt.Click += (_, _) => { agreed = true; window.Close(); };

        var refuse = new Button { Content = "Don't Connect", IsDefault = true, IsCancel = true };
        refuse.Click += (_, _) => window.Close();

        DialogChrome.Apply(window, Body(refusal, trustIt, refuse));

        await window.ShowDialog(owner);

        Log.Info(agreed
            ? $"The reader agreed to {refusal.Host}:{refusal.Port}'s certificate."
            : $"The reader declined {refusal.Host}:{refusal.Port}'s certificate.");

        return agreed;
    }

    private static Control Body(CertificateRefusal refusal, Button trustIt, Button refuse)
    {
        var heading = new TextBlock
        {
            Text = $"Mailbox cannot verify {refusal.Host}",
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var problems = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var problem in refusal.Problems)
        {
            var line = new TextBlock { Text = "• " + problem, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            Bind(line, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            problems.Children.Add(line);
        }

        // Which question is being asked. A name that does not match on a chain that is otherwise
        // sound is a much smaller thing to agree to than a key nobody has vouched for, and a
        // reader deciding should be told which of the two this is.
        var advice = new TextBlock
        {
            Text = refusal.NameOnly
                ? "The certificate itself is valid and properly signed — it is simply issued for "
                  + "another name. Shared hosting usually looks like this: the certificate carries "
                  + "the hosting company's own address. If that name is your mail provider's, this "
                  + "is expected."
                : "Take care. Something more than the name is wrong here, and a certificate nobody "
                  + "trustworthy has vouched for could belong to anyone — including somebody "
                  + "reading your mail as it passes.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Bind(advice, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var details = new StackPanel { Spacing = 3, Margin = new Thickness(0, 14, 0, 0) };
        details.Children.Add(Detail("Issued to", refusal.Certificate.CommonName));
        details.Children.Add(Detail("Also valid for", refusal.Certificate.NamesLine));
        details.Children.Add(Detail("Issued by", Short(refusal.Certificate.Issuer)));
        details.Children.Add(Detail(
            "Valid",
            $"{refusal.Certificate.NotBefore:d MMMM yyyy} to {refusal.Certificate.NotAfter:d MMMM yyyy}"));

        // The fingerprint is the thing a reader can actually check against what their provider
        // publishes, so it is shown whole and in the shape every other tool prints it.
        var fingerprint = new SelectableTextBlock
        {
            Text = refusal.Certificate.PrettyFingerprint,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
        };
        Bind(fingerprint, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Bind(fingerprint, TextBlock.FontFamilyProperty, "mono.fontfamily");
        details.Children.Add(Row("SHA-256", fingerprint));

        var promise = new TextBlock
        {
            Text = "Trusting this applies to this certificate only. If the server ever presents a "
                   + "different one, Mailbox will ask again.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Bind(promise, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { trustIt, refuse },
        };

        return new StackPanel
        {
            Margin = new Thickness(22),
            Children = { heading, problems, advice, details, promise, buttons },
        };
    }

    private static Control Detail(string label, string value)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return Row(label, text);
    }

    private static Control Row(string label, Control value)
    {
        var caption = new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Top };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { caption, value },
        };
    }

    /// <summary>
    /// The issuer as a person reads it: its common name, or the whole distinguished name when it
    /// has not got one.
    /// </summary>
    private static string Short(string distinguishedName)
    {
        foreach (var part in distinguishedName.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return part[3..];
        }

        return distinguishedName;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
