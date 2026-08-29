using Mailbox.App.Views;
using Mailbox.Scheduling;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The appointment form's Time zones tick does what its label says: reveals a zone per time,
/// carries the appointment's own zones in, and writes the chosen zone out.
/// </summary>
/// <remarks>
/// The tick was drawn and wired to nothing — three references, all of them paint — so an
/// appointment could not be written in any zone but the machine's, and one opened from another
/// zone would have been quietly re-filed as local on save. These hold the round trip.
/// </remarks>
public class AppointmentZoneTests
{
    private static CalendarEvent At(string tzId) => new()
    {
        Uid = "test",
        Summary = "Call",
        Start = EventTime.At(new DateTime(2026, 8, 20, 17, 0, 0), tzId),
        End = EventTime.At(new DateTime(2026, 8, 20, 18, 0, 0), tzId),
    };

    [Fact]
    public void AnAppointmentInAnotherZoneKeepsItOnSave()
    {
        var kept = HeadlessApp.OnUiThread(() =>
        {
            var surface = new AppointmentSurface(
                At("Pacific/Auckland"), [], collectionId: 1, meeting: false);

            return (surface.Current().Start.TzId, surface.Current().End.TzId);
        });

        Assert.Equal("Pacific/Auckland", kept.Item1);
        Assert.Equal("Pacific/Auckland", kept.Item2);
    }

    [Fact]
    public void ALocalAppointmentStaysLocalWithTheTickOff()
    {
        var kept = HeadlessApp.OnUiThread(() =>
        {
            var surface = new AppointmentSurface(
                At(TimeZoneInfo.Local.Id), [], collectionId: 1, meeting: false);

            return surface.Current().Start.TzId;
        });

        Assert.Equal(TimeZoneInfo.Local.Id, kept);
    }
}
