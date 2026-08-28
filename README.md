![Mailbox — the modern, open source email client](docs/banner.jpg)

# Mailbox

An email client for everyone.

Mail, Calendar, People, Tasks, Notes, Journal and Feeds in one desktop application for Linux.
Open protocols only — IMAP, POP3, SMTP, CalDAV, CardDAV — with no cloud service behind it, no
account required, no AI features and no telemetry.

**Status: early, but it moves mail.** Add an account and mail works end to end over IMAP, POP3
and SMTP against a local SQLite store. The calendar, contacts, tasks, notes, journal and feed
modules are built and run against that same store; adding a CalDAV or CardDAV account to sync
them is not in the interface yet, so those five are single-machine for now.

![The Mailbox shell: folder pane, message list and reading pane](docs/ribbon-simplified.png)

---

## What it is

A dense, keyboard-driven, offline-first mail client for people who want a real desktop
application rather than a web page in a window.

- **Everything is local.** Your mail lives in a store on your machine and stays readable when
  the network does not. Nothing is uploaded to us — there is no service behind this to upload
  it to, and the one thing that would call home, the update check, is off unless you turn it on.
- **Open protocols.** POP3 and IMAP for mail, SMTP for sending, CalDAV and CardDAV for
  calendars, contacts, tasks and notes. Works with self-hosted servers and any provider that
  speaks them.
- **Deeply customizable.** Every action is a named command, the toolbar is a rearrangeable
  document, and the theme system reaches every surface in the application — not just a
  palette swap.
- **No lock-in.** GPL-3.0, and your data stays in formats you can take elsewhere.

---

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test
dotnet run --project src/Mailbox.App
```

### Packaging

One script publishes a self-contained build and packs it two ways: a tarball that runs from
wherever it is extracted, and a `.deb` for Debian 13 and its relatives. `packaging/aur/PKGBUILD`
repackages the tarball for Arch.

```sh
bash packaging/build-release.sh     # packaging/out/Mailbox-<ver>-linux-x64.tar.gz and mailbox_<ver>_amd64.deb
bash packaging/test-deb.sh          # installs the .deb in a clean debian:trixie container (podman) and probes it
```

The test is part of every build: the reading pane's engine (WPE WebKit), the keyring tool and
the notification tool are exactly the dependencies that are present on a development machine and
missing on a clean one. It passes when the install resolves, no bundled library wants anything
the package did not bring, and the binary gets as far as looking for a display.

Runtime dependencies on the target: WPE WebKit (`libwpewebkit-2.0-1` / `wpewebkit`), libwpe and
WPEBackend-fdo, X11 or Wayland, fontconfig; and, recommended, `secret-tool` (libsecret) for the
keyring, `notify-send` (libnotify) for notifications, Hunspell dictionaries for spelling, a
desktop portal (with GTK 3 as the fallback) for the file dialogs, and the metric-compatible
fonts below.

### Fonts

Mailbox renders received mail at the metrics the sender intended by substituting
metric-compatible typefaces. Install these so common business fonts lay out correctly:

```sh
# Arch
sudo pacman -S --needed ttf-carlito ttf-caladea ttf-liberation gsfonts

# Debian / Ubuntu
sudo apt install fonts-crosextra-carlito fonts-crosextra-caladea \
                 fonts-liberation2 fonts-urw-base35
