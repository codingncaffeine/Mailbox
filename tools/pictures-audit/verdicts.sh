#!/usr/bin/env bash
cd "$(dirname "$0")"; OUT="$PWD/out"; failed=0
check() { if eval "$2"; then echo "  PASS  $1"; else echo "  FAIL  $1"; failed=1; fi; }
L() { echo "$OUT/$1-mailbox.log"; }
echo "audit: results"
check "sound: no sound server reachable" "! grep -qv 'No such file' '$OUT/sound-sockets.txt'"
check "s1 default: nothing fetched" "! grep -q 'Remote images:' $(L s1-default)"
check "s1 default: the bar says blocked and offers Show images + Always" "grep -q 'reading bar .*were blocked.*Show images, Always from this sender' $(L s1-default)"
check "s2 Show images: pressed, 6 of 6 fetched" "grep -q 'reading press — “Show images”' $(L s2-show) && grep -q 'Remote images: 6 of 6 fetched' $(L s2-show)"
check "s2 Show images: no blocked bar afterwards" "! tail -n +\$(grep -n 'reading press' $(L s2-show) | tail -1 | cut -d: -f1) $(L s2-show) | grep -q 'were blocked'"
check "s3 Always: pressed and 6 of 6 fetched" "grep -q 'reading press — “Always from this sender”' $(L s3-always) && grep -q 'Remote images: 6 of 6 fetched' $(L s3-always)"
check "s3 Always: stranger@example.net is on the safe list" "grep -qx 'stranger@example.net' '$OUT/safe-senders.txt'"
check "s3b safe sender: loads without asking, 6 of 6" "grep -q 'loading without asking (the sender is on the safe list)' $(L s3b-safe) && grep -q 'Remote images: 6 of 6 fetched' $(L s3b-safe)"
check "s3c other sender: still held back" "grep -q 'selected “corpus pics (other sender)”' $(L s3c-other) && ! grep -q 'Remote images:' $(L s3c-other) && grep -q 'were blocked' $(L s3c-other)"
check "s4 Trust Center + OK: the switch was pressed off" "grep -q \"pressed 'Don't download pictures automatically in messages', now off\" $(L s4-trust-ok)"
check "s4 Trust Center + OK: security.pictures.block=false survives in the kept file" "grep -q '\"security.pictures.block\": *false' '$PWD/work/kept-ok.json'"
check "s4b next run: loads without asking, 6 of 6" "grep -q 'loading without asking (pictures are not held back)' $(L s4b-auto) && grep -q 'Remote images: 6 of 6 fetched' $(L s4b-auto)"
check "s4c Trust Center + Cancel: the key is not kept" "! grep -q 'security.pictures.block' '$PWD/work/kept-cancel.json'"
check "s4d after Cancel: still held back" "! grep -q 'Remote images:' $(L s4d-still-blocked) && grep -q 'were blocked' $(L s4d-still-blocked)"
check "s5 message window: opened, pictures fetched for it" "grep -q 'windows: .*corpus pics' $(L s5-window) && [ \$(grep -c 'Remote images: 6 of 6 fetched' $(L s5-window)) -ge 2 ]"
check "s6 feed article in the mail pane: loads as a subscribed source" "grep -q 'loading without asking (a feed the reader subscribed to)' $(L s6-feed-in-mail) && grep -q 'Remote images: 1 of 1 fetched' $(L s6-feed-in-mail)"
check "s7 feed article in its own window: loads as a subscribed source" "grep -q 'step window — .*opened in its own window' $(L s7-feed-window) && [ \$(grep -c 'a feed the reader subscribed to' $(L s7-feed-window)) -ge 2 ]"
check "s8 feeds Show pictures off: held back" "grep -q 'step open 0' $(L s8-feed-off) && ! grep -q 'Remote images:' $(L s8-feed-off)"
check "no run crashed" "! grep -q 'FATAL' '$OUT'/*-stdout.log && ! grep -v 'exit=0' '$OUT/exits' | grep -q ."
[ "$failed" = 0 ] && echo "audit: PASSED" || { echo "audit: FAILED"; exit 1; }
