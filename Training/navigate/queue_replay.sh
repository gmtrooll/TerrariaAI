#!/usr/bin/env bash
# After best_30m: larger held-out evaluations (1,024 scenarios) of best_30m and long_v3, then replay_10m
# (near waypoint + memory + prioritized replay of hard scenarios, 10M steps) to compare with near_rec_10m.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
while true; do
    [ -f $OUT/best_30m/eval.json ] && break
    grep -q Traceback $OUT/best_30m.log 2>/dev/null && break
    grep -q Traceback $OUT/best_30m_eval.log 2>/dev/null && break
    sleep 60
done
sleep 20
log "big evaluations"
python -m navigate.evaluate --run best_30m --per-band 128 --tag big --port 5770 > $OUT/best_30m_evalbig.log 2>&1
python -m navigate.evaluate --run long_v3 --per-band 128 --tag big --port 5771 > $OUT/long_v3_evalbig.log 2>&1
log "start replay_10m"
python -m navigate.train --run replay_10m --steps 10000000 --bf16 --overlap --micro-batch 2048 --port 5657 \
    --obs grid=fovea,visibility=exposed,near=1 --recurrent --replay 0.5 > $OUT/replay_10m.log 2>&1
python -m navigate.evaluate --run replay_10m --record --port 5757 > $OUT/replay_10m_eval.log 2>&1
log "done replay_10m"
