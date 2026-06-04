#!/bin/bash
# Watchdog: restart any fleet instance whose process died, and detect "stuck"
# instances (no new Round advance in STALL_SECS). Runs as a loop; intended to
# be launched in the background once the fleet is up.
#
#   watchdog.sh [N] [CHECK_SECS] [STALL_SECS]
#     N           number of instances to keep alive (default 6)
#     CHECK_SECS  seconds between checks (default 60)
#     STALL_SECS  if an instance logs no Round advance in this window, restart it
#                 (default 300; 0 = disable stall detection)
set -u
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"
LAUNCH="$BOTDIR/scripts/launch_oracle.sh"
LOGS="$BOTDIR/logs"
PIDDIR="$BOTDIR/run"
N="${1:-6}"
CHECK="${2:-60}"
STALL="${3:-300}"
mkdir -p "$LOGS" "$PIDDIR"
echo "[watchdog] N=$N check=${CHECK}s stall=${STALL}s started $(date)"

restart_one() {
  local i="$1"; local why="$2"
  echo "[watchdog $(date '+%H:%M:%S')] restarting instance $i ($why)"
  # kill just this instance's process tree by its recorded pid
  local pidf="$PIDDIR/oracle${i}.pid"
  [ -f "$pidf" ] && pkill -9 -P "$(cat "$pidf" 2>/dev/null)" 2>/dev/null
  # also clear stale wineserver for its prefix
  nohup bash "$LAUNCH" "$i" > "$LOGS/oracle${i}-combined.log" 2>&1 &
  echo $! > "$pidf"
}

while true; do
  for i in $(seq 0 $((N-1))); do
    pidf="$PIDDIR/oracle${i}.pid"
    plog="$LOGS/oracle${i}-plugin.log"
    # 1) process-dead check
    if [ ! -f "$pidf" ] || ! kill -0 "$(cat "$pidf" 2>/dev/null)" 2>/dev/null; then
      restart_one "$i" "process dead"; sleep 8; continue
    fi
    # 2) stall check (no round advance in STALL window)
    if [ "$STALL" -gt 0 ] && [ -f "$plog" ]; then
      last_adv=$(stat -c %Y "$plog" 2>/dev/null || echo 0)
      # use file mtime as a cheap "is it still writing" proxy
      now=$(date +%s)
      if [ $((now - last_adv)) -gt "$STALL" ]; then
        restart_one "$i" "log idle >${STALL}s"; sleep 8
      fi
    fi
  done
  sleep "$CHECK"
done