```

Proprietary fonts are detected and preferred when you already have them installed. They are
never shipped, because they are not redistributable.

### Environment variables

Useful while the interface is still being built.

| Variable | Values | Purpose |
|---|---|---|
| `MAILBOX_THEME` | `colorful`, `white`, `darkgray`, `black` | Startup theme |
| `MAILBOX_DENSITY` | `compact`, `cozy`, `comfortable` | Row spacing |
| `MAILBOX_LAYOUT` | `classic`, `modern` | Which shell layout to use |
| `MAILBOX_RIBBON` | `simplified`, `classic`, `collapsed` | Toolbar display mode |
| `MAILBOX_TEXT_MODE` | `subpixel`, `antialias`, `alias` | Glyph rasterization |
| `MAILBOX_TEXT_HINTING` | `none`, `light`, `strong` | Hinting strength |
| `MAILBOX_CAPTURE` | a `.png` path | Render the window once and exit |
| `MAILBOX_WAYLAND` | `1` | Run on the native Wayland backend instead of X11 (through XWayland on a Wayland session). Experimental; the log says which backend came up |
| `MAILBOX_LOG_LEVEL` | `debug`, `info`, `warning`, `error` | Log verbosity |

Logs are written to `$XDG_STATE_HOME/mailbox/logs`, five runs kept.

---

## Themes

The four themes are complete token sets, and any of them can be the base of one of your own.
A theme is a JSON file in `~/.config/mailbox/themes/` — an id, a name, the theme it starts from,
and the tokens it changes, as few as it likes: three palette entries over `black` are a whole
theme, because everything the base derives from them follows. Edits show while Mailbox is
running; the theme picker in Options › General lists your files after the built-ins.

```sh
mailbox --export-theme darkgray my-theme.mailbox-theme.json   # a complete theme to start from
```

`assets/themes/` holds the four built-ins in that form, as the documentation of every token.

## Layout

```
src/Mailbox.Core             domain model, command catalogue, toolbar layout, settings, logging
src/Mailbox.Theming          token system, themes, font substitution, icons
src/Mailbox.Controls.*       the toolbar, calendar, people, tasks, notes and journal controls
src/Mailbox.Store            SQLite store, migrations, search index, backup and restore
src/Mailbox.Protocols        IMAP, POP3, SMTP, autoconfig, OAuth, feeds, credentials
src/Mailbox.Dav              CalDAV and CardDAV sync
src/Mailbox.Scheduling       iCalendar, recurrence, time zones, invitations
src/Mailbox.Contacts         vCard, the address book and name parsing
src/Mailbox.Editor           the rich text editor and its HTML
src/Mailbox.Rendering        the sanitizer and the offscreen engine the reading pane draws with
src/Mailbox.Security         S/MIME and OpenPGP
src/Mailbox.Import           Maildir, mbox, .eml, .msg and Thunderbird profiles
src/Mailbox.Pst              the read-only archive-file reader
src/Mailbox.Junk             the junk classifier
src/Mailbox.Google           Google Tasks
src/Mailbox.Plugins[.Api]    the plugin host and the API plugins compile against
src/Mailbox.App              the Avalonia shell: every module, Options, the dialogs
tests/Mailbox.Tests          the test suite, no UI thread required
tools/generate-icons.py      regenerates the icon glyph map
assets/                      bundled fonts, the icon ladder and the reminder sound
packaging/                   desktop entry, MIME associations, icon install, the release build and its dependency test
assets/themes/               the four built-in themes as theme files (generated: tools/export-themes.sh)
```

Several components are written from scratch rather than taken off the shelf, because no
GPL-3-compatible option exists: the toolbar, the message list, the rich text editor, the
calendar views, the DAV sync engine, the theme engine, the archive-file reader, the junk
filter and the plugin host.

---

## Design notes

**Subpixel antialiasing works on Linux.** Widely reported not to, and it was the biggest risk
to how the application would feel. Measured at 91.3% channel disagreement on glyph edges
against 0.0% for grayscale — it is honoured. See
[`docs/text-rendering.md`](docs/text-rendering.md).

**Themes cover every surface.** Four built-in themes, each authored as a complete explicit
token set rather than derived from one another, with a coverage gate that refuses to load a
theme missing any token. The usual failure of themeable applications is holes — a compose
window or a settings page the theme cannot reach — so every surface takes its colour from a
named token rather than a literal, and the gate makes a missing one loud.

**Fonts resolve honestly.** The substitution table separates metric-compatible pairs from
lookalikes, and a fallback can never inherit a metric claim it has not earned. Outgoing mail
names the original font first, so recipients who have it see it, while local rendering uses
the substitute at identical metrics and the layout matches either way.

**The toolbar is data, not markup.** It renders a layout document, which is what makes it
rearrangeable and lets plugin commands be placed through the same path as built-in ones.

---

## Licence

GPL-3.0-or-later. See [`LICENSE`](LICENSE).

Not affiliated with or endorsed by any other software vendor.
