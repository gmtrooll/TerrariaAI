#!/usr/bin/env bash
# One-off (Oct 18-19 overnight): after the parity runs, rebuild the mod with the corrected planner (2-tile-wide body,
# drops through platforms) and the route waypoint, regenerate every scenario set, evaluate the scripted baselines on
# the new held-out lists, then run the experiment queue.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }

log "rebuild"
PYTHONPATH=../tools python -c "
from terraria_mcp.game import GameManager
r = GameManager().build(); print(r); assert r['ok']" >> $OUT/queue.log 2>&1 || { log "build failed"; exit 1; }

log "regenerate scenarios"
PYTHONPATH=../tools python -m terraria_mcp.scenarios --worlds train_01 train_02 train_03 train_04 train_05 train_06 eval_01 eval_02     --seeds 101 102 103 104 105 106 901 902 >> $OUT/queue.log 2>&1 || { log "scenario generation failed"; exit 1; }

# Old-list results (deterministic actions) are kept as eval_v1_det.json. The parity models cannot run on the new
# mod (the goal input grew by the waypoint), so only the scripted baselines are re-evaluated on the new lists.
for run in parity_sb3 parity_custom baseline_greedy baseline_random; do
    [ -f $OUT/$run/eval.json ] && mv $OUT/$run/eval.json $OUT/$run/eval_v1_det.json
done
log "baselines on the new held-out lists"
python -m navigate.evaluate --policy greedy --name baseline_greedy --port 5697 > $OUT/baseline_greedy_eval2.log 2>&1
python -m navigate.evaluate --policy greedy_wp --name baseline_greedy_wp --port 5698 > $OUT/baseline_greedy_wp_eval.log 2>&1
python -m navigate.evaluate --policy random --name baseline_random --port 5699 > $OUT/baseline_random_eval2.log 2>&1

log "queue"
bash experiments/overnight_queue.sh
