#!/usr/bin/env bash
# Oct 20 morning: the navigate_v2 candidate = navigate_v1's setup (12 worlds, 16 bots each, seed 1) with the two
# changes that helped at 10M: the line-of-sight waypoint (los_10m) and 30% bending routes (detour_10m). 30M steps.
# Then the 1,024 set, the detour set and the hand-made tests (also for its 10M and 20M milestones).
# Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
RUN=losdetour12_30m
WORLDS="train_01 train_02 train_03 train_04 train_05 train_06 train_07 train_08 train_09 train_10 train_11 train_12"
log "morning: start $RUN"
python -m navigate.train --run $RUN --worlds $WORLDS --bots 16 --seed 1 --steps 30000000 --bf16 --overlap --micro-batch 2048 \
    --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3 --port 5683 > $OUT/$RUN.log 2>&1 || log "$RUN failed"
python -m navigate.evaluate --run $RUN --per-band 128 --tag big --port 5784 > $OUT/${RUN}_evalbig.log 2>&1
python -m navigate.evaluate --run $RUN --min-detour 1.7 --tag detours --port 5785 > $OUT/${RUN}_evaldetours.log 2>&1
python -m navigate.handmade --run $RUN --repeats 8 --port 5789 > $OUT/${RUN}_handmade.log 2>&1
for m in 10 20; do
    python -m navigate.evaluate --run $RUN --checkpoint model_${m}M.pt --per-band 128 --tag big_${m}M --port 5784 > $OUT/${RUN}_evalbig_${m}M.log 2>&1
done
log "done morning"
