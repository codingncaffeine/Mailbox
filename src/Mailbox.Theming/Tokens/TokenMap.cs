namespace Mailbox.Theming.Tokens;

/// <summary>What a token is for, at the altitude an editor cares about.</summary>
public enum TokenRole
{
    /// <summary>A surface something stands on: backgrounds, fills, panes.</summary>
    Ground,

    /// <summary>Something to be read against a ground: text, glyphs, labels.</summary>
    Ink,

    /// <summary>The brand colour and the signal colours that behave like it.</summary>
    Accent,

    /// <summary>A transient state over a ground: hover, pressed, selection.</summary>
    Wash,

    /// <summary>A rule, border, separator or outline.</summary>
    Line,

    /// <summary>A mark with an identity of its own: icon inks, swatches, stripes, category colours.</summary>
    Artwork,

    /// <summary>Sizes, spacing, families, shadows — anything that is not a colour choice.</summary>
    Geometry,
}

/// <summary>
/// One region of the application a person can mean when they say "make <em>that</em> part
/// look different": its user-facing name, the tokens that paint it, and the rules that decide
/// what an automated door may do there.
/// </summary>
/// <param name="Id">A stable slug for harness doors and settings.</param>
/// <param name="Name">What an editor calls the region.</param>
/// <param name="Pointable">Whether the region is a place on screen an inspect overlay can outline.</param>
/// <param name="IsContent">Protected by the light-content rule: content stays light under dark chrome, and nothing automated recolours it.</param>
/// <param name="IsDesktop">The desktop's own palette, identical in every built-in; no automated door touches it.</param>
/// <param name="MayTakeImage">Whether the region may ever carry a backdrop image.</param>
/// <param name="Tokens">Every token that paints the region.</param>
public sealed record TokenArea(
    string Id,
    string Name,
    bool Pointable,
    bool IsContent,
    bool IsDesktop,
    bool MayTakeImage,
    IReadOnlyList<string> Tokens)
{
    /// <summary>Whether a palette, importer or other automated writer may recolour this region.</summary>
    public bool MayAutomate => !IsContent && !IsDesktop;
}

/// <summary>
/// The knowledge layer over the token names: which area of the application each token paints,
/// what role it plays there, and which inks read against which grounds. Pure data — nothing
/// here changes how a theme composes — but it is what lets an area picker, a palette mapper
/// and the contrast repair share one answer instead of three hand-kept lists.
/// </summary>
/// <remarks>
/// <see cref="ContrastAudit.Pairs"/> stays the source of truth for readability pairs; the map
/// re-serves it by ground. Area membership is asserted complete over
/// <see cref="TokenKeys.Required"/> by a test, so a token added without a home fails the build
/// rather than falling silently outside every editor view.
/// </remarks>
public static class TokenMap
{
    // ------------------------------------------------------------------------------------
    // Areas
    // ------------------------------------------------------------------------------------

    public static IReadOnlyList<TokenArea> Areas { get; }

    private static readonly Dictionary<string, TokenArea> AreaByToken = new(StringComparer.OrdinalIgnoreCase);

