using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// What a command says about the selection it needs, and what a layout says about a control it
/// draws greyed.
/// </summary>
/// <remarks>
/// Both were declared and read by nobody: the shell dimmed nothing, so Reply, Delete, Move and
/// the rest stayed lit with nothing selected and did nothing when pressed. The reading of them
/// lives in the window and cannot be tested from here — what can, and what these hold, is that
/// the declarations themselves are complete and consistent, because a command that forgets to
/// say it needs a selection is one the window has no way to grey.
/// </remarks>
public class CommandEnablementTests
{
    /// <summary>Every command that acts on the item under the cursor, module by module.</summary>
    public static TheoryData<string> ActsOnSelection() =>
    [
        "journal.delete", "journal.open", "journal.forward", "journal.categorize",
        "notes.delete", "notes.open", "notes.forward", "notes.moveto", "notes.categorize",
        "tasks.delete", "tasks.open", "tasks.complete", "tasks.remove", "tasks.followup",
        "tasks.categorize", "tasks.private", "tasks.importance.high", "tasks.importance.low",
        "tasks.reply", "tasks.replyall", "tasks.forward", "tasks.moveto",
        "mail.delete", "mail.reply", "mail.reply.all", "mail.forward", "item.categorize",
    ];

    [Theory]
    [MemberData(nameof(ActsOnSelection))]
    public void ACommandThatActsOnASelectionSaysSo(string id)
    {
        var command = All().Single(c => c.Id.Value == id);

        Assert.True(
            command.RequiresSelection || command.RequiresSingleSelection,
            $"{id} acts on what is selected and does not say so, so nothing can grey it.");
    }

    [Fact]
    public void ACommandThatNeedsNothingSelectedDoesNotClaimOtherwise()
    {
        // The other half of the rule: New, the views and the ones that move through time work
        // with an empty list, and a claim on the selection would grey them for no reason.
        foreach (var id in new[]
        {
            "journal.new", "notes.new", "tasks.new", "mail.new",
            "journal.today", "journal.back", "journal.next",
            "notes.view.icons", "tasks.view.todo", "calendar.today",
        })
        {
            var command = All().Single(c => c.Id.Value == id);

            Assert.False(
                command.RequiresSelection || command.RequiresSingleSelection,
                $"{id} works with nothing selected and would be greyed for nothing.");
        }
    }

    [Fact]
    public void TheLayoutsOnlyMarkGreyWhatCannotWork()
    {
        // IsDisabled is the layout's strongest statement about a control and now has a reader,
        // so anything carrying it draws greyed for the life of the window. Four entries mean it,
        // and each has a reason nothing in the application can change:
        //
        //   mail.readaloud             — there is no speech engine here.
        //   folder.permissions         — sharing a folder needs a server that offers it.
        //   view.conversations.settings — there is no conversation options page behind it.
        //   view.reader                — the reference greys Immersive Reader here too.
        //
        // A fifth appearing without a reason is worth a second look.
        var greyed = DefaultRibbonLayouts.Mail.Tabs
            .SelectMany(tab => tab.Groups)
            .SelectMany(group => group.Items)
            .Where(item => item.IsDisabled)
            .Select(item => item.Command.Value)
            .ToList();

        Assert.Equal(
            ["mail.readaloud", "folder.permissions", "view.conversations.settings", "view.reader"],
            greyed);
    }

    [Fact]
    public void TheTimeScalesAreTheOnesTheSettingWillKeep()
    {
        // The menu offers six and the setting reads six; a seventh on either side would be a
        // menu entry that silently becomes thirty minutes.
        var settings = SettingsStore.Transient();
        var options = new CalendarOptions(settings);

        foreach (var minutes in CalendarOptions.TimeScales)
        {
            options.SetTimeScale(minutes);
            Assert.Equal(minutes, options.TimeScaleMinutes);
        }

        options.SetTimeScale(45);
        Assert.Equal(30, options.TimeScaleMinutes);
    }

    private static IReadOnlyList<MailboxCommand> All() =>
    [
        .. MailCommands.All,
        .. ViewCommands.All,
        .. CalendarCommands.All,
        .. PeopleCommands.All,
        .. TaskCommands.All,
        .. NoteCommands.All,
        .. JournalCommands.All,
    ];
}
