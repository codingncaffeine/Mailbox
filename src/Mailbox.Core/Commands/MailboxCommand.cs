namespace Mailbox.Core.Commands;

/// <summary>
/// Which window a command belongs to.
/// </summary>
/// <remarks>
/// Every command lives in one catalogue, so the shell and the windows that open over it share
/// one another's ids — and one another's keys. Ctrl+U marks a message unread in the shell and
/// underlines a word in a compose window, and both are ordinary commands; this is what tells
/// the shell's key map that only one of them is its to run.
/// </remarks>
public enum CommandSurface
{
    /// <summary>The main window: the modules, their ribbons and their lists.</summary>
    Shell,

    /// <summary>The compose window and the editor inside it.</summary>
    Compose,

    /// <summary>The appointment and meeting window.</summary>
    Appointment,

    /// <summary>The contact window, which opens one person or one group.</summary>
    Contact,
}

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

    /// <summary>
    /// The token whose colour draws this command's icon, when it is not the accent's.
    /// </summary>
    /// <remarks>
    /// The reference's ribbon icons are polychrome artwork, and a few of them carry a colour
    /// that is nothing to do with the accent: Reply and Reply All are magenta, Forward is blue.
    /// A ribbon that draws those in the accent like everything else is a different application,
    /// which is the same observation <see cref="NeutralIcon"/> records for the formatting run.
    /// Null leaves the choice to <see cref="NeutralIcon"/>.
    /// </remarks>
    public string? IconTint { get; init; }

    /// <summary>
    /// Names a drawing rather than a glyph, for the icons no monochrome font can carry: the
    /// four coloured swatches of Categorize, and Follow Up's red flag.
    /// </summary>
    public string? IconArtwork { get; init; }

    public ModuleScope Scope { get; init; } = ModuleScope.Any;

    /// <summary>
    /// The window this command belongs to. Stamped where each class lists its commands for
    /// registration, so no single definition can be the one that forgets.
    /// </summary>
    public CommandSurface Surface { get; init; } = CommandSurface.Shell;

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
    /// Further shortcuts that also run the command, as shipped: the reference gives a few
    /// commands two — F9 and Ctrl+M both send and receive, Ctrl+N and Ctrl+Shift+M both start a
    /// message, Ctrl+E and F3 both go to Search. These are consulted after every command's own
    /// shortcut, so a reader who gives one of these chords to another command takes it away
    /// from here without having to be asked.
    /// </summary>
    public IReadOnlyList<string> AlsoGestures { get; init; } = [];

    /// <summary>
    /// False for commands that exist but are not in the reference application — Snooze, Undo Send, Message
    /// Source. They are fully present in the catalogue and customization gallery, they are
    /// simply absent from the default ribbon layout so first run is an exact clone.
    /// </summary>
    public bool InDefaultLayout { get; init; } = true;

    /// <summary>Grouping label in the customization gallery, mirroring the reference's own grouping.</summary>
    public required string Category { get; init; }

    /// <summary>
    /// True for a command that is a state rather than an action — Work Offline, Use Tighter
    /// Spacing, one arrangement of eleven — so the bar can draw the box the reference draws
    /// round it while it is on.
    /// </summary>
    /// <remarks>
    /// Declared here rather than inferred from whoever answers the host's checked hook, because
    /// the bar has to know before the state does: a button that grew a line when it was switched
    /// on would shove its neighbours along the bar, so a toggle carries its line from the start
    /// and paints it transparent until it is wanted. A command that is not one is drawn exactly
    /// as it was before any of this existed, down to the pixel.
    /// </remarks>
    public bool IsToggle { get; init; }

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
