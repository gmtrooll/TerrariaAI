# Experiment queues

The scripts that ran the experiments in `docs/Navigate Progress.md` and `docs/Overnight Log.md`, kept as the record of
exactly what was trained and evaluated (flags, seeds, ports, order). Each runs from the `Training` folder:
`bash experiments/queue_gear3.sh`. Most wait for an earlier queue to log "done ..." in `results/training/queue.log`
before starting.
