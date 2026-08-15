using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.App.Options;

/// <summary>
/// The Quick Access Toolbar page: the same editor as Customize Ribbon, over a flat list.
/// </summary>
/// <remarks>
/// The toolbar's state already had a home — it is stored with the rest of the preferences and
/// the chevron flyout has been editing it since Phase 1. This page is the long way round to the
/// same object, so the two cannot disagree: place a command here and the flyout shows it
/// ticked, hide the bar there and the checkbox here clears.
/// </remarks>
public sealed class QuickAccessEditorView : CustomizationEditor
{
    private readonly QuickAccessLayout _toolbar;
    private readonly RibbonCustomization _ribbon;
    private readonly RibbonLayout _shipped;
    private readonly ListBox _placed = new();

    public QuickAccessEditorView(
        CommandCatalog catalog,
        QuickAccessLayout toolbar,
        RibbonCustomization ribbon,
        RibbonLayout shipped)
        : base(catalog)
    {
        _toolbar = toolbar;
        _ribbon = ribbon;
        _shipped = shipped;
        Build();
    }

    protected override string TargetHeading => "Customize Quick Access Toolbar:";

    /// <summary>
    /// The toolbar is the one surface that takes a separator, and the reference offers it at
    /// the top of the gallery rather than as a button of its own.
    /// </summary>
    protected override bool OffersSeparator => true;

    protected override Control BuildTarget()
    {
        _placed.ItemTemplate = new FuncDataTemplate<GalleryEntry>((entry, _) => PlacedRow(entry));
        _placed.SelectionChanged += (_, _) => RefreshButtons();
        Plain(_placed);

        RebuildPlaced();
        return Box(_placed);
    }

    private Control PlacedRow(GalleryEntry entry)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(4, 1),
        };

        if (!entry.IsSeparator) row.Children.Add(Glyph(entry.Icon));

        var label = new TextBlock
        {
            Text = entry.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        row.Children.Add(label);

        return row;
    }

    private void RebuildPlaced()
    {
        var index = _placed.SelectedIndex;

        _placed.ItemsSource = _toolbar.Commands
            .Select(id => Catalog.TryGet(id, out var command)
                ? GalleryEntry.For(command)
                : GalleryEntry.Separator)
            .ToList();

        _placed.SelectedIndex = Math.Min(index, _toolbar.Commands.Count - 1);
        RefreshButtons();
    }

    // ---- Editing ---------------------------------------------------------------------------

    protected override bool CanAdd => true;

    protected override bool CanRemove => _placed.SelectedIndex >= 0;

    protected override bool CanMove(int delta)
    {
        var to = _placed.SelectedIndex + delta;
        return _placed.SelectedIndex >= 0 && to >= 0 && to < _toolbar.Commands.Count;
    }

    protected override void OnAdd(GalleryEntry entry)
    {
        // A rule is furniture rather than a command, and a toolbar may carry several, so it
        // does not go through the "already placed" check that a command does.
        if (entry.Command is { } command) _toolbar.Add(command);
        else _toolbar.AddSeparator();

        RebuildPlaced();
    }

    protected override void OnRemove()
    {
        if (_toolbar.RemoveAt(_placed.SelectedIndex)) RebuildPlaced();
    }

    protected override void OnMove(int delta)
    {
        var index = _placed.SelectedIndex;
        if (!_toolbar.MoveAt(index, delta)) return;

        RebuildPlaced();
        _placed.SelectedIndex = index + delta;
    }

    protected override void OnReset(bool selectedTabOnly)
    {
        _toolbar.Reset();
        _ribbon.Reset();
        RebuildPlaced();
    }

    protected override void OnImport(string path)
    {
        try
        {
            var imported = RibbonCustomization.Import(path);

            if (imported.QuickAccess is { } toolbar) _toolbar.Replace(toolbar);

            imported.Tree.Reconcile(_shipped);
            _ribbon.Save(imported.Tree, _shipped);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn($"Could not import {path}.", ex);
            return;
        }

        RebuildPlaced();
    }

    protected override void OnExport(string path)
        => RibbonCustomization.Export(path, _ribbon.Load(_shipped), _toolbar.Commands);

    // ---- The three settings under the gallery ----------------------------------------------

    protected override Control BuildGalleryFooter()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        var show = new CheckBox { Content = "Show Quick Access Toolbar", IsChecked = _toolbar.IsVisible };
        Bind(show, ForegroundProperty, "dialog.foreground.brush");
        show.IsCheckedChanged += (_, _) =>
        {
            _toolbar.IsVisible = show.IsChecked == true;
            RaiseEdited();
        };
        stack.Children.Add(show);

        var position = new ComboBox
        {
            ItemsSource = new List<string> { "Above Ribbon", "Below Ribbon" },
            SelectedIndex = _toolbar.Placement == QuickAccessPlacement.BelowRibbon ? 1 : 0,
            MinWidth = 130,
            VerticalAlignment = VerticalAlignment.Center,
        };
        position.SelectionChanged += (_, _) =>
        {
            _toolbar.Placement = position.SelectedIndex == 1
                ? QuickAccessPlacement.BelowRibbon
                : QuickAccessPlacement.AboveRibbon;
            RaiseEdited();
        };

        var positionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var label = new TextBlock { Text = "Toolbar Position", VerticalAlignment = VerticalAlignment.Center };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        positionRow.Children.Add(label);
        positionRow.Children.Add(position);
        stack.Children.Add(positionRow);

        // The reference's third setting hides the labels on a toolbar below the ribbon. Ours
        // has never drawn them — the bar is icons in both placements — so it would be a
        // checkbox that changes nothing, and a control that lies is worse than one that is
        // absent.
        return stack;
    }
}
