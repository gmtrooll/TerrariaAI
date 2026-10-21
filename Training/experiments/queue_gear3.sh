#!/usr/bin/env bash
# Oct 21 morning, third equipment experiment: gear2_10m's setup (measured abilities, height sets) with 4 more training
# loadouts that wear several extra jumps at once (2-3 extra jumps, apex 21-58 tiles), so the held-out Bundle of
# Balloons (3 extra jumps, 60 tiles) falls inside the trained range. Blizzard stays held out. Compared with gear2_10m.
# Waits for queue_gear2 and the new reachability / height sets. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
until grep -q "done gear2" $OUT/queue.log && [ "$(grep -c 'need more than' $OUT/height_sets_train2.log 2>/dev/null)" -ge 24 ]; do sleep 30; done
TRAIN="none,tsunami,cloud,sandstorm,fart,balloon,cloud_balloon,hermes,tsunami_hermes,balloon_hermes,cloud_fart,sandstorm_cloud,tsunami_fart_cloud,balloon_tsunami_sandstorm_cloud"
log "gear3: start gear3_10m"
python -m navigate.train --run gear3_10m --seed 0 --port 5690 --obs grid=fovea,visibility=exposed,near=1,los=1,gear=1 \
    --steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent --detour 0.3 --height --loadouts $TRAIN > $OUT/gear3_10m.log 2>&1 || log "gear3_10m failed"
for pair in blizzard:14 fart:16 cloud_balloon:19 sandstorm:20 blizzard_balloon:25 sandstorm_balloon_hermes:36 bundle:59; do
    python -m navigate.evaluate --run gear3_10m --loadout ${pair%%:*} --set _h${pair##*:} --tag h_${pair%%:*} --port 5784 >> $OUT/gear3_10m_evalheight.log 2>&1
done
for l in tsunami none sandstorm bundle frogleg; do
    python -m navigate.evaluate --run gear3_10m --loadout $l --per-band 32 --tag gear_$l --port 5784 >> $OUT/gear3_10m_evalgear.log 2>&1
done
log "done gear3"
