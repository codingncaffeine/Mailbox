using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Theming;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// An appointment or a meeting in its own window, with its own ribbon — the shape the reference
/// opens one in, and the same shell the compose window is.
/// </summary>
/// <remarks>
/// A thin host, as <see cref="ComposeWindow"/> is: the frame, the caption, the ribbon and the
/// Backstage belong here and everything about the appointment is <see cref="AppointmentSurface"/>.
/// </remarks>
public sealed class AppointmentWindow : Window
{
    private readonly AppointmentSurface _surface;
    private readonly RibbonView _ribbon;
    private readonly Grid _workspace = new();
    private readonly bool _meeting;
    private TextBlock _caption = null!;

    /// <summary>Set when the window was closed with Save &amp; Close, Send or Delete.</summary>
    public AppointmentResult? Result { get; private set; }

    public AppointmentWindow(
        CommandCatalog catalog,
        CalendarEvent appointment,
        IReadOnlyList<Collection> calendars,
        long collectionId,
        bool meeting)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _surface = new AppointmentSurface(appointment, calendars, collectionId, meeting);
        _meeting = meeting;

        Title = _surface.Title;
        Width = 1000;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowFrame.Apply(this);
        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);
        TextRendering.Apply(this);

        _ribbon = new RibbonView(catalog, AppointmentRibbonLayout.For(meeting))
        {
            CommandEnabled = _surface.IsCommandEnabled,
        };
        RibbonDisplayMemory.Wire(_ribbon, RibbonWindow.Compose, Environment.GetEnvironmentVariable("MAILBOX_RIBBON"));

        _ribbon.CommandInvoked += (_, e) =>
        {
            _ribbon.CloseFloatingBody();
            if (_surface.Invoke(e.Command) is { Length: > 0 } message) _surface.InfoBar = message;
            else RefreshPickers();
        };
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);

        // Scheduling Assistant and Tracking replace the form rather than adding buttons over it,
        // which is what the reference's own Show group does.
        _ribbon.ActiveTabChanged += (_, tab) => ShowWorkspaceFor(tab);

        _surface.TitleChanged += (_, _) =>
        {
            Title = _surface.Title;
            _caption.Text = _surface.Title;
        };
        _surface.Changed += (_, _) =>
        {
            _ribbon.RefreshEnablement();
            RefreshPickers();
        };
        _surface.Finished += (_, result) =>
        {
            Result = result;
            Close();
        };
        _surface.Cancelled += (_, _) => Close();

        if (meeting) _surface.InfoBar = "You haven't sent this meeting invitation yet.";

        Content = BuildRoot();

        // The harness cannot click a tab, and two of them replace the whole workspace.
        if (Environment.GetEnvironmentVariable("MAILBOX_APPOINTMENT_TAB") is { Length: > 0 } posed)
        {
            Opened += (_, _) => SelectTab(posed.Trim().ToLowerInvariant());
        }
        // Escape gives up, and everything else goes through the key map asked for this window's
        // own commands — Alt+S saves and closes, Ctrl+Enter sends — so a rebound shortcut is
        // rebound here too and a plain keystroke stays the reader typing.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _surface.Cancel();
                e.Handled = true;
                return;
            }

            if (Keystroke.Of(e) is not { } chord || Keystroke.IsTyping(chord)) return;
            if (App.Keys.CommandFor(chord, CommandSurface.Appointment) is not { } id) return;

            if (_surface.Invoke(id) is { Length: > 0 } message) _surface.InfoBar = message;
            e.Handled = true;
        };
    }

    /// <summary>The surface, for the harness: it poses the form and presses the big button.</summary>
    internal AppointmentSurface Surface => _surface;

    /// <summary>Selects a ribbon tab by id. Used by the fidelity harness, which cannot click.</summary>
    public void SelectTab(string tabId) => _ribbon.ActiveTabId = tabId;

    /// <summary>
    /// Puts the right thing under the bar for the tab that is showing: the form, the free/busy
    /// grid, or the tracking table.
    /// </summary>
    /// <remarks>
    /// The form is kept rather than rebuilt — it holds everything typed so far, and a reader who
    /// looks at the Scheduling Assistant and comes back has not lost their subject line.
    /// </remarks>
    private void ShowWorkspaceFor(string tab)
    {
        var body = tab switch
        {
            "scheduling" => SchedulingAssistant(),
            "tracking" when _meeting => new TrackingView(_surface.Current()),
            _ => (Control)_surface,
        };

        if (_workspace.Children.Count > 0 && ReferenceEquals(_workspace.Children[0], body)) return;

        // Re-parenting needs the child out of its old panel first, and the float layer stays on
        // top whatever is under it.
        _workspace.Children.Clear();
        _workspace.Children.Add(body);
        if (_floatLayer is not null) _workspace.Children.Add(_floatLayer);
    }

    /// <summary>
    /// The free/busy grid for this meeting: the organizer's own day in full, and a row per
    /// attendee saying what is known of theirs — which, without a free/busy service, is nothing.
    /// </summary>
    private Control SchedulingAssistant()
    {
        var meeting = _surface.Current();
        var day = DateOnly.FromDateTime(meeting.Start.Wall);

        var mine = new List<(DateTime Start, DateTime End)>();
        try
        {
            var source = new CalendarSource(App.Pim);
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            var to = from.AddDays(1);
            mine.AddRange(source
                .Between(from, to)
                .Where(e => e.Busy != BusyStatus.Free && e.Occurrence.Event.Uid != meeting.Uid)
                .Select(e => (e.StartWall, e.EndWall)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The calendar could not be read for the Scheduling Assistant.", ex);
        }

        var rows = new List<FreeBusyRow>
        {
            new(
                App.Settings.GetString(Mailbox.App.Options.OptionsPages.Keys.UserName) is { Length: > 0 } me ? me : "You",
                meeting.Organizer,
                IsOrganizer: true,
                Known: true,
                mine),
        };

        rows.AddRange(meeting.Attendees.Select(a => new FreeBusyRow(
            a.Name.Length > 0 ? a.Name : a.Address,
            a.Address,
            IsOrganizer: false,
            // Free/busy for somebody else wants a service this application does not have and
            // will not invent; the reference says the same thing about anyone outside its own
            // organization.
            Known: false,
            [])));

        var view = new FreeBusyView
        {
            Day = day,
            Rows = rows,
            MeetingStart = meeting.Start.Wall,
            MeetingEnd = meeting.End.Wall,
            From = new TimeOnly(Math.Min(8, meeting.Start.Wall.Hour), 0),
            To = new TimeOnly(Math.Max(18, Math.Min(23, meeting.End.Wall.Hour + 1)), 0),
        };

        view.MeetingMoved += (_, when) => _surface.MoveTo(when.Start, when.End);
        return view;
    }

    /// <summary>
    /// The two pickers on the bar show a value, so the bar has to be rebuilt when one changes.
    /// </summary>
    /// <remarks>
    /// A layout document is data, so this is a <c>with</c> copy carrying the new text rather than
    /// a reach into the rendered control — the same path Customize Ribbon takes, and the reason
    /// the row cannot drift from what the layout says.
    /// </remarks>
    private void RefreshPickers()
    {
        var layout = _ribbon.Layout;
        var bars = new Dictionary<string, SimplifiedBar>(layout.Simplified);
        var changed = false;

        foreach (var (tab, bar) in layout.Simplified)
        {
            var groups = bar.Groups
                .Select(group => group with
                {
                    Items = group.Items
                        .Select(item =>
                            item.Command == AppointmentCommands.ShowAs.Id ? item with { Text = _surface.ShowAsText }
                            : item.Command == AppointmentCommands.Reminder.Id ? item with { Text = _surface.ReminderText }
                            : item)
                        .ToList(),
                })
                .ToList();

            if (groups.Zip(bar.Groups).Any(pair => !pair.First.Items.SequenceEqual(pair.Second.Items)))
            {
                bars[tab] = bar with { Groups = groups };
                changed = true;
            }
        }

        if (changed) _ribbon.Layout = layout with { Simplified = bars };
    }

    private Control BuildRoot()
    {
        var layered = new Grid();
        var root = new DockPanel { LastChildFill = true };

        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var ribbonHost = new Border { Child = _ribbon, ZIndex = 2, Padding = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(ribbonHost, Dock.Top);
        root.Children.Add(ribbonHost);

        _workspace.Children.Add(_surface);
        _floatLayer = new Canvas { IsHitTestVisible = true, ZIndex = 1 };
        _workspace.Children.Add(_floatLayer);
        root.Children.Add(_workspace);

        layered.Children.Add(root);
        return WindowFrame.Rounded(layered);
    }

    private Canvas _floatLayer = null!;
    private Control? _floatingRibbon;

    private void ShowFloatingRibbon(Control? body)
    {
        if (_floatingRibbon is not null)
        {
            _floatLayer.Children.Remove(_floatingRibbon);
            _floatingRibbon = null;
        }

        if (body is null) return;
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);
        _floatLayer.Children.Add(body);
        _floatingRibbon = body;
    }

    private Control BuildTitleBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("calendar", 16),
            FontFamily = IconFont.Family,
            FontSize = 15,
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(icon, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        leading.Children.Add(icon);

        _caption = new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Bind(_caption, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        leading.Children.Add(_caption);

        Grid.SetColumn(leading, 0);
        grid.Children.Add(leading);

        var buttons = new CaptionButtons(this) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var host = new Border { Child = grid, Height = 40 };
        Bind(host, Border.BackgroundProperty, "titlebar.background.brush");
        WindowFrame.Drags(this, host);
        return host;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
