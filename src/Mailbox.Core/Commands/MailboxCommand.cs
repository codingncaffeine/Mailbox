namespace Mailbox.Core.Commands;

/// <summary>
/// One thing Mailbox can do. Every user-invokable action is one of these — there is no
/// other way for UI to trigger behaviour, which is what makes the ribbon rearrangeable
/// and lets an unplaced command still be searchable, bindable and reachable.
/// </summary>
public sealed record MailboxCommand
{
    public required CommandId Id { get; init; }

    /// <summary>Label on a ribbon button. Short — "Reply All", not "Reply to all recipients".</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Screentip body: one sentence saying what happens. the reference's screentips are a bold
    /// heading (the <see cref="Label"/>) over a description, not a one-line tooltip.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>Icon key resolved against the active theme's icon set. Not a file path.</summary>
    public required string Icon { get; init; }

    /// <summary>
    /// Draws the icon in the text colour rather than the accent.
    /// </summary>
    /// <remarks>
    /// Office's icons are polychrome artwork; ours come from a monochrome icon font, so tone is
    /// the only part of that we can reproduce. The distinction is real and visible: the
    /// reference draws Bold, Italic, Underline and the rest of the formatting run in near-black,
    /// and reserves colour for the commands that act on a message. A ribbon with every glyph in
    /// the accent reads as a different application.
    /// </remarks>
    public bool NeutralIcon { get; init; }

    public ModuleScope Scope { get; init; } = ModuleScope.Any;

    /// <summary>
    /// KeyTip characters shown when Alt is pressed. 1–3 uppercase characters, no whitespace,
    /// per the published ribbon framework spec rules. Null means auto-assign at layout time.
    /// </summary>
    public string? KeyTip { get; init; }

    /// <summary>
    /// Default keyboard shortcut in Avalonia gesture syntax, e.g. <c>Ctrl+Shift+R</c>.
    /// Users may rebind; this is only the shipped default.
    /// </summary>
    public string? DefaultGesture { get; init; }

    /// <summary>
    /// False for commands that exist but are not in the reference application — Snooze, Undo Send, Message
    /// Source. They are fully present in the catalogue and customization gallery, they are
    /// simply absent from the default ribbon layout so first run is an exact clone.
    /// </summary>
    public bool InDefaultLayout { get; init; } = true;

    /// <summary>Grouping label in the customization gallery, mirroring the reference's own grouping.</summary>
    public required string Category { get; init; }

    /// <summary>
    /// True when the command needs one or more selected items. Drives enablement without
    /// each call site reimplementing the check.
    /// </summary>
    public bool RequiresSelection { get; init; }

    public bool RequiresSingleSelection { get; init; }

    /// <summary>Set by the plugin host for commands contributed by a plugin.</summary>
    public string? OwningPluginId { get; init; }

    public bool IsBuiltIn => OwningPluginId is null;

    public override string ToString() => $"{Id} ({Label})";
}
