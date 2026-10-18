#!/usr/bin/env bash
# Oct 19 overnight: after long_v3 (50M steps) and its evaluation, compare memory and decision interval at equal
# game time (40M ticks): recurrent 10M, plain 10M, 2-tick decisions 20M. Each run is evaluated on the held-out
# worlds with failure recording. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
while true; do  # wait for long_v3 to finish (its evaluation written) or to crash
    [ -f $OUT/long_v3/eval.json ] && break
    grep -q Traceback $OUT/long_v3.log 2>/dev/null && break
    grep -q Traceback $OUT/long_v3_eval.log 2>/dev/null && break
    sleep 60
done
run() {  # name port extra-args...
    local name=$1 port=$2; shift 2
    echo "$(date +%H:%M) start $name" >> $OUT/queue.log
    python -m navigate.train --run "$name" --bf16 --overlap --micro-batch 2048 --port "$port" "$@" > "$OUT/$name.log" 2>&1
    python -m navigate.evaluate --run "$name" --record --port $((port + 100)) > "$OUT/${name}_eval.log" 2>&1
    echo "$(date +%H:%M) done $name (exit $?)" >> $OUT/queue.log
}
run rec_10m  5650 --steps 10000000 --recurrent
run base_10m 5651 --steps 10000000
run fs2_20m  5652 --steps 20000000 --frame-skip 2 --gamma 0.995
