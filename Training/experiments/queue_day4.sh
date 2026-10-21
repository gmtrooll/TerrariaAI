#!/usr/bin/env bash
# After queue_day3: the v3 candidate again (the first try failed: train_07-12 had no _j6 sets; navigate.scenario_set
# made them). navigate_v2's setup + mixed loadouts, 30M steps, then evaluations on both loadouts, the detour and
# bending-route sets and the hand-made tests. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
START=$(wc -l < $OUT/queue.log)
until tail -n +$((START + 1)) $OUT/queue.log | grep -q "done day3"; do sleep 60; done
RUN=losdetourmix12_30m
WORLDS="train_01 train_02 train_03 train_04 train_05 train_06 train_07 train_08 train_09 train_10 train_11 train_12"
for w in $WORLDS; do [ -f ../Training/scenarios/${w}_j6.jsonl ] || { log "day4: missing ${w}_j6, not starting $RUN"; exit 1; }; done
rm -rf $OUT/$RUN
log "day4: start $RUN"
python -m navigate.train --run $RUN --worlds $WORLDS --bots 16 --seed 1 --steps 30000000 --bf16 --overlap --micro-batch 2048 \
    --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3 --loadouts tsunami,none --port 5687 > $OUT/$RUN.log 2>&1 || log "$RUN failed"
python -m navigate.evaluate --run $RUN --per-band 128 --tag big --port 5784 > $OUT/${RUN}_evalbig.log 2>&1
python -m navigate.evaluate --run $RUN --loadout none --per-band 128 --tag big_none --port 5784 > $OUT/${RUN}_evalbig_none.log 2>&1
python -m navigate.evaluate --run $RUN --min-detour 1.7 --tag detours --port 5785 > $OUT/${RUN}_evaldetours.log 2>&1
python -m navigate.evaluate --run $RUN --set _bend --tag bend_r1 --port 5797 > $OUT/${RUN}_evalbend_r1.log 2>&1
python -m navigate.handmade --run $RUN --repeats 8 --port 5789 > $OUT/${RUN}_handmade.log 2>&1
log "done day4"
