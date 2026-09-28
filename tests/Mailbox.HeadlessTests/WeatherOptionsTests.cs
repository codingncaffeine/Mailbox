using System.Globalization;
using Mailbox.App.Options;
using Mailbox.Core.Settings;
using Mailbox.Core.Weather;

namespace Mailbox.HeadlessTests;

/// <summary>The Calendar page's Weather section writes what the Weather module reads.</summary>
public class WeatherOptionsTests
{
    private static IReadOnlyList<ComboRow> Rows()
        => [.. OptionsPages.Find("calendar")!.Sections.Single(s => s.Heading == "Weather").Rows.Cast<ComboRow>()];

    [Fact]
    public void EachUnitIsOfferedOnceUnderTheKeyTheModuleReads()
    {
        var keys = Rows().Select(r => r.Key).ToList();

        Assert.Equal(
            [WeatherUnits.TemperatureKey, WeatherUnits.SpeedKey, WeatherUnits.PrecipitationKey, WeatherUnits.PressureKey, WeatherUnits.DistanceKey],
            keys);
        Assert.All(Rows(), r => Assert.Equal(r.Items.Count, r.Values!.Count));
    }

    /// <summary>Every entry, chosen, is read back as the unit it names — by position, so the words and the enum cannot drift apart.</summary>
    [Fact]
    public void EveryEntryChosenReadsBackAsItsUnit()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        foreach (var row in Rows())
        {
            for (var i = 0; i < row.Values!.Count; i++)
            {
                var settings = SettingsStore.Transient();
                settings.Set(row.Key!, row.Values[i]);
                var units = WeatherUnits.Load(settings, english);

                int chosen = row.Key switch
                {
                    WeatherUnits.TemperatureKey => (int)units.Temperature,
                    WeatherUnits.SpeedKey => (int)units.Speed,
                    WeatherUnits.PrecipitationKey => (int)units.Precipitation,
                    WeatherUnits.PressureKey => (int)units.Pressure,
                    _ => (int)units.Distance,
                };
                Assert.True(i == chosen, $"{row.Label} {row.Items[i]} read back as entry {chosen}");
            }
        }

        var celsius = SettingsStore.Transient();
        celsius.Set(WeatherUnits.TemperatureKey, Rows()[0].Values![Rows()[0].Items.ToList().IndexOf("Celsius °C")]);
        Assert.Equal(TemperatureUnit.Celsius, WeatherUnits.Load(celsius, english).Temperature);
    }
}
