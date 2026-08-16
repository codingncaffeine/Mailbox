using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Options;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Theming;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Themes;

namespace Mailbox.App.Views;

/// <summary>
/// The Options dialog.
/// </summary>
/// <remarks>
/// A page rail down the left with two rules grouping it, a scrolling content pane, and OK /
/// Cancel bottom-right. Each page opens with an icon and a one-line description, then sections
/// whose bold heading is followed by a rule running to the right edge.
/// <para>
/// Built in code rather than XAML because the pages are generated from a description of their
/// controls — thirteen sections of checkboxes, radios and dropdowns authored by hand would be
/// thousands of lines of markup that all looks the same.
/// </para>
/// </remarks>
public sealed class OptionsWindow : Window
{
    private const double RailWidth = 148;

    private readonly ThemeService _themes;
    private readonly StackPanel _rail = new();
    private readonly ContentControl _page = new();
    private string _selected = "general";

    /// <param name="initialPage">
    /// Which page to open on, by id. Lets a control that has its own Options page — the Quick
    /// Access Toolbar's "More Commands…" — land there instead of on General, which is the
    /// difference between a menu item that works and one that merely opens something.
    /// </param>
    public OptionsWindow(ThemeService themes, string? initialPage = null)
    {
        _themes = themes;

        if (!string.IsNullOrWhiteSpace(initialPage)
            && OptionsPages.All.Any(p => string.Equals(p.Id, initialPage, StringComparison.OrdinalIgnoreCase)))
        {
            _selected = initialPage;
        }

        Title = "Mailbox Options";
        Width = 915;
        Height = 995;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions($"{RailWidth},*"),
            Margin = new Thickness(12),
        };

        var railBox = new Border
        {
            Child = new ScrollViewer { Content = _rail },
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
        };
        Bind(railBox, BackgroundProperty, "dialog.surface.brush");
        Bind(railBox, BorderBrushProperty, "dialog.border.brush");
        Grid.SetRow(railBox, 0);
        Grid.SetRowSpan(railBox, 2);
        Grid.SetColumn(railBox, 0);
        root.Children.Add(railBox);

        Grid.SetRow(_page, 0);
        Grid.SetRowSpan(_page, 2);
        Grid.SetColumn(_page, 1);
        root.Children.Add(_page);

        var buttons = BuildButtonRow();
        Grid.SetRow(buttons, 2);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        DialogChrome.Apply(this, root);

        BuildRail();

        // Harness: render every page and report failures, so a broken description is caught
        // here rather than by a user clicking the rail.
        if (Environment.GetEnvironmentVariable("MAILBOX_OPTIONS_AUDIT") == "1")
        {
            AuditAllPages();
            return;
        }

