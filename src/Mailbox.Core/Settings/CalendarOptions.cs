namespace Mailbox.Core.Settings;

/// <summary>
/// The Calendar page's settings, read by the views that act on them.
/// </summary>
/// <remarks>
/// The reading half of the page, exactly as <see cref="MailOptions"/> is for Mail: the rows
/// already persist under their keys, and until something reads one the setting is a control that
/// remembers itself and does nothing. Every accessor here has a feature behind it; a row with no
/// accessor is one nothing reads yet, and the row records it.
/// </remarks>
public sealed class CalendarOptions(SettingsStore settings)
{
    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    // ---- Keys, declared on the rows in OptionsPages ----------------------------------------

    public const string WorkDayStartKey = "calendar.workday.start";
    public const string WorkDayEndKey = "calendar.workday.end";
    public const string FirstDayOfWeekKey = "calendar.firstdayofweek";
    public const string ShowWeekNumbersKey = "calendar.showweeknumbers";
    public const string DefaultReminderKey = "calendar.reminder.default";
    public const string DefaultColourKey = "calendar.colour.default";
    public const string ColourEveryCalendarKey = "calendar.colour.all";
    public const string DailyTaskListKey = "calendar.dailytasks";
    public const string ShowBellKey = "calendar.showbell";
    public const string TimeScaleKey = "calendar.timescale";
    public const string DefaultViewKey = "calendar.view.default";
    public const string TimeZoneLabelKey = "calendar.timezone.label";
    public const string SecondTimeZoneShownKey = "calendar.timezone.second.shown";
    public const string SecondTimeZoneLabelKey = "calendar.timezone.second.label";
    public const string SecondTimeZoneIdKey = "calendar.timezone.second.id";

    /// <summary>One per weekday, so the work week is exactly the days that are ticked.</summary>
    public static string WorkDayKey(DayOfWeek day) => "calendar.workweek." + day.ToString().ToLowerInvariant();

    // ---- The working day --------------------------------------------------------------------

    /// <summary>
    /// The combos hold an index into a list of half hours from midnight, so 16 is 8:00 and 34 is
    /// 17:00 — the reference's own defaults.
    /// </summary>
    public TimeOnly WorkDayStart => HalfHour(WorkDayStartKey, 16);

    public TimeOnly WorkDayEnd => HalfHour(WorkDayEndKey, 34);

