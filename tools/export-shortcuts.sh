#!/usr/bin/env bash
# Regenerates the wiki's Keyboard shortcuts page from the command catalogue. A transcribed
# shortcut list is wrong within a month and nothing tells the reader which half to trust, so the
# page is generated: change a command's DefaultGesture and run this.
#
#   tools/export-shortcuts.sh [path]        default: Keyboard-shortcuts.md in the working directory
#
# The wiki is a separate repository. Clone it beside this one and point the script at the page:
#
#   git clone git@github.com:codingncaffeine/Mailbox.wiki.git
#   tools/export-shortcuts.sh ../Mailbox.wiki/Keyboard-shortcuts.md
set -euo pipefail
cd "$(dirname "$0")/.."
out="${1:-Keyboard-shortcuts.md}"
dotnet build src/Mailbox.App -c Debug -v q > /dev/null
dotnet run --no-build --project src/Mailbox.App -- --export-shortcuts "$out"
