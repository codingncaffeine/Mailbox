#!/usr/bin/env bash
# The release gate: what a reader does with Mailbox, done for real before a tag — not the suites,
# which never send or receive, and not the capture harness, which turns the schedule off.
#
#   tools/release-gate.sh [path/to/mailbox]      (default: packaging/out/publish-x64/mailbox,
#                                                 the release artifact, else the Release build)
#
# Inside a private nested KWin with its own D-Bus (nothing appears on the desktop, no real
# keyring or account is touched), it checks:
#   1. send/receive — the password comes from a Secret Service through secret-tool, exactly as on
#      a desktop, and goes to a local POP3 server that says whether it was the right one; the
#      scheduled run at start-up must download both test messages;
#   2. remote images — against a server that labels a picture as octet-stream, refuses one,
#      serves a page for another and is slow for twelve: thirteen of fifteen must arrive, in
#      parallel, and the two refusals must be logged with their reasons;
#   3. the reading pane — Chromium must start sandboxed and draw frames holding the message.
# A run that cannot show a check passing fails it. 0.6.7 shipped broken send/receive because
# nothing before the tag ever sent or received; this is that something.
#
# Needs kwin_wayland, dbus-run-session, python3 with dbus-python and PyGObject, secret-tool.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
DOTNET="${DOTNET:-dotnet}"

APP="${1:-}"
if [ -z "$APP" ]; then
    if [ -x packaging/out/publish-x64/mailbox ]; then APP="$ROOT/packaging/out/publish-x64/mailbox"
    else APP="$ROOT/src/Mailbox.App/bin/Release/net10.0/mailbox"; fi
fi
[ -x "$APP" ] || { echo "release-gate: no application at $APP" >&2; exit 2; }
case "$ROOT" in *" "*) echo "release-gate: the repository path has a space; KWin's session launcher would split it" >&2; exit 2;; esac

WORK="$ROOT/scratch/release-gate"
rm -rf "$WORK"; mkdir -p "$WORK/out"
cp tools/release-gate/*.py tools/release-gate/picture.png "$WORK/"

echo "release-gate: $("$APP" --version)"
echo "release-gate: seeding a store"
XDG_CONFIG_HOME="$WORK/xdg/c" XDG_STATE_HOME="$WORK/xdg/s" XDG_DATA_HOME="$WORK/xdg/d" XDG_CACHE_HOME="$WORK/xdg/k" \
MAILBOX_SEED="$WORK/seed" MAILBOX_TODAY=2026-08-16 \
    "$DOTNET" test tests/Mailbox.Tests --filter SeedOnRequest -v quiet > "$WORK/seed.log" 2>&1 \
    || { echo "release-gate: seeding failed"; tail -20 "$WORK/seed.log"; exit 1; }

# The messages the pane is asked to draw: one with a picture from every kind of server.
python3 -I - "$WORK" <<'PY'
import sys, os
work = sys.argv[1]; os.makedirs(f"{work}/cases", exist_ok=True)
imgs = [f'<img src="http://127.0.0.1:18765/{name}.png" width="60" height="30">' for name in ("octet", "forbidden", "page")]
imgs += [f'<img src="http://127.0.0.1:18765/slow-{i}.png" width="60" height="30">' for i in range(12)]
open(f"{work}/cases/images.html", "w").write("<html><body><h1>Gate heading</h1><p>Gate paragraph.</p>" + "".join(imgs) + "</body></html>")
PY

cp tools/release-gate/session.sh "$WORK/session.sh"
chmod +x "$WORK/session.sh"

# A private compositor and runtime directory, its own XDG dirs: the same isolation the capture
# rigs use, so the real KWin configuration and sockets are never touched.
K="$WORK/kwin"; mkdir -p "$K/config" "$K/state" "$K/data" "$K/cache"
RT="/tmp/mailbox-gate-$$"; mkdir -p "$RT"; chmod 700 "$RT"
printf '[Xwayland]\nXwaylandEisNoPrompt=true\n' > "$K/config/kwinrc"
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS \
    NESTED_ROOT="$WORK" GATE_APP="$APP" \
    XDG_CONFIG_HOME="$K/config" XDG_STATE_HOME="$K/state" XDG_DATA_HOME="$K/data" XDG_CACHE_HOME="$K/cache" XDG_RUNTIME_DIR="$RT" \
    timeout -k 5 300 dbus-run-session -- kwin_wayland --virtual --xwayland --no-lockscreen --no-global-shortcuts \
        --socket "wayland-gate-$$" --width 1600 --height 1000 --exit-with-session "$WORK/session.sh" > "$K/kwin.txt" 2>&1 || true
for _ in 1 2 3 4 5 6 7 8 9 10; do grep -q " $RT/doc " /proc/mounts || break; fusermount3 -uz "$RT/doc" 2>/dev/null; sleep 0.5; done
rm -rf "$RT"

# ---- verdicts ---------------------------------------------------------------------------------
OUT="$WORK/out"; failed=0
check() { if eval "$2"; then echo "  PASS  $1"; else echo "  FAIL  $1"; failed=1; fi; }
echo "release-gate: results"
check "send/receive: the keyring's password reached the server" "grep -q 'PASS correct' '$OUT/pop3.log' 2>/dev/null"
check "send/receive: both messages downloaded" "grep -q 'Send/receive finished: 2 new' '$OUT/sr-mailbox.log' 2>/dev/null"
check "send/receive: no empty password was sent" "! grep -q 'PASS WRONG' '$OUT/pop3.log' 2>/dev/null"
check "images: 13 of 15 arrived" "grep -q 'Remote images: 13 of 15 fetched' '$OUT/pane-mailbox.log' 2>/dev/null"
check "images: the refusals say why" "[ \$(grep -c 'Remote image from 127.0.0.1 not shown' '$OUT/pane-mailbox.log' 2>/dev/null) -eq 2 ]"
check "images: fetched in parallel (under 8 s for 24 s of slow server)" "[ -f '$OUT/images-seconds' ] && [ \$(cat '$OUT/images-seconds') -lt 8 ]"
check "pane: Chromium started sandboxed" "grep -q 'Reading pane engine: Chromium .*sandboxed' '$OUT/pane-mailbox.log' 2>/dev/null"
check "pane: frames reached the pane holding the message" "grep -q 'engine holds .*Gate paragraph' '$OUT/pane-mailbox.log' && ! grep -q 'NOTHING has been drawn' '$OUT/pane-mailbox.log'"
check "no run crashed" "! grep -q 'FATAL' '$OUT'/*-stdout.log 2>/dev/null && grep -q 'sr exit=124' '$OUT/exits' && grep -q 'pane exit=0' '$OUT/exits'"
echo "release-gate: logs in $OUT"
[ "$failed" = 0 ] && echo "release-gate: PASSED" || { echo "release-gate: FAILED"; exit 1; }
