#!/usr/bin/env bash
# The press sweep: every shell-reachable command in the catalogue, pressed through the real
# dispatcher in its own posed run, and classified from the settled read-back the run door logs.
#
# The question is the one the owner asks: which buttons, pressed, do nothing? The classes:
#
#   EXCEPTION  the press threw — read the pose's exceptions.txt
#   NOPRESS    the run never reached the press (died, timed out, or the pose is wrong)
#   UNKNOWN    the id is not in the registered catalogue at all
#   NOTWIRED   the dispatcher's own fallback answered "not wired yet" — honest, and listed
#   GUARDED    the press was refused for want of a selection ("Select a …")
#   ACTED      something observable happened: a window, a row-count change, a status change
#   SILENT     none of the above — the class this sweep exists to find
#
# The verdict is the diff against tools/poses/press-expectations.tsv, which records the class
# every command is *expected* to land in and why the deliberate ones are deliberate. A command
# whose class drifts — or a new command with no expectation — is the finding.
#
# Usage:
#   tools/press-sweep.sh [--ci] [--timeout <s>] [--reuse]
#
# --ci exits non-zero on any drift from expectations (for a scheduled or dispatched job);
# --reuse classifies the existing artifacts/audit/phasepress batch without re-running it,
# which is how an expectations edit is checked without another half-hour of presses.
set -uo pipefail
cd "$(dirname "$0")/.."

ci=0
reuse=0
timeout_s=60
while [[ $# -gt 0 ]]; do
    case $1 in
        --ci)      ci=1; shift ;;
        --reuse)   reuse=1; shift ;;
        --timeout) timeout_s=${2:?--timeout wants seconds}; shift 2 ;;
        *) echo "press-sweep: unknown argument: $1" >&2; exit 2 ;;
    esac
done

out="artifacts/audit/phasepress"
expectations="tools/poses/press-expectations.tsv"
report="$out/press-report.tsv"

if [[ $reuse -eq 0 ]]; then
    list=$(mktemp -t mailbox-press-list-XXXXXX)
    seed=$(mktemp -d -t mailbox-press-seed-XXXXXX)
    trap 'rm -rf "$list" "$seed"' EXIT

    echo "── generating the pose list from the catalogue"
    MAILBOX_PRESS_LIST="$list" dotnet test tests/Mailbox.Tests --filter PressSweepList \
        -v q --nologo > /dev/null \
        || { echo "press-sweep: the list generator failed"; exit 2; }
    echo "   $(grep -vc '^#' "$list") pose(s)"

    echo "── seeding $seed"
    MAILBOX_SEED="$seed" MAILBOX_TODAY=2026-08-16 \
        dotnet test tests/Mailbox.Tests --filter SeedOnRequest -v q --nologo > /dev/null \
        || { echo "press-sweep: seeding failed"; exit 2; }

    # audit-run exits non-zero when any pose throws; here a throw is a finding to classify,
    # not a reason to stop classifying the rest.
    tools/audit-run.sh press "$list" --seed "$seed" --timeout "$timeout_s" || true
fi

[[ -d $out ]] || { echo "press-sweep: no batch at $out — run without --reuse first"; exit 2; }

echo "── classifying"
printf 'id\tclass\tevidence\n' > "$report"

for dir in "$out"/press-*/; do
    [[ -d $dir ]] || continue
    id=$(basename "$dir"); id=${id#press-}

    exceptions=0
    [[ -s "$dir/exceptions.txt" ]] && exceptions=$(grep -c . "$dir/exceptions.txt")

    settled=""
    [[ -f "$dir/harness.txt" ]] && settled=$(grep -F "Harness: ran $id — " "$dir/harness.txt" | tail -1)

    class=""; evidence=""
    if [[ $exceptions -gt 0 ]]; then
        class="EXCEPTION"; evidence="$exceptions exception line(s)"
    elif [[ -z $settled ]]; then
        class="NOPRESS"; evidence="no settled read-back in harness.txt"
    elif [[ $settled == *"UNKNOWN to the catalogue"* ]]; then
        class="UNKNOWN"; evidence="the catalogue has no such id"
    else
        before=${settled#*status “}; before=${before%%”→*}
        after=${settled#*”→“};       after=${after%%”*}
        windows=${settled##*windows: }
        rows_before=""; rows_after=""
        if [[ $settled =~ rows\ ([0-9]+)→([0-9]+) ]]; then
            rows_before=${BASH_REMATCH[1]}; rows_after=${BASH_REMATCH[2]}
        fi

        # The module status pair — the channel the drawn modules speak on. Absent from batches
        # taken before the run door grew it, so its checks only fire when the line carries it.
        module_before=""; module_after=""
        if [[ $settled == *"module “"* ]]; then
            m=${settled#*module “}
            module_before=${m%%”→*}
            module_after=${m#*”→“}; module_after=${module_after%%”*}
        fi

        if [[ $after == *"not wired yet"* ]]; then
            class="NOTWIRED"; evidence="status “$after”"
        elif [[ $after == Select\ * ]]; then
            class="GUARDED"; evidence="status “$after”"
        elif [[ $windows != "none" ]]; then
            class="ACTED"; evidence="opened $windows"
        elif [[ -n $rows_before && $rows_before != "$rows_after" ]]; then
            class="ACTED"; evidence="rows $rows_before→$rows_after"
        elif [[ $after != "$before" ]]; then
            class="ACTED"; evidence="status “$after”"
        elif [[ $module_before != "$module_after" ]]; then
            class="ACTED"; evidence="module “$module_after”"
        else
            class="SILENT"; evidence="status unchanged, rows unchanged, no window"
        fi
    fi

    printf '%s\t%s\t%s\n' "$id" "$class" "$evidence" >> "$report"
done

{ head -1 "$report"; tail -n +2 "$report" | sort -t$'\t' -k2,2 -k1,1; } > "$report.tmp" \
    && mv "$report.tmp" "$report"

echo "── the batch by class"
awk -F'\t' 'NR > 1 { n[$2]++ } END { for (c in n) printf "   %-10s %d\n", c, n[c] }' "$report" | sort

# ── the verdict: the report against the expectations ─────────────────────────────────────────
drift=0
if [[ -f $expectations ]]; then
    echo "── drift against $expectations"
    while IFS=$'\t' read -r id class evidence; do
        [[ $id == "id" ]] && continue
        expected=$(awk -F'\t' -v id="$id" '$1 == id { print $2; exit }' "$expectations")
        if [[ -z $expected ]]; then
            echo "   NEW        $id is $class ($evidence) and has no expectation"
            drift=$((drift + 1))
        elif [[ $expected != "$class" ]]; then
            echo "   CHANGED    $id is $class, expected $expected ($evidence)"
            drift=$((drift + 1))
        fi
    done < "$report"
    while IFS=$'\t' read -r id rest; do
        [[ -z $id || ${id:0:1} == "#" ]] && continue
        grep -q "^$id"$'\t' "$report" || { echo "   GONE       $id has an expectation and was not swept"; drift=$((drift + 1)); }
    done < "$expectations"
    [[ $drift -eq 0 ]] && echo "   none — every command lands where the expectations say"
else
    echo "── no $expectations yet: triage $report into one to arm the gate"
    drift=-1
fi

echo "── report: $report"
[[ $ci -eq 1 && $drift -ne 0 ]] && exit 1
exit 0
