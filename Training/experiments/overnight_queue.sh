#!/usr/bin/env bash
# Overnight experiment queue (run from the Training folder): each run trains 5M steps with the custom PPO, then
# is evaluated on the held-out worlds. All runs use bf16 + overlapped collection; parity_custom is the plain reference.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
STEPS=${STEPS:-5000000}

run() {  # name port extra-args...
    local name=$1 port=$2; shift 2
    echo "$(date +%H:%M) start $name" >> $OUT/queue.log
    python -m navigate.train --run "$name" --algo custom --steps "$STEPS" --bf16 --overlap --port "$port" "$@" > "$OUT/$name.log" 2>&1
    python -m navigate.evaluate --run "$name" --port $((port + 100)) > "$OUT/${name}_eval.log" 2>&1
    echo "$(date +%H:%M) done $name (exit $?)" >> $OUT/queue.log
}

run fast_fovea_exposed   5630
run no_waypoint          5636 --obs grid=fovea,visibility=exposed,waypoint=0
run stride2              5631 --stride 2
run vis_all              5632 --obs grid=fovea,visibility=all
run vis_memory           5633 --obs grid=fovea,visibility=exposedmemory
run grid_square40        5634 --obs grid=square40,visibility=exposed
run grid_full            5635 --obs grid=full,visibility=exposed
