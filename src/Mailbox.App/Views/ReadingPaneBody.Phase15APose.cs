using Mailbox.Rendering;

namespace Mailbox.App.Views;

/// <summary>
/// The document the pane handed to the rendering engine, for a run that needs the markup itself.
/// </summary>
/// <remarks>
/// The sanitizer's report dump says what it <em>came to</em> — how much was refused, which hosts,
/// and whether a short list of dangerous strings survived. That is the right read-back for a
/// message a reader might receive, and it is not enough for an adversarial corpus: the question
/// there is what the engine is handed, byte for byte, because the whole class of fault being
/// hunted is markup that means one thing to the sanitizer's tokenizer and another to the engine's
/// parser. A verdict computed from a list of strings cannot find what nobody thought to list.
/// </remarks>
public sealed partial class ReadingPaneBody
{
    /// <summary>The document as it went to the engine, or null when nothing has been rendered.</summary>
    internal string? RenderedDocument => _rendered?.Html;

    /// <summary>What this render refused, in the order the sanitizer met it.</summary>
    internal IReadOnlyList<BlockedResource> RefusedNow => _rendered?.Blocked ?? [];

    /// <summary>Whether the pane has already been allowed to fetch this message's pictures.</summary>
    internal bool PicturesAllowedNow => _policy != RemoteImagePolicy.Block;

    /// <summary>How many of them it is holding, fetched by the application rather than the engine.</summary>
    internal int InlinedNow => _inlined.Count;

    /// <summary>
    /// Hands Chromium a document that never went through the sanitizer. Capture runs only.
    /// </summary>
    /// <remarks>
    /// The proof that the engine's own wall holds without the sanitizer in front of it: every
    /// message reaches Chromium sanitized, so nothing an ordinary run shows can say whether
    /// Chromium itself would have fetched a remote picture, stylesheet or frame. A run hands it
    /// raw markup that asks for all of them, and a server that logs nothing is the answer.
    /// </remarks>
    internal void LoadUnsanitizedForHarness(string html)
    {
        if (!Mailbox.App.Theming.WindowCapture.IsRequested || _web is not { } web) return;

        _surface.Content = web;
        web.Navigate(html);
        Mailbox.Core.Diagnostics.Log.Info($"Harness: unsanitized document handed to the engine ({html.Length} characters).");
    }
}
