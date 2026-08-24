# Writing a Mailbox plugin

Mailbox has a .NET plugin system. A plugin is a directory you install, like a theme: it holds a
manifest and an assembly, it can be enabled and disabled from **File › Options › Add-ins**
without restarting, and everything it adds — commands, a ribbon tab, mail hooks, bars above a
message — enters the same registries Mailbox's own features use. There is no marketplace and
Mailbox will never fetch a plugin for you; a plugin is a file you chose to put there.

## Two things to read before writing one

**Trust.** A plugin runs in-process, with Mailbox's own reach. The permissions in the manifest
are *disclosure*: Mailbox refuses API calls whose permission a plugin did not declare and shows
the attempt on the Add-ins page, but .NET has no in-process sandbox, and a plugin determined to
misbehave can. Installing a plugin is trusting its author as far as you trust Mailbox itself.
That sentence is the security model, stated plainly.

**Licence.** Mailbox is GPL-3.0. A plugin links `Mailbox.Plugins.Api`, so a plugin you
*distribute* is a derived work and must itself be GPL-compatible. A plugin you write and keep
for yourself carries no obligation.

## The shape on disk

```
~/.local/share/mailbox/plugins/
└── wordcount/
    ├── plugin.json
    └── WordCount.dll        (plus any libraries of its own)
```

`plugin.json`:

```json
{
  "id": "example.wordcount",
  "name": "Word Count",
  "version": "1.0.0",
  "api": "1.0",
  "assembly": "WordCount.dll",
  "author": "A. Person",
  "description": "Counts the words in the selected folder.",
  "type": "WordCount.Plugin",
  "permissions": ["ui", "mail"]
}
```

| Field | Meaning |
|---|---|
| `id` | The plugin's stable name: lowercase letters and digits in dot-separated segments. Command ids are built from it (`plugin.example.wordcount.count`), and those are persisted in ribbon layouts and key bindings, so the id is API — once shipped it does not change. |
| `api` | The API version the plugin was compiled against. A plugin asking for a newer API than the running Mailbox carries is refused at the door, with both versions named. Growth is additive within a major version: a 1.0 plugin loads on every 1.x host. |
| `assembly` | A file name beside the manifest — never a path. |
| `type` | Optional: the entry type's full name. Without it the assembly must hold exactly one public `IPlugin`. |
| `permissions` | What the plugin intends to use — see the table below. Unknown names are kept and shown, so a manifest written for a newer Mailbox still reads. |

## The API

Reference `Mailbox.Plugins.Api` (and nothing else of Mailbox), implement `IPlugin`, and register
everything from `Initialize`:

```csharp
using Mailbox.Plugins.Api;

public sealed class Plugin : IPlugin
{
    public void Initialize(IPluginHost host)
    {
        host.Commands.Register(
            new PluginCommand
            {
                Name = "count",
                Label = "Word Count",
                Description = "Counts the words in every message of the open folder.",
            },
            () => host.Log("pressed"));

        host.Commands.AddRibbonTab(new PluginRibbonTab
        {
            Name = "tools",
            Label = "Word Count",
            Module = "mail",   // or calendar, people, tasks, notes, journal
            Groups = [new PluginRibbonGroup { Label = "Count", Commands = ["count"] }],
        });
    }
}
```

The host hands a plugin these surfaces; each is gated by a manifest permission.

| Surface | Permission | What it does |
|---|---|---|
| `host.Settings` | — | A few values of the plugin's own, kept with Mailbox's settings under keys namespaced by plugin id. |
| `host.Log` | — | One line in Mailbox's log, prefixed with the plugin's id. |
| `host.Commands.Register` | `ui` | A command in the same catalogue as every built-in: searchable in Customize Ribbon, placeable on any tab and the Quick Access Toolbar, bindable to a key. Runs on the UI thread. |
| `host.Commands.AddRibbonTab` | `ui` | A tab of the plugin's own, in both ribbon layouts. Plugins get no space on the shipped tabs — first run stays exactly what it is — but a reader can move plugin commands anywhere from Customize Ribbon. |
| `host.ReadingPane.AddInfoBar` | `ui` | Asked about every message the pane renders; a non-null answer draws a bar above it, after Mailbox's own bars. Called on the UI thread mid-render: answer from what you already know — never block, never touch the network here. |
| `host.Mail` | `mail`, writes `mail-write` | Accounts, folders and messages as plain records, plus the verbatim RFC822 bytes. Bring your own MIME parser for structure; the bytes are exactly what Mailbox stores. Move, delete and mark need `mail-write`. |
| `host.Pim` | `pim`, writes `pim-write` | Calendars, task lists, note lists and address books. Items travel as their own iCalendar or vCard text; `Save` goes through the same codecs Mailbox's editors use, so a plugin-written item has real columns and syncs like any other. |
| `host.Pipeline.OnArrival` | `arrival` | Runs on every message as it arrives, after the junk filter, ignored conversations, the Focused Inbox and the rules. Background thread. Answer `None`, `MoveTo(folderName)` or `Delete`; a name that is no folder is logged and the message stays put. |
| `host.Columns.Add` | `ui` | A column on the message list's table views: offered in Show Columns beside the built-in fields, placed and widened like any of them, persisted in saved views by its id. The value provider runs on the UI thread per visible row — answer from the row alone. |
| `host.Accounts.RegisterProvider` | `accounts` | Consulted by the Add Account wizard as an address is typed: recognise it and answer with the servers, and your guidance goes under the boxes. Password authentication only — a provider needing its own sign-in dance is a later API. |
| `host.Pipeline.OnSending` | `sending` | Runs on every message as the writer sends it, before it is queued and before any cryptography. UI thread. `SendDecision.Stop(reason)` keeps the message where it is and tells the writer which plugin stopped it and why. |
| — | `network` | Declares that the plugin does network I/O of its own. Nothing in the API does it for you; the declaration is for the reader deciding whether to trust the file. |

**Failure.** A plugin that throws — from `Initialize` or any hook — is disabled with the report
kept, and the Add-ins page shows it. Mailbox carries on; the other plugins carry on. Implement
`IDisposable` to be told when you are disabled; Mailbox revokes your registrations itself.

**Unloading is real.** Each plugin lives in a collectible load context, so disable genuinely
unloads the code. Don't park delegates in static state outside your own assembly, or you will
keep yourself alive in the way the manager can see but not fix.

## Building against the API

The simplest project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Mailbox.Plugins.Api">
      <HintPath>/usr/lib/mailbox/Mailbox.Plugins.Api.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

(Or a `ProjectReference` to `src/Mailbox.Plugins.Api` in a checkout. `Private=false` matters:
Mailbox always resolves the API assembly to its own copy, so shipping one beside your plugin is
at best dead weight.)

Libraries of your own resolve from your plugin's directory through its `.deps.json` — two
plugins may carry two versions of the same library without meeting each other. Anything Mailbox
itself already loads resolves to Mailbox's copy.
