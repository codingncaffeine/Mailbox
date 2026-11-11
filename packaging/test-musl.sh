#!/usr/bin/env bash
# The dependency test for the musl tarball, the counterpart of test-deb.sh: unpack it in a clean
# Alpine container, sweep every ELF file for a library the system does not have, start the
# binary with no display, and capture the window under Xvfb.
#
#   PASS is: nothing is reported missing after the listed packages are installed; the start
#   probe dies at the display — "XOpenDisplay failed" — meaning the runtime, Avalonia and every
#   native library loaded and only the screen was missing; and the capture is a picture of the
#   window. The packages installed here are the tarball's runtime dependencies on Alpine, and
#   the README's list for it is this one.
#
#   bash packaging/test-musl.sh         → packaging/out/musl/capture.png
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
TAR="Mailbox-$VER-linux-musl-x64.tar.gz"
IMAGE="${IMAGE:-docker.io/library/alpine:latest}"
OUT="$(pwd)/packaging/out"

[ -f "$OUT/$TAR" ] || { echo "No $OUT/$TAR — run packaging/build-release.sh musl first." >&2; exit 1; }
rm -rf "$OUT/musl"; mkdir -p "$OUT/musl"

echo "── $IMAGE: unpack $TAR, sweep, probe, capture"
podman run --rm -v "$OUT:/out:ro" -v "$OUT/musl:/musl" "$IMAGE" sh -c '
set -u
cat /etc/alpine-release | sed "s/^/  alpine /"
apk add -q --no-cache icu-libs tzdata libstdc++ libgcc fontconfig font-dejavu \
    libx11 libice libsm libxext libxi libxrandr libxcursor libxkbcommon mesa-egl libsecret libnotify xvfb-run \
    || { echo "APK FAILED"; exit 2; }
mkdir -p /opt/mailbox && tar -C /opt/mailbox -xzf /out/'"$TAR"'
echo "── ldd sweep (anything the system does not have):"
found=0; : > /tmp/missing
for f in $(find /opt/mailbox -maxdepth 1 -type f \( -perm -u+x -o -name "*.so*" \)); do
    if [ "$(head -c 4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ]; then
        found=$((found+1))
        ldd "$f" 2>&1 | grep -E "Error|not found" | sed "s|^|  $(basename "$f"): |" >> /tmp/missing
    fi
done
sort -u /tmp/missing; echo "  ($found ELF files scanned)"
[ -d /opt/mailbox/chromium ] && echo "  chromium: PRESENT (a musl tarball should carry none)" || echo "  chromium: none, so the reading pane renders text"
/opt/mailbox/mailbox --version | sed "s/^/  /"
for tool in secret-tool notify-send; do
    if command -v "$tool" > /dev/null; then echo "  $tool: present"; else echo "  $tool: missing (libsecret / libnotify, recommended)"; fi
done
export XDG_CONFIG_HOME=/tmp/p/c XDG_STATE_HOME=/tmp/p/s XDG_DATA_HOME=/tmp/p/d XDG_CACHE_HOME=/tmp/p/k
echo "── start probe (no display; expect it to stop at XOpenDisplay):"
timeout 15 /opt/mailbox/mailbox 2>&1 | grep -v -E "^[0-9:.]* *[0-9.]*s INF" | head -6
echo "── capture under Xvfb:"
rm -rf /tmp/p
if MAILBOX_CAPTURE=/musl/capture.png timeout 60 xvfb-run -a -s "-screen 0 1600x1000x24" /opt/mailbox/mailbox > /tmp/capture.log 2>&1 \
   && [ -s /musl/capture.png ]; then
    echo "  capture.png: $(stat -c %s /musl/capture.png) bytes"
else
    echo "  CAPTURE FAILED"; grep -v " INF " /tmp/capture.log | tail -12
    tail -n 12 /tmp/p/s/mailbox/logs/mailbox.log 2>/dev/null | grep -v " INF "
    exit 2
fi
' 2>&1 | grep -v -E 'overlay|graph driver|vfs'
