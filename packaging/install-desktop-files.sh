#!/usr/bin/env bash
# Installs the desktop entry, icons and MIME associations.
#
# Called by the AUR package, the .deb postinst and the tarball installer, so all three end up
# with identical desktop integration. PREFIX and DESTDIR follow the usual conventions.
set -euo pipefail

PREFIX="${PREFIX:-/usr}"
DESTDIR="${DESTDIR:-}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"

APPS="$DESTDIR$PREFIX/share/applications"
ICONS="$DESTDIR$PREFIX/share/icons/hicolor"

install -Dm644 "$HERE/mailbox.desktop" "$APPS/mailbox.desktop"

for size in 16 24 32 48 64 128 256 512; do
    src="$ROOT/assets/icons/mailbox-${size}.png"
    [ -f "$src" ] || continue
    install -Dm644 "$src" "$ICONS/${size}x${size}/apps/mailbox.png"
done

# Only refresh caches for a live install; a packaged build leaves that to the package manager.
if [ -z "$DESTDIR" ]; then
    command -v update-desktop-database >/dev/null 2>&1 \
        && update-desktop-database -q "$PREFIX/share/applications" || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 \
        && gtk-update-icon-cache -qtf "$PREFIX/share/icons/hicolor" || true
fi

echo "Installed desktop entry and icons under $PREFIX."
echo "Set Mailbox as the default mail client with:"
echo "    xdg-mime default mailbox.desktop x-scheme-handler/mailto"
