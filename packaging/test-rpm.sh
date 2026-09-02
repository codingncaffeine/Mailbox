#!/usr/bin/env bash
# The dependency test every .rpm build gets: install it into a clean Fedora container, sweep
# every ELF file for a library the package did not bring, and start the binary with no display.
#
#   PASS is: the install resolves; the only "not found" is liblttng-ust.so.0 (the .NET runtime's
#   optional LTTng tracer, dlopen'd only when tracing, benign on every dotnet publish); and the
#   start probe dies at the display — "XOpenDisplay failed" — meaning the runtime, Avalonia and
#   every native library loaded and only the screen was missing.
#
# Anything else missing is a Requires gap in build-rpm.sh; fix it before publishing.
#
# One difference from the .deb, and it is expected: Fedora packages no WPE WebKit at all, so the
# reading pane's engine is absent there and the pane says so and renders the message as text.
# The probe reports which way that went, because "the engine is missing" and "the engine is
# there and broken" look identical from the outside and only the first is acceptable.
set -euo pipefail
cd "$(dirname "$0")/.."

RPM="${RPM:-Mailbox.rpm}"
IMAGE="${IMAGE:-fedora:42}"
OUT="$(pwd)/packaging/out"

[ -f "$OUT/$RPM" ] || { echo "No $OUT/$RPM — run packaging/build-release.sh first." >&2; exit 1; }

echo "── $IMAGE: install $RPM, sweep, probe"
podman run --rm -v "$OUT:/out:ro" "$IMAGE" bash -c '
set -u
if ! dnf install -y -q /out/'"$RPM"' > /tmp/install.log 2>&1; then
    echo "INSTALL FAILED"; tail -30 /tmp/install.log; exit 2
fi
echo "installed:"; rpm -q --queryformat "%{NAME} %{VERSION}-%{RELEASE} %{ARCH} %{SIZE} bytes\n" mailbox
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
echo "── what the platform brought:"
for lib in libX11.so.6 libEGL.so.1 libGL.so.1 libfreetype.so.6 libfontconfig.so.1 libglib-2.0.so.0 libicuuc.so; do
    if ldconfig -p | grep -q "$lib"; then echo "  $lib: present"; else echo "  $lib: MISSING"; fi
done
echo "── the reading pane engine (Fedora packages none — text is the correct answer):"
for lib in libWPEWebKit-2.0.so.1 libwpe-1.0.so.1 libWPEBackend-fdo-1.0.so.1; do
    if ldconfig -p | grep -q "$lib"; then echo "  $lib: present"; else echo "  $lib: absent"; fi
done
for tool in secret-tool notify-send; do
    if command -v "$tool" > /dev/null; then echo "  $tool: present"; else echo "  $tool: missing (a Recommends)"; fi
done
echo "── start probe (no display; expect it to stop at XOpenDisplay):"
timeout 15 /usr/bin/mailbox 2>&1 | grep -v "^[0-9:.]* *[0-9.]*s INF" | head -8
echo "── what the log says about the engine:"
grep -h "Reading pane engine" /root/.local/state/mailbox/logs/mailbox.log 2>/dev/null | head -3
echo "── the crash log, if any:"
tail -n 12 /root/.local/state/mailbox/logs/mailbox.log 2>/dev/null | grep -v " INF " | head -12
echo "── desktop integration:"
ls /usr/share/applications/mailbox.desktop /usr/share/icons/hicolor/256x256/apps/mailbox.png
' 2>&1 | grep -v -E 'overlay|graph driver|vfs' || true
