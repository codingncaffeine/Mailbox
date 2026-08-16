#!/usr/bin/env bash
# Regenerates assets/themes/*.mailbox-theme.json from the built-in themes. The committed files
# are the format's documentation and a starting point for a theme of one's own; a test fails
# when they drift from the built-ins, and this is how to bring them back.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build src/Mailbox.App -c Debug -v q > /dev/null
for id in colorful white darkgray black; do
    dotnet run --no-build --project src/Mailbox.App -- --export-theme "$id" "assets/themes/$id.mailbox-theme.json"
done
