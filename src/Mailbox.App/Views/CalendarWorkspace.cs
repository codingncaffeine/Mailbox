using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>Which arrangement the calendar module is showing.</summary>
public enum CalendarViewKind
{
    Day,
    WorkWeek,
    Week,
    Month,
    Schedule,
}

/// <summary>
/// The Calendar module's workspace: the date navigator and calendar list down the left, the
/// toolbar row across the top, and whichever view is arranged beneath it.
/// </summary>
/// <remarks>
/// The panel the mail module's three panes sit in, with a different inside. Measured off the
/// calendar captures: the toolbar band is 61 tall on the workspace's own ground, with Today
/// 52×24 at x=11, the two arrows 24×24 at 68 and 96, the date at 140 in 18px semibold, and the
/// view picker against the right edge.
/// </remarks>
public sealed class CalendarWorkspace : Border
{
    /// <summary>The toolbar band above the grid.</summary>
    private const double ToolbarHeight = 61;

    /// <summary>Where the buttons sit in that band, and how tall they are.</summary>
    private const double ButtonTop = 20;
    private const double ButtonHeight = 24;

    private readonly PimRepository _repository;
    private readonly CalendarOptions _options;
    private readonly CalendarSource _source;
    private readonly DateNavigator _navigator = new();
    private readonly MonthView _month = new();
    private readonly TimeGridView _timeGrid = new();
    private readonly ScheduleView _schedule = new();
    private readonly DailyTaskListView _dailyTasks = new();
    private readonly DockPanel _timeGridWithTasks = new();
    private readonly Panel _viewHost = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _pickerLabel = new();
    private readonly TextBlock _pickerGlyph = new();
    private readonly StackPanel _calendarList = new();
    private readonly Border _navPane;

    private CalendarViewKind _kind = CalendarViewKind.Month;

    /// <summary>
    /// True while the Week view shows seven days from the anchor rather than the calendar week —
    /// Next 7 Days. Cleared the moment the reader chooses a view or a range of their own.
    /// </summary>
    private bool _rolling;
    private DateOnly _anchor;
    private IReadOnlyList<CalendarEntry> _entries = [];
    private DailyTaskListMode _dailyTaskList;

    public CalendarWorkspace(PimRepository repository, CalendarOptions options, DateOnly today, DateTime? now)
    {
        Avalonia.Automation.AutomationProperties.SetName(_month, "Month calendar");
        Avalonia.Automation.AutomationProperties.SetName(_timeGrid, "Calendar");
        Avalonia.Automation.AutomationProperties.SetName(_schedule, "Schedule");
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _source = new CalendarSource(repository);
        Today = today;
        Now = now;
        _anchor = today;
        FirstDayOfWeek = options.FirstDayOfWeek;
        _kind = Parse(options.DefaultView);
        _month.FirstDay = WeekStart(new DateOnly(today.Year, today.Month, 1));
        _month.Selected = today;
        _navigator.Anchor = new DateOnly(today.Year, today.Month, 1);

        Margin = Resource<Thickness>("workspace.inset.rightmargin") ?? default;
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        _navPane = BuildNavPane();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        var content = BuildContent();
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        Child = grid;

        WireViews();
        Reload();
    }

    /// <summary>Today, as the module believes it — pinned by the harness, live otherwise.</summary>
    public DateOnly Today { get; }

    /// <summary>The moment the now line is drawn at, or null when it should not be.</summary>
    public DateTime? Now { get; }

    /// <summary>The first day of the week, as Options names it.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Sunday;

    public CalendarViewKind Kind => _kind;

    /// <summary>The day the view is arranged around.</summary>
    public DateOnly Anchor => _anchor;

    public CalendarEntry? SelectedEntry { get; private set; }

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    /// <summary>What the status bar says: the reference counts the items the view is showing.</summary>
    public string Status => Search.Length == 0 ? $"Items: {_entries.Count}" : $"Items: {_entries.Count} found";

    /// <summary>What Instant Search is looking for here, matched against the store's own index.</summary>
    public string Search
    {
        get;
        set
        {
            var wanted = value?.Trim() ?? string.Empty;
            if (field == wanted) return;
            field = wanted;
            Reload();
        }
    } = string.Empty;

    /// <summary>What the view is showing, in start order.</summary>
    public IReadOnlyList<CalendarEntry> Entries => _entries;

