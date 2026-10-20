#!/usr/bin/env bash
# After queue_day2: does a "blocked" input (decisions pressing without moving) stop the snags? losblocked_10m =
# los_10m + blocked=1. Both evaluated with the snag measure (evaluate saves the longest pressing-while-blocked run per
# episode). Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
START=$(wc -l < $OUT/queue.log)
until tail -n +$((START + 1)) $OUT/queue.log | grep -q "done day2"; do sleep 60; done
log "day3: start losblocked_10m"
python -m navigate.train --run losblocked_10m --obs grid=fovea,visibility=exposed,near=1,los=1,blocked=1 --port 5688 \
    --steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent > $OUT/losblocked_10m.log 2>&1 || log "losblocked_10m failed"
for run in losblocked_10m los_10m; do
    for r in 1 2; do
        python -m navigate.evaluate --run $run --per-band 128 --tag big_snag_r$r --port 5784 > $OUT/${run}_evalsnag_r$r.log 2>&1
    done
    python -m navigate.evaluate --run $run --set _bend --tag bend_snag --port 5785 > $OUT/${run}_evalbendsnag.log 2>&1
done
python -m navigate.handmade --run losblocked_10m --repeats 8 --port 5789 > $OUT/losblocked_10m_handmade.log 2>&1
log "done day3"
