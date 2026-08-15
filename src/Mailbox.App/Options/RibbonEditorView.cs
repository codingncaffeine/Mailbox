using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.App.Options;

/// <summary>
/// One line of the tree on the right: a tab, a group under it, or a command under that.
/// </summary>
/// <remarks>
/// Rows and their children share one flat sequence, as the message list does. A panel per level
/// would nest three deep for a structure that is never more than a few dozen lines, and would
/// make "the row above this one" — which is what the reorder buttons act on — a question about
/// the visual tree rather than about the document.
/// </remarks>
internal sealed class RibbonTreeRow
{
    public required RibbonTreeTab Tab { get; init; }
    public RibbonTreeGroup? Group { get; init; }
    public CommandId? Command { get; init; }

    public int Depth => Command is not null ? 2 : Group is not null ? 1 : 0;

    public bool IsTab => Group is null;
    public bool IsGroup => Group is not null && Command is null;
    public bool IsCommand => Command is not null;

    /// <summary>Whether this row has anything under it to show.</summary>
    public bool HasChildren => IsTab ? Tab.Groups.Count > 0 : IsGroup && Group!.Commands.Count > 0;
}

/// <summary>
/// Customize Ribbon: the command gallery on the left, the ribbon as a tree on the right.
/// </summary>
/// <remarks>
/// The editor works on a <see cref="RibbonTree"/> and saves it, which is the whole payoff of
/// the ribbon being a document. Nothing here knows how a ribbon is drawn.
/// <para>
/// It edits the Simplified bar, which is what the reference's own editor does — its heading
/// reads "Customize the Single Line Ribbon". Two deliberate divergences: the reference refuses
/// to add commands to a built-in group, because its built-ins are fixed resources, and ours are
/// not; and a built-in group removed here comes back through Reset rather than being offered in
/// the gallery, which is where the reference's "Main Tabs" source would put it.
/// </para>
/// </remarks>
public sealed class RibbonEditorView : CustomizationEditor
{
    private readonly RibbonCustomization _store;
    private readonly RibbonLayout _shipped;
    private readonly ListBox _tree = new();
    private readonly ComboBox _scope = new();
    private readonly TextBlock _band = new() { Text = "Main Tabs" };
    private readonly HashSet<string> _collapsed = [];

    private RibbonTree _model;

    public RibbonEditorView(CommandCatalog catalog, RibbonCustomization store, RibbonLayout shipped)
        : base(catalog)
    {
        _store = store;
        _shipped = shipped;
        _model = store.Load(shipped);

        // Groups start folded, as the capture shows them — a tab with eight groups each holding
        // three commands is thirty lines before anything has been chosen.
        foreach (var group in _model.Tabs.SelectMany(t => t.Groups)) _collapsed.Add(Key(group.Id));

        Build();
    }

    /// <summary>The edited tree, for a host that wants to apply it without reloading.</summary>
    public RibbonTree Model => _model;

    protected override string TargetHeading => "Customize the Single Line Ribbon:";

    protected override bool HasPerTabReset => true;

    // ---- The tree --------------------------------------------------------------------------

    /// <summary>
    /// Which tabs the tree shows, as the reference's own picker offers them.
    /// </summary>
    /// <remarks>
    /// Tool Tabs is empty until something ships a contextual tab set — Search, in Phase 8, is
    /// the first that will. It is here rather than left out because it filters for real: the
    /// distinction is already in the layout document, so the picker starts working the day
    /// there is something to show rather than needing to be built then.
    /// </remarks>
    private static IReadOnlyList<string> Scopes { get; } = ["Main Tabs", "Tool Tabs", "All Tabs"];