        ShowPage(_selected);
    }

    private Control BuildButtonRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = DialogButton("OK", isDefault: true);
        ok.Click += (_, _) => Close(true);
        row.Children.Add(ok);

        var cancel = DialogButton("Cancel", isDefault: false);
        cancel.Click += (_, _) => Close(false);
        row.Children.Add(cancel);

        return row;
    }

    private Button DialogButton(string label, bool isDefault)
    {
        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var button = new Button
        {
            Content = text,
            Width = 74,
            Height = 24,
            BorderThickness = new Thickness(isDefault ? 2 : 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Bind(button, BorderBrushProperty, isDefault ? "accent.rest.brush" : "dialog.border.brush");
        Bind(button, BackgroundProperty, "dialog.surface.brush");
        return button;
    }

    private void BuildRail()
    {
        _rail.Children.Clear();

        foreach (var page in OptionsPages.All)
        {
            _rail.Children.Add(RailItem(page.Id, page.Title));
            if (OptionsPages.RuleAfter.Contains(page.Id)) _rail.Children.Add(RailRule());
        }
    }

    private Control RailRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(6, 5) };
        Bind(rule, BackgroundProperty, "dialog.border.brush");
        return rule;
    }

    private Control RailItem(string id, string label)
    {
        var selected = string.Equals(id, _selected, StringComparison.Ordinal);

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var button = new Button
        {
            Content = text,
            Height = 27,
            Padding = default,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Background = Brushes.Transparent,
        };

        // the reference application outlines the selected page and lightens its fill.
        if (selected)
        {
            Bind(button, BorderBrushProperty, "dialog.surface.text.brush");
            Bind(button, BackgroundProperty, "dialog.selection.brush");
        }

        button.Click += (_, _) =>
        {
            _selected = id;
            BuildRail();
            ShowPage(id);
        };
        return button;
    }

    private void AuditAllPages()
    {
        foreach (var page in OptionsPages.All)
        {
            try
            {
                // Through the same path a click takes, so the two editor pages are audited as
                // editors rather than as the empty descriptions they carry.
                if (BuildEditor(page) is null)
                {
                    var renderer = new OptionsPageRenderer(App.Settings);
                    _ = renderer.Render(page);
                }

                Console.WriteLine($"OK    {page.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {page.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// True when the ribbon or the toolbar was edited while the dialog was open, so the shell
    /// knows to take them back.
    /// </summary>
    public bool CustomizationChanged { get; private set; }

    private void ShowPage(string id)
    {
        if (OptionsPages.Find(id) is not { } page) return;

        // The two customization pages are editors rather than lists of settings, so they build
        // themselves instead of being rendered from a description.
        if (BuildEditor(page) is { } editor)
        {
            _page.Content = editor;
            return;
        }

        var renderer = new OptionsPageRenderer(App.Settings);
        renderer.ActionInvoked += (_, label) => OnAction(label);

        var content = renderer.Render(page);

        FillLiveSlots(renderer);
        var scroller = new ScrollViewer { Content = content };
        _page.Content = scroller;

        // The harness cannot scroll, and the long pages — Mail, Advanced — hold rows a capture
        // at the dialog's own height never reaches. MAILBOX_OPTIONS_SCROLL=<pixels> poses the
        // page part-way down, after the first layout has given the scroller an extent.
        if (Environment.GetEnvironmentVariable("MAILBOX_OPTIONS_SCROLL") is { Length: > 0 } scroll
            && double.TryParse(scroll, System.Globalization.CultureInfo.InvariantCulture, out var offset))
        {
            scroller.Loaded += (_, _) => scroller.Offset = new Vector(0, offset);
        }
    }

    private Control? BuildEditor(OptionsPage page)
    {
        CustomizationEditor? editor = page.Id switch
        {
            "ribbon" => new RibbonEditorView(
                App.Commands, App.RibbonEdits, DefaultRibbonLayouts.Mail),

            "qat" => new QuickAccessEditorView(
                App.Commands, App.QuickAccess, App.RibbonEdits, DefaultRibbonLayouts.Mail),

            _ => null,
        };

        if (editor is null) return null;

        editor.Edited += (_, _) => CustomizationChanged = true;

        // The editor fills what is left under the heading. Its two panes scroll inside
        // themselves, so the page must not scroll as well or the buttons between them walk off
        // the bottom of a tall ribbon.
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 10, 0),
        };

        var heading = PageHeading(page);
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        Grid.SetRow(editor, 1);
        grid.Children.Add(editor);

        return grid;
    }

    /// <summary>The icon and one-line description every page opens with.</summary>
    private Control PageHeading(OptionsPage page)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(page.Icon, 24),
            FontFamily = IconFont.Family,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        row.Children.Add(glyph);

        var text = new TextBlock
        {
            Text = page.Description,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        row.Children.Add(text);

        return row;
    }

    /// <summary>
    /// Sub-dialogs opened from a page's buttons. Only the shapes exist so far; the ones with
    /// reference captures are next.
    /// </summary>
    /// <summary>
    /// The buttons that open a sub-dialog. Most still open nothing, and §20 says which and why;
    /// this is where each one is wired as its dialog arrives.
    /// </summary>
    private void OnAction(string buttonLabel)
    {
        switch (buttonLabel)
        {
            case "Signatures...":
                _ = SignatureEditor.EditAsync(
                    this, App.Accounts.Default?.Account.Address, _ => { });
                break;

            case "AutoArchive Settings...":
                _ = new AutoArchiveSettingsDialog(App.AutoArchive).ShowDialog(this);
                break;
        }
    }

    // ------------------------------------------------------------------------------------
    // Live controls
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Fills the General page's named slots with controls wired to real state. Everything else
    /// on the page is inert until the settings store lands in Phase 2.
    /// </summary>
    private void FillLiveSlots(OptionsPageRenderer renderer)
    {
        if (renderer.Slots.TryGetValue("theme", out var theme))
        {
            theme.Content = LabelledLive("Mailbox Theme:", ThemeCombo());
        }

        if (renderer.Slots.TryGetValue("density", out var density))
        {
            density.Content = LabelledLive("Density:", DensityCombo());
        }

        if (renderer.Slots.TryGetValue("undosend", out var undo))
        {
            undo.Content = UndoSendRow();
        }

        if (renderer.Slots.TryGetValue("schedule", out var schedule))
        {
            schedule.Content = ScheduleRow();
        }

        if (renderer.Slots.TryGetValue("autocomplete", out var autocomplete))
        {
            autocomplete.Content = AutoCompleteRow();
        }

        if (renderer.Slots.TryGetValue("autostart", out var autostart))
        {
            autostart.Content = AutostartRows();
        }
    }

    /// <summary>
    /// Start at sign-in, and whether to start into the tray: two checkboxes over one XDG
    /// autostart entry (§10). Read from the entry rather than from a setting, so a desktop that
    /// has switched the entry off in its own session settings is shown the truth.
    /// </summary>
    private Control AutostartRows()
    {
        var autostart = new Mailbox.Core.Platform.Autostart();

        var minimised = new CheckBox
        {
            Content = "Start minimised to the notification area",
            IsChecked = autostart.StartsMinimized,
            IsEnabled = autostart.IsEnabled,
            Margin = new Thickness(24, 0, 0, 0),
        };
        Bind(minimised, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        var enabled = new CheckBox
        {
            Content = "Start Mailbox when I sign in",
            IsChecked = autostart.IsEnabled,
        };
        Bind(enabled, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        void Save()
        {
            try
            {
                if (enabled.IsChecked == true) autostart.Enable(minimised.IsChecked == true);
                else autostart.Disable();
            }
            catch (Exception ex)
            {
                Mailbox.Core.Diagnostics.Log.Warn("The autostart entry could not be written.", ex);
            }

            // Re-read rather than assume: what the file says is what will happen at sign-in.
            enabled.IsChecked = autostart.IsEnabled;
            minimised.IsEnabled = autostart.IsEnabled;
        }

        enabled.IsCheckedChanged += (_, _) => Save();
        minimised.IsCheckedChanged += (_, _) => { if (enabled.IsChecked == true) Save(); };

        return new StackPanel { Spacing = 6, Children = { enabled, minimised } };
    }

    /// <summary>
    /// The Auto-Complete List's row: whether the To, Cc and Bcc lines offer names, and the
    /// button that empties the list. One row, as the reference draws it.
    /// </summary>
    /// <remarks>
    /// Emptying is across every account, because the list is offered merged across them — a
    /// button that emptied one file's list would leave the names coming back from another's.
    /// </remarks>
    private Control AutoCompleteRow()
    {
        var enabled = new CheckBox
        {
            Content = "Use Auto-Complete List to suggest names when typing in the To, Cc, and Bcc lines",
            IsChecked = App.MailOptions.UseAutoCompleteList,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(enabled, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        enabled.IsCheckedChanged += (_, _) =>
            App.Settings.Set(MailOptions.UseAutoCompleteListKey, enabled.IsChecked == true);

        var empty = new Button
        {
            Content = "Empty Auto-Complete List",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        empty.Click += async (_, _) =>
        {
            var count = App.Accounts.All.Sum(a => a.Mail.RecipientCount());
            if (count == 0)
            {
                await Confirm.AskAsync(this, "Empty Auto-Complete List",
                    "The Auto-Complete List is already empty.", "OK", destructive: false);
                return;
            }

            var go = await Confirm.AskAsync(this, "Empty Auto-Complete List",
                $"Remove all {count} entr{(count == 1 ? "y" : "ies")} from the Auto-Complete List?",
                "Empty", destructive: true);
            if (!go) return;

            foreach (var account in App.Accounts.All) account.Mail.ClearRecipients();
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { enabled, empty },
        };
    }

    /// <summary>
    /// The All Accounts group's schedule, as one row: whether, and how often.
    /// </summary>
    /// <remarks>
    /// The same state the Send/Receive Groups dialog edits, so the two cannot disagree. A group
    /// somebody has renamed or removed leaves this row with nothing to edit, and it says so.
    /// </remarks>
    private Control ScheduleRow()
    {
        var group = App.Groups.Find(SendReceiveGroups.AllAccounts.Name);

        if (group is null)
        {
            var gone = new TextBlock
            {
                Text = "The All Accounts group has been removed. Schedules are on File › "
                    + "Send/Receive Groups.",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(gone, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            return gone;
        }

        var minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Increment = 1,
            FormatString = "0",
            Value = group.ScheduleMinutes,
            Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = group.ScheduleEnabled,
        };

        var enabled = new CheckBox
        {
            Content = "Schedule an automatic send/receive every",
            IsChecked = group.ScheduleEnabled,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Save()
        {
            var current = App.Groups.Find(SendReceiveGroups.AllAccounts.Name);
            if (current is null) return;

            var updated = current with
            {
                ScheduleEnabled = enabled.IsChecked == true,
                ScheduleMinutes = (int)(minutes.Value ?? current.ScheduleMinutes),
            };

            App.Groups.Replace(App.Groups.All.Select(g => ReferenceEquals(g, current) ? updated : g));
            minutes.IsEnabled = updated.ScheduleEnabled;
        }

        enabled.IsCheckedChanged += (_, _) => Save();
        minutes.ValueChanged += (_, _) => Save();

        var after = new TextBlock
        {
            Text = "minutes",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Bind(after, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { enabled, minutes, after },
        };
    }

    /// <summary>
    /// Undo Send's two settings, on one row: whether, and for how long.
    /// </summary>
    /// <remarks>
    /// One row rather than two, because the number means nothing without the checkbox and the
    /// checkbox is not worth a line of its own. Writing as it goes, like every other page here.
    /// </remarks>
    private Control UndoSendRow()
    {
        var seconds = new NumericUpDown
        {
            Minimum = 1,
            Maximum = UndoSend.MaximumSeconds,
            Increment = 1,
            FormatString = "0",
            Value = App.UndoSend.Seconds,
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = App.UndoSend.IsEnabled,
        };

        var enabled = new CheckBox
        {
            Content = "Let a sent message be taken back for",
            IsChecked = App.UndoSend.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
        };

        enabled.IsCheckedChanged += (_, _) =>
        {
            App.UndoSend.IsEnabled = enabled.IsChecked == true;
            seconds.IsEnabled = enabled.IsChecked == true;
        };

        seconds.ValueChanged += (_, _) =>
        {
            if (seconds.Value is { } value) App.UndoSend.Seconds = (int)value;
        };

        var after = new TextBlock
        {
            Text = "seconds",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Bind(after, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { enabled, seconds, after },
        };
    }

    private ComboBox ThemeCombo()
    {
        var combo = new ComboBox
        {
            ItemsSource = OptionsPages.ThemeNames.ToList(),
            SelectedIndex = OfficeThemes.All.ToList().IndexOf(_themes.ThemeId),
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;

            var id = OfficeThemes.All[combo.SelectedIndex];
            _themes.Apply(id);
            App.Settings.Set(App.ThemeSetting, id);
        };
        return combo;
    }

    private ComboBox DensityCombo()
    {
        var combo = new ComboBox
        {
            ItemsSource = new List<string> { "Compact", "Cozy", "Comfortable" },
            SelectedIndex = _themes.Density switch
            {
                Density.Compact => 0,
                Density.Comfortable => 2,
                _ => 1,
            },
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            var density = combo.SelectedIndex switch
            {
                0 => Density.Compact,
                2 => Density.Comfortable,
                _ => Density.Cozy,
            };
            _themes.SetDensity(density);
            App.Settings.Set(App.DensitySetting, density.ToString());
        };
        return combo;
    }

    private Control LabelledLive(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var text = new TextBlock
        {
            Text = label,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