    static TokenMap()
    {
        // The split families first: the message list's chrome and its rows disagree about the
        // light-content rule, as do the compose window's header and its body, and the peek's
        // docked and floating halves — so prefix alone cannot place them.
        string[] listRows =
        [
            TokenKeys.List.RowBackground, TokenKeys.List.RowHover, TokenKeys.List.RowSelected,
            TokenKeys.List.UnreadBar, TokenKeys.List.UnreadBarWidth, TokenKeys.List.UnreadText,
            TokenKeys.List.ReadText, TokenKeys.List.PreviewText, TokenKeys.List.RowHeight,
            TokenKeys.List.RowHeightCompact, TokenKeys.List.OverdueText,
        ];
        string[] listChrome =
        [
            TokenKeys.List.Background, TokenKeys.List.HeaderBackground, TokenKeys.List.HeaderText,
            TokenKeys.List.GroupHeaderBackground, TokenKeys.List.GroupHeaderText,
            TokenKeys.List.GroupHeaderHeight, TokenKeys.List.Separator, TokenKeys.List.Width,
        ];
        string[] composeHeader =
        [
            TokenKeys.Compose.HeaderBackground, TokenKeys.Compose.HeaderText,
            TokenKeys.Compose.HeaderLabel, TokenKeys.Compose.FieldRule,
        ];
        string[] composeBody = [TokenKeys.Compose.BodyBackground, TokenKeys.Compose.BodyText];

        var areas = new List<TokenArea>();

        TokenArea Add(string id, string name, IEnumerable<string> tokens,
            bool pointable = true, bool content = false, bool desktop = false, bool image = false)
        {
            var area = new TokenArea(id, name, pointable, content, desktop, image, [.. tokens]);
            areas.Add(area);
            foreach (var token in area.Tokens) AreaByToken.TryAdd(token, area);
            return area;
        }

        IEnumerable<string> Prefixed(params string[] prefixes)
            => TokenKeys.Required
                .Where(k => !AreaByToken.ContainsKey(k)
                            && prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        // Chrome, in the order a window stacks it. The backdrop family is optional — not in
        // Required — but it is still the title bar's, and an editor must list it there.
        Add("titlebar", "Title Bar",
            [
                .. Prefixed("titlebar.", "avatar."),
                TokenKeys.TitleBar.Backdrop, TokenKeys.TitleBar.BackdropAlignment,
                TokenKeys.TitleBar.BackdropTiling, TokenKeys.TitleBar.BackdropSize,
                TokenKeys.TitleBar.BackdropOpacity,
            ],
            image: true);
        Add("ribbon", "Ribbon", Prefixed("ribbon."));
        Add("backstage", "File view", Prefixed("backstage."));
        Add("rail", "App Rail", Prefixed("rail."));
        Add("nav", "Folder Pane", Prefixed("nav."));
        Add("list-chrome", "List Chrome", listChrome);
        Add("statusbar", "Status Bar", Prefixed("statusbar."));
        Add("compose-header", "Compose Header", composeHeader);
        Add("dialog", "Dialogs", Prefixed("dialog."));
        Add("peek", "Calendar Peek", TokenKeys.Peek.Docked);

        // The desktop's regions: themed by the desktop, never by an automated door.
        Add("systemdialog", "System Dialogs", Prefixed("systemdialog."), desktop: true);
        Add("peek-popup", "Peek Popup", TokenKeys.Peek.Floating, pointable: false, desktop: true);

        // Content: what the light-content rule protects.
        Add("list-rows", "List Rows", listRows, content: true);
        Add("reading", "Reading Pane", Prefixed("reading."), content: true);
        Add("compose-body", "Compose Body", composeBody, content: true);
        Add("calendar", "Calendar", Prefixed("calendar."), content: true);
        Add("notes", "Notes", Prefixed("notes."), content: true);
        Add("journal", "Journal", Prefixed("journal."), content: true);
        Add("people", "People", Prefixed("people."), content: true);

        // The families with no single place on screen.
        Add("foundations", "Content Foundations", Prefixed("surface.", "text.", "state.", "border."),
            pointable: false, content: true);
        Add("accent", "Accent & Status", Prefixed("accent.", "status."), pointable: false);
        Add("marks", "Tags & Categories", Prefixed("tags.", "category.", "pictogram."),
            pointable: false, content: true);
        Add("window", "Window Chrome", Prefixed("window.", "workspace.", "elevation.", "icons."),
            pointable: false);
        Add("typography", "Typography", Prefixed("type."), pointable: false);

        Areas = areas;
    }

    /// <summary>The area a token paints, or null for a token the map does not place (a primitive, or a key from a later version).</summary>
    public static TokenArea? AreaOf(string token)
        => AreaByToken.GetValueOrDefault(token);

    // ------------------------------------------------------------------------------------
    // Pairs, by ground
    // ------------------------------------------------------------------------------------

    private static readonly Lookup<string, string> InksByGround =
        (Lookup<string, string>)ContrastAudit.Pairs.ToLookup(p => p.Ground, p => p.Ink, StringComparer.OrdinalIgnoreCase);

    /// <summary>The inks somebody has to read against this ground — <see cref="ContrastAudit.Pairs"/>, served by ground.</summary>
    public static IReadOnlyList<string> InksOn(string ground) => [.. InksByGround[ground]];

    // ------------------------------------------------------------------------------------
    // Roles
    // ------------------------------------------------------------------------------------

    private static readonly HashSet<string> PairInks =
        new(ContrastAudit.Pairs.Select(p => p.Ink), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PairGrounds =
        new(ContrastAudit.Pairs.Select(p => p.Ground), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The role a token plays. The audit's own pairs answer first — a token somebody reads is
    /// ink, what it is read against is ground — and naming conventions answer for the rest.
    /// </summary>
    public static TokenRole RoleOf(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var key = token.ToLowerInvariant();

        // Not a colour choice at all.
        if (key.StartsWith("type.") || key.StartsWith("elevation.") || key.StartsWith("space.")
            || key.StartsWith("radius.") || key.StartsWith("motion.")
            || key is "icons.set" or "workspace.inset"
            || key.Contains(".backdrop")
            || key.Contains(".height") || key.EndsWith(".width") || key.EndsWith(".size")
            || key.EndsWith(".inset") || key.EndsWith(".offset") || key.EndsWith(".tint"))
        {
            return TokenRole.Geometry;
        }

        // What the audit already knows.
        if (PairInks.Contains(token)) return TokenRole.Ink;
        if (PairGrounds.Contains(token)) return TokenRole.Ground;

        // Marks with identities of their own, before the line rules can mistake a swatch's
        // outline for chrome.
        if (key.StartsWith("ribbon.icon.") || key.StartsWith("systemdialog.icon.")
            || key.StartsWith("tags.") || key.StartsWith("category.") || key.StartsWith("pictogram.")
            || key is "notes.default" or "calendar.chip.default" or "calendar.outofoffice"
                or "calendar.chip.free.stripe")
        {
            return TokenRole.Artwork;
        }

        if (key.StartsWith("accent.") || key.StartsWith("status.") || key.StartsWith("palette.brand.")
            || key is "nav.unreadcount" or "ribbon.tab.underline" or "rail.indicator"
                or "rail.item.active" or "calendar.currenttime")
        {
            return TokenRole.Accent;
        }

        if (key.Contains(".hover") || key.Contains(".pressed") || key.Contains(".selection")
            || key.EndsWith(".selected") || key.EndsWith(".open") || key.StartsWith("state."))
        {
            return TokenRole.Wash;
        }

        if (key.StartsWith("border.") || key.Contains(".border") || key.Contains(".rule")
            || key.Contains(".separator") || key.Contains(".gridline") || key.Contains(".divider")
            || key.Contains(".outline") || key.Contains(".edge") || key.EndsWith(".line")
            || key is "window.border" or "statusbar.slider")
        {
            return TokenRole.Line;
        }

        if (key.StartsWith("text.") || key.Contains(".text") || key.Contains(".foreground")
            || key.EndsWith(".label") || key.Contains(".link"))
        {
            return TokenRole.Ink;
        }

        if (key.StartsWith("surface.") || key.Contains(".background") || key.Contains(".fill")
            || key.Contains(".ground") || key.Contains(".hatch") || key.Contains(".scroll")
            || key.Contains(".field") || key.EndsWith(".frame") || key.EndsWith(".today")
            || key.EndsWith(".search") || key.EndsWith(".surface") || key.EndsWith(".banner")
            || key.EndsWith(".tab") || key.EndsWith(".button"))
        {
            return TokenRole.Ground;
        }

        return TokenRole.Artwork;
    }
}
