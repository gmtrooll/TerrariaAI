#!/usr/bin/env bash
# After the replay_10m chain: near waypoint + memory on 12 training worlds x 16 bots (same 192 bots as 6 x 32),
# 10M steps, to compare with near_rec_10m: does more terrain variety close the train/held-out gap?
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
while true; do
    [ -f $OUT/near_rec_10m/eval_big.json ] && break
    grep -q Traceback $OUT/replay_10m.log 2>/dev/null && break
    grep -q Traceback $OUT/replay_10m_eval.log 2>/dev/null && break
    sleep 60
done
sleep 20
log "start worlds12_10m"
python -m navigate.train --run worlds12_10m --steps 10000000 --bf16 --overlap --micro-batch 2048 --port 5659 \
    --worlds train_01 train_02 train_03 train_04 train_05 train_06 train_07 train_08 train_09 train_10 train_11 train_12 \
    --bots 16 --obs grid=fovea,visibility=exposed,near=1 --recurrent > $OUT/worlds12_10m.log 2>&1
python -m navigate.evaluate --run worlds12_10m --record --port 5761 > $OUT/worlds12_10m_eval.log 2>&1
python -m navigate.evaluate --run worlds12_10m --per-band 128 --tag big --port 5762 > $OUT/worlds12_10m_evalbig.log 2>&1
log "done worlds12_10m"
