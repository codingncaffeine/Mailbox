using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.App.Weather;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Localization;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Weather;

namespace Mailbox.App.Views;

/// <summary>
/// The Weather module in the shell: switching to it, the rail's live weather, the schedule that
/// keeps it current, and the commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell for the reason the other modules' halves are: it needs the window's
/// ribbon, its dialogs and its status line.
/// </remarks>
public partial class MainWindow
{
    private WeatherWorkspace? _weatherModule;
    private DispatcherTimer? _weatherTicker;
    private CalendarWeatherBar? _calendarWeather;

    /// <summary>What the calendar's weather was last drawn from, so a tick that changes nothing draws nothing.</summary>
    private string? _calendarWeatherShown;

    /// <summary>The Weather ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout WeatherRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(WeatherRibbonLayout.Build()));

    private WeatherWorkspace EnsureWeather(ShellViewModel shell)
    {
        if (_weatherModule is not null) return _weatherModule;

        var workspace = new WeatherWorkspace(App.WeatherPlaces, App.Weather, () => App.WeatherUnits, () => App.Transfer.WorkOffline)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) =>
        {
            if (shell.Module == MailboxModule.Weather) shell.ModuleStatusLeft = workspace.Status;
            RefreshCommandEnablement();
        };
        workspace.AddRequested += (_, _) => _ = AddWeatherPlaceAsync(shell);
        workspace.UpdateRequested += (_, place) => UpdateWeather(shell, place, force: true);
        workspace.PlaceShown += (_, place) => UpdateWeather(shell, place, force: false);
        workspace.RemoveRequested += (_, place) => RemoveWeatherPlace(shell, place);
        workspace.MakeHomeRequested += (_, place) => MakeWeatherHome(shell, place);
        workspace.MoveRequested += (_, move) => MoveWeatherPlace(shell, move.Place, move.By);
        workspace.LinkRequested += (_, url) => OpenWeatherLink(shell, url);

