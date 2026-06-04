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
N="${1:-4}"
CHECK="${2:-60}"
STALL="${3:-300}"
mkdir -p "$LOGS" "$PIDDIR"
echo "[watchdog] N=$N check=${CHECK}s stall=${STALL}s started $(date)"
# Per-instance boot-grace deadline: skip the bridge-ping health check until an
# instance has had time to boot after a (re)start, so we don't kill a booting
# instance whose bridge isn't up yet.
declare -A GRACE
BOOT_GRACE=150
# Give every instance an initial grace window — on watchdog (re)start the fleet
# may still be booting; don't ping-kill instances whose bridge isn't up yet.
for _i in $(seq 0 $((N-1))); do GRACE[$_i]=$(( $(date +%s) + BOOT_GRACE )); done

# Returns 0 if the instance's JSON bridge answers a ping within ~1.5s.
ping_bridge() {
  local port="$1"
  python3 - "$port" <<'PY' 2>/dev/null
import socket, json, sys
p = int(sys.argv[1])
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", 0)); s.settimeout(1.5)
try:
    s.sendto(json.dumps({"cmd": "ping"}).encode(), ("127.0.0.1", p)); s.recvfrom(4096); sys.exit(0)
except Exception:
    sys.exit(1)
PY
}

restart_one() {
  local i="$1"; local why="$2"
  GRACE[$i]=$(( $(date +%s) + BOOT_GRACE ))   # don't health-check until booted
  echo "[watchdog $(date '+%H:%M:%S')] restarting instance $i ($why)"
  # kill just this instance's process tree by its recorded pid
  local pidf="$PIDDIR/oracle${i}.pid"
  [ -f "$pidf" ] && pkill -9 -P "$(cat "$pidf" 2>/dev/null)" 2>/dev/null
  # also clear stale wineserver for its prefix
  nohup bash "$LAUNCH" "$i" > "$LOGS/oracle${i}-combined.log" 2>&1 &
  echo $! > "$pidf"
}

while true; do
  # Log rotation: headless SF spams a benign per-frame NullReferenceException
  # (game-side GetComponentInChildren<Torso>() in batch mode) at ~20/s, so the
  # per-instance + unity logs grow ~5 MB/min total. Truncate any that exceed
  # ~150 MB so a long unattended run can't fill the disk. Truncating an
  # append-mode-held file is safe (next write continues at the new EOF).
  for lf in "$LOGS"/oracle*-plugin.log "$LOGS"/oracle*-unity.log "$LOGS"/oracle*-combined.log; do
    [ -f "$lf" ] || continue
    sz=$(stat -c %s "$lf" 2>/dev/null || echo 0)
    if [ "$sz" -gt 157286400 ]; then
      : > "$lf"
      echo "[watchdog $(date '+%H:%M:%S')] truncated $lf (was $((sz/1048576))MB)"
    fi
  done
  for i in $(seq 0 $((N-1))); do
    pidf="$PIDDIR/oracle${i}.pid"
    plog="$LOGS/oracle${i}-plugin.log"
    # 1) process-dead check
    if [ ! -f "$pidf" ] || ! kill -0 "$(cat "$pidf" 2>/dev/null)" 2>/dev/null; then
      restart_one "$i" "process dead"; sleep 8; continue
    fi
    # 2) HUNG check — the process can be alive but unresponsive (Unity/Wine
    # wedged), which blocks the SubprocVecEnv on that env and stalls training
    # ~Nx. The process-alive check above misses this; ping the JSON bridge
    # instead and restart if it doesn't answer twice in a row. (Bridge port
    # = 1341 + i.)
    bport=$((1341 + i))
    grace_until=${GRACE[$i]:-0}
    if [ "$(date +%s)" -ge "$grace_until" ]; then
      if ! ping_bridge "$bport"; then
        sleep 2
        if ! ping_bridge "$bport"; then
          restart_one "$i" "bridge $bport unresponsive (hung)"; sleep 8; continue
        fi
      fi
    fi
  done
  sleep "$CHECK"
done
