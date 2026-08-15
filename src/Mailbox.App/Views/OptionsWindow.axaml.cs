using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Options;
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

        Bind(this, BackgroundProperty, "dialog.background.brush");

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

        Content = root;

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
                var renderer = new OptionsPageRenderer(App.Settings);
                _ = renderer.Render(page);
                Console.WriteLine($"OK    {page.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {page.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Environment.Exit(0);
    }

    private void ShowPage(string id)
    {
        if (OptionsPages.Find(id) is not { } page) return;

        var renderer = new OptionsPageRenderer(App.Settings);
        renderer.ActionInvoked += (_, label) => OnAction(label);

        var content = renderer.Render(page);

        FillLiveSlots(renderer);
        _page.Content = new ScrollViewer { Content = content };
    }

    /// <summary>
    /// Sub-dialogs opened from a page's buttons. Only the shapes exist so far; the ones with
    /// reference captures are next.
    /// </summary>
    private void OnAction(string buttonLabel)
    {
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
