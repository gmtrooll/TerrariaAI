#!/usr/bin/env bash
# After queue_day: every model of the ablation, the seeds and the v3 candidate on the 1,136-scenario bending-route set
# (Training/scenarios/eval_0X_bend.jsonl), 2 runs each. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
START=$(wc -l < $OUT/queue.log)
until tail -n +$((START + 1)) $OUT/queue.log | grep -q "done day$"; do sleep 60; done
log "day2: bending-route evaluations"
for run in grid_ctrl_10m detour_10m los_10m losdetour_10m grid_ctrl_10m_s2 los_10m_s2 losdetourmix12_30m; do
    for r in 1 2; do
        python -m navigate.evaluate --run $run --set _bend --tag bend_r$r --port 5797 > $OUT/${run}_evalbend_r$r.log 2>&1
    done
done
log "done day2"
