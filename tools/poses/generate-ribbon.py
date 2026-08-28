#!/usr/bin/env python3
"""Regenerates the ribbon lane's pose lists from the ribbon inventory dump.

A hand-typed tab list is how a tab goes unaudited, so every pose below comes from the dump the
application itself writes:

    MAILBOX_RIBBON_DUMP=artifacts/audit/phase2/ribbon dotnet test tests/Mailbox.Tests \
        --filter AuditRibbonTests
    tools/poses/generate-ribbon.py

Three lists come out, all under tools/poses/:

    ribbon-tabs.tsv      every tab of every module the shell can show, Classic and Simplified
    ribbon-narrow.tsv    the widest tab of each module at five widths, both modes
    ribbon-keytips.tsv   the Alt traversal, first level and into each tab

Only the seven modules the rail reaches are posed here: the compose, message, contact and
appointment windows carry their own ribbons and their own poses, and MAILBOX_TAB reaches the
shell's bar and the contact window's.
"""
import collections
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DUMP = ROOT / "artifacts/audit/phase2/ribbon/all-tabs.tsv"
OUT = ROOT / "tools/poses"

# The layouts the rail can put on the shell's bar, and what MAILBOX_MODULE calls each.
SHELL_MODULES = {
    "mail": "mail",
    "calendar": "calendar",
    "people": "people",
    "tasks": "tasks",
    "notes": "notes",
    "journal": "journal",
    "feeds": "feeds",
}

WIDTHS = ["1600x1000", "1280x900", "1100x900", "980x900", "820x900"]


def rows():
    if not DUMP.exists():
        print(f"generate-ribbon: no dump at {DUMP} — run the AuditRibbonTests dump first",
              file=sys.stderr)
        raise SystemExit(2)

    lines = DUMP.read_text().splitlines()
    header = lines[0].split("\t")
    for line in lines[1:]:
        yield dict(zip(header, line.split("\t")))


def main() -> int:
    tabs = collections.defaultdict(list)
    for row in rows():
        if row["layout"] in SHELL_MODULES:
            tabs[row["layout"]].append(row)

    if not tabs:
        print("generate-ribbon: the dump named none of the shell's modules", file=sys.stderr)
        return 2

    # --- every tab, both modes -------------------------------------------------------
    lines = [
        "# Every tab of every module the rail reaches, in both ribbon layouts.",
        "# Generated from artifacts/audit/phase2/ribbon/all-tabs.tsv — never transcribed.",
        "# Regenerate: tools/poses/generate-ribbon.py",
    ]
    count = 0
    for module, entries in tabs.items():
        for row in entries:
            # File takes over the whole window rather than changing the bar; the Backstage is
            # another lane's, and posing it here would photograph that instead of a ribbon.
            if row["backstage"] == "True":
                continue

            for mode in ("classic", "simplified"):
                name = f"tab-{module}-{row['tab']}-{mode}"
                # The trace is what makes the batch readable: the picture proves a tab was
                # photographed, the traced line says whether the tab holds what the layout
                # document declares.
                env = (f"MAILBOX_MODULE={module} MAILBOX_TAB={row['tab']} "
                       f"MAILBOX_RIBBON={mode} MAILBOX_SIZE=1600x1000 MAILBOX_RIBBON_TRACE=1")
                lines.append(f"{name}\t{env}")
                count += 1

    (OUT / "ribbon-tabs.tsv").write_text("\n".join(lines) + "\n")
    print(f"{count} tab poses -> tools/poses/ribbon-tabs.tsv")

    # --- narrowing -------------------------------------------------------------------
    # The tab with the most groups per module: the one with the most to give up, so the ladder
    # shows in a picture rather than only in arithmetic.
    lines = [
        "# The busiest tab of each module at five widths, both modes: the collapse ladder.",
        "# Regenerate: tools/poses/generate-ribbon.py",
    ]
    count = 0
    for module, entries in tabs.items():
        busiest = max(entries, key=lambda r: int(r["groups"]))
        for width in WIDTHS:
            for mode in ("classic", "simplified"):
                name = f"narrow-{module}-{busiest['tab']}-{mode}-{width.split('x')[0]}"
                env = (f"MAILBOX_MODULE={module} MAILBOX_TAB={busiest['tab']} "
                       f"MAILBOX_RIBBON={mode} MAILBOX_SIZE={width} MAILBOX_RIBBON_TRACE=1")
                lines.append(f"{name}\t{env}")
                count += 1

    (OUT / "ribbon-narrow.tsv").write_text("\n".join(lines) + "\n")
    print(f"{count} narrowing poses -> tools/poses/ribbon-narrow.tsv")

    # --- KeyTips ----------------------------------------------------------------------
    lines = [
        "# Alt traversal: the first level, then into every tab of every module.",
        "# Regenerate: tools/poses/generate-ribbon.py",
    ]
    count = 0
    for module, entries in tabs.items():
        lines.append(f"keytips-{module}-tabs\tMAILBOX_MODULE={module} MAILBOX_KEYTIPS=tabs "
                     f"MAILBOX_SIZE=1600x1000 MAILBOX_RIBBON_TRACE=1")
        count += 1
        for row in entries:
            if row["backstage"] == "True":
                continue
            for mode in ("classic", "simplified"):
                name = f"keytips-{module}-{row['tab']}-{mode}"
                env = (f"MAILBOX_MODULE={module} MAILBOX_KEYTIPS={row['tab']} "
                       f"MAILBOX_RIBBON={mode} MAILBOX_SIZE=1600x1000 MAILBOX_RIBBON_TRACE=1")
                lines.append(f"{name}\t{env}")
                count += 1

    (OUT / "ribbon-keytips.tsv").write_text("\n".join(lines) + "\n")
    print(f"{count} KeyTip poses -> tools/poses/ribbon-keytips.tsv")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
