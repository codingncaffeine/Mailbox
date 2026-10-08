#!/usr/bin/env bash
# The release gate's inside: runs in tools/release-gate.sh's private KWin and D-Bus session.
set -u
HERE="${NESTED_ROOT:?not started by tools/release-gate.sh — refusing}"
case "${XDG_RUNTIME_DIR:-}" in /tmp/mailbox-gate-*) ;; *) echo "not in the gate's runtime dir — refusing"; exit 99;; esac
APP="$GATE_APP"; OUT="$HERE/out"; : > "$OUT/exits"

# ---- 1. send/receive, through a Secret Service, as on a desktop -------------------------------
SECRET="gate-$RANDOM-$RANDOM"
GATE_ATTRS="service=mailbox,account=you@example.com,purpose=incoming" GATE_SECRET="$SECRET" \
    python3 -I "$HERE/fake-secrets.py" "$OUT/secrets.log" &
SECRETS=$!
python3 -I "$HERE/pop3.py" 11110 "$SECRET" "$OUT/pop3.log" &
POP=$!
sleep 1.5
[ "$(secret-tool lookup service mailbox account you@example.com purpose incoming 2>/dev/null)" = "$SECRET" ] \
    && echo "keyring control: secret-tool returns the test password" > "$OUT/keyring-control" \
    || echo "keyring control: FAILED" > "$OUT/keyring-control"

P="$HERE/sr"; mkdir -p "$P/config/mailbox" "$P/store/accounts"
cp "$HERE/seed/accounts/you@example.com.db" "$P/store/accounts/"
cat > "$P/config/mailbox/settings.json" <<JSON
{"accounts.order": "you@example.com", "accounts.default": "you@example.com",
 "account.you@example.com.incoming.host": "127.0.0.1", "account.you@example.com.incoming.port": 11110,
 "account.you@example.com.incoming.security": 0, "account.you@example.com.incoming.user": "you@example.com",
 "account.you@example.com.outgoing.host": "127.0.0.1", "account.you@example.com.outgoing.port": 12525,
 "account.you@example.com.outgoing.security": 0, "account.you@example.com.outgoing.user": ""}
JSON
# An ordinary start, not a capture run: the schedule runs at open, as it does for a reader.
env -u WAYLAND_DISPLAY MAILBOX_WAYLAND=0 MAILBOX_STORE="$P/store/accounts" \
    XDG_CONFIG_HOME="$P/config" XDG_STATE_HOME="$P/state" XDG_DATA_HOME="$P/data" XDG_CACHE_HOME="$P/cache" \
    timeout -k 5 40 "$APP" > "$OUT/sr-stdout.log" 2>&1
echo "sr exit=$?" >> "$OUT/exits"
cp "$P/state/mailbox/logs/mailbox.log" "$OUT/sr-mailbox.log" 2>/dev/null
kill $SECRETS $POP 2>/dev/null

# ---- 2 and 3. remote images and the reading pane ----------------------------------------------
python3 -I "$HERE/imgserver.py" 18765 "$HERE" "$OUT/images-server.log" &
IMG=$!
Q="$HERE/pane"; mkdir -p "$Q/config/mailbox" "$Q/store"
cp -r "$HERE/seed/." "$Q/store/"
rm -f "$Q/store/feeds.db"
printf '{"security.pictures.block": false}\n' > "$Q/config/mailbox/settings.json"
env -u WAYLAND_DISPLAY MAILBOX_WAYLAND=0 MAILBOX_CAPTURE="$OUT/pane.png" MAILBOX_LINGER=15 MAILBOX_READING=dump \
    MAILBOX_SANITIZER="$HERE/cases" MAILBOX_SIZE=1400x900 MAILBOX_STORE="$Q/store/accounts" \
    XDG_CONFIG_HOME="$Q/config" XDG_STATE_HOME="$Q/state" XDG_DATA_HOME="$Q/data" XDG_CACHE_HOME="$Q/cache" \
    timeout -k 5 90 "$APP" > "$OUT/pane-stdout.log" 2>&1
echo "pane exit=$?" >> "$OUT/exits"
kill $IMG 2>/dev/null
cp "$Q/state/mailbox/logs/mailbox.log" "$OUT/pane-mailbox.log" 2>/dev/null

# How long the fifteen pictures took, from the first request to the last answer.
python3 -I - "$OUT/images-server.log" > "$OUT/images-seconds" <<'PY'
import sys, datetime
times = [datetime.datetime.strptime(l.split()[0], "%H:%M:%S") for l in open(sys.argv[1]) if l.strip()]
print(int((max(times) - min(times)).total_seconds()) if times else 999)
PY
