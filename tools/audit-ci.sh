#!/usr/bin/env bash
# The audit's door batch as a CI job: seed a store, open every door, and fail the run if any
# pose threw or died. This is what keeps a door that stops opening from waiting for the next
# audit to be found — the doors are how everything else is verified, so a broken one is a
# verification outage, not a nicety.
#
# The gate is deliberately narrower than an audit session's reading of the batch: it holds
# every pose to "exited cleanly, logged no exception". Whether a door photographed the right
# surface is the audit's judgement, made against artifacts; CI holds the doors open.
set -euo pipefail
cd "$(dirname "$0")/.."

seed=$(mktemp -d -t mailbox-ci-seed-XXXXXX)
trap 'rm -rf "$seed"' EXIT

echo "── seeding $seed"
MAILBOX_SEED="$seed" MAILBOX_TODAY=2026-08-16 \
    dotnet test tests/Mailbox.Tests --filter SeedOnRequest -v q --nologo

# audit-run exits non-zero when any pose logs an exception (its own rule 8); the check below
# adds the poses that died without saying anything — a non-zero exit or a timeout.
tools/audit-run.sh ci tools/poses/doors.tsv --seed "$seed" --timeout 90

failed=$(awk -F'\t' 'NR > 1 && $2 != 0 { print $1 " exited " $2 }' artifacts/audit/phaseci/summary.tsv)
if [[ -n $failed ]]; then
    echo "── doors that did not exit cleanly:"
    echo "$failed"
    exit 1
fi

echo "── every door opened and exited cleanly"
