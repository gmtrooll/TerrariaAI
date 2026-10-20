#!/usr/bin/env bash
# Oct 20 night: obstacle templates. tmpl_10m / tmpl_10m_s2 = cat_10m / _s2 (category quota 0.3) with the template rows
# (Training/scenarios/train_XX_tmpl.jsonl: honey pits, honey-block floors) as two more categories, on the planner
# with measured honey jumps (commit 8c45014+). Baselines cat_10m / _s2 are re-evaluated on the same build (tag *_p2).
# Held-out template test: eval_0X_tmpl. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3"
for seed in 0 2; do
    run=tmpl_10m; [ $seed = 2 ] && run=tmpl_10m_s2
    log "templates: start $run"
    python -m navigate.train --run $run --seed $seed --categories 0.3 --port 5690 $COMMON > $OUT/$run.log 2>&1 || log "$run failed"
    python -m navigate.evaluate --run $run --set _tmpl --tag tmpl --port 5797 > $OUT/${run}_evaltmpl.log 2>&1
    python -m navigate.evaluate --run $run --set _cat --tag cat_p2 --port 5797 > $OUT/${run}_evalcat.log 2>&1
    python -m navigate.evaluate --run $run --per-band 128 --tag big_p2 --port 5784 > $OUT/${run}_evalbig.log 2>&1
    python -m navigate.evaluate --run $run --set _bend --tag bend_p2 --port 5785 > $OUT/${run}_evalbend.log 2>&1
    python -m navigate.handmade --run $run --repeats 8 --port 5789 --tag p2 > $OUT/${run}_handmade.log 2>&1
done
for run in cat_10m cat_10m_s2; do
    python -m navigate.evaluate --run $run --set _tmpl --tag tmpl --port 5797 > $OUT/${run}_evaltmpl.log 2>&1
    python -m navigate.evaluate --run $run --set _cat --tag cat_p2 --port 5797 > $OUT/${run}_evalcat_p2.log 2>&1
    python -m navigate.evaluate --run $run --per-band 128 --tag big_p2 --port 5784 > $OUT/${run}_evalbig_p2.log 2>&1
    python -m navigate.evaluate --run $run --set _bend --tag bend_p2 --port 5785 > $OUT/${run}_evalbend_p2.log 2>&1
done
python -m navigate.evaluate --run navigate_v3 --set _tmpl --tag tmpl --port 5797 > $OUT/navigate_v3_evaltmpl.log 2>&1
log "done templates"
