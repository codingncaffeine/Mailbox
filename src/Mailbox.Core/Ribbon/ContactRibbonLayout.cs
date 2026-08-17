using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Contact window's ribbon: what its bar carries with one person open.
/// </summary>
/// <remarks>
/// <b>Transcribed from the reference's own Contact window</b>, left to right: Save &amp; Close,
/// Delete, Save &amp; New, Forward, [Send to OneNote], General, Details, Certificates, All Fields,
/// Email, Address Book, Check Names, Business Card, Picture, and the "…". Send to OneNote reaches
/// a product that is out of scope and is not offered — a stated divergence, as on the People bar.
/// <para>
/// The Show group is the reference's own idea of a page rather than more buttons: General,
/// Details, Certificates and All Fields replace the form with a different one, exactly as the
/// appointment window's Scheduling Assistant and Tracking replace theirs.
/// </para>
/// </remarks>
public static class ContactRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.People,

        QuickAccess = [ContactCommands.SaveAndClose.Id, MailCommands.Undo.Id],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["contact"] = Bar(
                Cluster("actions", "Actions",
                    RibbonItem.Small(ContactCommands.SaveAndClose.Id),
                    RibbonItem.Small(ContactCommands.Delete.Id),
                    RibbonItem.Sheddable(ContactCommands.SaveAndNew.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ContactCommands.Forward.Id, RibbonItemKind.DropDown)),

                Cluster("show", "Show",
                    RibbonItem.Sheddable(ContactCommands.General.Id),
                    RibbonItem.Sheddable(ContactCommands.Details.Id),
                    RibbonItem.Sheddable(ContactCommands.Certificates.Id),
                    RibbonItem.Sheddable(ContactCommands.AllFields.Id)),

                Cluster("communicate", "Communicate",
                    RibbonItem.Sheddable(ContactCommands.Email.Id, RibbonItemKind.DropDown)),

                Cluster("names", "Names",
                    RibbonItem.Sheddable(ContactCommands.AddressBook.Id),
                    RibbonItem.Sheddable(ContactCommands.CheckNames.Id)),

                Cluster("options", "Options",
                    RibbonItem.Sheddable(ContactCommands.BusinessCard.Id),
                    RibbonItem.Sheddable(ContactCommands.Picture.Id, RibbonItemKind.DropDown))),
        },

        Tabs =
        [
            new RibbonTab
            {
                Id = "contact",
                Label = "Contact",
                KeyTip = "H",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.SaveAndClose.Id),
                            RibbonItem.Large(ContactCommands.SaveAndNew.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ContactCommands.Delete.Id),
                            RibbonItem.Large(ContactCommands.Forward.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "show",
                        Label = "Show",
                        KeyTip = "ZS",
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.General.Id),
                            RibbonItem.Large(ContactCommands.Details.Id),
                            RibbonItem.Large(ContactCommands.Certificates.Id),
                            RibbonItem.Large(ContactCommands.AllFields.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "communicate",
                        Label = "Communicate",
                        KeyTip = "ZC",
                        Items = [RibbonItem.Large(ContactCommands.Email.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "names",
                        Label = "Names",
                        KeyTip = "ZN",
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.AddressBook.Id),
                            RibbonItem.Large(ContactCommands.CheckNames.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "options",
                        Label = "Options",
                        KeyTip = "ZO",
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.BusinessCard.Id),
                            RibbonItem.Large(ContactCommands.Picture.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        Items =
                        [
                            RibbonItem.Large(PeopleCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.FollowUp.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.Private.Id),
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>The layout, built once.</summary>
    public static RibbonLayout Contact { get; } = Build();
}
