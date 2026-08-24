namespace Mailbox.Plugins.Api;

/// <summary>
/// Commands a plugin adds, and where they sit. Permission: <c>ui</c>.
/// </summary>
/// <remarks>
/// A plugin command enters the same catalogue every built-in command lives in, under the id
/// <c>plugin.&lt;pluginId&gt;.&lt;name&gt;</c> — searchable in Customize Ribbon, placeable on any
/// tab and the Quick Access Toolbar, and bindable to a key, with no second code path. Disabling
/// the plugin takes its commands out of the catalogue and off every surface they were placed on.
/// </remarks>
public interface IPluginCommands
{
    /// <summary>
    /// Registers a command. <paramref name="execute"/> runs on the UI thread when the command is
    /// pressed — from the ribbon, a key, the Quick Access Toolbar or the harness alike.
    /// </summary>
    void Register(PluginCommand command, Action execute);

    /// <summary>
    /// Adds a tab of the plugin's own to the Mail module's ribbon, in both layouts. The commands
    /// it places must be registered first. A plugin gets no space on the shipped tabs — first run
    /// is a clone, and additions live on their own tab exactly as unplaced built-ins do — but a
    /// reader can move its commands anywhere from Customize Ribbon, because they are ordinary
    /// catalogue entries.
    /// </summary>
    void AddRibbonTab(PluginRibbonTab tab);
}

/// <summary>One command a plugin registers.</summary>
public sealed record PluginCommand
{
    /// <summary>
    /// The id's last part: lowercase letters and digits, dot-separated segments allowed. The full
    /// id becomes <c>plugin.&lt;pluginId&gt;.&lt;name&gt;</c> and is stable API once shipped —
    /// ribbon layouts and key bindings persist it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Label on a ribbon button. Short — "Word Count", not a sentence.</summary>
    public required string Label { get; init; }

    /// <summary>Screentip body: one sentence saying what happens.</summary>
    public required string Description { get; init; }

    /// <summary>Icon key from the application's icon set. The host falls back when unknown.</summary>
    public string Icon { get; init; } = "apps";

    /// <summary>Default keyboard shortcut, e.g. <c>Ctrl+Shift+7</c>. Users may rebind it.</summary>
    public string? Gesture { get; init; }
}

/// <summary>A ribbon tab of the plugin's own.</summary>
public sealed record PluginRibbonTab
{
    /// <summary>The tab id's last part, same rules as a command name.</summary>
    public required string Name { get; init; }

    /// <summary>The word on the tab strip.</summary>
    public required string Label { get; init; }

    public required IReadOnlyList<PluginRibbonGroup> Groups { get; init; }

    /// <summary>
    /// Which module's ribbon carries the tab: <c>mail</c> (the default), <c>calendar</c>,
    /// <c>people</c>, <c>tasks</c>, <c>notes</c> or <c>journal</c>. Anything else is refused at
    /// registration with the accepted words named.
    /// </summary>
    public string Module { get; init; } = "mail";
}

/// <summary>A labelled cluster on a plugin's tab.</summary>
public sealed record PluginRibbonGroup
{
    public required string Label { get; init; }

    /// <summary>Command names of this plugin's own, in order.</summary>
    public required IReadOnlyList<string> Commands { get; init; }
}

/// <summary>
/// Columns a plugin adds to the message list's table views. Permission: <c>ui</c>.
/// </summary>
/// <remarks>
/// A plugin column is an ordinary column: it appears in Show Columns beside the built-in
/// fields, is placed and widened the same way, and survives in saved views by its id —
/// <c>plugin.&lt;pluginId&gt;.&lt;name&gt;</c>. A view that names a column whose plugin is
/// disabled draws it empty rather than breaking the view.
/// </remarks>
public interface IPluginColumns
{
    /// <summary>
    /// Registers a column. The value provider is called on the UI thread for each visible row
    /// as the list draws — answer from the row it is handed, never from the network or a store
    /// walk, or scrolling stutters by exactly the time taken.
    /// </summary>
    void Add(PluginColumn column, Func<PluginMessageSummary, string> value);
}

/// <summary>One column a plugin registers.</summary>
public sealed record PluginColumn
{
    /// <summary>The id's last part, same rules as a command name. Stable once shipped — saved views persist it.</summary>
    public required string Name { get; init; }

    /// <summary>What the header calls the column.</summary>
    public required string Label { get; init; }

    /// <summary>The width the column has until Format Columns says otherwise.</summary>
    public double Width { get; init; } = 110;
}

/// <summary>
/// Bars above a rendered message. Permission: <c>ui</c>.
/// </summary>
public interface IPluginReadingPane
{
    /// <summary>
    /// Asks the provider about every message the pane renders; a non-null answer draws a bar in
    /// the strip above it, after the application's own. Called on the UI thread while the message
    /// is being drawn, so it must answer from what it already knows — nothing on the render path
    /// may touch the network or block, which is a rule the application holds itself to as well.
    /// </summary>
    void AddInfoBar(Func<PluginMessageSummary, PluginInfoBar?> provider);
}

/// <summary>What a plugin's bar says, and the one button it may carry.</summary>
public sealed record PluginInfoBar(string Text, string? ButtonLabel = null, Action? ButtonPressed = null);
