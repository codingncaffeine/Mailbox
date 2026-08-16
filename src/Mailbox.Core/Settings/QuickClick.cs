namespace Mailbox.Core.Settings;

/// <summary>Which follow-up flag a single click sets.</summary>
public enum QuickFlag
{
    Today,
    Tomorrow,
    ThisWeek,
    NextWeek,
    NoDate,
    Complete,
}

/// <summary>
/// What a single click in the Categories or Flag column of the message list does.
/// </summary>
/// <remarks>
/// The reference's Quick Click, reached by "Set Quick Click…" at the foot of the Categorize and
/// Follow Up menus: one category and one flag are nominated, and thereafter clicking a row's
/// category cell tags it and clicking its flag cell flags it, without a menu. It is the fastest
/// way to work a folder, and the reason both columns are worth having at all.
/// <para>
/// Two settings rather than one so the two columns are independent: the reference nominates
/// them from separate menus and remembers them separately. The category is stored by name
/// rather than by row id, because a category belongs to an account's own file and the setting
/// is one for the application.
/// </para>
/// </remarks>
public sealed class QuickClickSettings(SettingsStore settings)
{
    /// <summary>The settings keys, named here so a test and the harness can pose them.</summary>
    public const string CategoryKey = "tags.quickclick.category";
    public const string FlagKey = "tags.quickclick.flag";

    private readonly SettingsStore _settings = settings;

    /// <summary>The nominated category's name, or empty for none — the reference ships with none.</summary>
    public string Category
    {
        get => _settings.GetString(CategoryKey);
        set
        {
            if (value.Length == 0) _settings.Remove(CategoryKey);
            else _settings.Set(CategoryKey, value);
        }
    }

    /// <summary>The nominated flag. Today is what the reference ships.</summary>
    public QuickFlag Flag
    {
        get => Enum.TryParse<QuickFlag>(_settings.GetString(FlagKey), ignoreCase: true, out var flag)
            ? flag
            : QuickFlag.Today;
        set => _settings.Set(FlagKey, value.ToString());
    }

    /// <summary>True when no category has been nominated, so a click on the column does nothing.</summary>
    public bool HasCategory => Category.Length > 0;

    /// <summary>The label the menus and dialogs use for a flag.</summary>
    public static string Label(QuickFlag flag) => flag switch
    {
        QuickFlag.Today => "Today",
        QuickFlag.Tomorrow => "Tomorrow",
        QuickFlag.ThisWeek => "This Week",
        QuickFlag.NextWeek => "Next Week",
        QuickFlag.NoDate => "No Date",
        QuickFlag.Complete => "Complete",
        _ => flag.ToString(),
    };

    /// <summary>
    /// When a flag falls due, from a clock. The reference's own presets: the end of the working
    /// day for today and tomorrow, the coming Friday for this week, and the one after for next.
    /// </summary>
    /// <returns>The due date, or null for a flag with no date — and for Complete, which has none.</returns>
    public static DateTimeOffset? DueDate(QuickFlag flag, DateTimeOffset now)
    {
        var daysToFriday = ((int)DayOfWeek.Friday - (int)now.DayOfWeek + 7) % 7;

        return flag switch
        {
            QuickFlag.Today => EndOfDay(now),
            QuickFlag.Tomorrow => EndOfDay(now.AddDays(1)),
            QuickFlag.ThisWeek => EndOfDay(now.AddDays(daysToFriday)),
            QuickFlag.NextWeek => EndOfDay(now.AddDays(daysToFriday + 7)),
            _ => null,
        };
    }

    /// <summary>Five in the afternoon, which is when the reference's presets fall due.</summary>
    public static DateTimeOffset EndOfDay(DateTimeOffset day)
        => new(day.Year, day.Month, day.Day, 17, 0, 0, day.Offset);
}
