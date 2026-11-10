#!/usr/bin/env bash
set -u
HERE="${NESTED_ROOT:?}"; case "${XDG_RUNTIME_DIR:-}" in /tmp/mbx-audit-*) ;; *) exit 99;; esac
APP="$GATE_APP"; OUT="$HERE/out"; W="$HERE/work"
ls "$XDG_RUNTIME_DIR"/pulse "$XDG_RUNTIME_DIR"/pipewire-0 > "$OUT/sound-sockets.txt" 2>&1
python3 -I "$W/imgserver.py" 18767 "$W" "$OUT/images-server.log" &
IMG=$!
FEED_SUB='{"rss.feeds": "[{\"url\": \"http://127.0.0.1:18767/feed.xml\", \"name\": \"Ars Technica\", \"category\": \"Technology\"}]"'
# run NAME STORE_SRC SETTINGS_JSON [env...]: a fresh profile over a copy of a store.
run() {
  local n="$1" src="$2" js="$3"; shift 3
  local P="$W/$n"; mkdir -p "$P/config/mailbox" "$P/store"
  cp -r "$src/." "$P/store/"
  printf '%s\n' "$js" > "$P/config/mailbox/settings.json"
  env -u WAYLAND_DISPLAY MAILBOX_WAYLAND=0 MAILBOX_CAPTURE="$OUT/$n.png" MAILBOX_SIZE=1400x900 MAILBOX_STORE="$P/store/accounts" \
    MAILBOX_READING=dump XDG_CONFIG_HOME="$P/config" XDG_STATE_HOME="$P/state" XDG_DATA_HOME="$P/data" XDG_CACHE_HOME="$P/cache" \
    "$@" timeout -k 5 90 "$APP" > "$OUT/$n-stdout.log" 2>&1
  echo "$n exit=$?" >> "$OUT/exits"
  cp "$P/state/mailbox/logs/mailbox.log" "$OUT/$n-mailbox.log" 2>/dev/null
}
MAIL='MAILBOX_FOLDER=imap@example.org/Inbox'
PICK='MAILBOX_SELECT=corpus pics'

# 0. File the case into the seeded Inbox (settings default, so nothing is fetched), then a copy of
#    it from a second sender. This store is what every mail scenario starts from.
run file "$HERE/seed" '{}' MAILBOX_LINGER=1 MAILBOX_SANITIZER="$HERE/cases"
BASE="$W/base"; mkdir -p "$BASE"; cp -r "$W/file/store/." "$BASE/"
python3 -I "$HERE/plant-sender.py" "$BASE/accounts/imap@example.org.db" "corpus pics" other@example.org > "$OUT/planted.txt"
python3 -I "$W/plant-feed-picture.py" "$BASE/feeds.db" "http://127.0.0.1:18767/feed.png" > "$OUT/feed-article.txt"

# 1. Defaults: the pictures are held back and the bar offers the two ways out.
run s1-default "$BASE" '{}' MAILBOX_LINGER=3 "$MAIL" "$PICK"
# 2. The bar's Show images.
run s2-show "$BASE" '{}' MAILBOX_LINGER=6 "$MAIL" "$PICK" "MAILBOX_READING_PRESS=Show images"
# 3. The bar's Always from this sender, then the same store opened again: this sender loads
#    without asking, the other sender is still held back.
run s3-always "$BASE" '{}' MAILBOX_LINGER=6 "$MAIL" "$PICK" "MAILBOX_READING_PRESS=Always"
sqlite3 "$W/s3-always/store/accounts/imap@example.org.db" "select address from safe_senders" > "$OUT/safe-senders.txt" 2>&1 \
  || sqlite3 "$W/s3-always/store/accounts/imap@example.org.db" ".schema safe_senders" > "$OUT/safe-senders.txt" 2>&1
run s3b-safe "$W/s3-always/store" '{}' MAILBOX_LINGER=6 "$MAIL" "$PICK"
run s3c-other "$W/s3-always/store" '{}' MAILBOX_LINGER=3 "$MAIL" "MAILBOX_SELECT=other sender"
# 4. Trust Center: untick "Don't download pictures automatically" and press OK; the settings file
#    is kept, and the next run reads it. Then the same with Cancel.
KEPT="$W/kept-ok.json"; KEPTC="$W/kept-cancel.json"
run s4-trust-ok "$BASE" '{}' MAILBOX_LINGER=4 "MAILBOX_SETTINGS=$KEPT" "MAILBOX_RIBBON_EDIT=page:trust;wait;ok" "MAILBOX_OPTIONS_PRESS=Don't download pictures"
run s4b-auto "$BASE" '{}' MAILBOX_LINGER=6 "MAILBOX_SETTINGS=$KEPT" "$MAIL" "$PICK"
run s4c-trust-cancel "$BASE" '{}' MAILBOX_LINGER=4 "MAILBOX_SETTINGS=$KEPTC" "MAILBOX_RIBBON_EDIT=page:trust;wait;cancel" "MAILBOX_OPTIONS_PRESS=Don't download pictures"
run s4d-still-blocked "$BASE" '{}' MAILBOX_LINGER=3 "MAILBOX_SETTINGS=$KEPTC" "$MAIL" "$PICK"
# 5. The message's own window, pictures not held back.
run s5-window "$BASE" '{"security.pictures.block": false}' MAILBOX_LINGER=8 "$MAIL" "$PICK" MAILBOX_RUN=mail.open MAILBOX_CAPTURE_DIALOG=1
# 6. A feed article read from the mail folder pane, mail pictures held back.
run s6-feed-in-mail "$BASE" "$FEED_SUB, \"rss.inmailpane\": true}" MAILBOX_LINGER=6 "MAILBOX_FOLDER=feeds@mailbox.invalid/Ars Technica" "MAILBOX_SELECT=quiet generation"
# 7. A feed article opened in its own window from the Feeds module.
run s7-feed-window "$BASE" "$FEED_SUB}" MAILBOX_LINGER=8 MAILBOX_MODULE=feeds "MAILBOX_FEED_DO=row:Today;open:0;window" MAILBOX_CAPTURE_DIALOG=1
# 8. Feeds' own "Show pictures" switch off: the article's picture is held back.
run s8-feed-off "$BASE" "$FEED_SUB, \"rss.pictures\": false}" MAILBOX_LINGER=4 MAILBOX_MODULE=feeds "MAILBOX_FEED_DO=row:Today;open:0"
kill $IMG 2>/dev/null