    /// <summary>The month grid, when it is the one on show.</summary>
    internal MonthView? Month => _kind == CalendarViewKind.Month ? _month : null;

    /// <summary>The day, work week or week grid, when one of them is on show.</summary>
    internal TimeGridView? TimeGrid
        => _kind is CalendarViewKind.Day or CalendarViewKind.WorkWeek or CalendarViewKind.Week ? _timeGrid : null;

    /// <summary>The schedule, when it is the one on show.</summary>
    internal ScheduleView? Schedule => _kind == CalendarViewKind.Schedule ? _schedule : null;

    /// <summary>The date navigator down the left, for a pose that presses a day in it.</summary>
    internal DateNavigator Navigator => _navigator;

    /// <summary>What the toolbar's date reads, which a capture can show and a log cannot.</summary>
    internal string TitleForHarness => _title.Text ?? string.Empty;

    public event EventHandler? Changed;

    /// <summary>A double click on empty time, or the New Appointment command with a day chosen.</summary>
    public event EventHandler<(DateTime Start, bool AllDay)>? NewRequested;

    public event EventHandler<CalendarEntry>? EntryOpened;

    /// <summary>An appointment dragged to another time, or an edge of one dragged to a new length.</summary>
    public event EventHandler<EntryMove>? EntryMoved;

    // ---- The left-hand pane ------------------------------------------------------------------

