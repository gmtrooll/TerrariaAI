#!/usr/bin/env bash
# Oct 20 night: the clean control for phys_10m (identical settings, grid reward), then movement graphs for the
# other 6 training worlds so a 12-world physics run can follow. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
log "start grid_ctrl_10m"
python -m navigate.train --run grid_ctrl_10m --steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --port 5675 \
    --obs grid=fovea,visibility=exposed,near=1 --recurrent > $OUT/grid_ctrl_10m.log 2>&1
python -m navigate.evaluate --run grid_ctrl_10m --per-band 128 --tag big --port 5775 > $OUT/grid_ctrl_10m_evalbig.log 2>&1
log "done grid_ctrl_10m"
for w in train_07 train_08 train_09 train_10 train_11 train_12; do
    log "movement graph $w"
    python -m navigate.movegraph --world $w --loadout 1 --servers 6 >> $OUT/movegraphs.log 2>&1 || { log "movegraph $w failed: stopping"; exit 1; }
    python -m navigate.annotate_physics --world $w --loadout 1 >> $OUT/movegraphs.log 2>&1 || { log "annotate $w failed: stopping"; exit 1; }
done
log "graphs done for 12 worlds"
