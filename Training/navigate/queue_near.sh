#!/usr/bin/env bash
# After fs2_20m and its evaluation: install the mod with the near-waypoint option, then train and evaluate
# near_10m (10M steps, near=1), to compare with base_10m. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
while true; do
    [ -f $OUT/fs2_20m/eval.json ] && break
    grep -q Traceback $OUT/fs2_20m.log 2>/dev/null && break
    grep -q Traceback $OUT/fs2_20m_eval.log 2>/dev/null && break
    sleep 60
done
sleep 30
PYTHONPATH=../tools python -c "
from terraria_mcp.game import GameManager
r = GameManager().build(); print(r); assert r['ok']" >> $OUT/queue.log 2>&1 || { echo "$(date +%H:%M) build failed" >> $OUT/queue.log; exit 1; }
echo "$(date +%H:%M) start near_10m" >> $OUT/queue.log
python -m navigate.train --run near_10m --steps 10000000 --bf16 --overlap --micro-batch 2048 --port 5653 \
    --obs grid=fovea,visibility=exposed,near=1 > $OUT/near_10m.log 2>&1
python -m navigate.evaluate --run near_10m --record --port 5753 > $OUT/near_10m_eval.log 2>&1
echo "$(date +%H:%M) done near_10m" >> $OUT/queue.log
