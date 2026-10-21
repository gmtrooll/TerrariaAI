#!/usr/bin/env bash
# Oct 20 night, after queue_night2: the line-of-sight waypoint (los=1, ObservationConfig.WaypointLos).
#   los_10m = grid_ctrl_10m + far waypoint kept in sight (handmade tests: the 12-step waypoint points under the ledge
#   after a short drop, and the policy follows it back).
# Evaluated on the 1,024 set, the detour set and the hand-made tests, with grid_ctrl_10m and detour_10m alongside.
# Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
START=$(wc -l < $OUT/queue.log)  # only look at lines written after this queue started
until tail -n +$((START + 1)) $OUT/queue.log | grep -q "done night2"; do sleep 60; done
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent"
log "night3: start los_10m"
python -m navigate.train --run los_10m --obs grid=fovea,visibility=exposed,near=1,los=1 --port 5682 $COMMON > $OUT/los_10m.log 2>&1 || log "los_10m failed"
python -m navigate.evaluate --run los_10m --per-band 128 --tag big --port 5782 > $OUT/los_10m_evalbig.log 2>&1
python -m navigate.evaluate --run los_10m --min-detour 1.7 --tag detours --port 5783 > $OUT/los_10m_evaldetours.log 2>&1
for run in los_10m grid_ctrl_10m detour_10m navigate_v1; do
    python -m navigate.handmade --run $run --repeats 8 --port 5789 > $OUT/${run}_handmade.log 2>&1
done
log "done night3"
