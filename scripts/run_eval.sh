#!/bin/bash
# Deterministic eval of a checkpoint on a DEDICATED 5th instance (game 1345 /
# bridge 1349) — never touches the 1341-1344 training fleet, so it can run
# alongside live training (load headroom permitting). Boots instance 8 if its
# bridge isn't already up, runs eval_checkpoint.py under a HARD timeout (the
# 5th instance reliably wedges ~20-25 eps; the timeout guarantees we still
# reach teardown), then kills ONLY the 1349/1345-bound instance by port PID.
#
# eval_checkpoint.py prints win/fell/arms/hits/len every 5 eps, so a run that
# wedges before the requested count still yields a usable partial breakdown.
#
#   scripts/run_eval.sh [EPISODES] [CKPT] [OPP_MODE]
#     EPISODES  default 40
#     CKPT      default = latest models/ppo_headless_*_steps.zip
#     OPP_MODE  hold (default) | patrol | scripted — match how the ckpt trained
#
# NOTE on the pkill self-match trap (hit 2026-06-13): do NOT teardown with
# `pkill -f 'launch_oracle.sh 8'` from inside a script whose own cmdline holds
# that string — it kills itself. This script's cmdline is `bash run_eval.sh N`
# (no match), and teardown is port-PID based, so it's safe.
set -u
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"
VENV="$HOME/stickfight-bot/.venv/bin/python"
EPS="${1:-40}"
CKPT="${2:-}"
OPP="${3:-hold}"
cd "$BOTDIR"
LOG="logs/eval_$(date +%Y%m%d_%H%M%S).log"
ready=0

if ! ss -ulpnH 2>/dev/null | grep -q :1349; then
  echo "[eval] booting instance 8 (game 1345 / bridge 1349)..." | tee -a "$LOG"
  SFHEADLESS_DEBUG=0 nohup bash scripts/launch_oracle.sh 8 >> logs/eval_oracle8.log 2>&1 &
  disown
fi
for i in $(seq 1 45); do
  if "$VENV" - <<'PY' 2>/dev/null
import socket,json,sys
s=socket.socket(2,2); s.settimeout(2.0)
try: s.sendto(json.dumps({"cmd":"ping"}).encode(),("127.0.0.1",1349)); s.recvfrom(4096); sys.exit(0)
except Exception: sys.exit(1)
PY
  then ready=1; echo "[eval] bridge 1349 up after ~$((i*8))s" | tee -a "$LOG"; break; fi
  sleep 8
done
if [ "$ready" = 1 ]; then
  sleep 35   # reach combat + spawn rigs
  [ -z "$CKPT" ] && CKPT=$(ls -t models/ppo_headless_*_steps.zip | head -1)
  echo "[eval] DETERMINISTIC $EPS eps on $(basename "$CKPT") $(date '+%H:%M:%S')" | tee -a "$LOG"
  timeout 480 "$VENV" python/eval_checkpoint.py "$CKPT" --bridge 1349 --episodes "$EPS" --deterministic 2>&1 | tee -a "$LOG"
else
  echo "[eval] bridge 1349 never came up — aborting" | tee -a "$LOG"
fi
# Teardown: kill ONLY the eval instance, by the PID bound to its ports.
for port in 1349 1345; do
  pid=$(ss -ulpnH 2>/dev/null | grep ":$port" | grep -oE 'pid=[0-9]+' | head -1 | cut -d= -f2)
  [ -n "$pid" ] && { echo "[eval] killing eval-instance port $port pid $pid" | tee -a "$LOG"; kill -9 "$pid" 2>/dev/null; }
done
echo "[eval] done -> $LOG"
