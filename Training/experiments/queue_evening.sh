#!/usr/bin/env bash
# Oct 20 evening: second seed of losdetour_10m (both changes vs line of sight alone rests on one seed so far).
# Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
log "evening: start losdetour_10m_s2"
python -m navigate.train --run losdetour_10m_s2 --seed 2 --obs grid=fovea,visibility=exposed,near=1,los=1 --detour 0.3 --port 5689 \
    --steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent > $OUT/losdetour_10m_s2.log 2>&1 || log "losdetour_10m_s2 failed"
python -m navigate.evaluate --run losdetour_10m_s2 --per-band 128 --tag big --port 5784 > $OUT/losdetour_10m_s2_evalbig.log 2>&1
python -m navigate.evaluate --run losdetour_10m_s2 --set _bend --tag bend_r1 --port 5797 > $OUT/losdetour_10m_s2_evalbend.log 2>&1
python -m navigate.handmade --run losdetour_10m_s2 --repeats 8 --port 5789 > $OUT/losdetour_10m_s2_handmade.log 2>&1
log "done evening"
