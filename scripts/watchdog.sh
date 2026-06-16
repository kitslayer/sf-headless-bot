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
declare -A PINGFAIL   # consecutive bridge-ping failures per instance
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
  # Kill this instance's whole chain. pkill -P <recorded pid> only reaches
  # *direct children* of the launcher bash (often long exited), so a wedged
  # StickFight.exe survived as an orphan still attached to the instance's
  # wineprefix — and every relaunch into that prefix then conflicted and
  # wedged too (observed as a ~5-min restart loop on inst 2, 2026-06-10).
  # The -logFile arg embeds the instance number in every process of the
  # proton/wine chain, so match on that; [.] keeps this script's own
  # cmdline from matching the pattern.
  local pidf="$PIDDIR/oracle${i}.pid"
  [ -f "$pidf" ] && pkill -9 -P "$(cat "$pidf" 2>/dev/null)" 2>/dev/null
  pkill -9 -f "oracle${i}-unity[.]log" 2>/dev/null
  # Desync jitter (2026-06-10): instances restarted in the same batch come
  # up phase-LOCKED (identical map + same boot time → same round/scene
  # cycle) and then hit the same hang-prone transition SIMULTANEOUSLY —
  # observed as repeating 3-4-instance mass hangs every ~15-30 min, each
  # costing ~4 min of fleet capacity. A random 1-20s stagger decorrelates
  # their round clocks so hangs return to independent singles.
  sleep $((1 + RANDOM % 20))
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
    if [ "$sz" -gt 104857600 ]; then
      : > "$lf"
      echo "[watchdog $(date '+%H:%M:%S')] truncated $lf (was $((sz/1048576))MB)"
    fi
  done
  # Orphan-Xvfb sweep (2026-06-16): each instance restart can LEAK an Xvfb —
  # xvfb-run's SIGKILL teardown doesn't always reap its Xvfb child, which then
  # reparents to init (ppid==1) and lingers holding an X server + RAM. Over a
  # long run these pile up (6 stale servers / ~570MB observed after 6h, displays
  # :132..:174) and the resulting fd/RAM pressure makes live X servers time out
  # (XIO error 110) — which itself caused a double-instance hang. A live
  # instance's Xvfb ALWAYS has a live xvfb-run parent, so ppid==1 is a definitive
  # orphan; kill it and clean its stale lock. Display-agnostic, so it needs no
  # instance→display map.
  for xpid in $(pgrep -x Xvfb 2>/dev/null); do
    [ "$(ps -o ppid= -p "$xpid" 2>/dev/null | tr -d ' ')" = "1" ] || continue
    xdisp=$(ps -o args= -p "$xpid" 2>/dev/null | grep -oE ':[0-9]+' | head -1)
    kill -9 "$xpid" 2>/dev/null && {
      echo "[watchdog $(date '+%H:%M:%S')] reaped orphan Xvfb $xpid (disp ${xdisp:-?})"
      [ -n "$xdisp" ] && rm -f "/tmp/.X${xdisp#:}-lock" "/tmp/.X11-unix/X${xdisp#:}" 2>/dev/null
    }
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
      if ping_bridge "$bport" || { sleep 2; ping_bridge "$bport"; }; then
        PINGFAIL[$i]=0   # responsive — clear strike count
      else
        PINGFAIL[$i]=$(( ${PINGFAIL[$i]:-0} + 1 ))
        # Require unresponsiveness across TWO consecutive cycles (~CHECK secs
        # apart) before restarting. A round-advance scene-load can block the
        # bridge for a few seconds (one check), but a real hang persists across
        # checks — this avoids false-restarting healthy-but-busy instances.
        if [ "${PINGFAIL[$i]}" -ge 2 ]; then
          PINGFAIL[$i]=0
          restart_one "$i" "bridge $bport unresponsive 2 cycles (hung)"; sleep 8; continue
        else
          echo "[watchdog $(date '+%H:%M:%S')] inst $i bridge $bport no-reply (strike ${PINGFAIL[$i]}/2) — watching"
        fi
      fi
    fi
  done
  sleep "$CHECK"
done
