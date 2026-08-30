using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Options;
using Mailbox.App.Theming;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Keyboard;
using Mailbox.Core.Ribbon;

namespace Mailbox.App.Views;

/// <summary>
/// The doors onto the customization editors: Customize Ribbon, the Quick Access Toolbar page and
/// Customize Keyboard.
/// </summary>
/// <remarks>
/// <b>Why a script, and why it reads the layout rather than the editor.</b> Every one of these
/// surfaces is a list that reports itself. Pressing Add and then asking the editor's own tree what
/// it holds proves that a list-box gained a row, which is not the claim — the claim is that the
/// ribbon a reader looks at gained a button. So the read-back here is
/// <see cref="App.MailRibbon"/> and each module's own ribbon builder, which is what the shell
/// renders, plus the document on disk. The editor's list is dumped too, for telling "the edit did
/// not land" from "the edit landed and the layout ignored it".
/// <para>
/// The presses go in as a reader's do: the gallery's own <c>ListBox</c>, the four move buttons'
/// own <c>Click</c>, the Reset menu's own <c>MenuItem</c> and the confirmation's own button. None
/// of these could be reached at all before — the editors live inside a modal dialog, one of them
/// behind a button on the other, and <c>MAILBOX_OPTIONS_PRESS</c> only knows about
/// <c>ToggleButton</c>s on a rendered options page.
/// </para>
/// <para>
/// Both doors run the whole flow inside one process for the reason the harness's own rules
/// give: a capture run's settings are a scratch copy, so two runs are two first runs and no
/// cross-run persistence claim can be made through two of them.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once, from the constructor.</summary>
    private void WirePhase12BDoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_RIBBON_EDIT") is { Length: > 0 } edits)
        {
            // The hold is taken on this pass, before the capture's own timer starts counting:
            // taken inside the async method the picture has already been written and the process
            // is on its way out.
            var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
            Opened += (_, _) => _ = PoseRibbonEditAsync(edits, hold);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_KEYS_EDIT") is { Length: > 0 } keys)
        {
            var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
            Opened += (_, _) => _ = PoseKeyboardEditAsync(keys, hold);
        }
    }

    // ---- Customize Ribbon and the Quick Access Toolbar page -------------------------------

    /// <summary>
    /// Opens Options at one of the two customization editors and drives it:
    /// <c>MAILBOX_RIBBON_EDIT=page:ribbon;target:Tags;gallery:Work Offline;add;tree;layout</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>page:&lt;id&gt;</c> — which editor to open on, <c>ribbon</c> or
    /// <c>qat</c>. Defaults to <c>MAILBOX_OPTIONS_PAGE</c>, then to <c>ribbon</c>.</description></item>
    /// <item><description><c>source:&lt;text&gt;</c> — the "Choose commands from" picker.</description></item>
    /// <item><description><c>scope:&lt;text&gt;</c> — the ribbon editor's Main/Tool/All Tabs picker.</description></item>
    /// <item><description><c>position:&lt;text&gt;</c> — the toolbar page's Above/Below picker.</description></item>
    /// <item><description><c>gallery:&lt;text|#n&gt;</c>, <c>target:&lt;text|#n&gt;</c> — pick a row
    /// in the left or the right pane, by what it draws.</description></item>
    /// <item><description><c>add</c>, <c>remove</c>, <c>up</c>, <c>down</c> — the four buttons
    /// between and beside the panes, pressed through their own Click.</description></item>
    /// <item><description><c>press:&lt;caption&gt;</c> — any other button on the page: New Tab, New
    /// Group, Rename…, Modify…, Customize….</description></item>
    /// <item><description><c>menu:&lt;button&gt;:&lt;entry&gt;</c> — a menu button's flyout entry:
    /// <c>menu:Reset:Reset all customizations</c>.</description></item>
    /// <item><description><c>confirm:&lt;caption&gt;</c> — presses a button in whatever dialog is
    /// now on top, which is how Reset's confirmation is answered.</description></item>
    /// <item><description><c>tick:&lt;tab&gt;</c> — the checkbox beside a tab in the tree.</description></item>
    /// <item><description><c>expand:&lt;row&gt;</c> — the chevron on a tree row.</description></item>
    /// <item><description><c>tree</c>, <c>gallerydump</c>, <c>buttons</c> — what the editor is
    /// showing and which of its buttons can act.</description></item>
    /// <item><description><c>layout</c> — the ribbon the shell would render, tab strip, Classic
    /// groups and Simplified clusters. The read-back that matters.</description></item>
    /// <item><description><c>modules</c> — the same for every module's own ribbon, because one
    /// document is applied to all of them.</description></item>
    /// <item><description><c>bar</c> — what the live ribbon control is holding now.</description></item>
    /// <item><description><c>qat</c> — the toolbar's commands and its three settings.</description></item>
    /// <item><description><c>file</c> — the customization document on disk and the toolbar's keys,
    /// which is the only proof an edit persisted rather than merely rendered.</description></item>
    /// <item><description><c>shot</c> — photograph the Options window as the steps left it.</description></item>
    /// <item><description><c>ok</c>, <c>cancel</c> — close the dialog the two ways a reader can.</description></item>
    /// <item><description><c>wait</c> — a beat, for a press whose handler awaits.</description></item>
    /// </list>
    /// </remarks>
    private async Task PoseRibbonEditAsync(string script, IDisposable? hold)
    {
        OptionsWindow? options = null;

        try
        {
            var steps = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var page = steps.FirstOrDefault(s => s.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
                is { } wanted
                ? wanted["page:".Length..].Trim()
                : Environment.GetEnvironmentVariable("MAILBOX_OPTIONS_PAGE") is { Length: > 0 } fromEnv
                    ? fromEnv
                    : "ribbon";

            options = new OptionsWindow(App.Themes, page);
            _ = options.ShowDialog(this);

            // A window that has just appeared is not answering the pointer yet: a press in the
            // pass it opened in raises the handler over a control that has never been laid out.
            await Task.Delay(500);
            options.UpdateLayout();

            if (Editor(options) is null)
            {
                Log.Warn($"Harness: ribbon edit — the “{page}” page draws no customization editor. "
                         + "Only 'ribbon' and 'qat' do.");
            }

            foreach (var step in steps)
            {
                if (!options.IsVisible && step is not ("layout" or "modules" or "bar" or "qat" or "file" or "wait"))
                {
                    Log.Info($"Harness: ribbon edit — the dialog has closed; “{step}” has nowhere to land.");
                    continue;
                }

                await RibbonEditStepAsync(options, step);
                options.UpdateLayout();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the ribbon edit pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    private async Task RibbonEditStepAsync(OptionsWindow options, string step)
    {
        var colon = step.IndexOf(':');
        var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
        var arg = colon > 0 ? step[(colon + 1)..].Trim() : string.Empty;

        switch (verb)
        {
            case "page":
                return;

            case "wait":
                await Task.Delay(400);
                return;

            case "source":
                ChooseCombo(options, "Popular Commands", arg, "the gallery source");
                return;

            case "scope":
                ChooseCombo(options, "Main Tabs", arg, "the tab scope");
                return;

            case "position":
                ChooseCombo(options, "Above Ribbon", arg, "the toolbar position");
                return;

            case "gallery":
                PickInList(options, 0, arg, "the gallery");
                return;

            case "target":
                PickInList(options, 1, arg, "the placed list");
                return;

            case "add":
            case "remove":
            case "up":
            case "down":
                PressMoveButton(options, verb);
                await Task.Delay(200);
                return;

            case "press":
                PressEditorButton(options, arg);
                await Task.Delay(400);
                return;

            case "menu":
                await PressMenuButtonAsync(options, arg);
                return;

            case "confirm":
                await PressInTopWindowAsync(arg);
                return;

            case "tick":
                ToggleTabTick(options, arg);
                await Task.Delay(200);
                return;

            case "expand":
                PressRowChevron(options, arg);
                await Task.Delay(200);
                return;

            case "tree":
                DumpList(options, 1, "the placed pane");
                return;

            case "gallerydump":
                DumpList(options, 0, "the gallery");
                return;

            case "buttons":
                DumpEditorButtons(options);
                return;

            case "layout":
                DescribeLayout("mail", App.MailRibbon());
                return;

            case "modules":
                DescribeLayout("mail", App.MailRibbon());
                DescribeLayout("calendar", CalendarRibbon());
                DescribeLayout("people", PeopleRibbon());
                DescribeLayout("tasks", TasksRibbon());
                DescribeLayout("notes", NotesRibbon());
                DescribeLayout("journal", JournalRibbon());
                DescribeLayout("feeds", FeedsRibbon());
                return;

            case "bar":
                DescribeLayout("the ribbon on screen", _ribbon.Layout);
                return;

            case "qat":
                DescribeQuickAccess();
                return;

            case "file":
                DescribeStoredCustomization();
                return;

            case "shot":
                Log.Info("Harness: ribbon edit — photographing the Options window.");
                CaptureWindow(options);
                return;

            case "ok":
            case "cancel":
                await CloseOptionsAsync(options, accept: verb == "ok");
                return;

            default:
                Log.Warn($"Harness: ribbon edit — “{step}” is not a step this door knows.");
                return;
        }
    }

    private static CustomizationEditor? Editor(OptionsWindow options)
        => options.GetVisualDescendants().OfType<CustomizationEditor>().FirstOrDefault();

    /// <summary>The editor's two panes, in the order the grid places them: gallery, then target.</summary>
    private static List<ListBox> Panes(OptionsWindow options)
        => Editor(options) is { } editor
            ? [.. editor.GetVisualDescendants().OfType<ListBox>()]
            : [];

    private static void ChooseCombo(OptionsWindow options, string marker, string wanted, string what)
    {
        var combo = Editor(options)?.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.ItemsSource?.OfType<string>()
                .Any(i => i.Equals(marker, StringComparison.OrdinalIgnoreCase)) == true);

        if (combo is null)
        {
            Log.Warn($"Harness: ribbon edit — {what} picker is not on this page.");
            return;
        }

        var items = combo.ItemsSource!.OfType<string>().ToList();
        var index = items.FindIndex(i => i.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Log.Warn($"Harness: ribbon edit — {what} has no “{wanted}”. It offers: {string.Join(", ", items)}.");
            return;
        }

        combo.SelectedIndex = index;
        Log.Info($"Harness: ribbon edit — {what} set to “{items[index]}”.");
    }

    /// <summary>Picks a row in one of the two panes, by the text it draws or by its index.</summary>
    private static void PickInList(OptionsWindow options, int pane, string wanted, string what)
    {
        if (Panes(options) is not { } panes || panes.Count <= pane)
        {
            Log.Warn($"Harness: ribbon edit — {what} is not on this page.");
            return;
        }

        var list = panes[pane];

        if (wanted.StartsWith('#') && int.TryParse(wanted[1..], out var index))
        {
            list.SelectedIndex = index;
        }
        else
        {
            var found = -1;
            for (var i = 0; i < list.ItemCount && found < 0; i++)
            {
                if (RowText(list, i).Contains(wanted, StringComparison.OrdinalIgnoreCase)) found = i;
            }

            if (found < 0)
            {
                Log.Warn($"Harness: ribbon edit — {what} holds no row reading “{wanted}”. "
                         + $"It holds {list.ItemCount}: {Rows(list)}.");
                return;
            }

            list.SelectedIndex = found;
        }

        Log.Info($"Harness: ribbon edit — picked row {list.SelectedIndex} of {list.ItemCount} in "
                 + $"{what}: “{RowText(list, list.SelectedIndex)}”.");
    }

    /// <summary>
    /// What a row says, taken from the model rather than from a container.
    /// </summary>
    /// <remarks>
    /// Both panes virtualise, so a row forty down has no container to read text off — and a pick
    /// that could only reach what is on screen would be a pick a reader can make and the harness
    /// cannot.
    /// </remarks>
    private static string RowText(ListBox list, int index)
    {
        if (index < 0) return string.Empty;

        var item = list.ItemsSource?.Cast<object>().ElementAtOrDefault(index);

        return item switch
        {
            GalleryEntry entry => entry.Label,
            RibbonTreeRow row => new string(' ', row.Depth * 2) + RowLabel(row),
            _ => item?.ToString() ?? string.Empty,
        };
    }

    private static string RowLabel(RibbonTreeRow row)
    {
        if (row.Command is { } command)
        {
            return App.Commands.TryGet(command, out var found) ? found.Label : command.Value;
        }

        return row.Group is { } group
            ? group.Label + (group.IsCustom ? " (Custom)" : string.Empty)
            : row.Tab.Label + (row.Tab.IsVisible ? string.Empty : " [unticked]")
                            + (row.Tab.IsCustom ? " (Custom)" : string.Empty);
    }

    private static string Rows(ListBox list)
        => string.Join(" | ", Enumerable.Range(0, list.ItemCount).Select(i => RowText(list, i).Trim()));

    private static void DumpList(OptionsWindow options, int pane, string what)
    {
        if (Panes(options) is not { } panes || panes.Count <= pane)
        {
            Log.Warn($"Harness: ribbon edit — {what} is not on this page.");
            return;
        }

        var list = panes[pane];
        Log.Info($"Harness: ribbon edit — {what} holds {list.ItemCount} row(s), "
                 + $"selected {list.SelectedIndex}:");

        for (var i = 0; i < list.ItemCount; i++)
        {
            Log.Info($"Harness:   [{i:00}] {RowText(list, i)}");
        }
    }

    private static void PressMoveButton(OptionsWindow options, string verb)
    {
        var button = verb switch
        {
            "add" => EditorButton(options, "Add >>"),
            "remove" => EditorButton(options, "<< Remove"),
            "up" => TippedButton(options, "Move Up"),
            _ => TippedButton(options, "Move Down"),
        };

        if (button is null)
        {
            Log.Warn($"Harness: ribbon edit — this page has no {verb} button.");
            return;
        }

        // Whether a reader could have pressed it is the first half of the claim: PressMenuEntry's
        // standing lesson is that a harness pressing a greyed control proves nothing.
        Log.Info($"Harness: ribbon edit — pressing {verb}"
                 + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is greyed, so a reader could not)")}.");

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static Button? EditorButton(OptionsWindow options, string caption)
        => Editor(options)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => Phase12BCaption(b).Replace("_", string.Empty)
                .Contains(caption, StringComparison.OrdinalIgnoreCase));

    private static Button? TippedButton(OptionsWindow options, string tip)
        => Editor(options)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => ToolTip.GetTip(b) as string == tip);

    private static string Phase12BCaption(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        Control control => string.Join(
            string.Empty,
            control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)),
        _ => string.Empty,
    };

    private static void PressEditorButton(OptionsWindow options, string caption)
    {
        if (EditorButton(options, caption) is not { } button)
        {
            Log.Warn($"Harness: ribbon edit — no button reads “{caption}”. This page offers: "
                     + string.Join(", ", Editor(options)?.GetVisualDescendants().OfType<Button>()
                         .Select(Phase12BCaption).Where(t => t.Length > 0) ?? []));
            return;
        }

        Log.Info($"Harness: ribbon edit — pressing “{Phase12BCaption(button)}”"
                 + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is greyed)")}.");

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Opens a menu button's flyout and presses one of its entries.</summary>
    /// <remarks>
    /// Reset and Import/Export are menus rather than buttons, and a popup's size is part of the
    /// claim — so the presenter is measured before an entry is
    /// pressed, which is how two flyouts that presented nothing at all were once caught.
    /// </remarks>
    private static async Task PressMenuButtonAsync(OptionsWindow options, string arg)
    {
        var parts = arg.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            Log.Warn("Harness: ribbon edit — menu wants <button>:<entry>.");
            return;
        }

        if (EditorButton(options, parts[0]) is not { Flyout: MenuFlyout flyout } button)
        {
            Log.Warn($"Harness: ribbon edit — “{parts[0]}” is not a menu button on this page.");
            return;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!flyout.IsOpen) flyout.ShowAt(button);
        await Task.Delay(300);

        Log.Info("Harness: ribbon edit — " + FlyoutProbe.Describe($"the {parts[0]} menu", flyout));

        var entry = flyout.Items.OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string ?? string.Empty)
                .Contains(parts[1], StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Log.Warn($"Harness: ribbon edit — the {parts[0]} menu has no “{parts[1]}”. It holds: "
                     + string.Join(" | ", flyout.Items.OfType<MenuItem>().Select(i => i.Header as string)));
            return;
        }

        Log.Info($"Harness: ribbon edit — pressing “{entry.Header}” in the {parts[0]} menu.");
        entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        flyout.Hide();
        await Task.Delay(400);
    }

    /// <summary>Presses a button in whatever window is on top — the Reset confirmation.</summary>
    private async Task PressInTopWindowAsync(string caption)
    {
        for (var waited = 0; waited < 2000; waited += 50)
        {
            if (Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life
                && life.Windows.LastOrDefault(w => !ReferenceEquals(w, this) && w is not OptionsWindow && w.IsVisible)
                    is { } top)
            {
                top.UpdateLayout();

                var button = top.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => Phase12BCaption(b).Replace("_", string.Empty)
                        .Contains(caption, StringComparison.OrdinalIgnoreCase));

                if (button is null)
                {
                    Log.Warn($"Harness: ribbon edit — {top.GetType().Name} has no “{caption}”. It has: "
                             + string.Join(", ", top.GetVisualDescendants().OfType<Button>()
                                 .Select(Phase12BCaption).Where(t => t.Length > 0)));
                    return;
                }

                Log.Info($"Harness: ribbon edit — pressing “{caption}” in {top.GetType().Name}.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(400);
                return;
            }

            await Task.Delay(50);
        }

        Log.Warn($"Harness: ribbon edit — nothing opened for “{caption}” to be pressed in.");
    }

    /// <summary>Ticks or unticks the checkbox beside a tab in the tree.</summary>
    private static void ToggleTabTick(OptionsWindow options, string wanted)
    {
        if (Panes(options) is not { Count: > 1 } panes)
        {
            Log.Warn("Harness: ribbon edit — this page has no tree to tick.");
            return;
        }

        var list = panes[1];
        list.UpdateLayout();

        for (var i = 0; i < list.ItemCount; i++)
        {
            if (list.ItemsSource?.Cast<object>().ElementAtOrDefault(i) is not RibbonTreeRow { IsTab: true } row) continue;
            if (!row.Tab.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            if (list.ContainerFromIndex(i)?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault() is not { } box)
            {
                Log.Warn($"Harness: ribbon edit — the row for “{row.Tab.Label}” has no tick to press "
                         + "(it is not realised).");
                return;
            }

            box.IsChecked = box.IsChecked != true;
            Log.Info($"Harness: ribbon edit — “{row.Tab.Label}” is now {(box.IsChecked == true ? "ticked" : "unticked")}.");
            return;
        }

        Log.Warn($"Harness: ribbon edit — the tree carries no tab reading “{wanted}”.");
    }

    private static void PressRowChevron(OptionsWindow options, string wanted)
    {
        if (Panes(options) is not { Count: > 1 } panes)
        {
            Log.Warn("Harness: ribbon edit — this page has no tree to expand.");
            return;
        }

        var list = panes[1];
        list.UpdateLayout();

        for (var i = 0; i < list.ItemCount; i++)
        {
            if (!RowText(list, i).Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            if (list.ContainerFromIndex(i)?.GetVisualDescendants().OfType<Button>().FirstOrDefault() is not { } chevron)
            {
                Log.Warn($"Harness: ribbon edit — row {i} has no chevron.");
                return;
            }

            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Log.Info($"Harness: ribbon edit — pressed the chevron on “{RowText(list, i).Trim()}”.");
            return;
        }

        Log.Warn($"Harness: ribbon edit — no row reads “{wanted}”.");
    }

    private static void DumpEditorButtons(OptionsWindow options)
    {
        if (Editor(options) is not { } editor)
        {
            Log.Warn("Harness: ribbon edit — this page draws no editor.");
            return;
        }

        foreach (var button in editor.GetVisualDescendants().OfType<Button>())
        {
            var caption = Phase12BCaption(button);
            var tip = ToolTip.GetTip(button) as string;
            if (caption.Length == 0 && tip is null) continue;

            Log.Info($"Harness: ribbon edit — button “{(caption.Length > 0 ? caption : tip)}” is "
                     + $"{(button.IsEffectivelyEnabled ? "black" : "greyed")}, "
                     + $"{button.Bounds.Width:0}x{button.Bounds.Height:0}.");
        }
    }

    /// <summary>The layout the shell would render: the strip, the Classic groups, the Simplified clusters.</summary>
    private static void DescribeLayout(string what, RibbonLayout layout)
    {
        Log.Info($"Harness: layout {what} — {layout.Tabs.Count} tab(s), "
                 + $"{layout.Simplified.Count} simplified bar(s), user-modified={layout.IsUserModified}.");

        foreach (var tab in layout.Tabs)
        {
            var classic = tab.Groups.Count == 0
                ? "no classic groups"
                : string.Join(", ", tab.Groups.Select(g => $"{g.Label}[{g.Items.Count}]"));

            var simplified = layout.Simplified.TryGetValue(tab.Id, out var bar)
                ? bar.Groups.Count == 0
                    ? "empty simplified bar"
                    : string.Join(", ", bar.Groups.Select(g => $"{g.Label}[{g.Items.Count}]"))
                : "no simplified bar";

            Log.Info($"Harness:   {what} tab “{tab.Label}” ({tab.Id})"
                     + $"{(tab.IsBackstage ? " backstage" : string.Empty)}"
                     + $"{(tab.ClassicOnly ? " classic-only" : string.Empty)} — classic: {classic}");
            Log.Info($"Harness:     simplified: {simplified}");
        }
    }

    private static void DescribeQuickAccess()
    {
        var toolbar = App.QuickAccess;

        Log.Info($"Harness: the toolbar holds {toolbar.Commands.Count}: "
                 + string.Join(" | ", toolbar.Commands.Select(c => c.Value)));

        Log.Info($"Harness: the toolbar is {(toolbar.IsVisible ? "shown" : "hidden")}, "
                 + $"{(toolbar.Placement == QuickAccessPlacement.BelowRibbon ? "below" : "above")} the ribbon, "
                 + $"labels {(toolbar.ShowLabels ? "on" : "off")}.");
    }

    /// <summary>What is on disk: the customization document, and the toolbar's settings keys.</summary>
    /// <remarks>
    /// The half of a customization claim a single process cannot otherwise make. The ribbon lives
    /// in a file of its own beside the settings; the toolbar lives in the settings, which under a
    /// capture run is the scratch copy — so this reads both from where the application really
    /// wrote them rather than from the objects that wrote them.
    /// </remarks>
    private static void DescribeStoredCustomization()
    {
        var path = RibbonCustomization.DefaultPath();

        if (!File.Exists(path))
        {
            Log.Info($"Harness: no customization document at {path} — the ribbon is the shipped one.");
        }
        else
        {
            var text = File.ReadAllText(path);
            Log.Info($"Harness: {path} is {text.Length} bytes.");

            var stored = RibbonCustomization.Import(path);
            foreach (var tab in stored.Tree.Tabs)
            {
                Log.Info($"Harness:   stored tab “{tab.Label}” ({tab.Id})"
                         + $"{(tab.IsVisible ? string.Empty : " [unticked]")}"
                         + $"{(tab.IsCustom ? " (Custom)" : string.Empty)} — "
                         + string.Join(", ", tab.Groups.Select(g => $"{g.Label}[{g.Commands.Count}]")));
                Log.Info("Harness:     stored classic: "
                         + (tab.ClassicGroups is { } classic
                             ? string.Join(", ", classic.Select(g => $"{g.Label}[{g.Commands.Count}]"))
                             : "(not recorded — shipped)"));
            }
        }

        foreach (var key in new[]
                 {
                     QuickAccessLayout.CommandsKey, QuickAccessLayout.PlacementKey,
                     QuickAccessLayout.VisibleKey, QuickAccessLayout.LabelsKey,
                     QuickAccessLayout.OverridesKey, KeyMap.OverridesKey,
                 })
        {
            Log.Info($"Harness:   stored {key} = {App.Settings.Stored(key) ?? "(unset)"}");
        }
    }

    private async Task CloseOptionsAsync(OptionsWindow options, bool accept)
    {
        options.UpdateLayout();

        var caption = accept ? "OK" : "Cancel";
        var button = options.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => Phase12BCaption(b).Replace("_", string.Empty)
                .Equals(caption, StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            Log.Warn($"Harness: ribbon edit — the Options window has no {caption} button.");
            return;
        }

        Log.Info($"Harness: ribbon edit — pressing {caption}.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(500);

        // What ShowOptions does when the dialog comes back, which is where a customization
        // reaches the screen. The pose opened the dialog itself, so it has to do this too, or a
        // "bar" step after an OK would report the ribbon the run started with.
        if (!options.CustomizationChanged) return;

        _ribbon.Layout = App.MailRibbon();

        if (DataContext is ViewModels.ShellViewModel shell)
        {
            shell.RebuildQuickAccess();
            WireToolbarCommands(shell);
            _ribbon.IsQuickAccessVisible = App.QuickAccess.IsVisible;
        }

        Log.Info("Harness: ribbon edit — the shell has taken the customization up.");
    }

    // ---- Customize Keyboard ---------------------------------------------------------------

    /// <summary>
    /// Opens Customize Keyboard and drives it:
    /// <c>MAILBOX_KEYS_EDIT=command:Reply All;chord:Ctrl+Shift+9;assigned;assign;dump:Reply All;key:Ctrl+Shift+9</c>.
    /// </summary>
    /// <remarks>
    /// The dialog is reachable only from a button on Customize Ribbon, so nothing could open it,
    /// and a rebind proven in its own list is a rebind proven nowhere: <c>key:</c> presses the
    /// chord through the window's real input route afterwards, in the same process, which is the
    /// only route that says whether the map the shell reads has moved.
    /// <list type="bullet">
    /// <item><description><c>category:&lt;text&gt;</c> — the left-hand list.</description></item>
    /// <item><description><c>command:&lt;text&gt;</c> — the command list.</description></item>
    /// <item><description><c>chord:&lt;Ctrl+Shift+9&gt;</c> — typed into the "Press new shortcut
    /// key" box through its own key handler, which is what a reader's keystroke reaches.</description></item>
    /// <item><description><c>assigned</c> — what the dialog says the chord is currently held by,
    /// which is its conflict detection.</description></item>
    /// <item><description><c>assign</c>, <c>remove</c>, <c>resetall</c> — the three buttons.</description></item>
    /// <item><description><c>dump:&lt;text&gt;</c> — what the map holds for that command now.</description></item>
    /// <item><description><c>key:&lt;chord&gt;</c> — presses the chord at the shell, through the
    /// real input path.</description></item>
    /// <item><description><c>stored</c> — the overrides as they were written.</description></item>
    /// <item><description><c>shot</c>, <c>close</c>, <c>wait</c>.</description></item>
    /// </list>
    /// </remarks>
    private async Task PoseKeyboardEditAsync(string script, IDisposable? hold)
    {
        CustomizeKeyboardDialog? dialog = null;

        try
        {
            dialog = new CustomizeKeyboardDialog(App.Keys, App.Commands);
            _ = dialog.ShowDialog(this);
            await Task.Delay(500);
            dialog.UpdateLayout();

            foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await KeyboardEditStepAsync(dialog, step);
                if (dialog.IsVisible) dialog.UpdateLayout();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the keyboard edit pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    private async Task KeyboardEditStepAsync(CustomizeKeyboardDialog dialog, string step)
    {
        var colon = step.IndexOf(':');
        var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
        var arg = colon > 0 ? step[(colon + 1)..].Trim() : string.Empty;

        var lists = dialog.GetVisualDescendants().OfType<ListBox>().ToList();

        switch (verb)
        {
            case "wait":
                await Task.Delay(400);
                return;

            case "category":
            {
                if (lists.Count < 1) return;
                var categories = lists[0].ItemsSource?.OfType<string>().ToList() ?? [];
                var index = categories.FindIndex(c => c.Contains(arg, StringComparison.OrdinalIgnoreCase));

                if (index < 0)
                {
                    Log.Warn($"Harness: keyboard — no category reads “{arg}”. It offers: "
                             + string.Join(" | ", categories) + ".");
                    return;
                }

                lists[0].SelectedIndex = index;
                await Task.Delay(150);
                Log.Info($"Harness: keyboard — category “{lists[0].SelectedItem}”, "
                         + $"{lists[1].ItemCount} command(s) under it.");
                return;
            }

            case "command":
            {
                if (lists.Count < 2) return;
                var commands = lists[1].ItemsSource?.OfType<MailboxCommand>().ToList() ?? [];

                // By id when the argument carries a dot: eight commands read "Forward", and a
                // pose that can only say a label cannot say which one it means.
                var found = arg.Contains('.')
                    ? commands.FirstOrDefault(c => c.Id.Value.Equals(arg, StringComparison.OrdinalIgnoreCase))
                    : commands.FirstOrDefault(c => c.Label.Equals(arg, StringComparison.OrdinalIgnoreCase))
                      ?? commands.FirstOrDefault(c => c.Label.Contains(arg, StringComparison.OrdinalIgnoreCase));

                if (found is null)
                {
                    Log.Warn($"Harness: keyboard — the list holds no command reading “{arg}”.");
                    return;
                }

                lists[1].SelectedItem = found;
                await Task.Delay(150);
                Log.Info($"Harness: keyboard — chose “{found.Label}” ({found.Id.Value}), "
                         + $"whose key is {App.Keys.GestureFor(found.Id)?.Display ?? "(none)"}.");
                return;
            }

            case "chord":
            {
                if (Chord.Parse(arg) is not { } chord
                    || !Enum.TryParse<Key>(chord.Key, out var key))
                {
                    Log.Warn($"Harness: keyboard — “{arg}” is not a chord.");
                    return;
                }

                if (dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } box)
                {
                    Log.Warn("Harness: keyboard — the dialog draws no shortcut box.");
                    return;
                }

                // Into the box's own tunnelled handler, which is the one a reader's keystroke
                // reaches. Assigning Text instead would set the caption and leave the dialog's
                // idea of the pressed chord null, so Assign would do nothing and read as a
                // button that is not wired.
                box.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = box,
                    Key = key,
                    KeyModifiers = Keystroke.Modifiers(chord.Modifiers),
                });

                await Task.Delay(150);
                Log.Info($"Harness: keyboard — the box reads “{box.Text}”.");
                return;
            }

            case "assigned":
            {
                var lines = dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty)
                    .Where(t => t.StartsWith("Currently assigned", StringComparison.Ordinal))
                    .ToList();

                Log.Info($"Harness: keyboard — {(lines.Count == 0 ? "the dialog says nothing about a conflict" : string.Join(" / ", lines))}.");
                return;
            }

            case "assign":
            case "remove":
            case "resetall":
            {
                var caption = verb switch { "assign" => "Assign", "remove" => "Remove", _ => "Reset All" };

                if (dialog.GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(b => Phase12BCaption(b).StartsWith(caption, StringComparison.OrdinalIgnoreCase))
                    is not { } button)
                {
                    Log.Warn($"Harness: keyboard — no {caption} button.");
                    return;
                }

                Log.Info($"Harness: keyboard — pressing {caption}"
                         + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is greyed, so a reader could not)")}.");

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(300);
                return;
            }

            case "confirm":
                await PressInTopWindowAsync(arg);
                return;

            case "dump":
            {
                var command = App.Commands.All.FirstOrDefault(c => c.Label.Equals(arg, StringComparison.OrdinalIgnoreCase))
                              ?? App.Commands.All.FirstOrDefault(c => c.Id.Value.Equals(arg, StringComparison.OrdinalIgnoreCase));

                if (command is null)
                {
                    Log.Warn($"Harness: keyboard — the catalogue holds no “{arg}”.");
                    return;
                }

                Log.Info($"Harness: keyboard — the map gives “{command.Label}” "
                         + $"{App.Keys.GestureFor(command.Id)?.Display ?? "(none)"}"
                         + $"{(App.Keys.IsCustomised(command.Id) ? ", customised" : ", as shipped")}"
                         + $"; also {string.Join(", ", App.Keys.AlsoGesturesFor(command.Id).Select(g => g.Display))}.");
                return;
            }

            case "holder":
            {
                if (Chord.Parse(arg) is not { } chord)
                {
                    Log.Warn($"Harness: keyboard — “{arg}” is not a chord.");
                    return;
                }

                var holder = App.Keys.CommandFor(chord);
                Log.Info($"Harness: keyboard — {chord.Display} is held by "
                         + $"{(holder is { } id ? id.Value : "nothing")}.");
                return;
            }

            case "key":
                PressChord(arg);
                await Task.Delay(300);
                return;

            case "stored":
                Log.Info($"Harness: keyboard — stored {KeyMap.OverridesKey} = "
                         + (App.Settings.Stored(KeyMap.OverridesKey) ?? "(unset)"));
                return;

            case "shot":
                Log.Info("Harness: keyboard — photographing the dialog.");
                CaptureWindow(dialog);
                return;

            case "close":
                dialog.Close();
                await Task.Delay(200);
                return;

            default:
                Log.Warn($"Harness: keyboard — “{step}” is not a step this door knows.");
                return;
        }
    }

    /// <summary>
    /// Photographs one window and stops the run, which is what every dialog pose ends with.
    /// </summary>
    private static void CaptureWindow(Window window)
    {
        if (WindowCapture.RequestedPath is not { } path) return;

        window.UpdateLayout();
        WindowCapture.Capture(window, path, WindowCapture.Scale);
        Console.WriteLine($"Captured {path}");
    }
}
