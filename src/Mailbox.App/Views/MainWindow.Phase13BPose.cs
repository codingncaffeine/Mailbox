using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The doors onto a system dialog's tabbed pages, onto its report list, and onto its caption.
/// </summary>
/// <remarks>
/// <b>Why a caption door was needed at all.</b> <c>MAILBOX_CAPTION</c> reaches the shell's own
/// caption buttons and nothing else, so every window that is not the shell — every dialog, and
/// the whole system-dialog family with it — had its hover and held states carried as inference
/// from the token file. Three token families define a hover and a held pair
/// (<c>titlebar.caption.*</c>, <c>dialog.caption.*</c>, <c>systemdialog.caption.*</c>) and only
/// the first of them could ever be photographed. <c>MAILBOX_DIALOG_CAPTION</c> closes that: it
/// finds the caption of whichever window a pose has opened, puts a named button into its hovered
/// or held state, and holds the capture open until it has, so the picture is of the state rather
/// than of the button at rest.
/// <para>
/// <b>Why a page door was needed.</b> <c>MAILBOX_ACCOUNTS_ACTION</c> reaches the Email and Data
/// Files tabs through fields the dialog happens to keep; the RSS Feeds, Published Calendars and
/// Address Books toolbars are built out of locals inside their own tab methods and nothing
/// outside could reach one. Every button on every tab is the standing rule for this family, and
/// three of its six tabs had no way of being pressed at all. This door presses by what a button
/// reads, through a real pointer press at the button's own middle — so a greyed button is a
/// press that does nothing, exactly as it is for a reader, rather than a <c>Click</c> raised over
/// the top of the enabled state.
/// </para>
/// <para>
/// Both doors take a <see cref="WindowCapture.Hold"/> on the dispatcher pass that wires them, so
/// the capture waits for the steps instead of photographing the dialog as it opened. And both
/// work on the <em>newest</em> window: a press that opens a confirmation over the dialog means
/// the confirmation for the step after it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once, from the constructor.</summary>
    private void WirePhase13BDoors()
    {
        var page = Environment.GetEnvironmentVariable("MAILBOX_SYSDIALOG");
        var caption = Environment.GetEnvironmentVariable("MAILBOX_DIALOG_CAPTION");
        var store = Environment.GetEnvironmentVariable("MAILBOX_SYSDIALOG_REPORT");

        if (page is not { Length: > 0 } && caption is not { Length: > 0 } && store is not { Length: > 0 }) return;

        // Taken here rather than inside the async pass, for the reason lane 4A's door records:
        // the capture's own timer starts the moment the peek asks for a dialog, and a hold taken
        // later is a hold taken after the picture.
        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
        Opened += (_, _) => _ = PoseSystemDialogAsync(page, caption, store, hold);
    }

    private async Task PoseSystemDialogAsync(string? page, string? caption, string? store, IDisposable? hold)
    {
        try
        {
            if (page is { Length: > 0 }) await RunSystemDialogStepsAsync(page);

            // After the page steps: the caption state is what the picture is of, and a page step
            // that opened a second window would otherwise leave the pose holding the wrong one.
            if (caption is { Length: > 0 }) await RunCaptionStepsAsync(caption);

            if (store is { Length: > 0 }) ReportSystemDialogStore();
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the system-dialog pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    // ---- The page ------------------------------------------------------------------------

    /// <summary>
    /// Steps over whichever system dialog is open, in order:
    /// <c>MAILBOX_SYSDIALOG=tab:RSS Feeds;row:0;buttons;press:Change...</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>tab:&lt;n|name&gt;</c> — the <see cref="ClassicTabControl"/>'s own
    /// selection, which is the line its strip runs on a click.</description></item>
    /// <item><description><c>row:&lt;n|text&gt;</c> — a row of the page's
    /// <see cref="ClassicListView"/>, by index or by what a cell reads.</description></item>
    /// <item><description><c>press:&lt;caption&gt;</c> — a real pointer press at the middle of the
    /// button whose label carries that text, with the button's enabled state logged beside
    /// it.</description></item>
    /// <item><description><c>type:&lt;text&gt;</c> — into the window's first text box, through its
    /// own <c>Text</c> so what the dialog listens to fires.</description></item>
    /// <item><description><c>buttons</c> — every button the page draws, with whether it is greyed:
    /// the read-back for "every button on every tab acts".</description></item>
    /// <item><description><c>rows</c> — what the page's list holds and which row is
    /// chosen.</description></item>
    /// <item><description><c>text</c> — the last lines the window draws, for a dialog whose answer
    /// is a sentence.</description></item>
    /// <item><description><c>paint</c> — every distinct ground and ink the window painted, for the
    /// claim that this family stays light in all four themes.</description></item>
    /// <item><description><c>windows</c> — every window open, which is how a press that raised a
    /// confirmation is told from one that did nothing.</description></item>
    /// <item><description><c>shot</c> — photograph the newest window rather than the
    /// shell.</description></item>
    /// <item><description><c>wait[:ms]</c> — a beat for an asynchronous press to land.</description></item>
    /// <item><description><c>seed:&lt;kind:name=value&gt;</c> — a precondition a tab cannot make
    /// for itself: a feed, a published calendar, a signature.</description></item>
    /// <item><description><c>store</c> — what the family's tabs have written, read from the
    /// repositories rather than off the lists that were just looked at.</description></item>
    /// </list>
    /// </remarks>
    private async Task RunSystemDialogStepsAsync(string spec)
    {
        var settled = false;

        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.IndexOf(':');
            var verb = (colon > 0 ? raw[..colon] : raw).Trim().ToLowerInvariant();
            var argument = colon > 0 ? raw[(colon + 1)..].Trim() : string.Empty;

            // The steps that want no window, taken first: a precondition is made before the tab
            // that reads it is opened, and a store read-back outlives the dialog that closed.
            if (verb == "wait")
            {
                await Task.Delay(int.TryParse(argument, out var ms) ? ms : 400);
                continue;
            }

            if (verb == "seed")
            {
                SeedPrecondition(argument);
                continue;
            }

            if (verb == "store")
            {
                ReportSystemDialogStore();
                continue;
            }

            if (await NewestWindowAsync() is not { } dialog)
            {
                Log.Warn($"Harness: sysdialog — no window is open for “{raw}”.");
                return;
            }

            // Once, before the first step: a window in the pass it opened in is not yet answering
            // the pointer, and a press there raises a handler that does nothing — which reads
            // exactly like a button nobody wired.
            if (!settled)
            {
                settled = true;
                await Task.Delay(400);
            }

            dialog.UpdateLayout();

            switch (verb)
            {
                case "tab":
                    PoseTab(dialog, argument);
                    await Task.Delay(150);
                    break;

                case "row":
                    PoseRow(dialog, argument);
                    await Task.Delay(150);
                    break;

                case "press":
                    PressByCaption(dialog, argument);
                    await Task.Delay(350);
                    break;

                case "type":
                    TypeIntoPage(dialog, argument);
                    await Task.Delay(150);
                    break;

                case "buttons":
                    ReportButtons(dialog);
                    break;

                case "rows":
                    ReportRows(dialog);
                    break;

                case "text":
                    ReportText(dialog);
                    break;

                case "paint":
                    ReportPaint(dialog);
                    break;

                case "windows":
                    ReportWindows();
                    break;

                case "shot":
                    Log.Info($"Harness: sysdialog — photographing {dialog.GetType().Name}.");
                    CaptureNextWindow();
                    break;

                default:
                    Log.Warn($"Harness: sysdialog — “{raw}” is not a step. Say tab, row, press, "
                             + "type, buttons, rows, text, paint, windows, shot, wait, seed or store.");
                    break;
            }
        }
    }

    /// <summary>The newest window that is not the shell, once one is up — or null after two seconds.</summary>
    private async Task<Window?> NewestWindowAsync()
    {
        for (var waited = 0; waited < 2000; waited += 50)
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                && life.Windows.LastOrDefault(w => !ReferenceEquals(w, this) && w.IsVisible) is { } window)
            {
                return window;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private static ClassicTabControl? TabsOf(Window dialog)
        => dialog.GetVisualDescendants().OfType<ClassicTabControl>().FirstOrDefault();

    /// <summary>
    /// The page's report list. The <em>last</em> one in the tree, because only the selected tab's
    /// page is in it — a page swapped out is not a visual child of anything.
    /// </summary>
    private static ClassicListView? ListOfPage(Window dialog)
        => dialog.GetVisualDescendants().OfType<ClassicListView>().FirstOrDefault();

    private static void PoseTab(Window dialog, string wanted)
    {
        if (TabsOf(dialog) is not { } tabs)
        {
            Log.Warn($"Harness: sysdialog — {dialog.GetType().Name} has no tab strip.");
            return;
        }

        var headers = tabs.Headers;
        var index = int.TryParse(wanted, out var n)
            ? n
            : headers.ToList().FindIndex(h => h.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index >= headers.Count)
        {
            Log.Warn($"Harness: sysdialog — no tab reads “{wanted}”. It has: {string.Join(", ", headers)}.");
            return;
        }

        tabs.SelectedIndex = index;
        dialog.UpdateLayout();
        Log.Info($"Harness: sysdialog — tab {index} “{headers[index]}” of {headers.Count} "
                 + $"[{string.Join(", ", headers)}].");
    }

    private static void PoseRow(Window dialog, string wanted)
    {
        if (ListOfPage(dialog) is not { } list)
        {
            Log.Warn($"Harness: sysdialog — this page draws no list.");
            return;
        }

        var index = int.TryParse(wanted, out var n)
            ? n
            : list.Rows.ToList().FindIndex(
                r => r.Cells.Any(c => c.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

        if (index < 0 || index >= list.Rows.Count)
        {
            Log.Warn($"Harness: sysdialog — no row reads “{wanted}” among {list.Rows.Count}.");
            return;
        }

        list.SelectedIndex = index;
        Log.Info($"Harness: sysdialog — row {index} “{string.Join(" | ", list.Rows[index].Cells)}”.");
    }

    /// <summary>
    /// Presses the button whose label carries this text, at the middle of the button, with a real
    /// pointer.
    /// </summary>
    /// <remarks>
    /// A pointer rather than <c>RaiseEvent(ClickEvent)</c> deliberately: a greyed button does not
    /// answer a pointer and does answer a raised Click, so the cheaper route would report every
    /// disabled button in the family as working. What the log carries is the enabled state at the
    /// moment of the press, so a press that did nothing is told from a button that is not wired.
    /// </remarks>
    private static void PressByCaption(Window dialog, string wanted)
    {
        var buttons = ButtonsOf(dialog);

        // By caption first, then by tooltip: the account list's reorder arrows carry a glyph
        // and a tip and no text, and nothing could press them at all.
        var button = buttons.FirstOrDefault(b => Reads(CaptionOf(b), wanted))
                     ?? buttons.FirstOrDefault(b => ToolTip.GetTip(b) is string tip && Reads(tip, wanted));

        if (button is null)
        {
            Log.Warn($"Harness: sysdialog — nothing reads “{wanted}”. The page offers: "
                     + $"{string.Join(", ", buttons.Select(b => CaptionOf(b) is { Length: > 0 } c ? c : ToolTip.GetTip(b) as string ?? string.Empty).Where(t => t.Length > 0))}.");
            return;
        }

        var enabled = button.IsEffectivelyEnabled;
        Log.Info($"Harness: sysdialog — pressing “{CaptionOf(button)}” in {dialog.GetType().Name} "
                 + $"({(enabled ? "black" : "greyed")}).");

        Press(button, new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));
    }

    /// <summary>
    /// Every button the dialog offers a reader, which is not every <see cref="Button"/> in it.
    /// </summary>
    /// <remarks>
    /// A <see cref="RepeatButton"/> is a Button, and a <see cref="ClassicListView"/> keeps its
    /// scroll gutter down whether or not it has anything to scroll — so the first read-back of
    /// this family counted the scrollbar's two arrows as toolbar buttons on all six tabs, and a
    /// tab whose toolbar has two buttons reported four. The caption's own buttons are left out
    /// for the same reason: they are the frame, and <c>MAILBOX_DIALOG_CAPTION</c> is what asks
    /// about them.
    /// </remarks>
    private static List<Button> ButtonsOf(Window dialog)
        => [.. dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not RepeatButton)
            .Where(b => !b.Classes.Contains("caption") && !b.Classes.Contains("caption-close"))];

    /// <summary>What a button reads, whether its content is a word or a panel with an icon in it.</summary>
    private static string CaptionOf(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        Control control => control.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static bool Reads(string caption, string wanted)
    {
        var trimmed = caption.Replace("_", string.Empty);
        return trimmed.Equals(wanted, StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Types into the window's first text box — the one field a dialog of this family has when it
    /// asks for one: the subscription's address, a data file's name.
    /// </summary>
    /// <remarks>
    /// Assigned through the box's own <see cref="TextBox.Text"/> so the <c>TextChanged</c> the
    /// dialog listens to fires: the New Internet Calendar Subscription dialog keeps its Add button
    /// greyed until something has been typed, and a value put anywhere else leaves it greyed and
    /// reads as a button that is not wired.
    /// </remarks>
    private static void TypeIntoPage(Window dialog, string text)
    {
        if (dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } box)
        {
            Log.Warn($"Harness: sysdialog — {dialog.GetType().Name} draws no text box.");
            return;
        }

        box.Text = text;
        Log.Info($"Harness: sysdialog — typed “{text}” into {dialog.GetType().Name}'s first box.");
    }

    /// <summary>
    /// Makes a precondition a tab needs and cannot make for itself.
    /// </summary>
    /// <remarks>
    /// <b>A precondition, never evidence.</b> The Published Calendars tab deliberately has no
    /// New… — the reference has none, because a calendar is published from the calendar — so with
    /// an empty table its Change… and Remove are greyed and nothing on the tab can be pressed at
    /// all. The same is true of the RSS tab against a mailbox with no subscriptions. What is
    /// written here is the state a reader would have arrived with; what is read back afterwards
    /// is what the tab's own buttons did to it.
    /// </remarks>
    private static void SeedPrecondition(string spec)
    {
        var (kind, rest) = spec.Split(':', 2) is [var head, var tail] ? (head.Trim().ToLowerInvariant(), tail) : (spec, string.Empty);
        var (name, value) = rest.Split('=', 2) is [var a, var b] ? (a.Trim(), b.Trim()) : (rest.Trim(), string.Empty);

        switch (kind)
        {
            case "feed":
                var feed = App.Feeds.Add(value.Length > 0 ? value : $"https://example.com/{name}.xml", name, "Technology");
                Log.Info($"Harness: sysdialog seeded feed — “{feed.Name}” {feed.Url}.");
                break;

            case "published":
                if (App.Pim.Collections(CollectionKind.Events).FirstOrDefault() is not { } calendar)
                {
                    Log.Warn("Harness: sysdialog seed — there is no calendar to publish.");
                    break;
                }

                App.Published.Set(calendar.Id, value.Length > 0 ? value : "https://example.com/calendar.ics", calendar.DisplayName);
                Log.Info($"Harness: sysdialog seeded published — collection {calendar.Id} "
                         + $"“{calendar.DisplayName}” → {App.Published.For(calendar.Id)?.Url}.");
                break;

            case "signature":
                App.Signatures.Save(new Mailbox.Core.Settings.Signature
                {
                    Name = name,
                    Text = value.Length > 0 ? value : $"{name} line",
                    Html = SignatureEditor.AsHtml(value.Length > 0 ? value : $"{name} line"),
                });
                Log.Info($"Harness: sysdialog seeded signature — “{name}”.");
                break;

            default:
                Log.Warn($"Harness: sysdialog seed — “{spec}” is not one of feed, published or signature.");
                break;
        }
    }

    private static void ReportButtons(Window dialog)
    {
        var tab = TabsOf(dialog) is { } tabs && tabs.SelectedIndex >= 0
            ? tabs.Headers[tabs.SelectedIndex]
            : dialog.Title ?? dialog.GetType().Name;

        var buttons = ButtonsOf(dialog)
            .Select(b => (Caption: CaptionOf(b), b.IsEffectivelyEnabled, b.Bounds))
            .Where(b => b.Bounds.Width > 0)
            .ToList();

        Log.Info($"Harness: sysdialog buttons on “{tab}” — {buttons.Count}: "
                 + string.Join(", ", buttons.Select(
                     b => $"“{(b.Caption.Length > 0 ? b.Caption : "(icon only)")}” "
                          + $"{(b.IsEffectivelyEnabled ? "black" : "greyed")} "
                          + $"{b.Bounds.Width:0}x{b.Bounds.Height:0}")));
    }

    private static void ReportRows(Window dialog)
    {
        if (ListOfPage(dialog) is not { } list)
        {
            Log.Info("Harness: sysdialog rows — this page draws no list.");
            return;
        }

        Log.Info($"Harness: sysdialog rows — {list.Rows.Count}, row {list.SelectedIndex} chosen; "
                 + $"columns [{string.Join(", ", list.Columns.Select(c => $"{c.Header} {c.Width:0}"))}].");

        foreach (var row in list.Rows)
        {
            Log.Info($"Harness: sysdialog row — “{string.Join(" | ", row.Cells)}”"
                     + (row.Marked ? " (marked)" : string.Empty)
                     + (row.Checked is { } ticked ? ticked ? " (ticked)" : " (unticked)" : string.Empty));
        }
    }

    private static void ReportText(Window dialog)
    {
        var lines = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .TakeLast(8);

        Log.Info($"Harness: sysdialog text — {dialog.GetType().Name} “{dialog.Title}” reads "
                 + $"“{string.Join(" / ", lines)}”.");
    }

    /// <summary>
    /// Every distinct ground the window painted and every distinct ink over them, most-carried
    /// first. The read-back for this family's standing claim — light in all four themes,
    /// <c>systemdialog.*</c> only: posed in Dark Gray or Black, the grounds here have to be the
    /// family's own values, not the theme's chrome.
    /// </summary>
    /// <remarks>
    /// The resolved brush rather than the token, on purpose: a wrong token that resolves to the
    /// right colour in three themes is exactly the drift this family keeps shipping, and only the
    /// painted value tells it from a right one. Grouping is the log's version of the capture
    /// rule about modal colours — a surface is its most-carried colour, not a point sample.
    /// </remarks>
    private static void ReportPaint(Window dialog)
    {
        static string Hex(IBrush? brush)
            => brush is ISolidColorBrush solid ? solid.Color.ToString() : brush?.ToString() ?? "(unset)";

        Log.Info($"Harness: sysdialog paint — {dialog.GetType().Name} “{dialog.Title}” ground "
                 + $"{Hex(dialog.Background)}.");

        var grounds = dialog.GetVisualDescendants()
            .Select(visual => (Visual: visual, Brush: visual switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                TemplatedControl control => control.Background,
                _ => null,
            }))
            .Where(v => v.Brush is not null)
            .GroupBy(v => Hex(v.Brush))
            .OrderByDescending(group => group.Count());

        foreach (var ground in grounds)
        {
            Log.Info($"Harness: sysdialog paint — ground {ground.Key} × {ground.Count()} "
                     + $"[{string.Join(", ", ground.Select(v => v.Visual.GetType().Name).Distinct().Take(4))}].");
        }

        var inks = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is { Length: > 0 })
            .GroupBy(t => Hex(t.Foreground))
            .OrderByDescending(group => group.Count());

        foreach (var ink in inks)
        {
            Log.Info($"Harness: sysdialog paint — ink {ink.Key} × {ink.Count()}, first "
                     + $"“{ink.First().Text}”.");
        }
    }

    private static void ReportWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life)
        {
            return;
        }

        Log.Info("Harness: sysdialog windows — "
                 + string.Join(", ", life.Windows.Select(
                     w => $"{w.GetType().Name} “{w.Title}” {(w.IsVisible ? "shown" : "hidden")}")));
    }

    // ---- The caption ---------------------------------------------------------------------

    /// <summary>
    /// <c>MAILBOX_DIALOG_CAPTION=hold:close</c> paints a dialog caption button's held state;
    /// <c>hover:</c> its hovered one, <c>press:</c> clicks it, and a bare <c>report</c> says what
    /// the caption carries. Several separated by commas run in order.
    /// </summary>
    /// <remarks>
    /// The door the plan's Doors-still-missing list named for this phase. Until it existed
    /// <c>systemdialog.caption.hover</c> and <c>systemdialog.caption.pressed</c> — and their
    /// <c>dialog.caption.*</c> siblings — were defined by all four themes and reachable by
    /// nothing: <c>MAILBOX_CAPTION</c> takes the shell's <see cref="CaptionButtons"/>, which is a
    /// field of this window, and a dialog's is built inside <see cref="SystemDialogChrome"/> and
    /// kept by nobody. It is found here the way a reader finds it — by looking at the window.
    /// <para>
    /// <see cref="CaptionButtons.Describe"/> is reported beside the picture because a caption
    /// button paints in two places: the button's own background and the content presenter's,
    /// which the control theme fills on <c>:pressed</c>. A capture says which colour won; the
    /// description says which of the two carried it.
    /// </para>
    /// </remarks>
    private async Task RunCaptionStepsAsync(string spec)
    {
        if (await NewestWindowAsync() is not { } dialog)
        {
            Log.Warn("Harness: dialog caption — no window is open.");
            return;
        }

        dialog.UpdateLayout();

        if (dialog.GetVisualDescendants().OfType<CaptionButtons>().FirstOrDefault() is not { } caption)
        {
            Log.Warn($"Harness: dialog caption — {dialog.GetType().Name} “{dialog.Title}” draws no "
                     + "caption buttons of its own. Every window in this application is supposed to.");
            return;
        }

        // What the caption is made of, before anything is done to it: the count is the claim that
        // a dialog carries a close button only and a system window carries all three.
        var all = caption.GetVisualDescendants().OfType<Button>().ToList();
        Log.Info($"Harness: dialog caption — {dialog.GetType().Name} “{dialog.Title}” carries "
                 + $"{all.Count} button(s) [{string.Join(", ", all.Select(b => $"{ToolTip.GetTip(b)} "
                     + $"{b.Bounds.Width:0}x{b.Bounds.Height:0} .{string.Join(".", b.Classes)}"))}].");

        foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).Trim().ToLowerInvariant();
            var which = (colon > 0 ? step[(colon + 1)..] : "close").Trim().ToLowerInvariant();

            switch (verb)
            {
                case "report":
                    foreach (var name in (string[])["minimize", "maximize", "close"])
                    {
                        Log.Info($"Harness: dialog caption — {caption.Describe(name)}.");
                    }
                    break;

                case "hover":
                    Log.Info(caption.ForceHover(which)
                        ? $"Harness: dialog caption hover {which} — {caption.Describe(which)}."
                        : $"Harness: dialog caption — “{which}” is not one of its buttons.");
                    break;

                case "hold":
                case "pressed":
                    Log.Info(caption.ForcePressed(which)
                        ? $"Harness: dialog caption held {which} — {caption.Describe(which)}."
                        : $"Harness: dialog caption — “{which}” is not one of its buttons.");
                    break;

                case "press":
                    var before = dialog.WindowState;
                    var acted = caption.Press(which);
                    Log.Info($"Harness: dialog caption press {which} — "
                             + $"{(acted ? "the button acted" : "no such button")}, from {before}.");
                    await Task.Delay(600);
                    Log.Info($"Harness: dialog caption {which} settled — {before} → "
                             + $"{(dialog.IsVisible ? dialog.WindowState.ToString() : "closed")}, "
                             + $"{dialog.ClientSize.Width:0}x{dialog.ClientSize.Height:0}.");
                    break;

                default:
                    Log.Warn($"Harness: dialog caption — “{step}” is not a step. Say report, hover, "
                             + "hold or press, each naming minimize, maximize or close.");
                    break;
            }
        }

        // A layout pass, so the picture is of the state just posed rather than of the arrangement
        // before it: the pseudo-class is set on the button and the presenter has not been asked to
        // draw again.
        dialog.UpdateLayout();
    }

    // ---- The store -----------------------------------------------------------------------

    /// <summary>
    /// What the tabs of this family have actually written, read out of the stores rather than off
    /// the lists that were just looked at.
    /// </summary>
    /// <remarks>
    /// The rule the audit turns on: a dialog's own list is the thing under test, so it cannot also
    /// be the evidence. Every line here comes from the repository the tab writes to — the feed
    /// subscriptions, the calendar collections, the published table, the address books and the
    /// account files on disk.
    /// </remarks>
    private static void ReportSystemDialogStore()
    {
        foreach (var account in App.Accounts.All)
        {
            Log.Info($"Harness: store account — {account.Account.Address}"
                     + $"{(account.IsDefault ? " (default)" : string.Empty)}, file {account.Path}, "
                     + $"{(File.Exists(account.Path) ? "on disk" : "MISSING")}.");
        }

        var detached = Path.Combine(App.Accounts.Directory_, "detached");
        Log.Info($"Harness: store detached — {(Directory.Exists(detached)
            ? string.Join(", ", Directory.GetFiles(detached).Select(Path.GetFileName))
            : "no detached folder")}.");

        foreach (var feed in App.Feeds.All)
        {
            Log.Info($"Harness: store feed — “{feed.Name}” {feed.Url}, heading "
                     + $"“{(feed.Category.Length > 0 ? feed.Category : "(none)")}”, folder “{feed.FolderPath}”.");
        }

        foreach (var collection in App.Pim.Collections(CollectionKind.Events))
        {
            Log.Info($"Harness: store calendar — “{collection.DisplayName}” id {collection.Id}, "
                     + $"{(collection.IsReadOnly ? "read-only" : "writable")}, "
                     + $"address “{collection.DavUrl}”.");
        }

        foreach (var published in App.Published.All)
        {
            Log.Info($"Harness: store published — collection {published.CollectionId} "
                     + $"“{published.Name}” → {published.Url}.");
        }

        foreach (var book in App.Contacts.AddressBooks())
        {
            Log.Info($"Harness: store address book — “{book.DisplayName}” id {book.Id}, "
                     + $"{App.Pim.Items(book.Id).Count} card(s), "
                     + $"{(book.DavUrl is { Length: > 0 } ? "CardDAV " + book.DavUrl : "local")}.");
        }

        foreach (var signature in App.Signatures.All)
        {
            Log.Info($"Harness: store signature — “{signature.Name}”, {signature.Text.Length} character(s) "
                     + $"of text, {signature.Html.Length} of markup.");
        }

        foreach (var account in App.Accounts.All)
        {
            Log.Info($"Harness: store signs — {account.Account.Address} new "
                     + $"“{App.Signatures.ForNew(account.Account.Address)?.Name ?? "(none)"}”, reply "
                     + $"“{App.Signatures.ForReply(account.Account.Address)?.Name ?? "(none)"}”.");
        }
    }
}
