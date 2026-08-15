![Mailbox — the modern, open source email client](docs/banner.jpg)

# Mailbox

An email client for everyone.

Mail, Calendar, People, Tasks, Notes and Journal in one desktop application for Linux. Open
protocols only — IMAP, POP3, SMTP, CalDAV, CardDAV — with no cloud service behind it, no
account required, no AI features and no telemetry.

**Status: early, but it moves mail.** Add an account, press F9, and POP3 and SMTP work
against a local SQLite store. The other five modules are still to come.

![The Mailbox shell: folder pane, message list and reading pane](docs/ribbon-simplified.png)

---

## What it is

A dense, keyboard-driven, offline-first mail client for people who want a real desktop
application rather than a web page in a window.

- **Everything is local.** Your mail lives in a store on your machine and stays readable when
  the network does not. Nothing is uploaded anywhere.
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

## Layout

```
src/Mailbox.Core             domain model, command catalogue, toolbar layout, logging
src/Mailbox.Theming          token system, themes, font substitution, icons
src/Mailbox.Controls.Ribbon  the toolbar control
src/Mailbox.Store            SQLite store, migrations, backup and restore
src/Mailbox.Protocols        POP3, SMTP, autoconfig, credentials
src/Mailbox.App              Avalonia shell, Options, account view
tests/Mailbox.Tests          247 tests, no UI thread required
tools/generate-icons.py      regenerates the icon glyph map
assets/                      bundled fonts and the application icon
packaging/                   desktop entry, MIME associations, icon install
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
window or a settings page the theme cannot reach — so there is a test that fails on a
hard-coded colour anywhere.

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
