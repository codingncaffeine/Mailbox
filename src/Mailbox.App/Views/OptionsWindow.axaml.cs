using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.LogicalTree;
using Mailbox.App.Options;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
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
    /// <summary>Set when the Advanced page's Export button was pressed, so the shell opens it.</summary>
    public bool ExportRequested { get; private set; }

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

        if (Environment.GetEnvironmentVariable("MAILBOX_OPTIONS_PRESS") is { Length: > 0 } press)
        {
            PressRows(content, renderer, press);
        }
    }

    /// <summary>
    /// Presses rows on the page that is up, and says what the settings hold afterwards.
    /// </summary>
    /// <param name="rows">
    /// Comma-separated; each names part of a row's label, matched case-insensitively. A tick box is
    /// toggled and a radio is chosen.
    /// </param>
    /// <remarks>
    /// §20 lists a hundred-odd rows waiting on the feature behind them, and each becomes a claim the
    /// day it is wired up. This is how that claim gets read back: press the row the reader would
    /// press and print the key it wrote, rather than photographing a tick and calling it done. A
    /// capture cannot click, so the press is raised here — on the control the renderer really made,
    /// found by the label the reader really sees.
    /// </remarks>
    private static void PressRows(Control page, OptionsPageRenderer renderer, string rows)
    {
        foreach (var wanted in rows.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var control = page.GetLogicalDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .FirstOrDefault(c => c.Content?.ToString()?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);

            if (control is null)
            {
                Mailbox.Core.Diagnostics.Log.Info($"Harness: no options row reads '{wanted}'.");
                continue;
            }

            // A radio is chosen rather than toggled: unticking one answers nothing.
            control.IsChecked = control is RadioButton || control.IsChecked != true;

            // What it wrote, not what it looks like. A row whose key nothing reads yet is one of
            // §20's hundred, and this says so rather than photographing a tick and calling it done.
            var wrote = renderer.Keys.TryGetValue(control, out var key)
                ? $"{key} = {App.Settings.Stored(key) ?? "(unset)"}"
                : "nothing — the row carries no key";

            Mailbox.Core.Diagnostics.Log.Info(
                $"Harness: pressed '{control.Content}', now {(control.IsChecked == true ? "on" : "off")}, wrote {wrote}.");
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
                _ = new StationeryDialog(App.Signatures, App.Stationery, App.Accounts.All, App.Accounts.Default?.Account.Address, tab: 0).ShowDialog(this);
                break;

            case "Editor Options...":
            case "Spelling and Autocorrect...":
                _ = new EditorOptionsDialog(App.MailOptions, App.Settings).ShowDialog(this);
                break;

            case "Stationery and Fonts...":
                _ = new StationeryDialog(App.Signatures, App.Stationery, App.Accounts.All, App.Accounts.Default?.Account.Address, tab: 1).ShowDialog(this);
                break;

            case "AutoArchive Settings...":
                _ = new AutoArchiveSettingsDialog(App.AutoArchive).ShowDialog(this);
                break;

            case "Reading Pane...":
                _ = new ReadingPaneOptionsDialog(App.MailOptions).ShowDialog(this);
                break;

            // Export leaves this window for the Backstage page that holds every exporter — one
            // door, rather than a second one here that would have to grow the same four entries.
            case "Export":
                ExportRequested = true;
                Close();
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
            var customize = new Button { Content = "Customize…", VerticalAlignment = VerticalAlignment.Center };
            customize.Click += async (_, _) => await new ThemeEditorWindow(_themes).ShowDialog(this);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ThemeCombo(), customize } };
            theme.Content = LabelledLive("Mailbox Theme:", row);
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

        if (renderer.Slots.TryGetValue("plugins", out var plugins))
        {
            plugins.Content = PluginRows();
        }

        if (renderer.Slots.TryGetValue("keys", out var keys))
        {
            keys.Content = KeyRingRows();
        }

        if (renderer.Slots.TryGetValue("certificates", out var certificates))
        {
            certificates.Content = TrustedCertificateRows();
        }

        if (renderer.Slots.TryGetValue("autostart", out var autostart))
        {
            autostart.Content = AutostartRows();
        }

        if (renderer.Slots.TryGetValue("cleanupfolder", out var cleanupFolder))
        {
            cleanupFolder.Content = CleanUpFolderRow();
        }

        if (renderer.Slots.TryGetValue("arrivalsound", out var arrivalSound))
        {
            arrivalSound.Content = ArrivalSoundRow();
        }

        if (renderer.Slots.TryGetValue("remindersound", out var reminderSound))
        {
            reminderSound.Content = ReminderSoundRow(renderer);
        }

        if (renderer.Slots.TryGetValue("display", out var display))
        {
            display.Content = DisplayRows();
        }
    }

    /// <summary>
    /// Windowing backend and scale, over <see cref="Mailbox.Core.Platform.DisplaySettings"/>:
    /// two combo boxes and the line that says they take effect at the next start, because
    /// neither can change under an open window.
    /// </summary>
    private Control DisplayRows()
    {
        var settings = new Mailbox.Core.Platform.DisplaySettings(App.Settings);

        var backend = new ComboBox
        {
            ItemsSource = new[] { "Automatic (currently X11)", "X11", "Wayland (experimental)" },
            SelectedIndex = settings.Backend switch
            {
                Mailbox.Core.Platform.DisplayBackend.X11 => 1,
                Mailbox.Core.Platform.DisplayBackend.Wayland => 2,
                _ => 0,
            },
            MinWidth = 240,
            VerticalAlignment = VerticalAlignment.Center,
        };
        backend.SelectionChanged += (_, _) => settings.Backend = backend.SelectedIndex switch
        {
            1 => Mailbox.Core.Platform.DisplayBackend.X11,
            2 => Mailbox.Core.Platform.DisplayBackend.Wayland,
            _ => Mailbox.Core.Platform.DisplayBackend.Auto,
        };

        var scales = Mailbox.Core.Platform.DisplaySettings.Scales;
        var scale = new ComboBox
        {
            ItemsSource = new[] { "Automatic (the desktop's own)" }.Concat(scales.Select(v => $"{v * 100:0}%")).ToList(),
            SelectedIndex = settings.Scale is { } pinned && scales.ToList().IndexOf(pinned) is var i && i >= 0 ? i + 1 : 0,
            MinWidth = 240,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scale.SelectionChanged += (_, _) => settings.Scale = scale.SelectedIndex > 0 ? scales[scale.SelectedIndex - 1] : null;

        Control Row(string label, Control control)
        {
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 200 };
            Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { caption, control } };
        }

        var note = new TextBlock
        {
            Text = "These take effect the next time Mailbox starts. Automatic scale follows the desktop; on X11 that is Xft.dpi.",
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        return new StackPanel
        {
            Spacing = 8,
            Children = { Row("Windowing:", backend), Row("Scale:", scale), note },
        };
    }

    /// <summary>"Cleaned-up items will go to this folder": the name, and Browse… over the default account's folders.</summary>
    /// <summary>Which sound mail arriving plays, under the switch that decides whether it plays one.</summary>
    /// <remarks>
    /// <b>A stated divergence.</b> The reference's Message-arrival group has no sound picker
    /// (options/mail1.png ends at "Display a Desktop Alert"): on its platform the sound is chosen
    /// in the desktop's control panel and the application only decides whether to make it. No
    /// Linux desktop offers a per-application new-mail sound to set, so under rule 2 the choice
    /// comes to where the switch is — drawn in the idiom the reference uses for the one sound
    /// picker it does have, the reminder's.
    /// </remarks>
    private Control ArrivalSoundRow()
        => SoundPicker(
            "Sound to play:",
            () => App.MailOptions.ArrivalSoundFile,
            chosen => App.MailOptions.ArrivalSoundFile = chosen,
            chosen => Notifications.Sounds.NameFor(chosen, "new-mail.ogg"),
            Notifications.Sounds.PlayArrival);

    /// <summary>
    /// The reference's reminder row: the tick, the label, the file and a Browse…, all on a line.
    /// </summary>
    /// <remarks>
    /// Measured off options/advanced1.png, where it reads "Play reminder sound:" with
    /// <c>reminder.wav</c> in a field beside it. A <c>CheckRow</c> and a <c>BrowseRow</c> would
    /// stack into two lines, so the whole row is built here — and the tick box is handed back to
    /// the renderer (<see cref="OptionsPageRenderer.Remember"/>) so the harness reads a press on
    /// it back like any other row's.
    /// </remarks>
    private Control ReminderSoundRow(OptionsPageRenderer renderer)
    {
        var tick = new CheckBox
        {
            Content = "Play reminder sound:",
            IsChecked = App.MailOptions.PlayReminderSound,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 150,
        };
        tick.IsCheckedChanged += (_, _) => App.Settings.Set(MailOptions.ReminderSoundKey, tick.IsChecked == true);
        renderer.Remember(tick, MailOptions.ReminderSoundKey);

        // The renderer binds every tick box it makes to this; one built here has to say so too,
        // or it draws a shade dimmer than the rows above and below it.
        Bind(tick, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        return SoundPicker(
            tick,
            () => App.MailOptions.ReminderSoundFile,
            chosen => App.MailOptions.ReminderSoundFile = chosen,
            chosen => Notifications.Sounds.NameFor(chosen, "reminder.ogg"),
            Notifications.Sounds.PlayReminder);
    }

    private Control SoundPicker(
        string caption,
        Func<string> read,
        Action<string> write,
        Func<string, string> name,
        Action<string> play)
    {
        var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return SoundPicker(label, read, write, name, play);
    }

    /// <summary>
    /// A sound: the path in a field, a Browse… beside it, and what plays when the field is empty
    /// written into the field as its watermark.
    /// </summary>
    /// <remarks>
    /// The field is editable and empty means default, which is how there is a way back without a
    /// Reset button the reference does not draw — clearing it is the reset. The watermark is
    /// asked of the same rule that picks the file, so a chosen sound that has since been deleted
    /// is described by what now actually plays rather than by a name that no longer resolves.
    /// <para>
    /// Choosing one plays it. Hearing it is the only way to know it is the right sound, and the
    /// only way to find out this machine cannot make one — and it costs no button.
    /// </para>
    /// </remarks>
    private Control SoundPicker(
        Control caption,
        Func<string> read,
        Action<string> write,
        Func<string, string> name,
        Action<string> play)
    {
        var field = new TextBox { Width = 240, Text = read(), VerticalAlignment = VerticalAlignment.Center };
        void Describe() => field.PlaceholderText = name(string.Empty);
        Describe();

        field.LostFocus += (_, _) => write(field.Text ?? string.Empty);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a sound",
                AllowMultiple = false,
                // Ogg, WAVE and FLAC are what the players here decode. MP3 is not offered:
                // nothing in the chain reads one, so choosing it would be choosing silence.
                FileTypeFilter = [new FilePickerFileType("Sound files") { Patterns = ["*.ogg", "*.oga", "*.wav", "*.flac"] }],
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            write(path);
            field.Text = path;
            play(path);
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { caption, field, browse },
        };
    }

    private Control CleanUpFolderRow()
    {
        var caption = new TextBlock { Text = "Cleaned-up items will go to this folder:", VerticalAlignment = VerticalAlignment.Center, Width = 240 };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        var name = new TextBox { Width = 200, IsReadOnly = true, Text = App.MailOptions.CleanUpFolder is { Length: > 0 } chosen ? chosen : "Deleted Items" };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            if (App.Accounts.Default is not { } account) return;
            var folders = account.Mail.Folders(account.Account.Id).Where(f => f.Role != Mailbox.Store.FolderRole.Outbox).ToList();
            var choices = folders.Select(f => new Choice(f.Name, f.Name)).ToList();
            var picked = await Chooser.AskAsync(this, "Select Folder", "Cleaned-up items will go to:", choices, name.Text);
            if (picked is null) return;
            App.MailOptions.CleanUpFolder = picked == "Deleted Items" ? string.Empty : picked;
            name.Text = picked;
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { caption, name, browse } };
    }

    /// <summary>
    /// Start at sign-in, and whether to start into the tray: two checkboxes over one XDG
    /// autostart entry (§10). Read from the entry rather than from a setting, so a desktop that
    /// has switched the entry off in its own session settings is shown the truth.
    /// </summary>
    /// <summary>
    /// The Trust Center's key list: what is in the ring, and how to put more there.
    /// </summary>
    /// <remarks>
    /// Reading the ring asks for no passphrase — a secret key's presence is a fact about the ring
    /// and opening it is a different operation — so this page can say what is here without
    /// summoning a prompt for a page nobody asked to unlock anything on.
    /// <para>
    /// A revoked or expired key is listed rather than filtered out, and says which it is: a reader
    /// wondering why a message will not encrypt is owed the reason, and the reason is usually
    /// sitting in this list.
    /// </para>
    /// </remarks>
    private Control KeyRingRows()
    {
        var panel = new StackPanel { Spacing = 8 };

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        Bind(summary, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };

        var import = DialogButton("Import…", isDefault: false);
        import.Width = 120;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, IsVisible = false };
        Bind(status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        void Fill()
        {
            list.Children.Clear();

            IReadOnlyList<Mailbox.Security.OpenPgp.KeyEntry> keys;
            try
            {
                using var ring = CryptoStores.KeyRing();
                keys = Mailbox.Security.OpenPgp.KeyInventory.Read(ring);
            }
            catch (Exception ex)
            {
                Log.Warn("The keyring could not be read.", ex);
                summary.Text = $"The keyring could not be read: {ex.Message}";
                return;
            }

            if (keys.Count == 0)
            {
                summary.Text = "No keys yet. Import brings a copy of GnuPG's across, if you have any there.";
                return;
            }

            var mine = keys.Count(k => k.HasSecret);
            summary.Text = $"{Count(keys.Count, "key")} in the ring, {mine} of them yours.";

            var now = DateTimeOffset.Now;
            foreach (var key in keys.OrderByDescending(k => k.HasSecret).ThenBy(k => k.Owner, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Children.Add(KeyLine(key, now));
            }
        }

        import.Click += async (_, _) =>
        {
            import.IsEnabled = false;
            status.IsVisible = true;
            status.Text = "Asking GnuPG…";

            try
            {
                using var ring = CryptoStores.KeyRing();
                var result = await Mailbox.Security.OpenPgp.GnuPgImport.RunAsync(ring);
                status.Text = result.Summary;
                Log.Info($"Trust Center: key import — {result.Summary}");
            }
            catch (Exception ex)
            {
                Log.Warn("Importing from GnuPG failed.", ex);
                status.Text = $"The import failed: {ex.Message}";
            }
            finally
            {
                import.IsEnabled = true;
                Fill();
            }
        };

        Fill();

        // Making a key here is the door for the reader who has none anywhere — Import assumes
        // one already exists. The dialog runs the generation off the UI thread and the list is
        // refilled from the ring afterwards, which is the read-back rather than the claim.
        var make = DialogButton("New…", isDefault: false);
        make.Width = 120;
        make.Click += async (_, _) =>
        {
            try
            {
                using var ring = CryptoStores.KeyRing();
                var account = App.Accounts.All.FirstOrDefault();
                var madeKey = await NewKeyDialog.MakeAsync(
                    this, ring, CryptoStores.Passphrases,
                    account?.Account.DisplayName ?? string.Empty,
                    account?.Account.Address ?? string.Empty);

                if (madeKey is not null)
                {
                    status.IsVisible = true;
                    status.Text = $"Made {madeKey.ShortId} for {madeKey.Owner}.";
                    Fill();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("The new-key dialog failed.", ex);
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { make, import },
        };

        if (!Mailbox.Security.OpenPgp.GnuPgImport.IsAvailable)
        {
            import.IsEnabled = false;
            var absent = new TextBlock
            {
                Text = "GnuPG is not installed on this machine.",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(absent, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            buttons.Children.Add(absent);
        }

        panel.Children.Add(summary);
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        panel.Children.Add(status);

        // What the harness reads back, a capture of a scrolled page being a poor way to check a
        // list. MAILBOX_OPTIONS_PAGE=trust is what opens it.
        if (WindowCapture.IsRequested) LogKeyRing();

        return panel;
    }

    /// <summary>
    /// The plugins as the host knows them: each with its state, what it asked for, and a button
    /// that enables or disables it now — §13's manager, on the page the reference keeps its own.
    /// </summary>
    private Control PluginRows()
    {
        var panel = new StackPanel { Spacing = 8 };

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        Bind(summary, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };

        void Fill()
        {
            list.Children.Clear();

            var records = App.Plugins.Plugins;
            summary.Text = records.Count == 0
                ? "No plugins are installed. A plugin is a directory holding plugin.json and " +
                  $"its assembly, under {App.Plugins.Root}."
                : $"{Count(records.Count, "plugin")} in {App.Plugins.Root}.";

            foreach (var record in records) list.Children.Add(PluginLine(record));
        }

        // The host raises Changed off whatever thread failed a plugin, and the page may be gone
        // by then — unhooked when the window closes, so the event does not keep it alive.
        EventHandler onChanged = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Fill);
        App.Plugins.Changed += onChanged;
        Closed += (_, _) => App.Plugins.Changed -= onChanged;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var open = DialogButton("Open Folder…", isDefault: false);
        open.Width = 120;
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(App.Plugins.Root);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("xdg-open", App.Plugins.Root)
                    {
                        UseShellExecute = false,
                    });
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the plugins directory.", ex);
            }
        };
        buttons.Children.Add(open);

        panel.Children.Add(summary);
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        Fill();

        // What the harness reads back, a capture of a scrolled page being a poor way to check a
        // list. MAILBOX_OPTIONS_PAGE=addins is what opens it.
        if (WindowCapture.IsRequested) LogPlugins();

        return panel;
    }

    private Control PluginLine(Mailbox.Plugins.PluginRecord record)
    {
        var lines = new StackPanel { Spacing = 1 };

        var name = new TextBlock
        {
            Text = record.Manifest is { } m
                ? $"{record.Name} {m.PluginVersion}" + (m.Author.Length > 0 ? $" — {m.Author}" : string.Empty)
                : record.Name,
            FontWeight = record.State == Mailbox.Plugins.PluginState.Enabled
                ? FontWeight.SemiBold
                : FontWeight.Normal,
            Width = 380,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        lines.Children.Add(name);

        var asked = record.Manifest?.Permissions is { Count: > 0 } permissions
            ? $"asks for {string.Join(", ", permissions)}"
            : "asks for nothing";

        var detail = new TextBlock
        {
            Text = $"{Word(record.State)} · {asked}"
                   + (record.Error is { Length: > 0 } error ? $" · {error}" : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        Bind(detail, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        lines.Children.Add(detail);

        if (record.UndeclaredUses.Count > 0)
        {
            var undeclared = new TextBlock
            {
                Text = $"Used without declaring: {string.Join(", ", record.UndeclaredUses)}. " +
                       "Those calls were refused.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
            Bind(undeclared, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            lines.Children.Add(undeclared);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { lines },
        };

        // Broken and incompatible plugins get the button too: Enable re-examines the directory,
        // which is what "I replaced the files, try again" needs — and a crashed one may be tried
        // again, its report having made its point.
        if (record.Manifest is not null)
        {
            var toggle = DialogButton(
                record.State == Mailbox.Plugins.PluginState.Enabled ? "Disable" : "Enable",
                isDefault: false);
            toggle.Width = 90;
            toggle.VerticalAlignment = VerticalAlignment.Top;
            var id = record.Manifest.Id;
            var enabled = record.State == Mailbox.Plugins.PluginState.Enabled;
            toggle.Click += (_, _) =>
            {
                if (enabled) App.Plugins.Disable(id);
                else App.Plugins.Enable(id);
            };
            row.Children.Add(toggle);
        }

        return row;

        static string Word(Mailbox.Plugins.PluginState state) => state switch
        {
            Mailbox.Plugins.PluginState.Enabled => "Enabled",
            Mailbox.Plugins.PluginState.Disabled => "Disabled",
            Mailbox.Plugins.PluginState.Crashed => "Disabled after an error",
            Mailbox.Plugins.PluginState.Incompatible => "Needs a newer Mailbox",
            _ => "Could not be read",
        };
    }

    /// <summary>Reads the list back for a capture run, with each plugin's state and report.</summary>
    private static void LogPlugins()
    {
        var records = App.Plugins.Plugins;
        Log.Info($"Harness: plugins — {records.Count} under {App.Plugins.Root}.");

        foreach (var record in records)
        {
            Log.Info($"Harness: plugin {record.Id} — {record.State.ToString().ToLowerInvariant()}"
                     + (record.Manifest is { } m ? $", asks for [{string.Join(", ", m.Permissions)}]" : string.Empty)
                     + (record.Error is { Length: > 0 } error ? $", “{error}”" : string.Empty)
                     + (record.UndeclaredUses.Count > 0
                         ? $", used undeclared [{string.Join(", ", record.UndeclaredUses)}]"
                         : string.Empty));
        }
    }

    private Control KeyLine(Mailbox.Security.OpenPgp.KeyEntry key, DateTimeOffset now)
    {
        var owner = new TextBlock
        {
            Text = key.Owner,
            FontWeight = key.HasSecret ? FontWeight.SemiBold : FontWeight.Normal,
            Width = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Bind(owner, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var detail = new TextBlock
        {
            Text = $"{key.ShortId} · {key.Algorithm} {key.Bits} · {key.State(now)}"
                   + (key.HasSecret ? " · yours" : string.Empty),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A key that will not be used says so in the ordinary subtle colour rather than in a
        // warning colour of its own: this is a list of facts, not a list of problems, and the
        // theme has no token for "bad" that is not the accent doing something else.
        Bind(detail, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        detail.Opacity = key.IsUsable(now) ? 1.0 : 0.7;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { owner, detail },
        };
    }

    /// <summary>Reads the ring back for a capture run, a list being a poor thing to photograph.</summary>
    private static void LogKeyRing()
    {
        try
        {
            using var ring = CryptoStores.KeyRing();
            var keys = Mailbox.Security.OpenPgp.KeyInventory.Read(ring);
            var now = DateTimeOffset.Now;

            Log.Info($"Harness: keyring — {keys.Count} key(s); GnuPG "
                     + $"{(Mailbox.Security.OpenPgp.GnuPgImport.IsAvailable ? "available" : "absent")}.");

            foreach (var key in keys)
            {
                Log.Info($"Harness: key {key.ShortId} — “{key.Owner}”, {key.Algorithm} {key.Bits}, "
                         + $"{key.State(now)}, {(key.HasSecret ? "secret half held" : "public only")}, "
                         + $"{(key.IsUsable(now) ? "usable" : "not usable")}.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the keyring could not be read.", ex);
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>
    /// The certificates the reader has agreed to, and a way to take each back.
    /// </summary>
    /// <remarks>
    /// The other half of what <see cref="CertificateDialog"/> promises: it tells the reader the
    /// decision covers one certificate and can be reconsidered, and this is where reconsidering
    /// happens. Without it, agreeing once in a dialog would be a decision with nowhere to look.
    /// </remarks>
    private Control TrustedCertificateRows()
    {
        var panel = new StackPanel { Spacing = 6 };

        void Fill()
        {
            panel.Children.Clear();

            var pins = App.Trust.Pins;
            if (pins.Count == 0)
            {
                var none = new TextBlock { Text = "None. Every server so far has verified normally." };
                Bind(none, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
                panel.Children.Add(none);

                if (WindowCapture.IsRequested) Log.Info("Harness: trusted certificates — none.");
                return;
            }

            foreach (var (host, fingerprint) in pins.OrderBy(p => p.Host, StringComparer.Ordinal))
            {
                var name = new TextBlock { Text = host, Width = 250, TextTrimming = TextTrimming.CharacterEllipsis };
                Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");

                var print = new TextBlock
                {
                    Text = fingerprint.Length >= 16 ? fingerprint[..16] + "…" : fingerprint,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Bind(print, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
                Bind(print, TextBlock.FontFamilyProperty, "mono.fontfamily");

                var forget = DialogButton("Forget", isDefault: false);
                forget.Width = 80;
                forget.Click += (_, _) =>
                {
                    App.Trust.Forget(host);
                    Log.Info($"Trust Center: forgot the certificate pinned for {host}.");
                    Fill();
                };

                panel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { name, print, forget },
                });

                if (WindowCapture.IsRequested)
                {
                    Log.Info($"Harness: trusted certificate — {host} pinned to {fingerprint[..16]}….");
                }
            }
        }

        Fill();
        return panel;
    }

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
        // The built-ins, then the reader's theme files, in the library's order.
        var ids = _themes.Library.Ids;
        var combo = new ComboBox
        {
            ItemsSource = ids.Select(_themes.DisplayName).ToList(),
            SelectedIndex = ids.ToList().FindIndex(id => string.Equals(id, _themes.ThemeId, StringComparison.OrdinalIgnoreCase)),
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= ids.Count) return;

            var id = ids[combo.SelectedIndex];
            try
            {
                _themes.Apply(id);
                App.Settings.Set(App.ThemeSetting, id);
            }
            catch (Mailbox.Theming.Tokens.ThemeResolutionException ex)
            {
                Mailbox.Core.Diagnostics.Log.Warn($"Theme \"{id}\" could not be applied: {ex.Message}");
            }
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
