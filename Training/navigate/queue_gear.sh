#!/usr/bin/env bash
# Oct 21 morning: first equipment experiment. 10 training loadouts (Training/loadouts.json, navigate.gear_measure:
# none, Tsunami, Cloud, Sandstorm, Fart, Shiny Red Balloon, Cloud in a Balloon, Hermes Boots and two combinations),
# 5 held out (Blizzard in a Bottle, Frog Leg, Bundle of Balloons, Sandstorm in a Balloon + Hermes, Blizzard in a Balloon).
# gear_10m sees the measured abilities of its gear (-obs gear=1); nogear_10m only the game's raw stats. Both 10M steps,
# 6 worlds, trained side by side; then each is evaluated on every loadout (and navigate_v3 on the held-out ones).
# Waits for queue_night5. Run from the Training folder.
set -u
cd "$(dirname "$0")/.."
OUT=../results/training
log() { echo "$(date +%H:%M) $*" >> $OUT/queue.log; }
until grep -q "done night5" $OUT/queue.log; do sleep 60; done
python -c "import sys; sys.path.insert(0, '../tools'); from terraria_mcp.game import GameManager; r = GameManager().build(); print(r); sys.exit(0 if r['ok'] else 1)" > $OUT/gear_build.log 2>&1 || { log "gear: build failed"; exit 1; }
TRAIN="none,tsunami,cloud,sandstorm,fart,balloon,cloud_balloon,hermes,tsunami_hermes,balloon_hermes"
TEST="blizzard frogleg bundle sandstorm_balloon_hermes blizzard_balloon"
COMMON="--steps 10000000 --bf16 --overlap --micro-batch 2048 --target-kl 0.03 --recurrent --detour 0.3 --loadouts $TRAIN"
OBS="grid=fovea,visibility=exposed,near=1,los=1"
log "gear: start gear_10m and nogear_10m"
python -m navigate.train --run gear_10m --seed 0 --port 5690 --obs $OBS,gear=1 $COMMON > $OUT/gear_10m.log 2>&1 &
sleep 120
python -m navigate.train --run nogear_10m --seed 0 --port 5720 --obs $OBS $COMMON > $OUT/nogear_10m.log 2>&1 &
wait
log "gear: training done"
evals() {  # run, port
    for l in ${TRAIN//,/ } $TEST; do
        python -m navigate.evaluate --run $1 --loadout $l --per-band 32 --tag gear_$l --port $2 >> $OUT/${1}_evalgear.log 2>&1
    done
}
evals gear_10m 5784 &
sleep 60
evals nogear_10m 5797 &
wait
for l in $TEST tsunami none; do
    python -m navigate.evaluate --run navigate_v3 --loadout $l --per-band 32 --tag gear_$l --port 5785 >> $OUT/navigate_v3_evalgear.log 2>&1
done
log "done gear"
