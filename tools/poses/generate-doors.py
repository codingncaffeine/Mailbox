#!/usr/bin/env python3
"""Regenerates tools/poses/doors.tsv from MainWindow's own MAILBOX_PEEK switch.

The audit's door inventory has to be generated rather than transcribed: a peek key added
to the switch and not to the list is exactly the surface that then goes unaudited, and a
hand-kept list cannot notice. Run this after adding or removing a case.

    tools/poses/generate-doors.py

Then run the batch it feeds:

    tools/audit-run.sh 0 tools/poses/doors.tsv --theme darkgray
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/Mailbox.App/Views/MainWindow.axaml.cs"
TARGET = ROOT / "tools/poses/doors.tsv"

"""Nested switches are not doors.

MAILBOX_PEEK=newkey parses name/address/pass out of its own argument, and
MAILBOX_PEEK=progress switches again on MAILBOX_PROGRESS_STATE for finished/failed. Both sit
in a switch inside the peek switch, and reading them as peek keys puts doors in the inventory
that do not exist — which is as bad as missing one, because the batch then reports a door
opening onto the shell and a reader goes looking for a bug. Taking labels at the peek switch's
own brace depth excludes them by construction, so the next nested switch needs no edit here.
"""


def main() -> int:
    text = SOURCE.read_text()

    anchor = text.find('GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant()')
    if anchor < 0:
        print(f"generate-doors: no MAILBOX_PEEK switch in {SOURCE}", file=sys.stderr)
        return 2

    # Brace-matched to the switch's own closing brace. A fixed line window was the first
    # version of this and it silently truncated the list at "groups" — the door inventory
    # missing a door is the one failure this file exists to prevent, so the end of the switch
    # has to be found rather than guessed.
    open_brace = text.find("{", anchor)
    depth, end = 0, len(text)
    for i in range(open_brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                end = i
                break

    # Walked character by character so a case label can be told from one in a switch nested
    # inside this one: the peek switch's own labels sit at depth 1 from its opening brace.
    keys, seen = [], set()
    body = text[open_brace:end]
    depth = 0
    line_start = 0
    for i, ch in enumerate(body):
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        elif ch == "\n":
            line = body[line_start:i]
            line_start = i + 1
            match = re.match(r'\s+case "([a-z0-9]+)":', line)
            if match and depth == 1 and match.group(1) not in seen:
                seen.add(match.group(1))
                keys.append(match.group(1))

    if not keys:
        print("generate-doors: the switch parsed to no keys at all", file=sys.stderr)
        return 2

    body = "\n".join(f"peek-{key}\tMAILBOX_PEEK={key}" for key in keys)
    TARGET.write_text(
        "# Every MAILBOX_PEEK key, generated from MainWindow's own switch — never transcribed.\n"
        "# Regenerate: tools/poses/generate-doors.py\n"
        f"# {len(keys)} keys.\n"
        f"{body}\n"
    )
    print(f"{len(keys)} peek keys -> {TARGET.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
