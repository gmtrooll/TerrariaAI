#!/usr/bin/env bash
# Oct 21 morning, second equipment experiment: as queue_gear.sh, but each loadout that jumps higher than Tsunami also
# trains on routes that need its height (--height: <world>_h<J>, navigate.height_sets). gear2_10m sees its measured
# abilities (gear=1), nogear2_10m does not. Evaluated on the held-out worlds' height sets per loadout (5 training,
# 3 held-out heights) and the main sets for a few loadouts; then repeat evaluations of v4 and v3. Run from Training.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
until [ "$(grep -c 'need more than' $OUT/height_sets_train.log 2>/dev/null)" -ge 18 ]; do sleep 30; done  # train_01-06 done
TRAIN="none,tsunami,cloud,sandstorm,fart,balloon,cloud_balloon,hermes,tsunami_hermes,balloon_hermes"
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent --detour 0.3 --height --loadouts $TRAIN"
OBS="grid=fovea,visibility=exposed,near=1,los=1"
log "gear2: start gear2_10m and nogear2_10m"
python -m navigate.train --run gear2_10m --seed 0 --port 5690 --obs $OBS,gear=1 $COMMON > $OUT/gear2_10m.log 2>&1 &
sleep 120
python -m navigate.train --run nogear2_10m --seed 0 --port 5720 --obs $OBS $COMMON > $OUT/nogear2_10m.log 2>&1 &
wait
log "gear2: training done"
evals() {  # run, port
    for pair in blizzard:14 fart:16 cloud_balloon:19 sandstorm:20 blizzard_balloon:25 sandstorm_balloon_hermes:36 bundle:59; do
        python -m navigate.evaluate --run $1 --loadout ${pair%%:*} --set _h${pair##*:} --tag h_${pair%%:*} --port $2 >> $OUT/${1}_evalheight.log 2>&1
    done
    for l in tsunami none sandstorm bundle frogleg; do
        python -m navigate.evaluate --run $1 --loadout $l --per-band 32 --tag gear_$l --port $2 >> $OUT/${1}_evalgear.log 2>&1
    done
}
evals gear2_10m 5784 &
sleep 60
evals nogear2_10m 5797 &
wait
for r in v4cand12_30m navigate_v3; do
    for k in 2 3; do
        python -m navigate.evaluate --run $r --per-band 128 --tag big_p2_r$k --port 5785 > $OUT/${r}_evalbig_p2_r$k.log 2>&1
        python -m navigate.evaluate --run $r --set _bend --tag bend_p2_r$k --port 5785 > $OUT/${r}_evalbend_p2_r$k.log 2>&1
    done
done
log "done gear2"
