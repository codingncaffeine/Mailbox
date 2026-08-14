# Text rendering on Linux

**Status: resolved, favourably. Subpixel antialiasing works.**

Investigated 13 August 2026, during Phase 0.

## Why this was the first thing built

The plan lists this as the highest fidelity risk in the project, above the rich text editor
and above overall scope. Windows draws Outlook's UI with ClearType subpixel antialiasing.
Grayscale antialiasing renders thinner and softer, and that single difference is most of what
makes a Linux clone feel wrong before you can articulate why. If it could not be fixed, it
would have capped how close Mailbox could ever get — so it needed answering before a single
piece of chrome was drawn on top of the assumption.

Prior reports were discouraging. Avalonia issue #13310 describes `SubpixelAntialias` having no
effect on several Linux distributions, and Skia is documented as disabling LCD subpixel
rendering and gamma correction on X11.

## Result

On the reference machine, `TextRenderingMode.SubpixelAntialias` **is honoured**.

| Mode | Non-background pixels | With channel disagreement | Mean channel spread | Max |
|---|---|---|---|---|
| `SubpixelAntialias` | 6,669 | 6,087 (**91.3%**) | 72.26 | 155 |
| `Antialias` | 5,674 | 0 (**0.0%**) | 1.03 | 0 |

Grayscale antialiasing produces pixels whose R, G and B are equal. Subpixel antialiasing
produces coloured fringes on glyph edges. A 91.3%-versus-0% split is unambiguous, and the
visual comparison shows the expected orange-and-blue fringing.

This materially de-risks the project. Mailbox can match Windows glyph rendering.

## Method

Reproducible, and worth re-running on any new target platform.

1. Launch twice against identical content, changing only the mode:
   ```
   MAILBOX_TEXT_MODE=subpixel  MAILBOX_THEME=white mailbox
   MAILBOX_TEXT_MODE=antialias MAILBOX_THEME=white mailbox
   ```
2. Screenshot, then crop a region of plain dark text on a white ground. Avoid anything
   coloured — accent text, the unread bar, selection highlight — or the measurement picks up
   UI colour rather than glyph fringing.
3. For every non-background pixel, compute `max(R,G,B) - min(R,G,B)`. Grayscale rendering
   yields ~0 throughout; subpixel yields a large spread on edge pixels.

Two traps, both of which produced wrong answers first time:

- **Crop coordinates must be window-relative.** Cropping full-screen coordinates landed on the
  desktop wallpaper and reported 100% "coloured".
- **`magick txt:-` emits `x,y: (r,g,b)`.** Splitting on punctuation puts the coordinates in
  fields 1 and 2 and the channels in 3, 4 and 5. Reading fields 2–4 measures the Y coordinate
  as if it were red, which reports enormous fake spreads.

## Test environment

| | |
|---|---|
| Distribution | Arch Linux |
| Kernel | 7.1.8-arch1-3 |
| Desktop | KDE Plasma |
| Session | Wayland, application running through XWayland (`DISPLAY=:1`) |
| Toolkit | Avalonia 12.1.1, Skia backend |
| Runtime | .NET 10.0.111 |

**Not yet verified:** Avalonia's native Wayland backend, non-KDE compositors, other
distributions, and HiDPI or fractional scaling. The upstream reports of failure are real for
someone; they are most likely compositor-, driver- or fontconfig-specific rather than
universal. Re-run the method above on any platform before assuming it holds.

## API notes

Avalonia 12.1 does **not** expose these as public attached-property fields, so they cannot be
set from XAML. `TextOptions` offers method pairs only:

```
TextOptions.SetTextRenderingMode(visual, TextRenderingMode.SubpixelAntialias)
TextOptions.SetTextHintingMode(visual, TextHintingMode.Strong)
TextOptions.SetBaselinePixelAlignment(visual, ...)
```

Attempting `<Setter Property="TextOptions.TextRenderingMode" ...>` fails at compile time with
`AVLN2000: Unable to find TextRenderingModeProperty field`. `RenderOptions` carries a parallel
`Get/SetTextRenderingMode` pair.

Available values:

- `TextRenderingMode`: `Unspecified`, `SubpixelAntialias`, `Antialias`, `Alias`
- `TextHintingMode`: `Unspecified`, `None`, `Light`, `Strong`

## What Mailbox does

`Mailbox.App/Theming/TextRendering.cs` applies `SubpixelAntialias` with `Strong` hinting to the
window root, inherited by everything beneath it. Full hinting snaps stems to the pixel grid,
which is what makes Windows UI text crisp at 12px.

Both are overridable at runtime so the comparison can be re-run at any time, and so a user on
a platform where subpixel renders badly can turn it off:

```
MAILBOX_TEXT_MODE=subpixel|antialias|alias|unspecified
MAILBOX_TEXT_HINTING=none|light|strong|unspecified
```

The active setting is shown in the status bar during Phase 0.

## Follow-up

- Re-run under the native Wayland backend (`Avalonia.Wayland`, opt-in at 12.1).
- Re-run at 125%, 150% and 200% scaling once HiDPI work lands.
- Expose the mode in Options rather than only as an environment variable, since the upstream
  reports mean some users will need to change it.
- Fold the measurement into the fidelity harness so a regression is caught by CI rather than
  by someone noticing the text looks thin.
