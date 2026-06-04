#!/bin/bash
# Keep PPO training alive for long unattended runs. If the trainer process
# exits (crash/OOM/etc.), relaunch it — it auto-resumes from the latest
# checkpoint in models/. Also ensures the RL fleet is up first.
#
#   train_supervisor.sh [INSTANCES] [STEPS]
set -u
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"
VENV="$HOME/stickfight-bot/.venv"
INSTANCES="${1:-4}"
STEPS="${2:-50000000}"
CHECK=60

cd "$BOTDIR"
echo "[train-sup] start $(date) instances=$INSTANCES steps=$STEPS"

while true; do
  # Ensure the RL fleet is up (count live instances by listening bridge ports).
  live=$(ss -ulpn 2>/dev/null | grep -oE ":134[1-9]" | sort -u | wc -l)
  if [ "$live" -lt "$INSTANCES" ]; then
    echo "[train-sup $(date '+%H:%M:%S')] only $live/$INSTANCES bridges up — (re)starting RL fleet"
    SFGYM_RL_SLOTS=0,1 bash scripts/fleet.sh start "$INSTANCES" >> logs/fleet-start.log 2>&1
    sleep 60   # let instances boot + reach combat
  fi
  # Ensure the trainer is running.
  if ! pgrep -f "train_headless_ppo.py" >/dev/null 2>&1; then
    echo "[train-sup $(date '+%H:%M:%S')] trainer not running — launching (resumes from checkpoint)"
    ( source "$VENV/bin/activate"
      cd "$BOTDIR/python"
      nohup python train_headless_ppo.py --instances "$INSTANCES" --base-bridge 1341 \
        --steps "$STEPS" --save-every 20000 >> "$BOTDIR/logs/train.log" 2>&1 ) &
    sleep 10
  fi
  sleep "$CHECK"
done
