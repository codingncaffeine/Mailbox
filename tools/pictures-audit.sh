#!/usr/bin/env bash
# The pictures audit: every control a reader has for allowing images in a message, pressed through
# the real application, and every one checked against what then loads — or does not.
#
#   tools/pictures-audit.sh [path/to/mailbox]      (default: the Release build)
#
# Inside a private nested KWin with its own D-Bus and runtime directory, over scratch profiles
# seeded by the test suite, it opens a message with six pictures from a local server and checks:
# the defaults hold them back; the bar's Show images; the bar's Always from this sender, and that
# the next run loads for that sender and still holds back another; the Trust Center switch
# through OK (kept) and through Cancel (not kept); the message's own window; a feed article in
# the mail folder pane, in the Feeds module's own window, and with the feeds' Show pictures off.
# Needs kwin_wayland, dbus-run-session, python3, sqlite3.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
DOTNET="${DOTNET:-dotnet}"
APP="${1:-$ROOT/src/Mailbox.App/bin/Release/net10.0/mailbox}"
[ -x "$APP" ] || { echo "pictures-audit: no application at $APP" >&2; exit 2; }
# The sessions run from their own folder, so a path given relative to here must not stay so.
APP="$(realpath "$APP")"
case "$ROOT" in *" "*) echo "pictures-audit: the repository path has a space; KWin's session launcher would split it" >&2; exit 2;; esac

HERE="$ROOT/scratch/pictures-audit"
rm -rf "$HERE"; mkdir -p "$HERE/out" "$HERE/work" "$HERE/cases"
cp tools/pictures-audit/*.sh tools/pictures-audit/*.py "$HERE/"
echo "pictures-audit: $("$APP" --version)"
echo "pictures-audit: seeding a store"
XDG_CONFIG_HOME="$HERE/xdg/c" XDG_STATE_HOME="$HERE/xdg/s" XDG_DATA_HOME="$HERE/xdg/d" XDG_CACHE_HOME="$HERE/xdg/k" \
MAILBOX_SEED="$HERE/seed" MAILBOX_TODAY=2026-08-16 \
    "$DOTNET" test "$ROOT/tests/Mailbox.Tests" --filter SeedOnRequest -v quiet > "$HERE/seed.log" 2>&1 \
    || { echo "pictures-audit: seeding failed"; tail -20 "$HERE/seed.log"; exit 1; }
cd "$HERE"
python3 -I - "$HERE" <<'PY'
import sys; h=sys.argv[1]
imgs="".join(f'<img src="http://127.0.0.1:18767/pic-{i}.png" width="60" height="30">' for i in range(6))
imgs+='<img src="http://127.0.0.1:18767/big.bmp" width="130" height="120">'
open(f"{h}/cases/pics.html","w").write("<html><body><h1>Audit heading</h1><p>Audit paragraph.</p>"+imgs+"</body></html>")
PY
cp "$ROOT/tools/release-gate/imgserver.py" "$ROOT/tools/release-gate/picture.png" "$ROOT/tools/release-gate/plant-feed-picture.py" work/
RT="/tmp/mbx-audit-$$"; mkdir -p "$RT"; chmod 700 "$RT"
K="$HERE/work/kwin"; mkdir -p "$K/config" "$K/state" "$K/data" "$K/cache"
printf '[Xwayland]\nXwaylandEisNoPrompt=true\n' > "$K/config/kwinrc"
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS NESTED_ROOT="$HERE" GATE_APP="$APP" \
    XDG_CONFIG_HOME="$K/config" XDG_STATE_HOME="$K/state" XDG_DATA_HOME="$K/data" XDG_CACHE_HOME="$K/cache" XDG_RUNTIME_DIR="$RT" \
    timeout -k 5 900 dbus-run-session -- kwin_wayland --virtual --xwayland --no-lockscreen --no-global-shortcuts \
        --socket "wayland-audit-$$" --width 1600 --height 1000 --exit-with-session "$HERE/session.sh" > "$HERE/out/kwin.txt" 2>&1 || true
for _ in 1 2 3 4 5; do grep -q " $RT/doc " /proc/mounts || break; fusermount3 -uz "$RT/doc" 2>/dev/null || true; sleep 0.5; done
rm -rf "$RT"
bash "$HERE/verdicts.sh"
