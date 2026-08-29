using Mailbox.App.Views;
using Mailbox.Scheduling;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The note window's one contract: closing it is the save, and closing it unchanged is not.
/// </summary>
/// <remarks>
/// A note has no Save button and no title field, so everything this window claims rests on what
/// it hands back when it closes — the writing, the title taken from the writing's first line, and
/// the categories that are also its colour. None of that had ever been exercised through the form:
/// every note in every seed and every test is written straight through the repository, so the one
/// path a reader actually uses was the one path nothing ran.
/// <para>
/// The second test is the regression. Because the window collects whatever it holds on any close,
/// opening a note to read it used to hand back a fresh modified time and the shell wrote the row
/// again — a spurious revision, queued to whatever server the folder lives on. What must hold is
/// that a window given a note and closed gives the same note back, field for field.
/// </para>
/// </remarks>
public class NoteWindowTests
{
    private static JournalEntry ANote() => new JournalEntry
    {
        Uid = "note@example.com",
        When = EventTime.At(new DateTime(2026, 8, 15, 18, 40, 0), TimeZoneInfo.Local.Id),
        Categories = ["Green Category"],
    }.WithBody("Shopping\nmilk, bread, coffee");

    [Fact]
    public void ClosingSavesTheWriting()
    {
        var saved = HeadlessApp.OnUiThread(() =>
        {
            var window = new NoteWindow(ANote());
            Assert.True(window.SetFormField("body", "Saturday list\nmilk and coffee"));
            Assert.True(window.SetFormField("categories", "Red Category"));

            window.Close();
            return window.Result;
        });

        Assert.NotNull(saved);
        Assert.Equal("Saturday list\nmilk and coffee", saved.Description);

        // The title is the first line and nothing else: there is no field to type one into.
        Assert.Equal("Saturday list", saved.Summary);
        Assert.Equal(["Red Category"], saved.Categories);
    }

    [Fact]
    public void ClosingWithoutTypingGivesTheSameNoteBack()
    {
        var before = ANote();
        var after = HeadlessApp.OnUiThread(() =>
        {
            var window = new NoteWindow(before);
            window.Close();
            return window.Result;
        });

        Assert.NotNull(after);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal(before.Categories, after.Categories);
    }

    /// <summary>
    /// An empty note is not a note. The shell throws away one closed without a word in it, and it
    /// can only tell by what the window gives back.
    /// </summary>
    [Fact]
    public void AnEmptyNoteComesBackEmpty()
    {
        var made = HeadlessApp.OnUiThread(() =>
        {
            var window = new NoteWindow(new JournalEntry { Uid = "empty@example.com" });
            window.Close();
            return window.Result;
        });

        Assert.NotNull(made);
        Assert.Equal(string.Empty, made.Description.Trim());
        Assert.Equal(JournalEntry.Untitled, made.Titled());
    }
}