    private TimeOnly HalfHour(string key, int fallback)
    {
        var slot = Math.Clamp((int)_settings.GetNumber(key, fallback), 0, 47);
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(slot * 30));
    }

    /// <summary>The days Work Week shows. Monday to Friday unless the page says otherwise.</summary>
    public IReadOnlySet<DayOfWeek> WorkDays
    {
        get
        {
            var days = new HashSet<DayOfWeek>();
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                var standard = day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                if (_settings.GetBool(WorkDayKey(day), standard)) days.Add(day);
            }

            // A week with no days in it is a view with no columns; fall back rather than draw one.
            return days.Count > 0 ? days : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        }
    }

    public DayOfWeek FirstDayOfWeek => (DayOfWeek)Math.Clamp((int)_settings.GetNumber(FirstDayOfWeekKey, 0), 0, 6);

    public bool ShowWeekNumbers => _settings.GetBool(ShowWeekNumbersKey, false);

    // ---- Appointments -----------------------------------------------------------------------

    /// <summary>
    /// How long before an appointment its reminder is due, or null when the page says none.
    /// </summary>
    /// <remarks>
    /// The combo's entries are 0, 5, 10, 15, 30 minutes, 1 hour and 2 hours, and the reference
    /// ships 15 — index 3.
    /// </remarks>
    public int? DefaultReminderMinutes => (int)_settings.GetNumber(DefaultReminderKey, 3) switch
    {
        0 => 0,
        1 => 5,
        2 => 10,
        4 => 30,
        5 => 60,
        6 => 120,
        _ => 15,
    };

    /// <summary>Whether a reminder shows a bell against the appointment.</summary>
    public bool ShowBell => _settings.GetBool(ShowBellKey, true);

    /// <summary>
    /// The colours a calendar can be given, in the order the Options combo and the bar's Colour
    /// menu both list them.
    /// </summary>
    /// <remarks>
    /// One table because there are two ways to set the same thing: the page that chooses what a
    /// new calendar starts as, and the button that recolours the one in front of the reader. Two
    /// lists would drift the first time either grew an entry, and "Purple" would mean two
    /// different purples depending on where it was chosen.
    /// </remarks>
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        ("Blue", ""), ("Green", "#107C10"), ("Orange", "#CA5010"), ("Purple", "#8764B8"),
        ("Red", "#D13438"), ("Gray", "#69797E"), ("Yellow", "#C19C00"), ("Teal", "#038387"),
    ];

    /// <summary>The colour a calendar with none of its own is drawn in — the combo's order.</summary>
    public string DefaultColour
    {
        get
        {
            var at = (int)_settings.GetNumber(DefaultColourKey, 0);
            return at >= 0 && at < Palette.Count ? Palette[at].Hex : string.Empty;
        }
    }

    /// <summary>Whether that colour is forced on every calendar rather than only the new ones.</summary>
    public bool ColourEveryCalendar => _settings.GetBool(ColourEveryCalendarKey, false);

    /// <summary>
    /// Whether the day's tasks are drawn in a band under the day and week grids, and how much
    /// of it shows — the reference's Normal, Minimized and Off.
    /// </summary>
    public DailyTaskListMode DailyTaskList
        => (int)_settings.GetNumber(DailyTaskListKey, (double)(int)DailyTaskListMode.Off) switch
        {
            (int)DailyTaskListMode.Normal => DailyTaskListMode.Normal,
            (int)DailyTaskListMode.Minimized => DailyTaskListMode.Minimized,
            _ => DailyTaskListMode.Off,
        };

    /// <summary>The minute scales the reference offers, longest first as its menu lists them.</summary>
    public static IReadOnlyList<int> TimeScales { get; } = [60, 30, 15, 10, 6, 5];

    /// <summary>Minutes a row of the day and week views covers: 5, 6, 10, 15, 30 or 60.</summary>
    public int TimeScaleMinutes => (int)_settings.GetNumber(TimeScaleKey, 30) switch
    {
        5 => 5,
        6 => 6,
        10 => 10,
        15 => 15,
        60 => 60,
        _ => 30,
    };

    public void SetTimeScale(int minutes) => _settings.Set(TimeScaleKey, minutes);

    /// <summary>The arrangement the module opens in, remembered as it is changed.</summary>
    public string DefaultView => _settings.GetString(DefaultViewKey, "month");

    public void SetDefaultView(string view) => _settings.Set(DefaultViewKey, view);

    // ---- Time zones ---------------------------------------------------------------------------

    /// <summary>
    /// The clock the calendar is drawn on: the machine's own.
    /// </summary>
    /// <remarks>
    /// <b>Divergence.</b> The reference's Time zone dropdown sets the operating system's zone,
    /// which on this desktop belongs to the desktop — an application that changed it would be
    /// changing every clock on the machine from inside a mail client. So the row states what the
    /// machine says and the calendar follows it; what is settable here is what the columns are
    /// <em>called</em>, and the second zone shown beside them.
    /// </remarks>
    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    /// <summary>What the machine's own column of hours is headed, or empty for its offset.</summary>
    public string TimeZoneLabel => _settings.GetString(TimeZoneLabelKey, string.Empty);

    /// <summary>Whether the day and week views draw a second column of hours.</summary>
    public bool ShowSecondTimeZone => _settings.GetBool(SecondTimeZoneShownKey, false);

    public string SecondTimeZoneLabel => _settings.GetString(SecondTimeZoneLabelKey, string.Empty);

    /// <summary>
    /// The second zone, or null when there is none to show — which is also what an id this
    /// machine has never heard of comes to, rather than a view that will not draw.
    /// </summary>
    public TimeZoneInfo? SecondTimeZone
        => ShowSecondTimeZone ? TimeZoneChoices.Find(_settings.GetString(SecondTimeZoneIdKey, string.Empty)) : null;
}

/// <summary>
/// How much of the Daily Task List shows under the day and week grids.
/// </summary>
/// <remarks>
/// The reference's own three, and its own default: a reader who has never asked for the band
/// does not get one. Minimized keeps the header row so the band can be brought back without
/// going to the menu again, which is what "minimized" means there.
/// </remarks>
public enum DailyTaskListMode
{
    Off,
    Normal,
    Minimized,
}