    private Border BuildNavPane()
    {
        var pane = new Border { Width = Resource<double>("nav.width.value") ?? 235 };
        pane[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");

        var stack = new StackPanel();

        // The collapse chevron, in the same corner the mail pane keeps it.
        var collapse = new Button
        {
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 4, 0),
            FontFamily = IconFont.Family,
            FontSize = 12,
            Content = IconGlyphs.GetOrEmpty("collapse-left", 16),
        };
        ToolTip.SetTip(collapse, "Collapse the Folder Pane");
        collapse.Click += (_, _) => IsNavVisible = false;
        stack.Children.Add(collapse);

        _navigator.Margin = new Thickness(6, 0, 5, 0);
        _navigator.Today = Today;
        _navigator.FirstDayOfWeek = FirstDayOfWeek;
        stack.Children.Add(_navigator);

        var rule = new Border { Height = 1, Margin = new Thickness(5, 6, 5, 0) };
        rule[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");
        stack.Children.Add(rule);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Height = 24,
            Margin = new Thickness(9, 12, 0, 0),
        };
        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(chevron);
        var headerText = new TextBlock { Text = "My Calendars", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        headerText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(headerText);
        stack.Children.Add(header);

        _calendarList.Margin = new Thickness(5, 0, 4, 0);
        stack.Children.Add(_calendarList);

        pane.Child = stack;
        return pane;
    }

    /// <summary>
    /// One row per calendar. The reference draws the shown one as a filled band with its name in
    /// bold, indented past where a tick would go, and no tick at all while there is one calendar
    /// to show; a second calendar is what makes hiding one mean anything, so that is when the
    /// tick appears.
    /// </summary>
    private void RefreshCalendarList()
    {
        _calendarList.Children.Clear();
        var calendars = _source.Calendars();

        foreach (var calendar in calendars)
        {
            var row = new Border { Height = 24, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            if (calendar.IsVisible) row[!BackgroundProperty] = new DynamicResourceExtension("nav.item.selected.brush");

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (calendars.Count > 1)
            {
                var tick = new CheckBox
                {
                    IsChecked = calendar.IsVisible,
                    Margin = new Thickness(22, 0, 0, 0),
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };
                line.Children.Add(tick);
            }

            var name = new TextBlock
            {
                Text = calendar.DisplayName,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(calendars.Count > 1 ? 0 : 43, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            line.Children.Add(name);
            row.Child = line;

            var id = calendar.Id;
            var visible = calendar.IsVisible;
            row.PointerPressed += (_, _) => ToggleCalendar(id, !visible);
            _calendarList.Children.Add(row);
        }
    }

    private void ToggleCalendar(long id, bool visible)
    {
        // The last shown calendar cannot be hidden — that would leave nothing to look at, which
        // is not a state the reference lets you reach either.
        if (!visible && _source.Calendars().Count(c => c.IsVisible) <= 1) return;
        _repository.SetCollectionVisible(id, visible);
        Reload();
    }

    // ---- The toolbar and the view -------------------------------------------------------------

    private Control BuildContent()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions($"{ToolbarHeight.ToString(CultureInfo.InvariantCulture)},*") };

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        left.Children.Add(ToolbarButton("Today", 52, 0, GoToday));
        left.Children.Add(ToolbarGlyph("chevron-left", 5, () => Step(-1), "Back"));
        left.Children.Add(ToolbarGlyph("chevron-right", 4, () => Step(1), "Forward"));

        _title.FontSize = 18;
        _title.FontWeight = FontWeight.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Top;
        _title.Margin = new Thickness(20, 22, 0, 0);
        _title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("calendar.toolbar.text.brush");
        left.Children.Add(_title);
        bar.Children.Add(left);

        var picker = BuildPicker();
        Grid.SetColumn(picker, 2);
        bar.Children.Add(picker);

        grid.Children.Add(bar);

        Grid.SetRow(_viewHost, 1);
        grid.Children.Add(_viewHost);
        return grid;
    }

    private Control BuildPicker()
    {
        var button = new Button
        {
            Height = ButtonHeight,
            Margin = new Thickness(0, ButtonTop, 20, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 0),
            BorderThickness = default,
            Background = Brushes.Transparent,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _pickerGlyph.FontFamily = IconFont.Family;
        _pickerGlyph.FontSize = 17;
        _pickerGlyph.VerticalAlignment = VerticalAlignment.Center;
        _pickerGlyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("calendar.toolbar.text.brush");
        row.Children.Add(_pickerGlyph);

        _pickerLabel.FontSize = 15;
        _pickerLabel.VerticalAlignment = VerticalAlignment.Center;
        _pickerLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("calendar.toolbar.text.brush");
        row.Children.Add(_pickerLabel);

        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("calendar.toolbar.text.brush");
        row.Children.Add(chevron);
        button.Content = row;

        // Left-aligned under the button, as every dropdown in the reference is, and each entry
        // carrying the same icon the ribbon draws that arrangement with.
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var kind in (CalendarViewKind[])[CalendarViewKind.Day, CalendarViewKind.WorkWeek, CalendarViewKind.Week, CalendarViewKind.Month, CalendarViewKind.Schedule])
        {
            var choice = kind;
            var item = new MenuItem { Header = Label(kind), Icon = ViewIcon(kind) };
            item.Click += (_, _) => SetView(choice);
            menu.Items.Add(item);
        }

        button.Flyout = menu;

        // An attached flyout is built once, here, and a posed run can only read it back at the
        // moment it exists — a popup never reaches a capture.
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            Mailbox.Core.Diagnostics.Log.Info(
                "Harness: " + FlyoutProbe.Describe("the calendar view picker", menu));
        }

        return button;
    }

    private Button ToolbarButton(string text, double width, double gap, Action click)
    {
        var button = new Button
        {
            Width = width,
            Height = ButtonHeight,
            Margin = new Thickness(gap, ButtonTop, 0, 0),
            Padding = default,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, FontSize = 14 },
        };
        button[!BackgroundProperty] = new DynamicResourceExtension("calendar.toolbar.button.brush");
        button[!BorderBrushProperty] = new DynamicResourceExtension("calendar.toolbar.button.border.brush");
        button[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("calendar.toolbar.button.text.brush");
        button.Click += (_, _) => click();
        return button;
    }

    private Button ToolbarGlyph(string icon, double gap, Action click, string tip)
    {
        var button = ToolbarButton(string.Empty, ButtonHeight, gap, click);
        button.Content = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 12,
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private void WireViews()
    {
        // One day picked is one day shown, in whatever arrangement was up. The reference answers
        // a single date with the Day view; picking the 3rd out of a month grid and being given the
        // month it falls in is not an answer to that gesture. GoToRange already says what a run of
        // days means, and one day is the run of length one.
        _navigator.DayPicked += (_, day) => GoToRange(day, day);
        _navigator.RangePicked += (_, range) => GoToRange(range.First, range.Last);

        // The arrows scroll the little months on their own, as the reference's do — looking
        // ahead is not the same as going there, and the grid moves only when a day is picked.
        // Any move of the view puts the navigator back where the view is.
        _navigator.Stepped += (_, months) =>
        {
            _navigatorAnchor = (_navigatorAnchor ?? NavigatorAnchor).AddMonths(months);
            RefreshNavigator();
        };

        _month.Scrolled += (_, weeks) =>
        {
            _month.FirstDay = _month.FirstDay.AddDays(weeks * 7);
            _anchor = _month.DominantMonth;
            AfterMove();
        };
        _month.DaySelected += (_, day) => Select(day);
        _month.MoreRequested += (_, day) => GoToRange(day, day);
        // At the working day's start, which Options names and AddFocusTime already reads — not a
        // nine o'clock of this file's own invention.
        _month.DayActivated += (_, day) => NewRequested?.Invoke(this, (day.ToDateTime(App.CalendarOptions.WorkDayStart), false));
        _month.EntrySelected += (_, entry) => SelectedEntry = entry;
        _month.EntryActivated += (_, entry) => EntryOpened?.Invoke(this, entry);

        _timeGrid.DaySelected += (_, day) => Select(day);
        _timeGrid.SlotActivated += (_, slot) => NewRequested?.Invoke(this, (slot.Day.ToDateTime(slot.At), false));
        _timeGrid.EntrySelected += (_, entry) => SelectedEntry = entry;
        _timeGrid.EntryActivated += (_, entry) => EntryOpened?.Invoke(this, entry);

        _schedule.EntrySelected += (_, entry) => SelectedEntry = entry;
        _schedule.EntryActivated += (_, entry) => EntryOpened?.Invoke(this, entry);
        _schedule.SlotActivated += (_, slot) => NewRequested?.Invoke(this, (slot.Day.ToDateTime(slot.At), false));

        // Schedule View shows one day, so its own page keys ask for the day either side — the
        // same move the toolbar's arrows make, through the same door.
        _schedule.DayStepped += (_, direction) => Step(direction);

        // A drag means the same thing in whichever view it happened, so all three hand it on
        // through one event rather than each being wired to its own handler. Schedule View's
        // second axis is which calendar a row belongs to, so its drag can also say to move the
        // appointment to another calendar — carried on the same move, in a field the other two
        // leave null.
        _month.EntryMoved += (_, move) => EntryMoved?.Invoke(this, move);
        _timeGrid.EntryMoved += (_, move) => EntryMoved?.Invoke(this, move);
        _schedule.EntryMoved += (_, move) => EntryMoved?.Invoke(this, move);
    }

    // ---- Moving about -------------------------------------------------------------------------

    public void GoToday() => GoTo(Today);

    public void GoTo(DateOnly day)
    {
        _anchor = day;
        _month.FirstDay = WeekStart(new DateOnly(day.Year, day.Month, 1));
        AfterMove();
    }

    /// <summary>The navigator's drag: show exactly the days picked, in whichever view fits them.</summary>
    public void GoToRange(DateOnly first, DateOnly last)
    {
        var days = last.DayNumber - first.DayNumber + 1;
        _anchor = first;

        // The picked day is highlighted in the grid it lands in, not merely shown.
        _month.Selected = first;
        _timeGrid.Selected = first;
        _rolling = false;
        _kind = days switch
        {
            1 => CalendarViewKind.Day,
            <= 7 => CalendarViewKind.Week,
            _ => CalendarViewKind.Month,
        };
        if (_kind == CalendarViewKind.Month) _month.FirstDay = WeekStart(first);
        AfterMove();
    }

    /// <summary>The next seven days, as the Go To group's second button asks for.</summary>
    /// <remarks>
    /// Rolling, not the calendar week containing today: pinned to a Thursday, the week view
    /// showed four days already past and three ahead, and was only ever right when today was
    /// the week's first day.
    /// </remarks>
    public void ShowNextSevenDays()
    {
        _kind = CalendarViewKind.Week;
        _rolling = true;
        _anchor = Today;
        AfterMove();
    }

    public void Step(int direction)
    {
        _anchor = _kind switch
        {
            CalendarViewKind.Day or CalendarViewKind.Schedule => _anchor.AddDays(direction),
            CalendarViewKind.WorkWeek or CalendarViewKind.Week => _anchor.AddDays(direction * 7),
            _ => _anchor.AddMonths(direction),
        };

        if (_kind == CalendarViewKind.Month) _month.FirstDay = WeekStart(new DateOnly(_anchor.Year, _anchor.Month, 1));
        AfterMove();
    }

    public void SetView(CalendarViewKind kind)
    {
        _kind = kind;
        _rolling = false;
        if (kind == CalendarViewKind.Month) _month.FirstDay = WeekStart(new DateOnly(_anchor.Year, _anchor.Month, 1));
        _options.SetDefaultView(kind.ToString().ToLowerInvariant());
        AfterMove();
    }

    internal static CalendarViewKind Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "day" => CalendarViewKind.Day,
        "workweek" or "work-week" => CalendarViewKind.WorkWeek,
        "week" => CalendarViewKind.Week,
        "schedule" => CalendarViewKind.Schedule,
        _ => CalendarViewKind.Month,
    };

    /// <summary>
    /// Selects the entry whose summary carries the words, as a click on its chip does, for a
    /// harness run — a command that acts on the selection has to find one, and a run cannot click.
    /// </summary>
    public string PoseSelect(string named)
    {
        var entry = _entries.FirstOrDefault(e => e.Summary.Contains(named, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return $"nothing on show matches “{named}” ({_entries.Count} item(s))";

        // What the click does, in the click's own order: the entry on every view — the object
        // itself, since the chip's selected look is a reference comparison — and the entry's
        // first day, which is where the grids put their day mark.
        SelectedEntry = entry;
        _month.SelectedEntry = entry;
        _timeGrid.SelectedEntry = entry;
        _schedule.SelectedEntry = entry;
        _month.Selected = entry.Days().First;
        _timeGrid.Selected = entry.Days().First;
        Changed?.Invoke(this, EventArgs.Empty);
        return $"“{entry.Summary}”";
    }

    private void Select(DateOnly day)
    {
        _anchor = day;
        _month.Selected = day;
        _timeGrid.Selected = day;
        RefreshNavigator();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AfterMove()
    {
        _navigatorAnchor = null;
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the navigator has been scrolled to on its own, or null when it follows the view.</summary>
    private DateOnly? _navigatorAnchor;

    private DateOnly WeekStart(DateOnly date)
    {
        var lead = (((int)date.DayOfWeek - (int)FirstDayOfWeek) + 7) % 7;
        return date.AddDays(-lead);
    }

    // ---- Reading the store --------------------------------------------------------------------

    /// <summary>The first and last day on show, which is what the navigator marks.</summary>
    public (DateOnly First, DateOnly Last) VisibleDays()
    {
        switch (_kind)
        {
            case CalendarViewKind.Day:
            case CalendarViewKind.Schedule:
                return (_anchor, _anchor);
            case CalendarViewKind.WorkWeek:
            case CalendarViewKind.Week:
            {
                var days = DaysOf(_kind);
                return (days[0], days[^1]);
            }

            default:
                return (_month.FirstDay, _month.FirstDay.AddDays((_month.Weeks * 7) - 1));
        }
    }

    private IReadOnlyList<DateOnly> DaysOf(CalendarViewKind kind)
    {
        _timeGrid.Anchor = _anchor;
        _timeGrid.FirstDayOfWeek = FirstDayOfWeek;
        _timeGrid.Span = kind == CalendarViewKind.WorkWeek
            ? TimeGridSpan.WorkWeek
            : _rolling ? TimeGridSpan.Rolling : TimeGridSpan.Week;
        return _timeGrid.Days();
    }

    /// <summary>Reads the store for the days on show and hands them to whichever view is up.</summary>
    public void Reload()
    {
        var (first, last) = VisibleDays();
        var zone = TimeZoneInfo.Local;
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone.GetUtcOffset(first.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var to = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone.GetUtcOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

        // Two Options rows that used to persist and change nothing, read on every reload so a
        // change on the page shows on the next draw: whether every calendar takes the default
        // colour, and whether a reminder draws a bell.
        _source.ForcedColour = _options.ColourEveryCalendar ? _options.DefaultColour : null;

        // A colour category outranks the calendar's colour on the block, as the reference
        // draws it; the token resolves through the live theme, so a theme change re-reads it
        // on the next reload.
        _source.CategoryColour = name =>
            App.Categories.Named(name)?.ColourToken is { Length: > 0 } token
            && Avalonia.Application.Current?.FindResource(token + ".brush") is Avalonia.Media.ISolidColorBrush brush
                ? brush.Color
                : null;
        _month.ShowBell = _options.ShowBell;
        _timeGrid.ShowBell = _options.ShowBell;
        _schedule.ShowBell = _options.ShowBell;

        try
        {
            _entries = _source.Between(from, to);

            // Instant Search narrows what the grid draws to what the store's own index found,
            // which is this module's answer to the box. **Divergence, stated:** the reference
            // swaps the calendar for a list of results; a grid drawing only the matches keeps
            // them where they are in time, which is the thing a calendar is for, and the status
            // bar counts them.
            if (Search.Length > 0)
            {
                var found = _repository.Search(Search).Select(i => i.Id).ToHashSet();
                _entries = [.. _entries.Where(e => found.Contains(e.ItemId))];
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The calendar store could not be read.", ex);
            _entries = [];
        }

        RefreshCalendarList();
        RefreshNavigator();
        ShowView();
        RefreshDailyTasks();
        _title.Text = TitleText();
        _pickerLabel.Text = Label(_kind);
        _pickerGlyph.Text = IconGlyphs.GetOrEmpty(IconFor(_kind), 20);
    }

    /// <summary>
    /// The month the navigator opens on: the one the view is <em>titled</em> for, not the one its
    /// first cell falls in.
    /// </summary>
    /// <remarks>
    /// A month view starting on 26 July is August's, and the reference's navigator says August
    /// and September — anchoring on the first cell's month showed July and August, one month
    /// behind the grid beside it, with the range block sliding off the bottom.
    /// </remarks>
    private DateOnly NavigatorAnchor => _kind == CalendarViewKind.Month
        ? _month.DominantMonth
        : new DateOnly(_anchor.Year, _anchor.Month, 1);

    private void RefreshNavigator()
    {
        var (first, last) = VisibleDays();
        _navigator.RangeStart = first;
        _navigator.RangeEnd = last;
        _navigator.Today = Today;
        _navigator.FirstDayOfWeek = FirstDayOfWeek;
        _navigator.Anchor = _navigatorAnchor ?? NavigatorAnchor;

        // Bold the days with something on them, over the whole stretch the navigator draws.
        var from = _navigator.Anchor.AddDays(-7);
        var to = _navigator.Anchor.AddMonths(Math.Max(1, _navigator.MonthsShown)).AddDays(14);
        try
        {
            _navigator.BusyDays = _source.DaysWithItems(
                new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The calendar store could not be read for the date navigator.", ex);
        }
    }

    private void ShowView()
    {
        Control view;
        switch (_kind)
        {
            case CalendarViewKind.Schedule:
                _schedule.Day = _anchor;
                _schedule.Today = Today;
                _schedule.Now = Now;
                _schedule.WorkDayStart = _options.WorkDayStart;
                _schedule.WorkDayEnd = _options.WorkDayEnd;
                _schedule.Rows = _source.Calendars()
                    .Where(c => c.IsVisible)
                    .Select(c => new ScheduleRow(c.Id, c.DisplayName, ParseColour(c.Color), c.IsReadOnly))
                    .ToList();
                _schedule.Entries = _entries;
                view = _schedule;
                break;

            case CalendarViewKind.Day:
            case CalendarViewKind.WorkWeek:
            case CalendarViewKind.Week:
                _timeGrid.Anchor = _anchor;
                _timeGrid.Today = Today;
                _timeGrid.Now = Now;
                _timeGrid.FirstDayOfWeek = FirstDayOfWeek;
                _timeGrid.WorkDays = _options.WorkDays;
                _timeGrid.WorkDayStart = _options.WorkDayStart;
                _timeGrid.WorkDayEnd = _options.WorkDayEnd;
                _timeGrid.SlotMinutes = _options.TimeScaleMinutes;
                _timeGrid.ViewZone = _options.TimeZone;
                _timeGrid.ZoneLabel = _options.TimeZoneLabel;
                _timeGrid.SecondZone = _options.SecondTimeZone;
                _timeGrid.SecondZoneLabel = _options.SecondTimeZoneLabel;
                _timeGrid.Span = _kind switch
                {
                    CalendarViewKind.Day => TimeGridSpan.Day,
                    CalendarViewKind.WorkWeek => TimeGridSpan.WorkWeek,
                    _ => TimeGridSpan.Week,
                };
                _timeGrid.Entries = _entries;
                view = ShowDailyTasks ? WithDailyTasks() : _timeGrid;
                break;

            default:
                _month.Today = Today;
                _month.Entries = _entries;
                view = _month;
                break;
        }

        if (_viewHost.Children.Count == 1 && ReferenceEquals(_viewHost.Children[0], view)) return;
        _viewHost.Children.Clear();
        _viewHost.Children.Add(view);
    }

    /// <summary>
    /// The Daily Task List's state, and the band it draws under the day and week grids.
    /// </summary>
    /// <remarks>
    /// Only under the time grids. A month cell has no room beneath it for a band and the
    /// reference draws none there either, so the setting is kept and the band simply does not
    /// appear — switching to Week brings it back rather than losing what was asked for.
    /// </remarks>
    public DailyTaskListMode DailyTasks
    {
        get => _dailyTaskList;
        set
        {
            if (_dailyTaskList == value) return;
            _dailyTaskList = value;
            ShowView();
            RefreshDailyTasks();
        }
    }

    private bool ShowDailyTasks => _dailyTaskList != DailyTaskListMode.Off;

    private Control WithDailyTasks()
    {
        if (_timeGridWithTasks.Children.Count == 0)
        {
            DockPanel.SetDock(_dailyTasks, Dock.Bottom);
            _timeGridWithTasks.Children.Add(_dailyTasks);
            _timeGridWithTasks.Children.Add(_timeGrid);
        }

        return _timeGridWithTasks;
    }

    /// <summary>
    /// Fills the band: what is due on each of the days the grid is showing.
    /// </summary>
    /// <remarks>
    /// Due date, not start date — the reference's band is what has to be done that day, and a
    /// task that began last week and is due on Friday belongs under Friday. A task with no due
    /// date belongs under no day and is left to the to-do list, which is where a reader looks for
    /// what is not yet urgent.
    /// </remarks>
    private void RefreshDailyTasks()
    {
        if (!ShowDailyTasks) return;

        _dailyTasks.Minimized = _dailyTaskList == DailyTaskListMode.Minimized;
        _dailyTasks.RulerWidth = TimeGridView.RulerSpanFor(_options.SecondTimeZone is not null);

        var days = _timeGrid.Days();
        _dailyTasks.Days = days;

        if (_dailyTaskList == DailyTaskListMode.Minimized || days.Count == 0)
        {
            _dailyTasks.Tasks = [];
            return;
        }

        try
        {
            var wanted = days.ToHashSet();
            var tasks = new List<DailyTask>();

            foreach (var row in new TaskBook(_repository).Rows(Today, includeCompleted: true))
            {
                if (row.Task.Due is not { } due) continue;
                var day = DateOnly.FromDateTime(due.Wall);
                if (!wanted.Contains(day)) continue;
                tasks.Add(new DailyTask(day, row.Summary, row.IsComplete, row.IsOverdue));
            }

            _dailyTasks.Tasks = tasks;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("The task store could not be read for the Daily Task List.", ex);
            _dailyTasks.Tasks = [];
        }
    }

    private string TitleText() => _kind switch
    {
        CalendarViewKind.Day or CalendarViewKind.Schedule => _anchor.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture),
        CalendarViewKind.WorkWeek or CalendarViewKind.Week => WeekTitle(),
        _ => _month.DominantMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
    };

    private string WeekTitle()
    {
        var days = DaysOf(_kind);
        var first = days[0];
        var last = days[^1];
        return first.Month == last.Month
            ? $"{first.ToString("MMMM d", CultureInfo.CurrentCulture)} – {last.Day.ToString(CultureInfo.CurrentCulture)}, {last.Year.ToString(CultureInfo.CurrentCulture)}"
            : $"{first.ToString("MMMM d", CultureInfo.CurrentCulture)} – {last.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)}";
    }

    /// <summary>The icon the ribbon draws an arrangement with, so the picker's menu agrees with it.</summary>
    private static Control ViewIcon(CalendarViewKind kind)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(IconFor(kind), 16),
            FontFamily = IconFont.Family,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        return glyph;
    }

    private static string IconFor(CalendarViewKind kind) => kind switch
    {
        CalendarViewKind.Day => "day-view",
        CalendarViewKind.WorkWeek => "work-week",
        CalendarViewKind.Week => "week-view",
        CalendarViewKind.Schedule => "schedule-view",
        _ => "month-view",
    };

    internal static string Label(CalendarViewKind kind) => kind switch
    {
        CalendarViewKind.Day => "Day",
        CalendarViewKind.WorkWeek => "Work Week",
        CalendarViewKind.Week => "Week",
        CalendarViewKind.Schedule => "Schedule View",
        _ => "Month",
    };

    private static Color? ParseColour(string text)
        => !string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var colour) ? colour : null;

    private static T? Resource<T>(string key) where T : struct
        => Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed ? typed : null;
}
