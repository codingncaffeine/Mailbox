#!/usr/bin/env bash
# Runs a list of harness poses, one capture each, and files the results by phase.
#
# The audit's batch runner. A pose list is a text file of one pose per line:
#
#     <name><TAB><VAR=VALUE> [<VAR=VALUE> ...]
#
# Blank lines and lines starting with # are ignored. Each pose is run with
# MAILBOX_CAPTURE pointed at its own PNG and MAILBOX_STORE pointed at a scratch store,
# so nothing a pose writes can reach the real one. What comes back per pose:
#
#     artifacts/audit/<phase>/<name>/capture.png   what it drew, if it drew anything
#     artifacts/audit/<phase>/<name>/run.log       stdout and stderr
#     artifacts/audit/<phase>/<name>/harness.txt   the Harness: lines, which are the read-back
#     artifacts/audit/<phase>/<name>/exceptions.txt  grep -i exception, per rule 8
#
# and one summary.tsv across the batch: name, exit code, PNG bytes, harness lines,
# exception count. A pose that writes no PNG and logs no Harness line reached nothing —
# which for a door inventory is the answer, not a failure.
#
# Usage:
#   tools/audit-run.sh <phase> <pose-list> [--seed <dir>] [--theme <id>] [--timeout <s>]
#   tools/audit-run.sh 0 tools/poses/doors.tsv --theme darkgray
#
# Rule 2 of the plan's evidence: a capture without MAILBOX_CAPTURE is not a harness run.
# Rule 3: a pose that writes runs with MAILBOX_STORE. Both are set here so a caller cannot
# forget either.
set -uo pipefail
cd "$(dirname "$0")/.."

die() { echo "audit-run: $*" >&2; exit 2; }

[[ $# -ge 2 ]] || die "usage: audit-run.sh <phase> <pose-list> [--seed <dir>] [--theme <id>] [--timeout <s>]"

phase=$1; shift
list=$1; shift
[[ -f $list ]] || die "no such pose list: $list"

seed=""
theme=""
timeout_s=60

while [[ $# -gt 0 ]]; do
    case $1 in
        # The directory a seeding run produced, not MAILBOX_SEED itself: MAILBOX_SEED is what
        # *makes* a seed, and a run *reads* one through MAILBOX_STORE pointed at its accounts
        # directory — pim.db, feeds.db and the OpenPGP ring are found beside it. Each pose gets
        # its own copy, so one that writes cannot change what the next one sees.
        --seed)    seed=${2:?--seed wants the directory a seeding run produced}; shift 2 ;;
        --theme)   theme=${2:?--theme wants a theme id}; shift 2 ;;
        --timeout) timeout_s=${2:?--timeout wants seconds}; shift 2 ;;
        *) die "unknown argument: $1" ;;
    esac
done

if [[ -n $seed ]]; then
    [[ -d "$seed/accounts" ]] || die "$seed holds no accounts directory — is it a seeded store?"
fi

out="artifacts/audit/phase${phase}"
mkdir -p "$out"
summary="$out/summary.tsv"

# The binary. Built once here rather than per pose: a hundred poses is a hundred builds
# otherwise, and MSBuild's reusable nodes hold on to whatever lock a caller wrapped this in.
export MSBUILDDISABLENODEREUSE=1
echo "── building"
dotnet build src/Mailbox.App -c Debug -v q --nologo > "$out/build.log" 2>&1 \
    || { tail -20 "$out/build.log"; die "the build failed; see $out/build.log"; }

# A scratch store and state directory, so no pose can touch the real ones even if its own
# line forgets to say so. A pose's own MAILBOX_STORE overrides this by being later in env.
scratch=$(mktemp -d -t mailbox-audit-XXXXXX)
trap 'rm -rf "$scratch"' EXIT

printf 'name\texit\tpng_bytes\tharness_lines\texceptions\n' > "$summary"

total=0; drew=0; spoke=0; threw=0

while IFS= read -r line || [[ -n $line ]]; do
    [[ -z ${line// } || ${line:0:1} == "#" ]] && continue

    name=${line%%$'\t'*}
    env_spec=${line#*$'\t'}
    [[ $name == "$env_spec" ]] && env_spec=""      # a bare name with no env of its own

    total=$((total + 1))
    dir="$out/$name"
    mkdir -p "$dir"

    # Built per pose rather than exported once: every pose gets a clean environment, so one
    # that sets a variable cannot leak it into the next.
    # A pristine store per pose. Copied rather than shared because poses write — a move, a
    # flag, a new folder — and the next pose must not inherit what the last one did.
    store="$scratch/$name"
    if [[ -n $seed ]]; then
        rm -rf "$store"
        cp -r "$seed" "$store"
    else
        mkdir -p "$store/accounts"
    fi

    env_args=(
        "MAILBOX_CAPTURE=$dir/capture.png"
        "MAILBOX_STORE=$store/accounts"
        "XDG_STATE_HOME=$scratch/state"
        "XDG_CONFIG_HOME=$scratch/config"
    )
    [[ -n $theme ]] && env_args+=("MAILBOX_THEME=$theme")
    # The pose's own settings come last so they win over the defaults above.
    # shellcheck disable=SC2206
    [[ -n $env_spec ]] && env_args+=($env_spec)

    timeout --kill-after=5s "${timeout_s}s" \
        env "${env_args[@]}" \
        dotnet run --no-build --project src/Mailbox.App > "$dir/run.log" 2>&1
    rc=$?

    grep -a 'Harness:' "$dir/run.log" > "$dir/harness.txt" 2>/dev/null

    # Rule 8, minus the runner's own footprint. Both the capture path and the scratch store are
    # named after the pose, so a pose called peek-autocorrectexceptions matched its own
    # directory and read as a run that threw. The paths are struck out of each line before the
    # match rather than the lines dropped, so a genuine exception that happens to mention a
    # path still counts.
    sed -e "s#${dir}##g" -e "s#${store}##g" "$dir/run.log" 2>/dev/null \
        | grep -ai exception > "$dir/exceptions.txt" 2>/dev/null

    png=0
    [[ -f "$dir/capture.png" ]] && png=$(stat -c %s "$dir/capture.png" 2>/dev/null || echo 0)
    hl=$(wc -l < "$dir/harness.txt" 2>/dev/null || echo 0)
    ex=$(wc -l < "$dir/exceptions.txt" 2>/dev/null || echo 0)

    [[ $png -gt 0 ]] && drew=$((drew + 1))
    [[ $hl  -gt 0 ]] && spoke=$((spoke + 1))
    [[ $ex  -gt 0 ]] && threw=$((threw + 1))

    printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$rc" "$png" "$hl" "$ex" >> "$summary"

    flag=""
    [[ $rc -eq 124 || $rc -eq 137 ]] && flag=" TIMEOUT"
    [[ $ex -gt 0 ]] && flag="$flag EXCEPTION"
    [[ $png -eq 0 && $hl -eq 0 ]] && flag="$flag REACHED-NOTHING"
    printf '%-34s exit %-4s png %-9s harness %-4s%s\n' "$name" "$rc" "$png" "$hl" "$flag"
done < "$list"

echo
echo "── $total poses: $drew drew, $spoke spoke, $threw threw"
echo "── $summary"

# A pose that threw is the thing rule 8 exists for, so the runner's own exit says so.
[[ $threw -eq 0 ]]
