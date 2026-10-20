#!/usr/bin/env bash
# Oct 20 day (user away until 20:00): the ablation and seed runs for the thesis, then a v3 candidate.
#   losdetour_10m       both changes at 10M on 6 worlds (with grid_ctrl_10m, los_10m, detour_10m: a 2x2 ablation)
#   grid_ctrl_10m_s2    second training seed of the control
#   los_10m_s2          second training seed of the line-of-sight run
#   losdetourmix12_30m  navigate_v2's setup + mixed loadouts (half the episodes without the extra jump)
# Each is evaluated on the 1,024 set, the detour set and the hand-made tests (v3 also without the extra jump).
# Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent"
OBS="grid=fovea,visibility=exposed,near=1"

evals() {  # run [extra evaluate args...]
    python -m navigate.evaluate --run $1 --per-band 128 --tag big --port 5784 > $OUT/${1}_evalbig.log 2>&1
    python -m navigate.evaluate --run $1 --min-detour 1.7 --tag detours --port 5785 > $OUT/${1}_evaldetours.log 2>&1
    python -m navigate.handmade --run $1 --repeats 8 --port 5789 > $OUT/${1}_handmade.log 2>&1
}

log "day: start losdetour_10m"
python -m navigate.train --run losdetour_10m --obs $OBS,los=1 --detour 0.3 --port 5684 $COMMON > $OUT/losdetour_10m.log 2>&1 || log "losdetour_10m failed"
evals losdetour_10m
log "start grid_ctrl_10m_s2"
python -m navigate.train --run grid_ctrl_10m_s2 --obs $OBS --seed 2 --port 5685 $COMMON > $OUT/grid_ctrl_10m_s2.log 2>&1 || log "grid_ctrl_10m_s2 failed"
evals grid_ctrl_10m_s2
log "start los_10m_s2"
python -m navigate.train --run los_10m_s2 --obs $OBS,los=1 --seed 2 --port 5686 $COMMON > $OUT/los_10m_s2.log 2>&1 || log "los_10m_s2 failed"
evals los_10m_s2
RUN=losdetourmix12_30m
WORLDS="train_01 train_02 train_03 train_04 train_05 train_06 train_07 train_08 train_09 train_10 train_11 train_12"
log "start $RUN"
python -m navigate.train --run $RUN --worlds $WORLDS --bots 16 --seed 1 --steps 30000000 --bf16 --overlap --micro-batch 2048 \
    --target-kl 0.03 --recurrent --obs $OBS,los=1 --detour 0.3 --loadouts tsunami,none --port 5687 > $OUT/$RUN.log 2>&1 || log "$RUN failed"
evals $RUN
python -m navigate.evaluate --run $RUN --loadout none --per-band 128 --tag big_none --port 5784 > $OUT/${RUN}_evalbig_none.log 2>&1
python -m navigate.evaluate --run navigate_v2 --loadout none --per-band 128 --tag big_none --port 5784 > $OUT/navigate_v2_evalbig_none.log 2>&1
log "done day"
