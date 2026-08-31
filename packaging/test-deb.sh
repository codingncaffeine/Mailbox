#!/usr/bin/env bash
# The dependency test every build gets: install the .deb into a clean Debian container, sweep
# every ELF file for a library the package did not bring, and start the binary with no display.
#
#   PASS is: the install resolves; the only "not found" is liblttng-ust.so.0 (the .NET runtime's
#   optional LTTng tracer, dlopen'd only when tracing, benign on every dotnet publish); and the
#   start probe dies at the display — "XOpenDisplay failed" — meaning the runtime, Avalonia and
#   every native library loaded and only the screen was missing.
#
# Anything else missing is a Depends gap in build-release.sh; fix it before publishing.
# Podman on this box: the docker daemon is not running, and podman's vfs/overlay graph-driver
# warnings are noise.
set -euo pipefail
cd "$(dirname "$0")/.."

DEB="Mailbox.deb"
IMAGE="${IMAGE:-debian:trixie}"
OUT="$(pwd)/packaging/out"

[ -f "$OUT/$DEB" ] || { echo "No $OUT/$DEB — run packaging/build-release.sh first." >&2; exit 1; }

echo "── $IMAGE: install $DEB, sweep, probe"
podman run --rm -v "$OUT:/out:ro" "$IMAGE" bash -c '
set -u
apt-get update -qq > /dev/null
if ! apt-get install -y -qq /out/'"$DEB"' > /tmp/install.log 2>&1; then
    echo "INSTALL FAILED"; tail -30 /tmp/install.log; exit 2
fi
echo "installed:"; dpkg -s mailbox | grep -E "^(Version|Installed-Size)"
echo "── ldd sweep (anything not found):"
found=0
: > /tmp/missing
for f in $(find /usr/lib/mailbox -type f \( -perm -u+x -o -name "*.so*" \) ); do
    # A minimal image has no `file`; the ELF magic is enough.
    if [ "$(head -c 4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ]; then
        found=$((found+1))
        ldd "$f" 2>/dev/null | grep "not found" | sed "s|^|  $(basename "$f"): |" >> /tmp/missing
    fi
done
sort -u /tmp/missing
echo "  ($found ELF files scanned)"
echo "── the reading pane engine and the tools the app calls:"
for lib in libWPEWebKit-2.0.so.1 libwpe-1.0.so.1 libWPEBackend-fdo-1.0.so.1 libsoup-3.0.so.0; do
    if ldconfig -p | grep -q "$lib"; then echo "  $lib: present"; else echo "  $lib: MISSING"; fi
done
for tool in secret-tool notify-send; do
    if command -v "$tool" > /dev/null; then echo "  $tool: present"; else echo "  $tool: missing (a Recommends)"; fi
done
echo "── start probe (no display; expect it to stop at XOpenDisplay):"
timeout 15 /usr/bin/mailbox 2>&1 | grep -v "^[0-9:.]* *[0-9.]*s INF" | head -8
echo "── the crash log, if any:"
tail -n 12 /root/.local/state/mailbox/logs/mailbox.log 2>/dev/null | grep -v " INF " | head -12
echo "── desktop integration:"
ls /usr/share/applications/mailbox.desktop /usr/share/icons/hicolor/256x256/apps/mailbox.png
' 2>&1 | grep -v -E 'overlay|graph driver|vfs' || true
