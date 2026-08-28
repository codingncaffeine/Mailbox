#!/usr/bin/env bash
# Builds the application's icon ladder from the two drawings the reader supplied.
#
# The point of this script is the frame. Both drawings are cropped to ONE box — the union of
# what the two of them actually cover — before either is resized, so the mailbox is the same
# size and in the same place whether the flag is up or down. Trimming each drawing to its own
# content instead would scale them differently, and the icon would jump every time the last
# message was read. That frame was worked out once from the sources and is written down here
# because it is not recoverable from the PNGs afterwards.
#
# Usage: tools/generate-panel-icons.sh [source-directory]
# The source directory holds "mailbox full.png" and "mailbox empty regular.png" at full size.
# It is not in the repository — pass it, or set MAILBOX_ICON_SOURCES to where it lives.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="${1:-${MAILBOX_ICON_SOURCES:-}}"
[ -n "$src" ] || {
    echo "generate-panel-icons: pass the directory holding the two drawings," >&2
    echo "                      or set MAILBOX_ICON_SOURCES to it" >&2
    exit 2
}
out="$root/assets/icons"

command -v magick >/dev/null 2>&1 || { echo "generate-panel-icons: ImageMagick is not installed" >&2; exit 1; }

# The shared frame, in the sources' own pixels, and the square it is centred in.
FRAME="927x787+31+69"
SQUARE=927

# The ladder an icon theme is given. 512 is here because Plasma will pick it and scale down;
# a ladder that stops at 256 leaves the largest size saying whatever the installer left there.
SIZES=(16 24 32 48 64 128 256 512)

render() { # source-file, output-prefix
    local file="$1" prefix="$2" size
    [ -f "$file" ] || { echo "generate-panel-icons: no such drawing: $file" >&2; exit 1; }

    for size in "${SIZES[@]}"; do
        magick "$file" \
            -crop "$FRAME" +repage \
            -background none -gravity center -extent "${SQUARE}x${SQUARE}" \
            -resize "${size}x${size}" \
            "$out/${prefix}-${size}.png"
    done
    echo "  ${prefix}: ${#SIZES[@]} sizes"
}

mkdir -p "$out"
echo "── panel icons from $src"
render "$src/mailbox full.png"          mailbox-tray-full
render "$src/mailbox empty regular.png" mailbox-tray-empty

# The application's own identity icon is the empty mailbox: what a launcher shows before there
# is anything to say. PanelIcon overwrites the installed copy of it as mail arrives; this is
# the drawing the install starts from.
for size in "${SIZES[@]}"; do
    cp "$out/mailbox-tray-empty-${size}.png" "$out/mailbox-app-${size}.png"
done
echo "  mailbox-app: ${#SIZES[@]} sizes (the empty mailbox, which is what an install starts at)"
