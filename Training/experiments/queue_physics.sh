#!/usr/bin/env bash
# Oct 19 night: after final12_50m and its evaluations, the physics planner's first test (option C):
# install the new build, sample movement graphs (Tsunami) for the 6 training and 2 held-out worlds, annotate their
# scenarios with physics times, then train phys_10m (near + memory, physics progress reward) and evaluate it on the
# 1,024 set, to compare with near_rec_10m (91.5%, grid reward). Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
log "physics queue: install the new build"
PYTHONPATH=../tools python -c "
from terraria_mcp.game import GameManager
r = GameManager().build(); print(r); assert r['ok'] and 'not yet' not in str(r.get('installed'))" >> $OUT/queue.log 2>&1 || { log "build/install failed"; exit 1; }
for w in train_02 train_03 train_04 train_05 train_06 eval_01 eval_02; do  # train_01: sampled and annotated (graph v2)
    log "movement graph $w"
    python -m navigate.movegraph --world $w --loadout 1 --servers 6 >> $OUT/movegraphs.log 2>&1 || { log "movegraph $w failed: stopping"; exit 1; }
    python -m navigate.annotate_physics --world $w --loadout 1 >> $OUT/movegraphs.log 2>&1 || { log "annotate $w failed: stopping"; exit 1; }
done
log "start phys_10m"
python -m navigate.train --run phys_10m --steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --port 5673 \
    --obs grid=fovea,visibility=exposed,near=1 --recurrent --physics-reward > $OUT/phys_10m.log 2>&1
python -m navigate.evaluate --run phys_10m --record --port 5773 > $OUT/phys_10m_eval.log 2>&1
python -m navigate.evaluate --run phys_10m --per-band 128 --tag big --port 5774 > $OUT/phys_10m_evalbig.log 2>&1
log "done phys_10m"
