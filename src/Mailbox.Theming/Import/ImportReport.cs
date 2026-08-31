namespace Mailbox.Theming.Import;

/// <summary>
/// One import, told plainly: what was read, what it landed on, what was repaired, what a
/// browser says that a mail client cannot use. The same sentences go to the console, the log's
/// read-back and, later, the summary dialog — one wording, so the three never disagree.
/// </summary>
public static class ImportReport
{
    public static IReadOnlyList<string> Lines(ImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var result = outcome.Result;
        var lines = new List<string>
        {
            $"\"{result.File.Name}\" {(outcome.Updated ? "updated" : "imported")} as \"{result.File.Id}\", "
            + $"based on {result.BaseId} ({result.DarkSignal}).",
        };

        // An images-only theme is the common case and a good result: a few tokens plus a
        // backdrop inheriting the rest from the base — said in those words, not as a ratio.
        lines.Add(result.TokensWritten.Count == 0
            ? "It brings no colours of its own; the base stands whole."
            : $"It sets {result.TokensWritten.Count} token(s) — the caption strip and left chrome — and the base carries every content surface.");

        if (result.Repaired.Count > 0)
        {
            lines.Add($"Repaired for contrast: {string.Join("; ", result.Repaired.Select(r => $"{r.Token} {r.From} → {r.To} ({r.Before:0.00}:1 → {r.After:0.00}:1)"))}.");
        }

        if (result.Residual.Count > 0)
        {
            lines.Add($"Still hard to read, as the author stated it: {string.Join("; ", result.Residual.Select(f => f.ToString()))}.");
        }

        if (result.Unmapped.Count > 0)
        {
            lines.Add($"Seen but unmapped (a browser's keys a mail client does not use): {string.Join(", ", result.Unmapped)}.");
        }

        if (result.Skipped.Count > 0)
        {
            lines.Add($"Skipped: {string.Join("; ", result.Skipped)}.");
        }

        foreach (var note in outcome.Notes) lines.Add(char.ToUpperInvariant(note[0]) + note[1..] + ".");

        lines.Add($"Written to {outcome.Path}.");
        return lines;
    }

    /// <summary>The full mapping table, one line per token — what makes the mapping reviewable without reading the mapper.</summary>
    public static IReadOnlyList<string> Dump(ImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return [.. outcome.Result.File.Tokens.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k} = {outcome.Result.File.Tokens[k]}")];
    }
}
