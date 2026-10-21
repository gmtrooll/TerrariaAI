#!/usr/bin/env bash
# Oct 21 night: navigate_v4 candidate = navigate_v3's setup (12 worlds, 16 bots, seed 1, 30M, both loadouts) + the category
# quota with the template rows (--categories 0.3), on the planner with measured honey jumps. Then both v4 and v3 (re-run on
# this build, tag *_p2) on every test set for paired comparisons. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
RUN=v4cand12_30m
WORLDS="train_01 train_02 train_03 train_04 train_05 train_06 train_07 train_08 train_09 train_10 train_11 train_12"
log "night5: start $RUN"
python -m navigate.train --run $RUN --worlds $WORLDS --bots 16 --seed 1 --steps 30000000 --bf16 --overlap --micro-batch 2048 \
    --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3 --loadouts tsunami,none \
    --categories 0.3 --port 5687 > $OUT/$RUN.log 2>&1 || log "$RUN failed"
for r in $RUN navigate_v3; do
    python -m navigate.evaluate --run $r --per-band 128 --tag big_p2 --port 5784 > $OUT/${r}_evalbig_p2.log 2>&1
    python -m navigate.evaluate --run $r --loadout none --per-band 128 --tag big_none_p2 --port 5784 > $OUT/${r}_evalbignone_p2.log 2>&1
    python -m navigate.evaluate --run $r --set _bend --tag bend_p2 --port 5785 > $OUT/${r}_evalbend_p2.log 2>&1
    python -m navigate.evaluate --run $r --set _cat --tag cat_p2 --port 5797 > $OUT/${r}_evalcat_p2.log 2>&1
done
python -m navigate.evaluate --run $RUN --set _tmpl --tag tmpl --port 5797 > $OUT/${RUN}_evaltmpl.log 2>&1
python -m navigate.handmade --run $RUN --repeats 8 --port 5789 --tag p2 > $OUT/${RUN}_handmade.log 2>&1
log "done night5"
