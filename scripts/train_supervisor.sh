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

# Stall detection (2026-06-06): a Proton instance occasionally native-crashes,
# which can kill a SubprocVecEnv worker — the vec-env then blocks FOREVER on the
# dead worker's pipe (trainer process alive, timesteps frozen, 0 progress). The
# process-alive check below misses that. So also restart the trainer if it makes
# < MIN_PROG steps in WINDOW seconds (resumes from checkpoint + reconnects to
# live workers). WINDOW is long enough that a mere SLOWDOWN — one bad instance
# limping the fleet ~6x while the watchdog recovers it — still clears MIN_PROG
# and does NOT trigger a restart (no thrashing); only a true freeze does.
# 2026-06-10: 900/1000 → 600/200. Progress is ROLLOUT-QUANTIZED: train.log's
# total_timesteps only updates every n_steps*4=2048 steps (~256s at normal
# speed, 400s+ while the watchdog reboots an instance), so any window below
# the worst-case rollout duration false-kills a healthy trainer mid-rollout
# (a 300s window did exactly that at 14:58). 600s covers a half-speed
# rollout; a true freeze still recovers in ~10 min unattended.
WINDOW=600; MIN_PROG=200
anchor_ts=-1; anchor_time=$(date +%s)

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
      # Kickstart (decaying BC anchor) is DISABLED by default as of 2026-06-13:
      # a deterministic eval at 920k showed the BC clone marches off the void
      # edge (fell=0.83) because the fixed-spawn teacher demos were ~85% one
      # move-direction — argmax replays that off the cliff, and the anchor
      # PINNED the policy there (win regressed 0.08->0.05, fell 0.13->0.23 vs
      # pre-BC PPO). Reverted to the pre-BC 800k checkpoint, plain PPO + HP-25.
      # The kickstart machinery is intact: drop a `run/USE_KICKSTART` file and
      # set the anchors to the new resume point to re-enable (only sensible
      # once demos come from a diverse-spawn or flat map — see TRAINING_LOG).
      KS_ARGS=""
      if [ -f "$BOTDIR/run/USE_KICKSTART" ] && [ -f "$BOTDIR/demos/teacher_demos.npz" ]; then
        KS_ARGS="--kickstart-demos $BOTDIR/demos/teacher_demos.npz --ks-coef 0.5 --ks-anchor 858000 --ks-decay 400000 --ks-warmup-until 858000"
      fi
      # Self-play (stage 2, 2026-06-13): the opponent slot is driven by a FROZEN
      # PPO snapshot inside the env (SFGYM_RL_SLOTS=0,1 so the env owns both
      # slots). The frozen-opp checkpoint is PINNED in run/SELFPLAY_CKPT (league
      # refresh = overwrite that file with a newer snapshot + restart); if the
      # file is absent the env auto-loads the latest models/ checkpoint at launch.
      [ -f "$BOTDIR/run/SELFPLAY_CKPT" ] && export SF_SELFPLAY_CKPT="$(cat "$BOTDIR/run/SELFPLAY_CKPT")"
      nohup python train_headless_ppo.py --instances "$INSTANCES" --base-bridge 1341 \
        --steps "$STEPS" --save-every 8000 --opp-mode patrol $KS_ARGS >> "$BOTDIR/logs/train.log" 2>&1 ) &
    anchor_ts=-1; anchor_time=$(date +%s)   # reset stall tracking for the fresh trainer
    sleep 10
  else
    # Trainer alive — verify it's actually progressing (not wedged on a dead worker).
    cur_ts=$(grep total_timesteps logs/train.log 2>/dev/null | tail -1 | grep -oE '[0-9]+')
    now=$(date +%s)
    if [ -n "$cur_ts" ]; then
      # cur_ts < anchor_ts means the timestep counter went BACKWARD: a fresh
      # run (or resume from an older checkpoint) in the appended train.log.
      # Re-anchor instead of letting the negative delta read as "no progress"
      # — that false-killed a healthy fresh trainer 900s into v7 (2026-06-10:
      # anchor was the OLD run's 257k; new run counted 0→10k, never ≥ anchor).
      if [ "$anchor_ts" -lt 0 ] || [ "$cur_ts" -lt "$anchor_ts" ] || [ "$((cur_ts - anchor_ts))" -ge "$MIN_PROG" ]; then
        anchor_ts="$cur_ts"; anchor_time="$now"             # progressing → healthy
      elif [ "$((now - anchor_time))" -ge "$WINDOW" ]; then
        echo "[train-sup $(date '+%H:%M:%S')] trainer WEDGED: <${MIN_PROG} steps in ${WINDOW}s (stuck ~${cur_ts}) — killing to force resume-from-checkpoint"
        pkill -9 -f "train_headless_ppo.py" 2>/dev/null
        anchor_ts=-1; anchor_time="$now"
        sleep 5
      fi
    fi
  fi
  sleep "$CHECK"
done
