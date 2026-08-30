namespace Mailbox.Core.Settings;

/// <summary>How a contact's full name is put together, as the People page's first row offers.</summary>
public enum FullNameOrder
{
    FirstMiddleLast,
    LastFirst,
    FirstLastLast,
}

/// <summary>
/// The People page's settings, read by the module that acts on them.
/// </summary>
/// <remarks>
/// The reading half of the page, exactly as <see cref="CalendarOptions"/> is for the calendar:
/// every accessor here has a feature behind it, and a row with no accessor is one nothing reads
/// yet, recorded per row.
/// </remarks>
public sealed class PeopleOptions(SettingsStore settings)
{
    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public const string FullNameOrderKey = "people.fullname.order";
    public const string FileAsOrderKey = "people.fileas.order";
    public const string CheckDuplicatesKey = "people.duplicates.check";
    public const string ShowIndexKey = "people.index.show";
    public const string ShowPhotographsKey = "people.photos.show";

    /// <summary>How a full name is assembled from its parts.</summary>
    public FullNameOrder FullName => (int)_settings.GetNumber(FullNameOrderKey, 0) switch
    {
        1 => FullNameOrder.LastFirst,
        2 => FullNameOrder.FirstLastLast,
        _ => FullNameOrder.FirstMiddleLast,
    };

    /// <summary>
    /// How the list files a contact — what it sorts by, and what the index letters down its side
    /// are taken from. The combo's own order.
    /// </summary>
    public int FileAsIndex => (int)_settings.GetNumber(FileAsOrderKey, 0);

    /// <summary>Whether saving a contact looks for one that is already there.</summary>
    public bool CheckDuplicates => _settings.GetBool(CheckDuplicatesKey, true);

    /// <summary>Whether the alphabet runs down the side of the list.</summary>
    public bool ShowIndex => _settings.GetBool(ShowIndexKey, true);

    /// <summary>Whether a contact's own picture is shown where there is one.</summary>
    public bool ShowPhotographs => _settings.GetBool(ShowPhotographsKey, true);
}
