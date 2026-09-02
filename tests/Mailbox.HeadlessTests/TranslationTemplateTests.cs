using System.Text.RegularExpressions;
using Mailbox.App;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The translation template is what the source says it is, and every adopted string is one a
/// translator can actually answer.
/// </summary>
/// <remarks>
/// <b>The ratchet.</b> Adopting a surface for translation is easy; keeping it adopted is the part
/// that fails, and it fails silently — a string reworded without the template being regenerated
/// becomes a translation that quietly stops applying, and looks from the outside exactly like a
/// string nobody has translated yet. Nobody reading English will ever notice. So the template is
/// checked against the source rather than trusted, the same way the schema fixtures and the
/// shortcuts page are.
/// </remarks>
public class TranslationTemplateTests
{
    private static string Root()
    {
        var root = StringsExport.RepoRoot();
        Assert.SkipWhen(root is null, "Not running from a checkout, so there is no source to read.");
        return root!;
    }

    /// <summary>
    /// Regenerating the template changes nothing — so what a translator was handed is what the
    /// interface actually asks for.
    /// </summary>
    [Fact]
    public void TheTemplateMatchesWhatTheSourceAsksFor()
    {
        var root = Root();
        var checkedIn = Path.Combine(root, "assets", "locales", "mailbox.pot");
        Assert.True(File.Exists(checkedIn), $"{checkedIn} is missing; run `mailbox --export-strings`.");

        var fresh = Path.Combine(Path.GetTempPath(), $"mailbox-pot-{Guid.NewGuid():N}.pot");
        try
        {
            Assert.Equal(0, StringsExport.Write(fresh));

            Assert.True(
                File.ReadAllText(fresh) == File.ReadAllText(checkedIn),
                "assets/locales/mailbox.pot is out of date with the strings in the source. "
                + "Run `dotnet run --project src/Mailbox.App -- --export-strings`.");
        }
        finally
        {
            if (File.Exists(fresh)) File.Delete(fresh);
        }
    }

    /// <summary>
    /// A plural's two English forms take the same placeholders.
    /// </summary>
    /// <remarks>
    /// A singular written with no <c>{0}</c> and a plural written with one is the shape that
    /// throws at run time in exactly one of the two cases — the one a developer reading English
    /// sees least, because the singular is what a test fixture usually produces. The localizer
    /// falls back rather than throwing, but a fallback is not the intent and this is where the
    /// intent is checked.
    /// </remarks>
    [Fact]
    public void APluralsTwoFormsAgreeOnTheirPlaceholders()
    {
        var wrong = new List<string>();

        foreach (var (singular, plural) in Plurals(Root()))
        {
            if (!Placeholders(singular).SetEquals(Placeholders(plural)))
            {
                wrong.Add($"“{singular}” and “{plural}” do not take the same placeholders.");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// Adoption never goes backwards.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact number, so adopting more never breaks the build and removing
    /// a surface's translation always does. Raise it as surfaces are adopted; it is the only part
    /// of this that anybody has to remember, and forgetting it costs nothing but a smaller floor.
    /// </remarks>
    [Fact]
    public void AdoptedStringsAreNeverGivenBack()
    {
        var template = File.ReadAllText(Path.Combine(Root(), "assets", "locales", "mailbox.pot"));

        // The header's own empty msgid is not a string anybody translates.
        var adopted = Regex.Matches(template, @"^msgid ""(?!"")", RegexOptions.Multiline).Count;

        Assert.True(
            adopted >= 4,
            $"the interface asks for {adopted} translatable string(s); it used to ask for more. "
            + "A surface has lost its translation.");
    }

    /// <summary>The English pairs out of the template: msgid with its msgid_plural.</summary>
    private static IEnumerable<(string Singular, string Plural)> Plurals(string root)
    {
        var lines = File.ReadAllLines(Path.Combine(root, "assets", "locales", "mailbox.pot"));

        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("msgid \"", StringComparison.Ordinal)) continue;
            if (!lines[i + 1].StartsWith("msgid_plural \"", StringComparison.Ordinal)) continue;

            yield return (
                lines[i]["msgid ".Length..].Trim('"'),
                lines[i + 1]["msgid_plural ".Length..].Trim('"'));
        }
    }

    private static HashSet<string> Placeholders(string text)
        => [.. Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => m.Value)];
}
