#!/usr/bin/env python3
"""Regenerates the Backstage and Quick Access Toolbar pose lists from the code that owns them.

Three lists, none of them transcribed:

  backstage-pages.tsv   every rail entry BuildRail() places, opened by MAILBOX_BACKSTAGE
  backstage-actions.tsv every action a page's button or a menu entry raises, pressed by
                        MAILBOX_BACKSTAGE=<page>:<action> / MAILBOX_BACKSTAGE_MENU=<menu>:<action>
  qat.tsv               the toolbar's two placements and its hidden state, plus one pose per
                        QuickAccessCandidates entry so the flyout's list is exercised

A Backstage button added to a page and not to the list is exactly the button that then goes
unaudited, and a hand-kept list cannot notice. Run this after editing BackstageView.

    tools/poses/generate-backstage.py
    tools/audit-run.sh 2 tools/poses/backstage-actions.tsv --seed <dir> --theme darkgray
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
VIEW = ROOT / "src/Mailbox.App/Views/BackstageView.cs"
LAYOUTS = ROOT / "src/Mailbox.Core/Ribbon/DefaultRibbonLayouts.cs"
POSES = ROOT / "tools/poses"


def main() -> int:
    text = VIEW.read_text()

    # The rail, in the order BuildRail() places it. `enabled: false` is kept and marked: a rail
    # entry that is greyed is a claim too, and the pose proves it draws greyed rather than
    # opening a page nobody meant to ship.
    rail = re.findall(r'RailItem\("([a-z]+)", "([^"]+)", "[^"]+"(, enabled: false)?\)', text)
    if not rail:
        print("generate-backstage: BuildRail parsed to no entries", file=sys.stderr)
        return 2

    # Which of those the page switch actually builds; everything else falls to Placeholder().
    pages = re.findall(r'^\s+"([a-z]+)" => Build\w+\(\),', text, re.MULTILINE)

    # Every action a section button or a menu entry raises. `action:` is the named argument
    # BuildSection takes; MenuEntry's fourth positional argument is the same thing.
    section_actions = re.findall(r'action: "([a-z.]+)"\)', text)
    menu_actions = re.findall(
        r'MenuEntry\("[^"]+", "([^"]+)",\s*"[^"]*"\s*,?\s*"([a-z.]+)"', text, re.DOTALL)

    # Which page each section action belongs to, so the pose opens the page that has the button.
    page_of = {}
    for page, builder in [
        ("info", "BuildAccountInformation"), ("openexport", "BuildOpenExport"),
        ("saveas", "BuildSaveAs"), ("print", "BuildPrint"), ("account", "BuildAccount"),
    ]:
        start = text.find(f"private Control {builder}()")
        if start < 0:
            continue
        end = text.find("\n    private ", start + 1)
        for action in re.findall(r'action: "([a-z.]+)"\)', text[start:end if end > 0 else len(text)]):
            page_of[action] = page

    lines = [
        "# Every Backstage rail page, generated from BuildRail() — never transcribed.",
        "# Regenerate: tools/poses/generate-backstage.py",
        f"# {len(rail)} rail entries, {len(pages)} of which the page switch builds.",
    ]
    for rail_id, label, disabled in rail:
        # The note goes on its own line: the runner reads everything after the tab as
        # environment, so a trailing comment becomes two nonsense variables.
        note = "greyed in the rail" if disabled else (
            "the page switch builds it" if rail_id in pages else "falls through to Placeholder()")
        lines.append(f"# {label} — {note}")
        lines.append(f"backstage-{rail_id}\tMAILBOX_PEEK=backstage MAILBOX_BACKSTAGE={rail_id}")
    (POSES / "backstage-pages.tsv").write_text("\n".join(lines) + "\n")

    lines = [
        "# Every Backstage action, generated from the page builders and the two Info menus.",
        "# Regenerate: tools/poses/generate-backstage.py",
        f"# {len(set(section_actions))} section buttons, {len(menu_actions)} menu entries.",
    ]
    for action in dict.fromkeys(section_actions):
        page = page_of.get(action, "info")
        lines.append(
            f"act-{action}\tMAILBOX_PEEK=backstage MAILBOX_BACKSTAGE={page}:{action}")
    # Two entries raise account.server ("Account Name and Sync Settings" and "Server Settings"),
    # so the pose name carries its ordinal — two poses writing to one directory would leave the
    # batch with one result for two buttons.
    for n, (title, action) in enumerate(menu_actions, start=1):
        menu = "settings" if action.startswith("account.") else "tools"
        lines.append(f"# {title}")
        lines.append(
            f"menu{n}-{action}\tMAILBOX_PEEK=backstage MAILBOX_BACKSTAGE_MENU={menu}:{action}")
    (POSES / "backstage-actions.tsv").write_text("\n".join(lines) + "\n")

    layouts = LAYOUTS.read_text()
    candidates = re.search(
        r"QuickAccessCandidates \{ get; \} =\s*\[(.*?)\];", layouts, re.DOTALL)
    names = re.findall(r"(\w+)Commands\.(\w+)\.Id", candidates.group(1)) if candidates else []

    # The stable id each candidate declares, so the pose list is ids and the door resolves the
    # label — a list carrying transcribed labels goes stale the first time one is reworded.
    # Keyed by "<Group>Commands.<Name>", not by the bare name: Delete, Forward and Move are
    # declared in four command files each, and a flat map hands MailCommands.Delete the
    # appointment's id — which is a pose list that quietly audits the wrong command.
    ids = {}
    for source in (ROOT / "src/Mailbox.Core/Commands").glob("*Commands.cs"):
        for name, value in re.findall(
                r'MailboxCommand (\w+) = new\(\)\s*\{\s*Id = new\("([a-z0-9.]+)"\)',
                source.read_text()):
            ids[f"{source.stem}.{name}"] = value

    lines = [
        "# The Quick Access Toolbar's poses: its two placements, its hidden state, and the",
        "# customize flyout's candidate list, generated from DefaultRibbonLayouts.",
        "# Regenerate: tools/poses/generate-backstage.py",
        f"# {len(names)} candidates.",
        "qat-default\tMAILBOX_MODULE=mail",
        "qat-above\tMAILBOX_QAT=above",
        "qat-below\tMAILBOX_QAT=below",
        "qat-hidden\tMAILBOX_QAT=hidden",
        "qat-labels\tMAILBOX_SETTING=ribbon.qat.labels=true",
        "qat-flyout\tMAILBOX_QAT=flyout",
        "qat-placement\tMAILBOX_QAT=flyout:Show",
        "qat-hide\tMAILBOX_QAT=flyout:Hide",
        "qat-reset\tMAILBOX_QAT=flyout:Reset",
        "qat-more\tMAILBOX_QAT=flyout:More",
        "qat-modify\tMAILBOX_PEEK=modifybutton",
        "qat-order\tMAILBOX_SETTING=ribbon.qat.commands=mail.reply,mail.new,app.redo",
        "qat-badid\tMAILBOX_SETTING=ribbon.qat.commands=mail.new,NotACommand,mail.delete",
    ]

    # One press per candidate: the flyout's tick is how a command is added and removed, and the
    # read-back is what the toolbar holds afterwards rather than a photograph of a menu.
    missing = [f"{g}Commands.{n}" for g, n in names if f"{g}Commands.{n}" not in ids]
    if missing:
        print(f"generate-backstage: no id parsed for {', '.join(missing)}", file=sys.stderr)
        return 2

    for group, name in names:
        lines.append(f"# {group}Commands.{name}")
        lines.append(f"qat-toggle-{name.lower()}\tMAILBOX_QAT=flyout:{ids[f'{group}Commands.{name}']}")
    (POSES / "qat.tsv").write_text("\n".join(lines) + "\n")

    print(f"{len(rail)} pages, {len(section_actions) + len(menu_actions)} actions, "
          f"{len(names)} QAT candidates -> {POSES.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
