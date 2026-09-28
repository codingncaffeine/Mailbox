namespace Mailbox.Core.Commands;

/// <summary>
/// The Weather module's commands: the places a reader keeps, and how their weather is shown.
/// </summary>
/// <remarks>
/// The reference keeps its weather in a strip across the top of the calendar, where a place is
/// added by "Add Location" and a city name or zip code — so that is what adding one is called
/// here too. The rest is authored, in the order a reader reaches for it: add a place, put it in
/// order, see its latest, and choose the units.
/// <para>
/// Every one of them is in the catalogue, so each can be searched for, rebound and placed like
/// any other command.
/// </para>
/// </remarks>
public static class WeatherCommands
{
    public static readonly MailboxCommand AddPlace = new()
    {
        Id = new("weather.place.add"),
        Label = "Add Location",
        Description = "Add a place by its name or its zip code.",
        Icon = "location",
        Category = "New",
        Scope = ModuleScope.Weather,
        KeyTip = "AL",

        // Ctrl+N makes the open module's new thing, which here is a place.
        AlsoGestures = ["Ctrl+N"],
        GestureHome = ModuleScope.Weather,
    };

    public static readonly MailboxCommand RemovePlace = new()
    {
        Id = new("weather.place.remove"),
        Label = "Remove Location",
        Description = "Stop keeping this place's weather.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Weather,
        KeyTip = "RL",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MakeHome = new()
    {
        Id = new("weather.place.home"),
        Label = "Set as Home",
        Description = "Show this place's weather on the rail, and open the module on it.",
        Icon = "star",
        Category = "Arrange",
        Scope = ModuleScope.Weather,
        KeyTip = "SH",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MoveUp = new()
    {
        Id = new("weather.place.up"),
        Label = "Move Up",
        Description = "Move this place up the list.",
        Icon = "chevron-up",
        Category = "Arrange",
        Scope = ModuleScope.Weather,
        KeyTip = "MU",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MoveDown = new()
    {
        Id = new("weather.place.down"),
        Label = "Move Down",
        Description = "Move this place down the list.",
        Icon = "chevron-down",
        Category = "Arrange",
        Scope = ModuleScope.Weather,
        KeyTip = "MD",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Update = new()
    {
        Id = new("weather.update"),
        Label = "Update Now",
        Description = "Fetch the latest weather for this place now, whether or not it is due.",
        Icon = "refresh",
        Category = "Send & Receive",
        Scope = ModuleScope.Weather,
        KeyTip = "UN",
    };

    /// <summary>
    /// Celsius on, Fahrenheit off: the one unit people switch often, on the ribbon. Wind, rain,
    /// pressure and distance each have their own choice in Options.
    /// </summary>
    public static readonly MailboxCommand Celsius = new()
    {
        Id = new("weather.units.celsius"),
        Label = "Celsius",
        Description = "Show temperatures in degrees Celsius; off shows them in Fahrenheit.",
        Icon = "view-settings",
        Category = "View",
        Scope = ModuleScope.Weather,
        KeyTip = "CE",
        IsToggle = true,
    };

    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        AddPlace, RemovePlace, MakeHome, MoveUp, MoveDown, Update, Celsius,
    ];
}
