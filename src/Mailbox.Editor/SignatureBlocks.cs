using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;

namespace Mailbox.Editor;

/// <summary>
/// Keeps hold of the blocks an automatic signature put into a compose document, so that changing
/// the sending account can replace that signature — and only it — in the place it holds, around
/// whatever has been written. The document model has no marker to hang on a block, but blocks
/// keep their identity while the document is edited, so remembering the instances is the
/// bookmark.
/// </summary>
public static class SignatureBlocks
{
    /// <summary>The blocks a piece of signature markup would put into a document.</summary>
    public static List<Block> Parse(string html)
        => [.. HtmlDocumentFormatter.ParseHtml(html, allowLocalFileImages: false, allowRemoteImages: false).Blocks];

    /// <summary>
    /// Replaces the tracked signature blocks in <paramref name="document"/> with
    /// <paramref name="replacement"/>, where the old ones stand. With none of the tracked blocks
    /// left in the document — the writer deleted the signature — nothing is touched and the
    /// second half of the answer says so, because a deleted signature was a choice and putting
    /// another in would overrule it. With nothing tracked to begin with, the replacement goes in
    /// above <paramref name="insertBefore"/> when that block is still present, and at the end of
    /// the document when it is not — the end is where a new message keeps its signature, and
    /// above the quote is where a reply does.
    /// </summary>
    /// <returns>The blocks now standing as the signature, and whether the writer had removed it.</returns>
    public static (List<Block> Tracked, bool WriterRemovedIt) Swap(
        FlowDocument document, IReadOnlyList<Block> tracked, Block? insertBefore, IReadOnlyList<Block> replacement)
    {
        // By identity throughout, never equality: the writer may well have typed a paragraph
        // reading the same as one of the signature's, and it is not ours to remove.
        int IndexOf(Block? sought)
        {
            for (var i = 0; i < document.Blocks.Count; i++)
            {
                if (ReferenceEquals(document.Blocks[i], sought)) return i;
            }

            return -1;
        }

        var kept = tracked.Where(t => IndexOf(t) >= 0).ToList();
        if (tracked.Count > 0 && kept.Count == 0) return ([], true);

        int at;
        if (kept.Count > 0)
        {
            at = IndexOf(kept[0]);
            foreach (var block in kept) document.Blocks.RemoveAt(IndexOf(block));
        }
        else
        {
            var anchor = IndexOf(insertBefore);
            at = anchor >= 0 ? anchor : document.Blocks.Count;
        }

        for (var i = 0; i < replacement.Count; i++) document.Blocks.Insert(at + i, replacement[i]);
        return ([.. replacement], false);
    }
}
