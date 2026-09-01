using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The paint sweeps, promoted from the audit so the classes of fault they caught stay caught.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> <c>ThemeResourceBridge</c> says of itself that it is "the single seam
/// between theme data and the UI. Nothing else in the application is permitted to name a colour
/// — the coverage audit enforces that." Nothing enforced it: the coverage audit
/// (<c>EveryBuiltInDefinesEveryRequiredToken</c>) asks whether a <em>theme</em> is complete, and
/// never looks at the source at all. A hard-coded brush could be introduced anywhere under
/// <c>src/</c> and every test stayed green. These are the missing half.
/// </para>
/// <para>
/// <b>How they work.</b> Text scans over the tree, run from the test binary through
/// <see cref="RepoRoot"/>. Cheap, and they need no display: a colour named in code is a fact
/// about the file, not about a render.
/// </para>
/// </remarks>
public partial class AuditPaintSweepTests
{
    // ---- Sweep A: colour literals ------------------------------------------------------------

    /// <summary>
    /// No <c>#RRGGBB</c> anywhere under <c>src/</c> outside the theming project, except in the
    /// documented places where the value is not paint at all.
    /// </summary>
    /// <remarks>
    /// The exemptions are all one idea: a colour that leaves the application. A message's own
    /// stylesheet, the quote bar that goes out in the MIME, the palette a user picks a document
    /// colour from, the colour a calendar is stored with, and the printed page — none of those
    /// follow the Office theme, and one that did would be wrong. Everything else takes a token.
    /// <para>
    /// Comments are stripped first, because the tree quotes measured values in prose constantly
    /// and a measurement written down is the opposite of a hard-coded colour.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingOutsideTheThemingProjectNamesAColour()
    {
        var offenders = new List<string>();

        foreach (var (path, body) in SourceFiles())
        {
            if (ColourLiteralExemptions.ContainsKey(path)) continue;

            foreach (var (line, text) in Lines(body))
            {
                foreach (Match hit in HexColour().Matches(text))
                {
                    offenders.Add($"{path}:{line}: {hit.Value}  |  {text.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A colour outside the theme system cannot be themed, and nothing can reach it. Take "
            + "it from a token, or — if it is a colour that leaves the application — add the file "
            + "to ColourLiteralExemptions with the reason.\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The same rule for named brushes. Two names are allowed and no others: <c>Transparent</c>,
    /// which is the absence of paint rather than a colour, and <c>Magenta</c>, which is what the
    /// drawn surfaces deliberately fall back to when a theme has not defined a token — the loud
    /// fallback that keeps a gap visible instead of papering over it.
    /// </summary>
    [Fact]
    public void NothingOutsideTheThemingProjectNamesABrush()
    {
        var offenders = new List<string>();

        foreach (var (path, body) in SourceFiles())
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal)) continue;

            foreach (var (line, text) in Lines(body))
            {
                foreach (Match hit in NamedBrush().Matches(text))
                {
                    var name = hit.Groups["name"].Value;
                    if (name is "Transparent" or "Magenta") continue;
                    offenders.Add($"{path}:{line}: {hit.Value}  |  {text.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A named brush is a colour the theme cannot reach. Bind the token, or fall back to "
            + "Magenta so a missing token is visible.\n" + string.Join("\n", offenders));
    }

    // ---- Sweep B: font literals --------------------------------------------------------------

    /// <summary>
    /// No typeface is built from a string in code. Every face comes from
    /// <c>ui.fontfamily</c>, <c>content.fontfamily</c>, <c>mono.fontfamily</c>, the bundled icon
    /// font, or the font resolver — which is what makes a substituted family reach every surface
    /// at once, and what the passphrase dialog's code-named font did not do.
    /// </summary>
    [Fact]
    public void NoTypefaceIsNamedInCode()
    {
        var offenders = new List<string>();

        foreach (var (path, body) in SourceFiles())
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal)) continue;

            foreach (var (line, text) in Lines(body))
            {
                if (FontFamilyFromLiteral().IsMatch(text))
                {
                    offenders.Add($"{path}:{line}: {text.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A font family named here is a family the theme cannot substitute. Bind "
            + "ui.fontfamily / content.fontfamily / mono.fontfamily, or ask the resolver.\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// And nothing in XAML sets a family by name either — the four <c>.axaml</c> files bind the
    /// token or the icon family, and a literal there would be invisible to the resolver.
    /// </summary>
    [Fact]
    public void NoXamlNamesAFontFamily()
    {
        var offenders = new List<string>();

        foreach (var (path, body) in SourceFiles())
        {
            if (!path.EndsWith(".axaml", StringComparison.Ordinal)) continue;

            foreach (var (line, text) in Lines(body))
            {
                if (XamlFontFamilyLiteral().IsMatch(text)) offenders.Add($"{path}:{line}: {text.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    // ---- Sweep C: token discipline -----------------------------------------------------------

    /// <summary>
    /// Above the palette, the four built-ins define exactly the same tokens.
    /// </summary>
    /// <remarks>
    /// The primitive layer is each theme's own — Dark Gray names chrome greys that Colorful has
    /// no use for, and that is the point of the layer. Everything above it is the application's
    /// surface, and a key present in one theme and absent in another is a
    /// <c>{DynamicResource}</c> that resolves in three themes and paints nothing in the fourth.
    /// The coverage gate cannot catch it: it only knows the required list.
    /// </remarks>
    [Fact]
    public void TheBuiltInsDefineTheSameTokensAboveThePalette()
    {
        var sets = OfficeThemes.All.ToDictionary(
            id => id,
            id => OfficeThemes.Build(id).Resolve().Keys
                .Where(k => TokenLayerExtensions.InferLayer(k) != TokenLayer.Primitive)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var union = sets.Values.Aggregate(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            (all, one) => { all.UnionWith(one); return all; });

        foreach (var (id, keys) in sets)
        {
            var missing = union.Except(keys).Order(StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0,
                $"'{id}' lacks {missing.Count} token(s) the other built-ins define: "
                + string.Join(", ", missing));
        }
    }

    /// <summary>
    /// <c>SystemDialog.All</c> is the whole family. The equality test that keeps the system
    /// dialogs light in every theme walks that list, so a key added to the family and not to the
    /// list would be a value free to differ between themes with nothing to notice.
    /// </summary>
    [Fact]
    public void TheSystemDialogListNamesEverySystemDialogToken()
    {
        var listed = TokenKeys.SystemDialog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in OfficeThemes.All)
        {
            var present = OfficeThemes.Build(id).Resolve().Keys
                .Where(k => k.StartsWith("systemdialog.", StringComparison.OrdinalIgnoreCase));

            foreach (var key in present)
            {
                Assert.True(listed.Contains(key),
                    $"'{key}' is a systemdialog token but is not in TokenKeys.SystemDialog.All, "
                    + "so nothing holds it to being the same in all four themes.");
            }
        }
    }

    /// <summary>
    /// One mark, one token — the flag test generalised to every mark that is drawn in more than
    /// one place. Each family below is a single thing the reference draws once; a theme that
    /// gave two of them different values would draw it two ways in the same window.
    /// </summary>
    /// <remarks>
    /// Curated rather than derived, because "resolves to the same colour in Colorful" is not the
    /// same statement as "is the same mark": Colorful is a light theme where a great many
    /// unrelated things are white. Each entry here is a claim about what the surface is.
    /// </remarks>
    public static TheoryData<string, string[]> SharedMarks() => new()
    {
        // The flag: the ribbon's Follow Up icon, the list's flag column, the to-do rows.
        { "the flag's cloth", [TokenKeys.Tags.Flag, TokenKeys.RibbonIcon.Flag] },
        { "the flag's outline", [TokenKeys.Tags.FlagOutline, TokenKeys.RibbonIcon.FlagOutline] },
        { "the flag's pole", [TokenKeys.Tags.FlagPole, TokenKeys.RibbonIcon.FlagPole] },

        // A new note is the yellow category, on the wall and in the note window alike.
        { "a note's default colour", [TokenKeys.Category.Yellow, TokenKeys.Notes.Default] },

        // The selected row: the message list draws the shared selection, not one of its own.
        { "a selected row", [TokenKeys.State.Selected, TokenKeys.List.RowSelected] },

        // A gallery box is a hole in the ribbon, not a panel on it: its ground is the ribbon's.
        { "the ribbon's ground", [TokenKeys.Ribbon.Background, TokenKeys.Ribbon.GalleryBackground] },
    };

    [Theory]
    [MemberData(nameof(SharedMarks))]
    public void AMarkDrawnTwiceIsOneColourInEveryTheme(string mark, string[] keys)
    {
        foreach (var id in OfficeThemes.All)
        {
            var tokens = OfficeThemes.Build(id).Resolve();
            var values = keys.ToDictionary(k => k, tokens.GetString);

            Assert.True(values.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                $"{id}: {mark} is drawn in more than one colour — "
                + string.Join(", ", values.Select(p => $"{p.Key}={p.Value}")));
        }
    }

    /// <summary>
    /// The close button's three values are the same in all four built-ins, like the system
    /// dialog family: it is Windows' red wherever it appears, not the theme's.
    /// </summary>
    [Fact]
    public void TheCloseButtonIsTheSameRedInEveryBuiltIn()
    {
        string[] keys =
        [
            TokenKeys.TitleBar.CaptionClose,
            TokenKeys.TitleBar.CaptionClosePressed,
            TokenKeys.TitleBar.CaptionCloseText,
        ];

        var reference = OfficeThemes.Build(OfficeThemes.Colorful).Resolve();
        foreach (var id in OfficeThemes.All)
        {
            var tokens = OfficeThemes.Build(id).Resolve();
            foreach (var key in keys) Assert.Equal(reference.GetString(key), tokens.GetString(key));
        }
    }

    /// <summary>
    /// A caption button's hover wash has to be visible against the thing it stands on, and what
    /// it stands on is three different colours inside one theme.
    /// </summary>
    /// <remarks>
    /// This is the regression guard for the bug the sweep found: the wash was one literal,
    /// <c>#22FFFFFF</c>, for every caption in the application. Over Colorful's blue title bar
    /// that reads; over White's own #E9EEF2 caption, and over the white dialogs both light
    /// themes draw, it is a change of three parts in 255 — a hover state that does not exist.
    /// <para>
    /// The threshold is deliberately low. A wash is not text and not even a mark: it is a hint
    /// that the pointer is somewhere, and the reference's own is subtle. Six levels per channel
    /// is roughly what an eye picks up on a flat field, and is four times what the broken one
    /// managed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(TokenKeys.TitleBar.CaptionHover, TokenKeys.TitleBar.Background)]
    [InlineData(TokenKeys.TitleBar.CaptionPressed, TokenKeys.TitleBar.Background)]
    [InlineData(TokenKeys.Dialog.CaptionHover, TokenKeys.Dialog.Background)]
    [InlineData(TokenKeys.Dialog.CaptionPressed, TokenKeys.Dialog.Background)]
    [InlineData(TokenKeys.SystemDialog.CaptionHover, TokenKeys.SystemDialog.TitleBar)]
    [InlineData(TokenKeys.SystemDialog.CaptionPressed, TokenKeys.SystemDialog.TitleBar)]
    public void ACaptionWashChangesWhatItIsDrawnOver(string wash, string ground)
    {
        const int Visible = 6;

        foreach (var id in OfficeThemes.All)
        {
            var tokens = OfficeThemes.Build(id).Resolve();
            var over = tokens.GetColor(wash);
            var under = tokens.GetColor(ground);

            // Source-over: the wash's alpha decides how far the ground moves toward it.
            var a = over.A / 255.0;
            var moved = Math.Max(
                Math.Max(Math.Abs(over.R - under.R), Math.Abs(over.G - under.G)),
                Math.Abs(over.B - under.B)) * a;

            Assert.True(moved >= Visible,
                $"{id}: {wash} ({over}) over {ground} ({under}) moves it by {moved:0.0} levels, "
                + "which is not a hover state anyone can see.");
        }
    }

    /// <summary>
    /// Every token a built-in defines is named by something. A token nothing reads is a control
    /// a theme author is given that turns nothing — the reverse of the coverage gate's fault,
    /// and just as quiet.
    /// </summary>
    /// <remarks>
    /// The exemptions are the two kinds of token that are meant to be unread by the application:
    /// the design scales a theme file may reach for, and the chrome geometry the built-ins carry
    /// so a theme file cannot contradict the measurements the controls are built from. Both are
    /// deliberate and documented where they are set.
    /// </remarks>
    [Fact]
    public void EveryTokenIsNamedBySomething()
    {
        var defined = OfficeThemes.All
            .SelectMany(id => OfficeThemes.Build(id).Resolve().Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referenced = TokensNamedInSource();

        // A token reached only through another token's {reference} counts as read.
        foreach (var id in OfficeThemes.All)
        {
            var set = OfficeThemes.Build(id);
            foreach (var key in set.Keys)
            {
                if (!set.TryGetRaw(key, out var raw)) continue;
                foreach (Match m in TokenReference().Matches(raw)) referenced.Add(m.Groups[1].Value);
            }
        }

        var orphans = defined
            .Where(k => !referenced.Contains(k))
            .Where(k => !UnreadByDesign.ContainsKey(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These tokens are defined and nothing reads them, so a theme setting one changes "
            + "nothing: " + string.Join(", ", orphans)
            + ". Either bind them or add them to UnreadByDesign with the reason.");
    }

    /// <summary>
    /// And the other direction: every <c>{DynamicResource}</c> in the tree names a token the
    /// built-ins define. A name with a typo in it binds to nothing and paints nothing, silently.
    /// </summary>
    [Fact]
    public void EveryDynamicResourceNamesATokenThatExists()
    {
        var defined = OfficeThemes.All
            .SelectMany(id => OfficeThemes.Build(id).Resolve().Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();

        foreach (var (path, body) in SourceFiles(includeDeclarations: true))
        {
            foreach (var (line, text) in Lines(body))
            {
                foreach (Match m in DynamicResourceName().Matches(text))
                {
                    var name = Unsuffixed(m.Groups["name"].Value);
                    if (defined.Contains(name) || BridgeAliases.Contains(name)) continue;
                    offenders.Add($"{path}:{line}: {m.Groups["name"].Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These resource names are not tokens any built-in defines, so they bind to nothing:\n"
            + string.Join("\n", offenders));
    }

    // ---- The scan ----------------------------------------------------------------------------

    /// <summary>
    /// Files under <c>src/</c>, comments removed. The theming project is where colour lives, so
    /// it is not scanned; the two files that declare the token names and the built-ins' values
    /// are inside it.
    /// </summary>
    private static IEnumerable<(string Path, string Body)> SourceFiles(bool includeDeclarations = false)
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)
                                 || f.EndsWith(".axaml", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (!includeDeclarations && relative.StartsWith("src/Mailbox.Theming/", StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            yield return (relative, relative.EndsWith(".axaml", StringComparison.Ordinal)
                ? WithoutXmlComments(text)
                : WithoutCodeComments(text));
        }
    }

    private static IEnumerable<(int Number, string Text)> Lines(string body)
    {
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0) yield return (i + 1, lines[i]);
        }
    }

    /// <summary>
    /// Blanks out <c>//</c> and <c>/* */</c> while leaving string literals — including verbatim
    /// and raw ones — intact, because a colour hidden in a string is exactly what is being looked
    /// for and a <c>//</c> inside a URL is not a comment. Line count is preserved so a hit can
    /// still name its line.
    /// </summary>
    private static string WithoutCodeComments(string text)
    {
        var kept = new StringBuilder(text.Length);
        var i = 0;

        void Blank(int from, int to)
        {
            for (var k = from; k < to; k++) kept.Append(text[k] == '\n' ? '\n' : ' ');
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                Blank(i, end);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(i, end);
                i = end;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var end = EndOfLiteral(text, i);
                kept.Append(text, i, end - i);
                i = end;
                continue;
            }

            kept.Append(c);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>The index just past the literal starting at <paramref name="start"/>.</summary>
    private static int EndOfLiteral(string text, int start)
    {
        var quote = text[start];

        // A raw string: three or more quotes, closed by at least as many.
        if (quote == '"')
        {
            var fence = 0;
            while (start + fence < text.Length && text[start + fence] == '"') fence++;
            if (fence >= 3)
            {
                var closing = new string('"', fence);
                var end = text.IndexOf(closing, start + fence, StringComparison.Ordinal);
                return end < 0 ? text.Length : end + fence;
            }
        }

        // A verbatim string: no backslash escapes, and "" is one quote.
        var verbatim = start > 0 && text[start - 1] == '@' && quote == '"';
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == quote)
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            if (!verbatim && c == '\n') return i;   // an unterminated literal: do not run away
            i++;
        }
        return text.Length;
    }

    private static string WithoutXmlComments(string text)
        => XmlComment().Replace(text, m => string.Concat(m.Value.Select(c => c == '\n' ? "\n" : " ")));

    /// <summary>Every token named by a string, a <c>TokenKeys</c> constant or a resource lookup.</summary>
    private static HashSet<string> TokensNamedInSource()
    {
        var byConstant = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var nested in typeof(TokenKeys).GetNestedTypes(BindingFlags.Public))
        {
            foreach (var field in nested.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false }
                    && field.GetRawConstantValue() is string value)
                {
                    byConstant[$"{nested.Name}.{field.Name}"] = value;
                }
            }
        }

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The whole tree, theming project included — ControlThemePalette, ContrastAudit and
        // CategoryTokens read tokens like anything else — but not the two files that *declare*
        // the names and the values. A key listed in TokenKeys.Required is not a reader of it.
        foreach (var (path, body) in SourceFiles(includeDeclarations: true))
        {
            if (path is "src/Mailbox.Theming/Tokens/TokenKeys.cs"
                     or "src/Mailbox.Theming/Themes/OfficeThemes.cs") continue;

            foreach (Match m in TokenKeysConstant().Matches(body))
            {
                if (byConstant.TryGetValue(m.Groups["name"].Value, out var value)) named.Add(value);
            }

            foreach (Match m in TokenLikeLiteral().Matches(body)) named.Add(Unsuffixed(m.Groups[1].Value));
            foreach (Match m in DynamicResourceName().Matches(body)) named.Add(Unsuffixed(m.Groups["name"].Value));
        }

        return named;
    }

    private static string Unsuffixed(string name)
    {
        foreach (var suffix in BridgeSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        }
        return name;
    }

    private static readonly string[] BridgeSuffixes =
    [
        ".brush", ".color", ".value", ".thickness", ".gridlength",
        ".leftmargin", ".rightmargin", ".boxshadow",
    ];

    /// <summary>Names the bridge publishes that are not tokens themselves.</summary>
    private static readonly HashSet<string> BridgeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ui.fontfamily", "content.fontfamily", "mono.fontfamily",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "The repository root was not found above the test binary.");
    }

    // ---- What is allowed, and why ------------------------------------------------------------

    /// <summary>
    /// Files that name a colour on purpose. Every one of them is a colour that leaves the
    /// application — a document, a printed page, a stored value, a page in someone's browser —
    /// and none of them is chrome. Adding a file here is a decision; adding one because the test
    /// went red is not.
    /// </summary>
    private static readonly Dictionary<string, string> ColourLiteralExemptions = new(StringComparer.Ordinal)
    {
        ["src/Mailbox.Rendering/MessageRenderer.cs"] =
            "the message document's own stylesheet, and its @media print block — paper is white.",
        ["src/Mailbox.Rendering/RenderedMessage.cs"] =
            "RenderStyle.Plain, the palette-free default for tests about markup.",
        ["src/Mailbox.Rendering/TablePrint.cs"] = "the printed table's stylesheet.",
        ["src/Mailbox.Rendering/CalendarPrint.cs"] = "the printed calendar's stylesheet — rules on paper.",
        ["src/Mailbox.Rendering/Reply.cs"] = "the quote bar in the HTML that goes out on the wire.",
        ["src/Mailbox.Editor/EmailHtml.cs"] = "the same quote bar, written by the editor.",
        ["src/Mailbox.Protocols/OAuth/LoopbackRedirect.cs"] =
            "the landing page served to the user's browser, which is not our window.",
        ["src/Mailbox.Core/Settings/CalendarOptions.cs"] =
            "the palette a calendar's colour is picked from, stored per collection.",
        ["src/Mailbox.App/Views/FontDialog.cs"] = "the document font-colour palette.",
        ["src/Mailbox.App/Views/EditorCommands.cs"] = "the highlight and font-colour palettes.",
        ["src/Mailbox.App/Views/JournalWorkspace.cs"] = "the colour a new journal is stored with.",
        ["src/Mailbox.App/Views/PosedDavServer.cs"] =
            "the calendar-color property a posed server answers discovery with — wire data, not paint.",
        ["src/Mailbox.App/Views/NotesWorkspace.cs"] = "the colour a new note book is stored with.",
        ["src/Mailbox.App/Views/TasksWorkspace.cs"] = "the colour a new task list is stored with.",
        ["src/Mailbox.App/Views/MainWindow.Tasks.cs"] = "the colour a new task list is stored with.",
        ["src/Mailbox.Google/GoogleTasks.cs"] = "the colour a pulled task list is stored with.",
        ["src/Mailbox.Store/Schema/Migrations.cs"] =
            "a SQL comment explaining why a category holds a token name and not a value.",
    };

    /// <summary>
    /// Tokens the application deliberately does not read, and why. Each is a control offered to a
    /// theme file rather than one the shell binds.
    /// </summary>
    private static readonly Dictionary<string, string> UnreadByDesign = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space.0"] = "the spacing scale, for a theme file to reach for.",
        ["space.1"] = "the spacing scale.",
        ["space.2"] = "the spacing scale.",
        ["space.3"] = "the spacing scale.",
        ["space.4"] = "the spacing scale.",
        ["space.5"] = "the spacing scale.",
        ["space.6"] = "the spacing scale.",
        ["space.7"] = "the spacing scale.",
        ["radius.none"] = "the radius scale.",
        ["radius.small"] = "the radius scale.",
        ["radius.medium"] = "the radius scale.",
        ["motion.fast"] = "the duration scale.",
        ["motion.normal"] = "the duration scale.",
        ["border.width.hairline"] = "the border scale.",
        ["type.ui.size.large"] = "the type scale.",
        ["type.content.size"] = "the type scale.",
        ["palette.neutral.dark"] = "the darkest step of the neutral ramp, for a theme file.",
        ["accent.pressed"] = "the accent ramp a theme file overrides one primitive to restyle.",
        ["accent.disabled"] = "the accent ramp.",
        ["status.info"] = "the status ramp.",

        // Geometry the controls read from RibbonMetrics; carried here so a theme file cannot
        // contradict the measurement the layout is built from. See OfficeThemes.Geometry.
        ["ribbon.height"] = "measured chrome geometry; RibbonMetrics.BodyHeight is what draws it.",
        ["ribbon.tabstrip.height"] = "measured chrome geometry; RibbonMetrics.TabStripHeight draws it.",
        ["list.row.unread.bar.width"] = "measured chrome geometry; the row template holds the column.",

        // Surfaces that have no state to paint yet. Recorded so the sweep does not re-find them.
        ["ribbon.tab.hover"] = "the ribbon's tab strip draws no hover state — audit finding F1.4.",
        ["nav.item.hover"] = "the folder pane hovers with state.hover — audit finding F1.5.",
        ["state.selectedinactive"] = "no list dims its selection when it loses the focus — F1.6.",
    };

    // ---- Patterns ----------------------------------------------------------------------------

    [GeneratedRegex(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?![0-9A-Za-z])")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\b(?:Brushes|Colors)\.(?<name>[A-Za-z]+)\b")]
    private static partial Regex NamedBrush();

    [GeneratedRegex(@"new\s+(?:[\w.]*\.)?(?:FontFamily|Typeface)\s*\(\s*""|\bFontFamily\s*=\s*""")]
    private static partial Regex FontFamilyFromLiteral();

    [GeneratedRegex(@"FontFamily\s*=\s*""(?!\{)")]
    private static partial Regex XamlFontFamilyLiteral();

    [GeneratedRegex(@"TokenKeys\.(?<name>\w+\.\w+)")]
    private static partial Regex TokenKeysConstant();

    [GeneratedRegex(@"""([a-z][a-z0-9]*(?:\.[a-z0-9\-]+)+)""")]
    private static partial Regex TokenLikeLiteral();

    [GeneratedRegex(@"DynamicResource(?:Extension\(\s*""|\s+)(?<name>[A-Za-z0-9_.\-]+)")]
    private static partial Regex DynamicResourceName();

    [GeneratedRegex(@"\{([A-Za-z0-9_.\-]+)\}")]
    private static partial Regex TokenReference();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
