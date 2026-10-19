#!/usr/bin/env bash
# Oct 19 afternoon (user away 4-5 h): after near_10m, test near waypoint + memory (near_rec_10m), then train the
# better combination for 30M steps (best_30m). Memory is kept only if it adds >= 1.5 points held-out on top of
# the near waypoint (it costs ~30% speed). Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
wait_for() {  # run name: its eval.json, or a crash in its logs
    while true; do
        [ -f $OUT/$1/eval.json ] && return 0
        grep -q Traceback $OUT/$1.log 2>/dev/null && return 1
        grep -q Traceback $OUT/$1_eval.log 2>/dev/null && return 1
        sleep 60
    done
}
run() {  # name port extra-args...
    local name=$1 port=$2; shift 2
    log "start $name $*"
    python -m navigate.train --run "$name" --bf16 --overlap --micro-batch 2048 --port "$port" "$@" > "$OUT/$name.log" 2>&1
    python -m navigate.evaluate --run "$name" --record --port $((port + 100)) > "$OUT/${name}_eval.log" 2>&1
    log "done $name"
}
success() { python -c "import json; print(json.load(open('$OUT/$1/eval.json'))['overall']['success'])" 2>/dev/null || echo 0; }

wait_for near_10m
sleep 20
run near_rec_10m 5655 --steps 10000000 --obs grid=fovea,visibility=exposed,near=1 --recurrent

near=$(success near_10m)
both=$(success near_rec_10m)
if python -c "import sys; sys.exit(0 if $both >= $near + 0.015 else 1)"; then
    log "near=$near near+memory=$both: best_30m with memory"
    run best_30m 5656 --steps 30000000 --obs grid=fovea,visibility=exposed,near=1 --recurrent
else
    log "near=$near near+memory=$both: best_30m without memory"
    run best_30m 5656 --steps 30000000 --obs grid=fovea,visibility=exposed,near=1
fi
