#!/usr/bin/env python3
"""Regenerates the frame/rail/theme pose lists from the code they are about.

Three lists, each read out of the one file that decides what belongs on it:

    frame-caption.tsv   the caption buttons CaptionButtons.cs builds, in every state
    frame-rail.tsv      the modules ShellViewModel hands the rail, and every MailboxModule
    frame-themes.tsv    the built-ins OfficeThemes declares, started in and switched to
    frame-menus.tsv     the frame's own popups, checked against MainWindow's peek switch

A hand-kept list cannot notice a module added to the enum, a theme added to the built-ins or
a fourth caption button — which is exactly the surface that then goes unaudited. Run this
after touching any of the three sources.

    tools/poses/generate-frame.py
    tools/audit-run.sh 2/frame tools/poses/frame-rail.tsv --seed <dir>
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]

CAPTIONS = ROOT / "src/Mailbox.App/Views/CaptionButtons.cs"
SHELL = ROOT / "src/Mailbox.App/ViewModels/ShellViewModel.cs"
MODULES = ROOT / "src/Mailbox.Core/Commands/MailboxModule.cs"
THEMES = ROOT / "src/Mailbox.Theming/Themes/OfficeThemes.cs"

# Every pose runs against the daily configuration unless it is about something else: Dark Gray
# is what the owner reads in, and rule 4 wants a second theme, which frame-themes.tsv is.
COMMON = "MAILBOX_TODAY=2026-08-16"


def write(target: pathlib.Path, note: str, rows: list[tuple[str, str]]) -> None:
    body = "\n".join(f"{name}\t{env}" for name, env in rows)
    target.write_text(
        f"# {note}\n"
        "# Generated from the code — never transcribed. Regenerate: tools/poses/generate-frame.py\n"
        f"# {len(rows)} poses.\n"
        f"{body}\n"
    )
    print(f"{len(rows):3d} poses -> {target.relative_to(ROOT)}")


def caption_buttons() -> list[str]:
    """The tips CaptionButtons.Build is called with — the buttons that exist, in their order."""
    text = CAPTIONS.read_text()
    found = re.findall(r'Build\(\s*\w+Glyph\(\),\s*"([A-Za-z]+)"', text)
    return [f.lower() for f in found]


def rail_modules() -> list[str]:
    """The ModuleTab rows ShellViewModel builds: what the rail actually carries."""
    text = SHELL.read_text()
    block = re.search(r"public ModuleTab\[\] Modules \{ get; \} =\s*\[(.*?)\];", text, re.S)
    if not block:
        raise SystemExit("generate-frame: ShellViewModel's Modules array did not parse")
    return re.findall(r"new\(MailboxModule\.(\w+),", block.group(1))


def enum_modules() -> list[tuple[str, int]]:
    """Every MailboxModule and its number, which is also its Ctrl+N accelerator."""
    text = MODULES.read_text()
    block = re.search(r"public enum MailboxModule\s*\{(.*?)\}", text, re.S)
    if not block:
        raise SystemExit("generate-frame: the MailboxModule enum did not parse")
    return [(m, int(n)) for m, n in re.findall(r"(\w+)\s*=\s*(\d+),", block.group(1))]


def built_in_themes() -> list[str]:
    """The ids OfficeThemes.All lists, in its own order."""
    text = THEMES.read_text()
    block = re.search(r"public static IReadOnlyList<string> All \{ get; \} =\s*\[(.*?)\];", text, re.S)
    if not block:
        raise SystemExit("generate-frame: OfficeThemes.All did not parse")
    names = [n.strip() for n in block.group(1).split(",") if n.strip()]
    ids = []
    for name in names:
        value = re.search(rf'public const string {re.escape(name)} = "([a-z]+)";', text)
        if not value:
            raise SystemExit(f"generate-frame: OfficeThemes.{name} has no id")
        ids.append(value.group(1))
    return ids


def main() -> int:
    buttons = caption_buttons()
    if not buttons:
        print("generate-frame: no caption buttons parsed", file=sys.stderr)
        return 2

    rows = [("caption-rest", COMMON)]
    for b in buttons:
        rows.append((f"caption-hover-{b}", f"{COMMON} MAILBOX_HOVER={b}"))
    for b in buttons:
        rows.append((f"caption-hold-{b}", f"{COMMON} MAILBOX_CAPTION=hold:{b}"))
    # Pressed, not painted: what the button does. Close is left out on purpose — pressing it
    # ends the run before a picture, and its own log line is the proof.
    rows.append(("caption-press-maximize", f"{COMMON} MAILBOX_CAPTION=press:maximize"))
    rows.append(("caption-press-restore", f"{COMMON} MAILBOX_CAPTION=press:maximize,press:maximize"))
    rows.append(("caption-press-minimize", f"{COMMON} MAILBOX_CAPTION=press:minimize"))
    rows.append(("caption-press-close", f"{COMMON} MAILBOX_CAPTION=press:close"))
    # The frame at its own declared minimum and at a wide width. The minimum is read out of
    # MainWindow rather than typed: it is the size at which the rail has the least room, and a
    # minimum lowered without checking what still fits is exactly the change this catches.
    window = (ROOT / "src/Mailbox.App/Views/MainWindow.axaml").read_text()
    smallest = re.search(r'MinWidth="(\d+)"\s+MinHeight="(\d+)"', window)
    if not smallest:
        raise SystemExit("generate-frame: MainWindow declares no MinWidth/MinHeight")
    rows.append(("frame-minimum", f"{COMMON} MAILBOX_SIZE={smallest.group(1)}x{smallest.group(2)}"))
    rows.append(("frame-wide", f"{COMMON} MAILBOX_SIZE=1600x1000"))
    write(ROOT / "tools/poses/frame-caption.tsv",
          "The caption buttons, in every state, and the frame at two widths.", rows)

    rail = rail_modules()
    rows = []
    for name, number in enum_modules():
        # Every accelerator in the enum, including the two the rail does not carry: a key that
        # reaches nothing is a result, and only pressing it says so.
        rows.append((f"rail-key-ctrl{number}", f"{COMMON} MAILBOX_KEY=Ctrl+{number}"))
    for name in rail:
        rows.append((f"rail-module-{name.lower()}", f"{COMMON} MAILBOX_MODULE={name.lower()}"))
    for name in rail:
        rows.append((f"rail-hover-{name.lower()}", f"{COMMON} MAILBOX_HOVER=rail:{name}"))
    write(ROOT / "tools/poses/frame-rail.tsv",
          "Every module accelerator, every rail module posed, every rail icon hovered.", rows)

    themes = built_in_themes()
    rows = [(f"theme-{t}", f"{COMMON} MAILBOX_THEME={t}") for t in themes]
    # The live swap, which is a different claim from the startup theme: each built-in is both
    # a source and a destination once round the cycle, and the close button is held through the
    # swap because its hover wash is the one caption token that flips with the title bar's
    # lightness — a swap that did not re-resolve would show the wrong one.
    for a, b in zip(themes, themes[1:] + themes[:1]):
        rows.append((f"themeswitch-{a}-to-{b}",
                     f"{COMMON} MAILBOX_THEME={a} MAILBOX_THEME_SWITCH={b} MAILBOX_HOVER=close"))
    write(ROOT / "tools/poses/frame-themes.tsv",
          "All four built-ins at startup, and every live swap around the cycle.", rows)

    # The two popups that belong to the frame rather than to the ribbon or a row: the All Apps
    # menu and the window menu behind the app icon. Named here because "which popups are the
    # frame's" is not something the code says, but checked against MainWindow's own peek switch
    # so a key that is renamed or removed fails this script rather than quietly dropping a pose.
    doors = set(re.findall(r'\s+case "([a-z0-9]+)":', (ROOT / "src/Mailbox.App/Views/MainWindow.axaml.cs").read_text()))
    rows = []
    for key in ("allapps", "windowmenu"):
        if key not in doors:
            raise SystemExit(f"generate-frame: MainWindow has no MAILBOX_PEEK={key} any more")
        rows.append((f"menu-{key}", f"{COMMON} MAILBOX_PEEK={key}"))
    write(ROOT / "tools/poses/frame-menus.tsv",
          "The frame's own popups. Measured rather than photographed — rule 6.", rows)

    # Rule 4: a surface proven in one theme is not proven. The batches above run in Dark Gray,
    # which is the daily configuration; these repeat the two states whose paint depends on what
    # the control is standing on — a held caption button and a hovered rail icon — in every other
    # built-in, because Dark Gray is the one theme with dark chrome over a light semantic palette
    # and a fault there can be either the theme's or the style's.
    rows = []
    for theme in themes:
        if theme == "darkgray":
            continue
        for button in buttons:
            rows.append((f"crosstheme-{theme}-hold-{button}",
                         f"{COMMON} MAILBOX_THEME={theme} MAILBOX_CAPTION=hold:{button}"))
        rows.append((f"crosstheme-{theme}-rail-hover",
                     f"{COMMON} MAILBOX_THEME={theme} MAILBOX_HOVER=rail:{rail[1].lower()}"))
    write(ROOT / "tools/poses/frame-crosstheme.tsv",
          "The two state-dependent paints, in every built-in but the daily one.", rows)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
