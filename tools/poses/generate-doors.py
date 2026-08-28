#!/usr/bin/env python3
"""Regenerates tools/poses/doors.tsv from HarnessDoors.All.

Read from HarnessDoors.All rather than parsed out of MainWindow's switch. Parsing the switch
is what this script used to do, and it got the answer wrong twice in one session — a fixed
line window dropped nine doors, and a nested switch added two that were not doors at all,
both silently. AuditDoorInventoryTests holds that list against the switch's own cases, so a
disagreement is a failing test rather than a quietly wrong inventory here.

    tools/poses/generate-doors.py

Then run the batch it feeds:

    tools/audit-run.sh 0 tools/poses/doors.tsv --theme darkgray
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/Mailbox.App/Views/HarnessDoors.cs"
TARGET = ROOT / "tools/poses/doors.tsv"

def main() -> int:
    text = SOURCE.read_text()

    inside = re.search(r"All\s*=\s*\[(?P<names>.*?)\];", text, re.S)
    if not inside:
        print(f"generate-doors: no All list in {SOURCE}", file=sys.stderr)
        return 2

    keys, seen = [], set()
    for name in re.findall(r'"([a-z0-9]+)"', inside.group("names")):
        if name not in seen:
            seen.add(name)
            keys.append(name)

    if not keys:
        print(f"generate-doors: {SOURCE} lists no keys at all", file=sys.stderr)
        return 2

    body = "\n".join(f"peek-{key}\tMAILBOX_PEEK={key}" for key in keys)
    TARGET.write_text(
        "# Every MAILBOX_PEEK key, generated from HarnessDoors.All — never transcribed.\n"
        "# Regenerate: tools/poses/generate-doors.py\n"
        f"# {len(keys)} keys.\n"
        f"{body}\n"
    )
    print(f"{len(keys)} peek keys -> {TARGET.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
