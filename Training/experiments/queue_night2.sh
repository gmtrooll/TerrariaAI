#!/usr/bin/env bash
# Oct 20 night (restart after the disk filled up): the two-loadout experiment, then the detour experiment.
#   none_10m  trained on "none" only;  mix_10m  50/50;  grid_ctrl_10m (exists) = Tsunami only.
#   detour_10m  = grid_ctrl_10m + 30% of episodes from detour scenarios (route >= 1.7x straight line).
# Same settings as grid_ctrl_10m (near + memory, 6 worlds, 10M, target KL, damage penalty, grid reward).
# The movement graphs for train_11/12 are skipped: the physics reward gave no gain, and nothing here uses them.
# Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --obs grid=fovea,visibility=exposed,near=1 --recurrent"
log "night2: start none_10m"
python -m navigate.train --run none_10m --loadouts none --port 5677 $COMMON > $OUT/none_10m.log 2>&1 || log "none_10m failed"
log "start mix_10m"
python -m navigate.train --run mix_10m --loadouts tsunami,none --port 5678 $COMMON > $OUT/mix_10m.log 2>&1 || log "mix_10m failed"
log "loadout evaluations"
for run in none_10m mix_10m grid_ctrl_10m; do
    for lo in tsunami none; do
        [ "$run" = grid_ctrl_10m ] && [ "$lo" = tsunami ] && continue   # exists (eval_big2.json)
        python -m navigate.evaluate --run $run --loadout $lo --per-band 128 --tag big_$lo --port 5779 > $OUT/${run}_eval_$lo.log 2>&1
    done
done
log "start detour_10m"
python -m navigate.train --run detour_10m --detour 0.3 --port 5679 $COMMON > $OUT/detour_10m.log 2>&1 || log "detour_10m failed"
python -m navigate.evaluate --run detour_10m --per-band 128 --tag big --port 5780 > $OUT/detour_10m_evalbig.log 2>&1
for run in detour_10m grid_ctrl_10m navigate_v1; do
    python -m navigate.evaluate --run $run --min-detour 1.7 --tag detours --port 5781 > $OUT/${run}_evaldetours.log 2>&1
done
log "done night2"