        _weatherModule = workspace;
        return workspace;
    }

    /// <summary>
    /// The rail's live weather and the schedule behind it, wired once when the window is made.
    /// </summary>
    /// <remarks>
    /// The tick asks every minute and the receiver's own schedule decides whether that is a
    /// request: the home place, for the rail, and the place on screen while the module is up.
    /// Capture runs leave the tick off, as they leave the mail schedule off; a place brought on
    /// screen still fetches what is due, which is what a photograph of the module wants.
    /// </remarks>
    private void WireWeather(ShellViewModel shell)
    {
        App.Weather.Changed += (_, id) =>
        {
            if (App.WeatherPlaces.Home?.Id == id) Dispatcher.UIThread.Post(() => _ = ShowRailWeatherAsync(shell));
        };
        App.WeatherPlaces.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = ShowRailWeatherAsync(shell));
        App.Settings.Changed += (_, key) =>
        {
            if (key.Length > 0 && !key.StartsWith("weather.units.", StringComparison.Ordinal)) return;
            Dispatcher.UIThread.Post(() =>
            {
                _ = ShowRailWeatherAsync(shell);
                _weatherModule?.Reload();
                RefreshCommandChecked();
            });
        };

        // The calendar's weather follows its place's forecast, the list of places, the units, and
        // its own two settings.
        App.Weather.Changed += (_, id) => Dispatcher.UIThread.Post(() =>
        {
            if (CalendarWeatherPlace()?.Id == id) RefreshCalendarWeather();
        });
        App.WeatherPlaces.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshCalendarWeather);
        App.Settings.Changed += (_, key) =>
        {
            if (key.Length > 0 && !key.StartsWith("weather.units.", StringComparison.Ordinal)
                && !key.StartsWith("calendar.weather.", StringComparison.Ordinal))
            {
                return;
            }

            Dispatcher.UIThread.Post(RefreshCalendarWeather);
        };

        _ = ShowRailWeatherAsync(shell);

        if (Mailbox.App.Theming.WindowCapture.IsRequested) return;
        _weatherTicker = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _weatherTicker.Tick += (_, _) => TickWeather(shell);
        _weatherTicker.Start();
        TickWeather(shell);
    }

    private void TickWeather(ShellViewModel shell)
    {
        // Past midnight, Tomorrow is Today: the calendar's days move on whether or not anything
        // new has arrived.
        RefreshCalendarWeather();
        if (App.Transfer.WorkOffline) return;

        var home = App.WeatherPlaces.Home;
        if (home is not null) _ = Task.Run(() => App.Weather.RefreshAsync(home));

        // The calendar's place is kept as current as home is, when it is another: the calendar
        // shows it whether or not the Weather module has been opened.
        if (App.CalendarOptions.ShowWeather && CalendarWeatherPlace() is { } shown && shown.Id != home?.Id)
        {
            _ = Task.Run(() => App.Weather.RefreshAsync(shown));
        }

        if (shell.Module != MailboxModule.Weather) return;
        foreach (var place in App.WeatherPlaces.All.ToList())
        {
            if (place.Id != home?.Id) _ = Task.Run(() => App.Weather.RefreshAsync(place));
        }
    }

    /// <summary>
    /// The Weather tab on the rail draws the weather at home now, and says it when hovered — the
    /// picture decoded on the pool, set here once it is ready.
    /// </summary>
    private async Task ShowRailWeatherAsync(ShellViewModel shell)
    {
        var tab = shell.WeatherTab;
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        if (App.WeatherPlaces.Home is not { } home)
        {
            tab.Picture = await WeatherArt.LoadAsync(WeatherArt.Placeholder);
            tab.Tip = Strings.T("Weather — add a place to see its weather here");
            return;
        }

        if (App.Weather.Get(home.Id).Forecast is not { } forecast)
        {
            tab.Picture = await WeatherArt.LoadAsync(WeatherArt.Placeholder);
            tab.Tip = string.Format(culture, Strings.T("Weather — {0}"), home.Name);
            return;
        }

        var now = forecast.Current;
        var condition = now.Condition;
        tab.Picture = await WeatherArt.LoadAsync(condition.Icon) ?? await WeatherArt.LoadAsync(WeatherArt.Placeholder);
        tab.Tip = string.Format(culture, Strings.T("Weather — {0}, {1} in {2}"),
            App.WeatherUnits.FormatTemperature(now.Temperature), condition.Description.ToLower(culture), home.Name);
    }

    private void UpdateWeather(ShellViewModel shell, WeatherPlace place, bool force)
    {
        if (App.Transfer.WorkOffline)
        {
            if (force) shell.StatusRight = Strings.T("Working offline: the weather updates when you go back online.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await App.Weather.RefreshAsync(place, force);
            }
            catch (Exception ex)
            {
                // A refresh reports its failures on the place itself; anything that escapes it is
                // a fault here, logged rather than left to take the pool down.
                Log.Warn($"Updating the weather for {place.Label} failed.", ex);
            }
        });
    }

    /// <summary>
    /// The weather on the calendar's toolbar, built with the calendar: the place chosen on it, or
    /// home, and its next three days. A day, or Open Weather, opens the place in the Weather module.
    /// </summary>
    private CalendarWeatherBar CalendarWeather(ShellViewModel shell)
    {
        var bar = new CalendarWeatherBar();
        bar.PlaceChosen += (_, id) =>
        {
            App.CalendarOptions.WeatherPlace = id;
            if (App.WeatherPlaces.Find(id) is { } place) UpdateWeather(shell, place, force: false);
        };
        bar.WeatherRequested += (_, id) =>
        {
            SwitchModule(shell, MailboxModule.Weather);
            EnsureWeather(shell).Select(id);
        };
        bar.AddRequested += async (_, _) =>
        {
            // A place added from the calendar is the one the calendar then shows, as the
            // reference's location list does.
            if (await AddWeatherPlaceAsync(shell) is { } added) App.CalendarOptions.WeatherPlace = added.Id;
        };

        _calendarWeather = bar;
        _calendarWeatherShown = null;
        RefreshCalendarWeather();

        // A photograph of the calendar is of its weather too: the shot waits for the place's
        // refresh and the pictures that follow it, and the harness reads back what the bar says.
        if (Mailbox.App.Theming.WindowCapture.IsRequested && App.CalendarOptions.ShowWeather && CalendarWeatherPlace() is { } posed)
        {
            var hold = Mailbox.App.Theming.WindowCapture.Hold();
            _ = Task.Run(async () =>
            {
                try
                {
                    await App.Weather.RefreshAsync(posed);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        RefreshCalendarWeather();
                        Log.Info($"Harness: calendar weather — {bar.Said}.");
                    });
                    await Task.Delay(500);
                }
                finally
                {
                    hold.Dispose();
                }
            });
        }

        return bar;
    }

    /// <summary>The place the calendar shows the weather for: the one chosen on its toolbar, or home.</summary>
    private static WeatherPlace? CalendarWeatherPlace()
        => App.WeatherPlaces.Find(App.CalendarOptions.WeatherPlace) ?? App.WeatherPlaces.Home;

    /// <summary>Draws the calendar's weather again, when anything it shows has changed.</summary>
    /// <remarks>
    /// Drawn again only on a change, because drawing makes new buttons: a tick that redrew
    /// regardless would close the list of places, or a day's tip, under the reader's pointer.
    /// </remarks>
    private void RefreshCalendarWeather()
    {
        if (_calendarWeather is not { } bar) return;

        var place = App.CalendarOptions.ShowWeather ? CalendarWeatherPlace() : null;
        bar.IsVisible = place is not null;
        if (place is null)
        {
            _calendarWeatherShown = null;
            return;
        }

        var forecast = App.Weather.Get(place.Id).Forecast;
        var places = App.WeatherPlaces.All.ToList();
        var units = App.WeatherUnits;
        var now = Mailbox.Core.PosedClock.UtcNow;
        var day = forecast is null ? default : DateOnly.FromDateTime(forecast.LocalTime(now));
        var shown = $"{place.Id}|{forecast?.Fetched:O}|{day}|{units}|{string.Join(",", places.Select(p => $"{p.Id}={p.Label}"))}";
        if (shown == _calendarWeatherShown) return;

        _calendarWeatherShown = shown;
        bar.Show(place, forecast, places, units, now);
    }

    private async Task<WeatherPlace?> AddWeatherPlaceAsync(ShellViewModel shell)
    {
        var dialog = new AddLocationDialog(App.Weather);

        // MAILBOX_WEATHER_ADD types a search and, with "|add", takes the first match — so the
        // whole flow, searching through to the first forecast, is provable rather than only the box.
        // Without it, the photograph is of the dialog and the matches it found.
        if (Environment.GetEnvironmentVariable("MAILBOX_WEATHER_ADD") is { Length: > 0 } typed)
        {
            if (!typed.Contains("|add", StringComparison.OrdinalIgnoreCase)) CaptureNextWindow();
            dialog.Opened += (_, _) => dialog.Pose(typed);
        }

        await dialog.ShowDialog(this);
        if (dialog.Chosen is not { } chosen) return null;

        var kept = App.WeatherPlaces.Add(chosen);
        shell.StatusRight = ReferenceEquals(kept, chosen)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("Added {0}."), kept.Label)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("{0} is already in the list."), kept.Label);

        var workspace = EnsureWeather(shell);
        workspace.Reload();
        workspace.Select(kept.Id);

        // A capture of the flow is of the place's weather: the shot waits for its first fetch.
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            using (Mailbox.App.Theming.WindowCapture.Hold())
            {
                await Task.Run(() => App.Weather.RefreshAsync(kept));
                Log.Info($"Harness: added {kept.FullName}; forecast {(App.Weather.Get(kept.Id).Forecast is null ? "missing" : "arrived")}.");
                await Task.Delay(600);
            }

            return kept;
        }

        UpdateWeather(shell, kept, force: false);
        return kept;
    }

    private void RemoveWeatherPlace(ShellViewModel shell, WeatherPlace place)
    {
        if (!App.WeatherPlaces.Remove(place.Id)) return;
        _ = Task.Run(() => App.Weather.Forget(place.Id));
        shell.StatusRight = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("Removed {0}."), place.Label);
    }

    private void MakeWeatherHome(ShellViewModel shell, WeatherPlace place)
    {
        if (!App.WeatherPlaces.MakeHome(place.Id)) return;
        shell.StatusRight = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("{0} is home now: its weather shows on the rail."), place.Label);
        UpdateWeather(shell, place, force: false);
    }

    private void MoveWeatherPlace(ShellViewModel shell, WeatherPlace place, int by)
    {
        var at = App.WeatherPlaces.All.ToList().FindIndex(p => p.Id == place.Id);
        if (at < 0) return;
        App.WeatherPlaces.Move(place.Id, at + by);
        _weatherModule?.Select(place.Id);
    }

    private void OpenWeatherLink(ShellViewModel shell, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return;
        switch (Mailbox.Core.Platform.DesktopOpen.Open(address.AbsoluteUri))
        {
            case Mailbox.Core.Platform.DesktopOpenResult.Opened:
            case Mailbox.Core.Platform.DesktopOpenResult.Posed:
                shell.StatusRight = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("Opened {0}."), address.Host);
                break;
            default:
                shell.StatusRight = Strings.T("The desktop could not open that address.");
                break;
        }
    }

    /// <summary>
    /// The Weather module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunWeatherCommand(ShellViewModel shell, CommandId id)
    {
        if (id == WeatherCommands.AddPlace.Id)
        {
            _ = AddWeatherPlaceAsync(shell);
            return true;
        }

        if (id == WeatherCommands.Celsius.Id)
        {
            var celsius = App.WeatherUnits.Temperature != TemperatureUnit.Celsius;
            App.Settings.Set(WeatherUnits.TemperatureKey, celsius ? "celsius" : "fahrenheit");
            shell.StatusRight = celsius ? Strings.T("Temperatures in Celsius.") : Strings.T("Temperatures in Fahrenheit.");
            return true;
        }

        if (id != WeatherCommands.Update.Id && id != WeatherCommands.RemovePlace.Id && id != WeatherCommands.MakeHome.Id
            && id != WeatherCommands.MoveUp.Id && id != WeatherCommands.MoveDown.Id)
        {
            return false;
        }

        if (shell.Module != MailboxModule.Weather || _weatherModule?.Selected is not { } place)
        {
            shell.StatusRight = Strings.T("Choose a place first.");
            return true;
        }

        if (id == WeatherCommands.Update.Id) UpdateWeather(shell, place, force: true);
        else if (id == WeatherCommands.RemovePlace.Id) RemoveWeatherPlace(shell, place);
        else if (id == WeatherCommands.MakeHome.Id) MakeWeatherHome(shell, place);
        else if (id == WeatherCommands.MoveUp.Id) MoveWeatherPlace(shell, place, -1);
        else MoveWeatherPlace(shell, place, 1);
        return true;
    }

    /// <summary>Selects a place for a harness run: <c>MAILBOX_SELECT</c> names it, as it names an item in every module.</summary>
    private void PoseWeather(ShellViewModel shell)
    {
        var workspace = EnsureWeather(shell);
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } wanted)
        {
            Log.Info($"Harness: weather place {workspace.PoseSelect(wanted)}.");
        }

        // A photograph of the module is of the weather, not of "Fetching the weather…": the shot
        // waits for the place's refresh — whatever was due, or nothing when the cache was fresh —
        // and for the redraw that follows it.
        if (Mailbox.App.Theming.WindowCapture.IsRequested && workspace.Selected is { } place)
        {
            var hold = Mailbox.App.Theming.WindowCapture.Hold();
            _ = Task.Run(async () =>
            {
                try
                {
                    await App.Weather.RefreshAsync(place);
                    var weather = App.Weather.Get(place.Id);
                    Log.Info($"Harness: weather for {place.Label}: "
                             + (weather.Forecast is { } f ? $"{f.Current.Temperature:0.#}°C, code {f.Current.Code}" : $"no forecast ({weather.Error})")
                             + $", {weather.Alerts.Count} alert(s), discussion {(weather.Discussion is null ? "none" : weather.Discussion.Office)}"
                             + $", air {(weather.AirQuality?.On(AirQualityIndex.For(place.CountryCode)) is { } air ? $"{AirQualityIndex.Name(AirQualityIndex.For(place.CountryCode))} {air.Value} {air.Leading}" : "none")}.");
                    await Task.Delay(600);

                    // MAILBOX_WEATHER_EXPAND=1 opens the warnings' details and the discussion.
                    if (Environment.GetEnvironmentVariable("MAILBOX_WEATHER_EXPAND") is { Length: > 0 })
                    {
                        var opened = await Dispatcher.UIThread.InvokeAsync(workspace.PoseExpand);
                        Log.Info($"Harness: opened {opened} expander(s) on the weather page.");
                        await Task.Delay(400);
                    }

                    // MAILBOX_WEATHER_SCROLL=map photographs the map, once it has painted.
                    if (Environment.GetEnvironmentVariable("MAILBOX_WEATHER_SCROLL") == "map")
                    {
                        var map = await Dispatcher.UIThread.InvokeAsync(() => workspace.PoseMapAsync(
                            Environment.GetEnvironmentVariable("MAILBOX_WEATHER_LAYER"),
                            Environment.GetEnvironmentVariable("MAILBOX_WEATHER_OVERLAYS")));
                        Log.Info($"Harness: weather page {map}.");
                        await Task.Delay(300);
                    }

                    // MAILBOX_WEATHER_SCROLL=air photographs the air quality card.
                    if (Environment.GetEnvironmentVariable("MAILBOX_WEATHER_SCROLL") == "air")
                    {
                        var said = await Dispatcher.UIThread.InvokeAsync(workspace.PoseScrollToAir);
                        Log.Info($"Harness: weather page {said}.");
                        await Task.Delay(400);
                    }

                    // MAILBOX_WEATHER_SCROLL=<pixels> photographs what is below the fold.
                    if (double.TryParse(Environment.GetEnvironmentVariable("MAILBOX_WEATHER_SCROLL"),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset))
                    {
                        var said = await Dispatcher.UIThread.InvokeAsync(() => workspace.PoseScroll(offset));
                        Log.Info($"Harness: weather page {said}.");
                        await Task.Delay(400);
                    }
                }
                finally
                {
                    hold.Dispose();
                }
            });
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_WEATHER_ADD") is { Length: > 0 })
        {
            _ = AddWeatherPlaceAsync(shell);
        }
    }
}
