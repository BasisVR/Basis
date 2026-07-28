#!/usr/bin/env bash
#
# Deterministic regression gate: replay every committed crash repro through its
# fuzz target and fail if any still crashes. Each repro crashed the parser before
# its fix and passes after, so a red here means a memory-safety fix was reverted
# or a parser change reintroduced the bug. Fast, no fuzzing, no corpus growth.
#
#   ./build.sh && ./ci-replay.sh
#
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin="$here/build"
fail=0
total=0

for tcdir in "$here"/testcases/*/; do
    [ -d "$tcdir" ] || continue
    target=$(basename "$tcdir")
    exe="$bin/fuzz_${target}"
    [ -x "$exe" ] || exe="$bin/fuzz_${target}.exe"
    if [ ! -x "$exe" ]; then
        echo "no target binary for '$target' (build it first)"; fail=1; continue
    fi
    shopt -s nullglob
    cases=("$tcdir"*)
    shopt -u nullglob
    [ ${#cases[@]} -eq 0 ] && continue
    echo "fuzz_${target}: replaying ${#cases[@]} repro(s)"
    for c in "${cases[@]}"; do
        total=$((total + 1))
        # -timeout so a non-crashing parser loop fails fast instead of hanging
        # the gate for libFuzzer's default 1200 s.
        if "$exe" -timeout=30 "$c" >/tmp/fuzz_replay.log 2>&1; then
            echo "  ok    $(basename "$c")"
        else
            echo "  CRASH $(basename "$c")"
            grep -iE "ERROR:|runtime error|SUMMARY:" /tmp/fuzz_replay.log | head -2 | sed 's/^/        /'
            fail=1
        fi
    done
done

echo
if [ $total -eq 0 ]; then
    echo "FAIL: no pinned repro(s) found under testcases/ — gate would pass vacuously"
    fail=1
elif [ $fail -eq 0 ]; then
    echo "PASS: $total pinned repro(s) all clean"
else
    echo "FAIL: a pinned repro crashed — a memory-safety fix has regressed"
fi
exit $fail
