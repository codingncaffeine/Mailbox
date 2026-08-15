using Mailbox.Core.Accounts;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

public class CommandIdTests
{
    [Theory]
    [InlineData("mail.reply")]
    [InlineData("mail.reply.all")]
    [InlineData("calendar.appointment.new")]
    [InlineData("plugin.acme.dothing")]
    public void AcceptsWellFormedIds(string value)
        => Assert.Equal(value, new CommandId(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("noDot")]
    [InlineData("Mail.Reply")]      // uppercase
    [InlineData("mail..reply")]     // empty segment
    [InlineData(".mail")]
    [InlineData("mail.")]
    [InlineData("mail reply")]      // whitespace
    public void RejectsMalformedIds(string value)
        => Assert.ThrowsAny<ArgumentException>(() => new CommandId(value));

    [Fact]
    public void AreaIsTheLeadingSegment()
        => Assert.Equal("mail", new CommandId("mail.reply.all").Area);

    [Fact]
    public void RecognisesPluginCommands()
    {
        Assert.True(new CommandId("plugin.acme.dothing").IsPluginCommand);
        Assert.False(new CommandId("mail.reply").IsPluginCommand);
    }
}

public class CommandCatalogTests
{
    private static CommandCatalog Loaded()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        return catalog;
    }

    /// <summary>Every set the application registers, which is what a layout resolves against.</summary>
    internal static CommandCatalog Everything()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        catalog.RegisterRange(ComposeCommands.All);
        return catalog;
    }

    [Fact]
    public void RegistersTheMailCommandSet()
        => Assert.Equal(MailCommands.All.Count(), Loaded().Count);

    [Fact]
    public void RejectsDuplicateIds()
    {
        var catalog = Loaded();
        Assert.Throws<InvalidOperationException>(() => catalog.Register(MailCommands.Reply));
    }

    [Fact]
    public void ResolvesById()
        => Assert.Equal("Reply All", Loaded().Get(new CommandId("mail.reply.all")).Label);

    [Fact]
    public void UnknownIdThrows()
        => Assert.Throws<KeyNotFoundException>(() => Loaded().Get(new CommandId("mail.nope")));

    /// <summary>
    /// Rule 5: additions beyond the reference application are present and findable, they are simply not placed
    /// by the default ribbon layout. Unplaced must never mean unreachable.
    /// </summary>
    [Fact]
    public void CommandsBeyondOutlookAreCataloguedButNotInDefaultLayout()
    {
        var catalog = Loaded();
        var beyond = catalog.BeyondDefaultLayout.Select(c => c.Id.Value).ToHashSet();

        Assert.Contains("mail.snooze", beyond);
        Assert.Contains("mail.viewsource", beyond);
        Assert.Contains("mail.trackers", beyond);

        // Present in the catalogue despite being unplaced.
        Assert.True(catalog.TryGet(new CommandId("mail.snooze"), out _));

        // And findable by search, which is how a user puts them on the ribbon.
        Assert.Contains(catalog.Search("snooze"), c => c.Id.Value == "mail.snooze");
    }

    [Fact]
    public void OutlookParityCommandsAreInTheDefaultLayout()
    {
        var catalog = Loaded();
        foreach (var id in (string[])["mail.reply", "mail.reply.all", "mail.forward", "mail.delete"])
        {
            Assert.True(catalog.Get(new CommandId(id)).InDefaultLayout, $"{id} should ship placed");
        }
    }

    [Fact]
    public void SearchRanksLabelMatchesAboveDescriptionMatches()
    {
        var results = Loaded().Search("reply");
        Assert.NotEmpty(results);
        Assert.StartsWith("Reply", results[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchIsEmptyForBlankTerms()
        => Assert.Empty(Loaded().Search("   "));

    /// <summary>
    /// KeyTip collisions are invisible until someone presses Alt, and a tab is the unit that
    /// matters: traversal shows one tab's commands at a time, so two tabs may reuse a letter
    /// and only a clash inside one tab makes it ambiguous.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void NoKeyTipConflictsWithinATab(string name, RibbonLayout layout)
    {
        var conflicts = layout.FindKeyTipConflicts(Everything());
        Assert.True(conflicts.Count == 0, $"{name}:\n{string.Join("\n", conflicts)}");
    }

    public static TheoryData<string, RibbonLayout> Layouts => new()
    {
        { "mail", DefaultRibbonLayouts.Mail },
        { "compose", DefaultRibbonLayouts.Compose },
    };

    [Fact]
    public void ModuleScopingFiltersCorrectly()
    {
        var catalog = Loaded();

        // Scoped to Mail only.
        Assert.Contains(catalog.ForModule(MailboxModule.Mail), c => c.Id.Value == "mail.reply");
        Assert.DoesNotContain(catalog.ForModule(MailboxModule.Calendar), c => c.Id.Value == "mail.reply");

        // ModuleScope.Any reaches every module.
        Assert.Contains(catalog.ForModule(MailboxModule.Calendar), c => c.Id.Value == "item.categorize");
    }

    [Fact]
    public void PluginCommandsCanBeUnregisteredWholesale()
    {
        var catalog = Loaded();
        var before = catalog.Count;

        catalog.Register(new MailboxCommand
        {
            Id = new("plugin.acme.one"),
            Label = "One",
            Description = "First.",
            Icon = "x",
            Category = "Acme",
            OwningPluginId = "acme",
        });
        catalog.Register(new MailboxCommand
        {
            Id = new("plugin.acme.two"),
            Label = "Two",
            Description = "Second.",
            Icon = "x",
            Category = "Acme",
            OwningPluginId = "acme",
        });

        Assert.Equal(before + 2, catalog.Count);
        Assert.Equal(2, catalog.UnregisterPlugin("acme"));
        Assert.Equal(before, catalog.Count);
    }

    [Fact]
    public void BuiltInCommandsHaveNoOwningPlugin()
        => Assert.All(Loaded().All.Where(c => !c.Id.IsPluginCommand), c => Assert.True(c.IsBuiltIn));

    /// <summary>Every command needs an icon and a screentip. A blank tooltip is a bug.</summary>
    [Fact]
    public void EveryCommandIsFullyDescribed()
    {
        foreach (var command in Loaded().All)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Label), $"{command.Id} has no label");
            Assert.False(string.IsNullOrWhiteSpace(command.Description), $"{command.Id} has no description");
            Assert.False(string.IsNullOrWhiteSpace(command.Icon), $"{command.Id} has no icon");
            Assert.False(string.IsNullOrWhiteSpace(command.Category), $"{command.Id} has no category");
            Assert.EndsWith(".", command.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KeyTipsFollowTheRibbonFrameworkRules()
    {
        foreach (var command in Loaded().All.Where(c => c.KeyTip is not null))
        {
            var tip = command.KeyTip!;
            Assert.InRange(tip.Length, 1, 3);
            Assert.DoesNotContain(tip, char.IsWhiteSpace);
            Assert.Equal(tip.ToUpperInvariant(), tip);
        }
    }
}

/// <summary>The letter drawn on the account disc when there is no photograph.</summary>
public class AccountIdentityTests
{
    [Theory]
    [InlineData("you@example.com", "Y")]
    [InlineData("alice.chen@example.com", "A")]
    [InlineData("7hills@example.com", "7")]
    [InlineData("  spaced@example.com", "S")]
    [InlineData("\"quoted\"@example.com", "Q")]
    [InlineData("<wrapped@example.com>", "W")]
    public void TakesTheFirstLetterOrDigit(string address, string expected)
        => Assert.Equal(expected, AccountIdentity.Initial(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@#$%")]
    public void FallsBackWhenThereIsNothingToDraw(string? address)
        => Assert.Equal(AccountIdentity.Unknown, AccountIdentity.Initial(address));
}
