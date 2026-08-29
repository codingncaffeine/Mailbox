using Mailbox.Protocols;
using Mailbox.Store.Lists;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// By Type can tell items apart: the kind is marked when a message is stored, from the MIME
/// only the arrival ever sees, and the arrangement reads the mark.
/// </summary>
public sealed class ItemTypeTests
{
    private static MimeMessage Message(MimeEntity body)
    {
        var message = new MimeMessage { Subject = "Hello" };
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.Body = body;
        return message;
    }

    private static string? TypeOf(MimeEntity body)
        => MessageMapper.ToSummary(Message(body), "uid", 100, DateTimeOffset.UnixEpoch).ItemType;

    [Fact]
    public void AnOrdinaryMessageCarriesNoMark()
        => Assert.Null(TypeOf(new TextPart("plain") { Text = "Just words." }));

    [Theory]
    [InlineData("REQUEST", "meeting:request", "Meeting request")]
    [InlineData("REPLY", "meeting:reply", "Meeting response")]
    [InlineData("CANCEL", "meeting:cancel", "Meeting cancellation")]
    public void ACalendarPartsMethodMakesTheMeetingKind(string method, string mark, string band)
    {
        var calendar = new TextPart("calendar")
        {
            Text = $"BEGIN:VCALENDAR\nMETHOD:{method}\nEND:VCALENDAR",
        };
        calendar.ContentType.Parameters.Add("method", method);

        var multipart = new Multipart("alternative")
        {
            new TextPart("plain") { Text = "You are invited." },
            calendar,
        };

        Assert.Equal(mark, TypeOf(multipart));
        var groups = Arrangements.Group([new StubRow { ItemType = mark }], Arrangement.Type,
            descending: false, today: DateTimeOffset.UnixEpoch);
        Assert.Equal(band, groups.Single().Header);
    }

    [Fact]
    public void ASenderWhoOmitsTheMethodParameterStillReadsAsAMeeting()
    {
        var calendar = new TextPart("calendar")
        {
            Text = "BEGIN:VCALENDAR\nMETHOD:CANCEL\nEND:VCALENDAR",
        };

        Assert.Equal("meeting:cancel", TypeOf(new Multipart("mixed") { calendar }));
    }

    [Fact]
    public void ADispositionNotificationIsAReceipt()
    {
        var report = new MultipartReport("disposition-notification")
        {
            new TextPart("plain") { Text = "Your message was displayed." },
            new MessageDispositionNotification(),
        };

        Assert.Equal("receipt", TypeOf(report));
        Assert.Equal("Receipt", Arrangements.Group([new StubRow { ItemType = "receipt" }],
            Arrangement.Type, descending: false, today: DateTimeOffset.UnixEpoch).Single().Header);
    }

    [Fact]
    public void ARowStoredBeforeTheMarkExistedIsAMessage()
        => Assert.Equal("Message", Arrangements.Group([new StubRow { ItemType = null }],
            Arrangement.Type, descending: false, today: DateTimeOffset.UnixEpoch).Single().Header);

    private sealed record StubRow : IArrangeable
    {
        public string DisplayFrom => "Alice";
        public string Subject => "Hello";
        public DateTimeOffset Received => DateTimeOffset.UnixEpoch;
        public long SizeBytes => 100;
        public bool IsFlagged => false;
        public bool HasAttachment => false;
        public string? ItemType { get; init; }
    }
}
