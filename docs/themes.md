# Themes

Mailbox ships four built-in themes — Colorful, White, Dark Gray and Black — and everything
beyond them is a small text file you can write, generate, import or share. This page is the
reference for all of it.

## The one rule everything else follows

**Choosing a built-in theme always returns Mailbox to its original look.** Whatever a theme,
a palette, an import or an edit did, the built-ins cannot be changed: a user theme is always
a separate file layered over one of them, applying a theme rebuilds every colour from
scratch, and picking a theme in Options gives you exactly that theme with no leftover edits
riding along. If switching back ever looks different from a fresh install, that is a bug.

The second rule: **content stays readable.** The surfaces you read mail on — the message
rows, the reading pane, the compose body, the calendar — are never recoloured by anything
automated. Dark chrome over a light page is how Dark Gray works, and imported or generated
themes follow the same shape. A contrast check runs every time a user theme is applied, and
anything hard to read is named in the log — the theme still applies, because a theme that is
hard to read is yours to fix and still yours to use.

## Theme files

A theme is a `.mailbox-theme.json` file in `~/.config/mailbox/themes`:

```json
{
  "id": "midnight",
  "name": "Midnight",
  "base": "darkgray",
  "tokens": {
    "palette.brand.primary": "#4FA3E0",
    "titlebar.background": "#101822"
  }
}
```

`base` is what makes a three-line file a complete theme: everything the file does not set
comes from the named built-in (or another file), and setting `palette.brand.primary` alone
derives the whole accent ramp — hovers, presses, selection tints — from the base's own
measured relationships. The directory is watched: saving a file applies it live, no restart.
`mailbox --export-theme <id>` writes a built-in out whole, which is both the format's
documentation and a starting point.

An id may not be a built-in's, and a name may not shadow one — "Dark Gray" cannot be taken,
however it is spelled.

## The editor

Options › General › **Customize…** opens the token editor: every colour, size and family the
theme is made of, applied to the running application as you change it. Edits are a session
scratchpad until **Save As Theme…** writes them into a file of your own; **Reset All** wipes
the scratchpad and nothing else. **Pick an area…** dims the shell and lets you point at the
part you mean — the title bar, the ribbon, the folder pane — and the editor scopes to the
tokens that paint it, ordered by role: grounds, inks, washes, lines.

## Mailbox Background

Options › General › **Mailbox Background:** puts a texture or an image behind the title
bar's controls: one of the shipped patterns, drawn in the caption's own ink in every theme,
or an image of yours (**Image…**, copied under the themes directory, re-encoded to PNG).
**Align…** lets you drag the image along the band — each release keeps the position, Ctrl+Z
puts it back, Esc finishes. The choice is a personal setting like density: it survives theme
switches, and "(From the theme)" hands the decision back to the theme, which for every
built-in means none.

Themes can carry a background of their own through the same tokens:

| token | value |
|---|---|
| `titlebar.backdrop` | `""`, `pattern:<name>`, or an image path (relative to the themes directory) |
| `titlebar.backdrop.alignment` | CSS `background-position` keywords, or `x% y%` |
| `titlebar.backdrop.tiling` | `no-repeat`, `repeat`, `repeat-x`, `repeat-y` |
| `titlebar.backdrop.size` | `auto`, `cover`, `contain` |
| `titlebar.backdrop.opacity` | `0`–`1` |
| `titlebar.backdrop.extent` | `caption`, or `tabs` to reach through the tab strip |

None are required; absent means none. Imported browser themes reach through the tab strip
by default — they were drawn for a band that tall — and the Background row's **Reach**
choice can rein any theme back to the title bar, or send your own image further down.
Patterns keep to the caption whatever the reach says.

## Palettes

Options › General › **Palette:** builds a theme from a colour scheme: one of the curated
base16 schemes that ship with Mailbox (vendored from the tinted-theming project under the
MIT licence, each with its author), your desktop's own colour scheme, or a scheme sampled
off your background image. A palette recolours the chrome — the caption strip, the left
panes, the accent — and never the content; the accent keeps the scheme's hue but is moved
until it reads on the message list's rows. The result is an ordinary theme file
(`palette-<scheme>`), so trying one is applying it and the theme row's **Remove** takes it
away again.

## Importing a browser theme

Options › General › **Import…** (or `mailbox --import-theme <file>`) reads a Firefox static
theme — an `.xpi`, a zip, an unpacked directory or a bare `manifest.json` — and takes from
it exactly what a mail client can use: the frame colour and its text onto the caption strip,
the sidebar pair onto the left panes, and the header image onto the title bar's backdrop,
placed as the manifest placed it. Everything else the theme says is listed in the summary as
seen-but-unmapped rather than guessed at. Dark themes land on a Dark Gray base, light on
White; the reading surfaces stay the base's, always.

**Animated themes animate.** A GIF or APNG header keeps its animation: every frame is
decoded, composited and re-encoded individually, the timing travels beside them, and the
title bar plays them back. Animation is bounded like everything imported — at most ninety
frames within a pixel budget; a longer one keeps its first frames and the summary says so.

The import is careful with strangers' files: archives are size- and entry-limited, paths
that climb out are refused, and every image is re-encoded through a real decoder before it
touches disk — nothing arrives with its original bytes, animation frames included.
Importing the same theme again updates it in place.

**Licensing, plainly:** importing a theme you downloaded, for your own use on your own
machine, is ordinary use of a file you lawfully obtained; nothing is redistributed and
nothing is fetched from any network. Mailbox itself never ships anyone else's theme artwork
— not in the repository, not in screenshots, not in releases — and that is a project rule,
not an accident.

## Browsing themes

Options › General › **Browse…** opens the theme browser: the community themes on
addons.mozilla.org — over half a million — searched by words, popularity, rating, trending
or colour, from inside the application. Selecting one downloads its file (they are tiny)
and shows it **as Mailbox would wear it**: the preview is drawn from the same mapping an
install produces, not from a screenshot of another program. Each theme's licence is named
in the pane, because you should know a theme's terms while declining is still a button.

**Install** runs the downloaded file through the same door as any import — the size limits,
the traversal checks, the image re-encoding — and asks first, in plain words: these themes
are made and uploaded by their authors, not by Mailbox or Mozilla; Mailbox reads only the
colours and re-encodes the images, but anything downloaded from the internet deserves a
moment's doubt, so install only what you trust. An installed theme is an ordinary theme
file: the theme row's **Remove** takes it away whole, and switching to a built-in is always
a complete return to stock.

**What touches the network, exactly:** the browser, only while it is open, and only
addons.mozilla.org. Importing a file, applying a theme, palettes, backgrounds — none of it
speaks to any network, ever.

## Sharing a theme

A colours-only theme is one file: hand somebody the `.mailbox-theme.json` and they drop it
in their themes directory — hot reload does the rest. A theme with images travels as a
pack: `mailbox --export-theme-pack <id>` zips the json with its images, and the same
**Import…** button (or `--import-theme`) installs it whole. Packs are held to the same
hardening as any imported file, and a pack carrying a built-in's name is refused.
