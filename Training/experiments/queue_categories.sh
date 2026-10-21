#!/usr/bin/env bash
# Oct 20 evening: movement-category quotas. cat_10m / cat_10m_s2 = losdetour_10m / _s2 + 30% of episodes from the
# category training sets (Training/scenarios/train_XX_cat.jsonl). Evaluated on the per-category held-out tests
# (eval_0X_cat), the 1,024 set, the bending-route set and the hand-made tests; losdetour_10m / _s2 get the category
# test too. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3"
for seed in 0 2; do
    run=cat_10m; [ $seed = 2 ] && run=cat_10m_s2
    log "categories: start $run"
    python -m navigate.train --run $run --seed $seed --categories 0.3 --port 5690 $COMMON > $OUT/$run.log 2>&1 || log "$run failed"
    python -m navigate.evaluate --run $run --set _cat --tag cat --port 5797 > $OUT/${run}_evalcat.log 2>&1
    python -m navigate.evaluate --run $run --per-band 128 --tag big --port 5784 > $OUT/${run}_evalbig.log 2>&1
    python -m navigate.evaluate --run $run --set _bend --tag bend_r1 --port 5785 > $OUT/${run}_evalbend.log 2>&1
    python -m navigate.handmade --run $run --repeats 8 --port 5789 > $OUT/${run}_handmade.log 2>&1
done
for run in losdetour_10m losdetour_10m_s2; do
    python -m navigate.evaluate --run $run --set _cat --tag cat --port 5797 > $OUT/${run}_evalcat.log 2>&1
done
log "done categories"
