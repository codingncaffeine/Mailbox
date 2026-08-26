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

            // Transcribed from `new contact/new contact insert tab.png`, left to right: Attach
            // File, Signature, Table, Pictures, Screenshot, Shapes, Icons, 3D Models, Link,
            // Quick Parts, WordArt, Object. Not the compose window's Insert row — the reference
            // gives the two windows different ones, and each was read off its own capture.
            ["insert"] = Bar(
                Cluster("include", "Include",
                    RibbonItem.Sheddable(ComposeCommands.AttachFile.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ComposeCommands.Signature.Id, RibbonItemKind.DropDown)),

                Cluster("tables", "Tables",
                    RibbonItem.Sheddable(ComposeCommands.Table.Id, RibbonItemKind.DropDown)),

                Cluster("illustrations", "Illustrations",
                    RibbonItem.Sheddable(ComposeCommands.Pictures.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ComposeCommands.Screenshot.Id, RibbonItemKind.DropDown),
                    RibbonItem.Glyph(ComposeCommands.Shapes.Id, RibbonItemKind.DropDown),
                    RibbonItem.Glyph(ComposeCommands.Icons.Id),
                    RibbonItem.Glyph(ComposeCommands.Models3D.Id, RibbonItemKind.DropDown)),

                Cluster("links", "Links",
                    RibbonItem.Sheddable(ComposeCommands.Link.Id, RibbonItemKind.DropDown)),

                Cluster("text", "Text",
                    RibbonItem.Sheddable(ComposeCommands.QuickParts.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ComposeCommands.WordArt.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ComposeCommands.InsertObject.Id))),

        },

        // Format Text and Review are the compose window's own rows: the reference gives both
        // windows the same two tabs, so both bars carry the same run.
        SimplifiedRows = new Dictionary<string, IReadOnlyList<RibbonItem>>
        {
            ["formattext"] = ComposeRibbonLayout.FormatTextRow,
            ["review"] = ComposeRibbonLayout.ReviewRow,
        },

        Tabs =
        [
            // Leftmost, as in every window: File opens the Backstage rather than a ribbon.
            new RibbonTab
            {
                Id = "file",
                Label = "File",
                KeyTip = "F",
                IsBackstage = true,
                Groups = [],
            },

            new RibbonTab
            {
                Id = "contact",
                Label = "Contact",
                KeyTip = "H",
                Groups =
                [
                    // Transcribed from `classic contact ribbon.png`: eight groups, left to
                    // right, and only Show has a stack in it.
                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.SaveAndClose.Id),
                            RibbonItem.Large(ContactCommands.Delete.Id),
                            RibbonItem.Large(ContactCommands.SaveAndNew.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ContactCommands.Forward.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    // General leads, and the other three pages stack beside it — the one place
                    // on this tab the reference uses small buttons.
                    new RibbonGroup
                    {
                        Id = "show",
                        Label = "Show",
                        KeyTip = "ZS",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.General.Id),
                            RibbonItem.Small(ContactCommands.Details.Id),
                            RibbonItem.Small(ContactCommands.Certificates.Id),
                            RibbonItem.Small(ContactCommands.AllFields.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "communicate",
                        Label = "Communicate",
                        KeyTip = "ZC",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(ContactCommands.Email.Id),
                            RibbonItem.Large(ContactCommands.Meeting.Id),
                            RibbonItem.Large(ContactCommands.More.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "names",
                        Label = "Names",
                        KeyTip = "ZN",
                        CollapsePriority = 6,
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
                        CollapsePriority = 5,
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
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(PeopleCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.FollowUp.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.Private.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "immersive",
                        Label = "Immersive",
                        KeyTip = "ZI",
                        CollapsePriority = 8,
                        Items =
                        [
                            new RibbonItem
                            {
                                Command = ViewCommands.ImmersiveReader.Id,
                                Size = RibbonItemSize.Large,
                                IsDisabled = true,
                            },
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "zoom",
                        Label = "Zoom",
                        KeyTip = "ZZ",
                        CollapsePriority = 7,
                        Items = [RibbonItem.Large(ViewCommands.Zoom.Id)],
                    },
                ],
            },

            // The rest of the strip the capture shows: Insert, Format Text, Review, Help. The
            // notes field is a rich document like a message's body, so the two document tabs are
            // the compose window's own — one transcription, used twice.
            // Transcribed from `classic insert tab.png`: six groups, and the only stack is the
            // one under Text. Not the compose window's Insert tab — the reference gives the two
            // windows different ones, and each was read off its own capture.
            new RibbonTab
            {
                Id = "insert",
                Label = "Insert",
                KeyTip = "N",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "include",
                        Label = "Include",
                        KeyTip = "ZI",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.AttachFile.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.AttachItem.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.InsertBusinessCard.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Signature.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tables",
                        Label = "Tables",
                        KeyTip = "ZT",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(ComposeCommands.Table.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "illustrations",
                        Label = "Illustrations",
                        KeyTip = "ZL",
                        CollapsePriority = 6,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Pictures.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Shapes.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Icons.Id),
                            RibbonItem.Large(ComposeCommands.Models3D.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.SmartArt.Id),
                            RibbonItem.Large(ComposeCommands.Chart.Id),
                            RibbonItem.Large(ComposeCommands.Screenshot.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "links",
                        Label = "Links",
                        KeyTip = "ZK",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Link.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Bookmark.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "text",
                        Label = "Text",
                        KeyTip = "ZX",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.TextBox.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.QuickParts.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.WordArt.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.DropCap.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.DateAndTime.Id),
                            RibbonItem.Small(ComposeCommands.InsertObject.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "symbols",
                        Label = "Symbols",
                        KeyTip = "ZY",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Equation.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Symbol.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.HorizontalLine.Id),
                        ],
                    },
                ],
            },

            ComposeRibbonLayout.FormatTextTab,
            ComposeRibbonLayout.ReviewTab,

            new RibbonTab
            {
                Id = "help",
                Label = "Help",
                KeyTip = "Y",
                Groups = DefaultRibbonLayouts.HelpGroups,
            },
        ],
    };

    /// <summary>The layout, built once.</summary>
    public static RibbonLayout Contact { get; } = Build();
}
