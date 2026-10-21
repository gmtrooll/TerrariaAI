#!/usr/bin/env bash
# Oct 20 night: the two-loadout experiment (no extra jump vs Tsunami in a Bottle). After the graph queue:
#   none_10m  trained on "none" only;  mix_10m  trained on a 50/50 mix;  grid_ctrl_10m (exists) = Tsunami only.
# Same settings as grid_ctrl_10m (near + memory, 6 worlds, 10M, target KL, damage penalty, grid reward).
# Every model is evaluated on both loadouts (1,024 sets). Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
START=$(wc -l < $OUT/queue.log)  # only look at lines written after this queue started
until tail -n +$((START + 1)) $OUT/queue.log | grep -q "graphs done for 12 worlds\|failed: stopping"; do sleep 60; done
sleep 20
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --obs grid=fovea,visibility=exposed,near=1 --recurrent"
log "start none_10m"
python -m navigate.train --run none_10m --loadouts none --port 5677 $COMMON > $OUT/none_10m.log 2>&1
log "start mix_10m"
python -m navigate.train --run mix_10m --loadouts tsunami,none --port 5678 $COMMON > $OUT/mix_10m.log 2>&1
log "loadout evaluations"
for run in none_10m mix_10m grid_ctrl_10m; do
    for lo in tsunami none; do
        [ "$run" = grid_ctrl_10m ] && [ "$lo" = tsunami ] && continue   # exists (eval_big.json)
        python -m navigate.evaluate --run $run --loadout $lo --per-band 128 --tag big_$lo --port 5779 > $OUT/${run}_eval_$lo.log 2>&1
    done
done
log "done loadout experiment"
