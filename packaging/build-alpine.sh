#!/usr/bin/env bash
# Mailbox built from source on Alpine, the way a musl user builds it: a clean alpine:latest
# container, the distribution's own .NET SDK, the tree as it stands, and the unit suite run on
# what came out. The build is then started twice: once with no display, where it must get as far
# as looking for one, and once under Xvfb for a capture, so that the runtime, Avalonia and every
# native library the build carries are seen to load and draw against musl.
#
#   bash packaging/build-alpine.sh      → packaging/out/alpine/{build.log, capture.png}
#
# PASS is: the build compiles with no warning, every test passes, the version line prints, the
# display-less start stops at XOpenDisplay, and the capture is a picture of the window. A musl
# build carries no Chromium — the engine is built for glibc — so its reading pane renders text;
# the project file leaves Chromium out of such a build rather than failing to link its helper.
#
# NuGet's packages are kept between runs under scratch/alpine-nuget; delete it for a cold run.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
OUT="$ROOT/packaging/out/alpine"
NUGET="$ROOT/scratch/alpine-nuget"
IMAGE="${IMAGE:-docker.io/library/alpine:latest}"

rm -rf "$OUT"; mkdir -p "$OUT/src" "$NUGET"
# The tree as it stands: tracked files and untracked ones the ignore rules do not cover, so an
# edit is tested before it is committed and build output is not carried in.
git ls-files -co --exclude-standard -z | tar --null -T - -c | tar -x -C "$OUT/src"

echo "── $IMAGE: build from source with the distribution's SDK, test, start, capture"
podman run --rm -v "$OUT/src:/src" -v "$OUT:/out" -v "$NUGET:/nuget" -w /src \
    -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$IMAGE" sh -c '
set -u
cat /etc/alpine-release | sed "s/^/  alpine /"
# The SDK, and what the built application needs to start and draw: ICU for globalization, the
# zone database (the base image has none, and the calendar and its tests read zones by name),
# the C++ runtime, fontconfig with one font family, the X11 libraries Avalonia opens by name,
# and Xvfb for the capture.
apk add -q --no-cache dotnet10-sdk bash icu-libs tzdata libstdc++ libgcc fontconfig font-dejavu \
    libx11 libice libsm libxext libxi libxrandr libxcursor libxkbcommon mesa-egl xvfb-run \
    || { echo "APK FAILED"; exit 2; }
echo "  dotnet $(dotnet --version)"
echo "── build (Release)"
if ! dotnet build Mailbox.slnx -c Release -v q > /out/build.log 2>&1; then
    echo "BUILD FAILED"; grep -E "error|warning" /out/build.log | sort -u | head -20; exit 2
fi
grep -c -E "warning [A-Z]+[0-9]+" /out/build.log | sed "s/^/  warnings: /"
ls src/Mailbox.App/bin/Release/net10.0/chromium > /dev/null 2>&1 && echo "  chromium: PRESENT (should not be, on musl)" || echo "  chromium: left out, as a musl build should"
echo "── unit suite"
if ! dotnet test tests/Mailbox.Tests -c Release --no-build -v q > /out/test.log 2>&1; then
    echo "TESTS FAILED"; grep -E "Failed |error" /out/test.log | head -20; exit 2
fi
grep -E "^(Passed!|Failed!|Test summary|  total|  failed|  succeeded)" /out/test.log | sed "s/^/  /"
APP=src/Mailbox.App/bin/Release/net10.0/mailbox
echo "── the build"
"$APP" --version | sed "s/^/  /"
echo "── ldd sweep over the build (anything the system does not have):"
found=0; : > /tmp/missing
for f in $(find src/Mailbox.App/bin/Release/net10.0 src/Mailbox.App/bin/Release/net10.0/runtimes/linux-musl-x64/native -maxdepth 1 -type f \( -perm -u+x -o -name "*.so*" \)); do
    if [ "$(head -c 4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ]; then
        found=$((found+1))
        ldd "$f" 2>&1 | grep -E "Error|not found" | sed "s|^|  $(basename "$f"): |" >> /tmp/missing
    fi
done
sort -u /tmp/missing; echo "  ($found ELF files scanned)"
export XDG_CONFIG_HOME=/tmp/p/c XDG_STATE_HOME=/tmp/p/s XDG_DATA_HOME=/tmp/p/d XDG_CACHE_HOME=/tmp/p/k
echo "── start probe (no display; expect it to stop at XOpenDisplay):"
timeout 15 "$APP" 2>&1 | grep -v -E "^[0-9:.]* *[0-9.]*s INF" | head -6
echo "── capture under Xvfb:"
rm -rf /tmp/p
if MAILBOX_CAPTURE=/out/capture.png timeout 60 xvfb-run -a -s "-screen 0 1600x1000x24" "$APP" > /tmp/capture.log 2>&1 \
   && [ -s /out/capture.png ]; then
    echo "  capture.png: $(stat -c %s /out/capture.png) bytes"
else
    echo "  CAPTURE FAILED"; grep -v " INF " /tmp/capture.log | tail -12
    tail -n 12 /tmp/p/s/mailbox/logs/mailbox.log 2>/dev/null | grep -v " INF "
    exit 2
fi
' 2>&1 | grep -v -E 'overlay|graph driver|vfs'