    protected override Control BuildTargetHeader()
    {
        _scope.ItemsSource = Scopes.ToList();
        _scope.SelectedIndex = 0;
        _scope.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scope.SelectionChanged += (_, _) =>
        {
            _band.Text = Scopes[Math.Max(_scope.SelectedIndex, 0)];
            RebuildTree();
        };

        return new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { Heading(TargetHeading, info: true), _scope },
        };
    }

    protected override Control BuildTarget()
    {
        _tree.ItemTemplate = new FuncDataTemplate<RibbonTreeRow>((row, _) => TreeRow(row));
        _tree.SelectionChanged += (_, _) => RefreshButtons();
        Plain(_tree);

        var panel = new DockPanel();
        var band = BandHeading();
        DockPanel.SetDock(band, Dock.Top);
        panel.Children.Add(band);
        panel.Children.Add(_tree);

        RebuildTree();
        return Box(panel);
    }

    /// <summary>The band at the top of the pane naming what the tree is showing.</summary>
    private Control BandHeading()
    {
        _band.Margin = new Thickness(6, 3);
        _band.FontWeight = FontWeight.SemiBold;
        Bind(_band, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var band = new Border { Child = _band, BorderThickness = new Thickness(0, 0, 0, 1) };
        Bind(band, Border.BorderBrushProperty, "dialog.border.brush");
        Bind(band, Border.BackgroundProperty, "dialog.selection.brush");
        return band;
    }

    /// <summary>Whether a tab belongs to what the picker is currently showing.</summary>
    private bool InScope(RibbonTreeTab tab)
    {
        if (_scope.SelectedIndex == 2) return true;

        var contextual = _shipped.FindTab(tab.Id)?.IsContextual ?? false;
        return _scope.SelectedIndex == 1 ? contextual : !contextual;
    }

    private void RebuildTree()
    {
        var selected = _tree.SelectedItem as RibbonTreeRow;
        var rows = new List<RibbonTreeRow>();

        foreach (var tab in _model.Tabs.Where(InScope))
        {
            rows.Add(new RibbonTreeRow { Tab = tab });
            if (_collapsed.Contains(Key(tab.Id))) continue;

            foreach (var group in tab.Groups)
            {
                rows.Add(new RibbonTreeRow { Tab = tab, Group = group });
                if (_collapsed.Contains(Key(group.Id))) continue;

                rows.AddRange(group.Commands.Select(command => new RibbonTreeRow
                {
                    Tab = tab,
                    Group = group,
                    Command = command,
                }));
            }
        }

        _tree.ItemsSource = rows;

        // Rows are rebuilt rather than mutated, so selection is restored by what a row stands
        // for. Holding the old instance would leave the buttons acting on a row nobody can see.
        // With nothing selected yet the first tab is, as the capture shows it — every button on
        // the page acts on the selection, and a page that opens with none reads as inert.
        _tree.SelectedItem = selected is null
            ? rows.FirstOrDefault()
            : rows.FirstOrDefault(r => Stands(r, selected));

        RefreshButtons();
    }

    private static bool Stands(RibbonTreeRow row, RibbonTreeRow other)
        => ReferenceEquals(row.Tab, other.Tab)
           && ReferenceEquals(row.Group, other.Group)
           && Nullable.Equals(row.Command, other.Command);

    private static string Key(string id) => id;

    private Control TreeRow(RibbonTreeRow row)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4 + (row.Depth * 14), 1, 2, 1),
        };

        line.Children.Add(Chevron(row));

        if (row.IsTab) line.Children.Add(TabTick(row.Tab));

        if (row.IsCommand && Catalog.TryGet(row.Command!.Value, out var command))
        {
            line.Children.Add(Glyph(command.Icon));
        }

        var label = new TextBlock
        {
            Text = LabelFor(row),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        line.Children.Add(label);

        return line;
    }

    private string LabelFor(RibbonTreeRow row)
    {
        if (row.IsCommand)
        {
            return Catalog.TryGet(row.Command!.Value, out var command)
                ? command.Label
                : row.Command.Value.Value;
        }

        // The reference marks what a user made rather than what it shipped, in the list only —
        // the ribbon itself shows the name alone.
        if (row.IsGroup) return row.Group!.Label + (row.Group.IsCustom ? " (Custom)" : string.Empty);
        return row.Tab.Label + (row.Tab.IsCustom ? " (Custom)" : string.Empty);
    }

    private Control Chevron(RibbonTreeRow row)
    {
        if (!row.HasChildren)
        {
            return new Panel { Width = 14 };
        }

        var id = row.IsTab ? row.Tab.Id : row.Group!.Id;
        var folded = _collapsed.Contains(Key(id));

        var glyph = Glyph(folded ? "chevron-right" : "chevron-down", 9);
        Bind(glyph, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        glyph.Width = 14;

        var button = new Button
        {
            Content = glyph,
            Width = 14,
            Height = 16,
            Padding = default,
            Background = null,
            BorderThickness = default,
            Classes = { "plain" },
        };

        button.Click += (_, _) =>
        {
            if (!_collapsed.Remove(Key(id))) _collapsed.Add(Key(id));
            RebuildTree();
        };

        return button;
    }

    private Control TabTick(RibbonTreeTab tab)
    {
        var box = new CheckBox
        {
            IsChecked = tab.IsVisible,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Padding = default,
        };

        box.IsCheckedChanged += (_, _) =>
        {
            if (tab.IsVisible == (box.IsChecked == true)) return;
            tab.IsVisible = box.IsChecked == true;
            Save();
        };

        return box;
    }

    // ---- Editing ---------------------------------------------------------------------------

    private RibbonTreeRow? SelectedRow => _tree.SelectedItem as RibbonTreeRow;

    protected override bool CanAdd => SelectedRow is { IsTab: false };

    protected override bool CanRemove => SelectedRow is { } row
        && (row.IsCommand || row.IsGroup || row.Tab.IsCustom);

    protected override bool CanMove(int delta)
    {
        if (SelectedRow is not { } row) return false;

        var siblings = SiblingCount(row);
        var index = IndexOf(row);
        return index >= 0 && index + delta >= 0 && index + delta < siblings;
    }

    private int SiblingCount(RibbonTreeRow row)
        => row.IsCommand ? row.Group!.Commands.Count
            : row.IsGroup ? row.Tab.Groups.Count
            : _model.Tabs.Count;

    private int IndexOf(RibbonTreeRow row)
        => row.IsCommand ? row.Group!.Commands.IndexOf(row.Command!.Value)
            : row.IsGroup ? row.Tab.Groups.IndexOf(row.Group!)
            : _model.Tabs.IndexOf(row.Tab);

    protected override void OnAdd(GalleryEntry entry)
    {
        if (entry.Command is not { } command || SelectedRow is not { } row) return;

        var group = row.Group;
        if (group is null) return;

        var at = row.IsCommand ? group.Commands.IndexOf(row.Command!.Value) + 1 : group.Commands.Count;
        group.Commands.Insert(at, command);

        _collapsed.Remove(Key(group.Id));
        Save();
    }

    protected override void OnRemove()
    {
        if (SelectedRow is not { } row) return;

        if (row.IsCommand) row.Group!.Commands.Remove(row.Command!.Value);
        else if (row.IsGroup) row.Tab.Groups.Remove(row.Group!);
        else if (row.Tab.IsCustom) _model.Tabs.Remove(row.Tab);
        else return;

        Save();
    }

    protected override void OnMove(int delta)
    {
        if (SelectedRow is not { } row) return;

        var index = IndexOf(row);
        var to = index + delta;
        if (index < 0 || to < 0 || to >= SiblingCount(row)) return;

        if (row.IsCommand) Move(row.Group!.Commands, index, to);
        else if (row.IsGroup) Move(row.Tab.Groups, index, to);
        else Move(_model.Tabs, index, to);

        Save();
    }

    private static void Move<T>(List<T> items, int from, int to)
    {
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }

    protected override void OnReset(bool selectedTabOnly)
    {
        if (selectedTabOnly)
        {
            if (SelectedRow is not { } row) return;
            _model.ResetTab(_shipped, row.Tab.Id);
        }
        else
        {
            _store.Reset();
            _model = RibbonTree.From(_shipped);
        }

        Save();
    }

    protected override void OnImport(string path)
    {
        try
        {
            var imported = RibbonCustomization.Import(path);
            imported.Tree.Reconcile(_shipped);
            _model = imported.Tree;

            if (imported.QuickAccess is { Count: > 0 } toolbar)
            {
                App.QuickAccess.Replace(toolbar);
            }
        }
        catch (Exception ex)
        {
            // A file the user chose is allowed to be the wrong file. Say so in the log and
            // leave the ribbon as it was rather than half-importing it.
            Core.Diagnostics.Log.Warn($"Could not import {path}.", ex);
            return;
        }

        Save();
    }

    protected override void OnExport(string path)
        => RibbonCustomization.Export(path, _model, App.QuickAccess.Commands);

    private void Save()
    {
        _store.Save(_model, _shipped);
        RebuildTree();
    }

    // ---- Footer ----------------------------------------------------------------------------

    protected override Control BuildTargetFooter()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var newTab = DialogButton("New Tab");
        newTab.Click += (_, _) => AddTab();
        row.Children.Add(newTab);

        var newGroup = DialogButton("New Group");
        newGroup.Click += (_, _) => AddGroup();
        row.Children.Add(newGroup);

        var rename = DialogButton("Rename...");
        rename.Click += async (_, _) => await RenameAsync();
        row.Children.Add(rename);

        return row;
    }

    /// <summary>
    /// A new tab arrives with a group already in it, because a tab with no groups holds
    /// nothing and the next thing anyone does is make one.
    /// </summary>
    private void AddTab()
    {
        var group = new RibbonTreeGroup
        {
            Id = _model.NextGroupId(),
            Label = "New Group",
            IsCustom = true,
        };

        var tab = new RibbonTreeTab
        {
            Id = _model.NextTabId(),
            Label = "New Tab",
            IsCustom = true,
            Groups = { group },
        };

        var after = SelectedRow is { } row ? _model.Tabs.IndexOf(row.Tab) + 1 : _model.Tabs.Count;
        _model.Tabs.Insert(after, tab);
        Save();
    }

    private void AddGroup()
    {
        if (SelectedRow is not { } row) return;

        var group = new RibbonTreeGroup
        {
            Id = _model.NextGroupId(),
            Label = "New Group",
            IsCustom = true,
        };

        var after = row.Group is null ? row.Tab.Groups.Count : row.Tab.Groups.IndexOf(row.Group) + 1;
        row.Tab.Groups.Insert(after, group);
        Save();
    }

    private async Task RenameAsync()
    {
        if (SelectedRow is not { } row) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var current = row.IsGroup ? row.Group!.Label : row.IsTab ? row.Tab.Label : null;
        if (current is null) return;

        if (await Views.Prompt.AskAsync(window, "Rename", "Display name:", current) is not { } name) return;
        if (string.IsNullOrWhiteSpace(name)) return;

        if (row.IsGroup) row.Group!.Label = name.Trim();
        else row.Tab.Label = name.Trim();

        Save();
    }
}
