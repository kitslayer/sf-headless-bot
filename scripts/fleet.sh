#!/bin/bash
# Fleet orchestrator for the headless Stick Fight bot arena.
#
#   fleet.sh start [N]   launch N instances (default 6), ports 1337..1337+N-1
#   fleet.sh stop        kill all instances
#   fleet.sh status      show running instances + per-instance health + host load
#   fleet.sh restart [N] stop then start N
#
# Each instance runs 2 scripted bots fighting each other. A separate watchdog
# (watchdog.sh) restarts any that crash.
set -u
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"
LAUNCH="$BOTDIR/scripts/launch_oracle.sh"
LOGS="$BOTDIR/logs"
PIDDIR="$BOTDIR/run"
mkdir -p "$LOGS" "$PIDDIR"

start_one() {
  local i="$1"
  local pidf="$PIDDIR/oracle${i}.pid"
  if [ -f "$pidf" ] && kill -0 "$(cat "$pidf" 2>/dev/null)" 2>/dev/null; then
    echo "  instance $i already running (pid $(cat "$pidf"))"; return
  fi
  nohup bash "$LAUNCH" "$i" > "$LOGS/oracle${i}-combined.log" 2>&1 &
  echo $! > "$pidf"
  echo "  started instance $i (pid $!, port $((1337+i)))"
}

cmd="${1:-status}"
# Default 4: on this 24-core host, 6 instances thrash (load ~29) and starve the
# co-resident docker media stacks. 4 runs at load ~13 with headroom. Override
# with an explicit arg if the host is dedicated.
N="${2:-4}"

case "$cmd" in
  start)
    echo "[fleet] starting $N instance(s)..."
    # Persist this start's config so watchdog/supervisor relaunches match it
    # (otherwise a restarted instance defaults to random maps + scripted —
    # diverges from the run and hits hang-prone scenes). launch_oracle.sh
    # sources this file on every (re)launch.
    #
    # RESTART-SAFETY (2026-06-06): the supervisor re-runs `fleet.sh start` but
    # only re-exports SFGYM_RL_SLOTS — NOT SF_FIXED_MAP — so a naive rewrite
    # clobbered SF_FIXED_MAP to empty → random maps → hang/flap (and that very
    # path was live the night the box froze). So fall back to the PREVIOUSLY
    # persisted fleet.env for any var the caller didn't explicitly export.
    # Precedence: caller's non-empty env  >  prior fleet.env  >  built-in default.
    _c_bots="${SFGYM_BOT_SLOTS:-}"; _c_rl="${SFGYM_RL_SLOTS:-}"
    _c_map="${SF_FIXED_MAP:-}";     _c_stall="${SF_BOT_STALL_SECS:-}"
    _c_excl="${SF_EXCLUDE_MAPS:-}"
    _c_ts="${SF_TIMESCALE:-}";      _c_grace="${SF_PRE_COMBAT_DELAY:-}"
    _c_nmd="${SF_NEXT_MATCH_DELAY:-}"
    _c_vy="${SF_VOID_Y:-}";         _c_vz="${SF_VOID_Z:-}"
    _c_hp="${SF_STAGE_HP:-}"
    [ -f "$PIDDIR/fleet.env" ] && . "$PIDDIR/fleet.env"
    [ -n "$_c_bots" ]  && SFGYM_BOT_SLOTS="$_c_bots"
    [ -n "$_c_rl" ]    && SFGYM_RL_SLOTS="$_c_rl"
    [ -n "$_c_map" ]   && SF_FIXED_MAP="$_c_map"
    [ -n "$_c_stall" ] && SF_BOT_STALL_SECS="$_c_stall"
    [ -n "$_c_excl" ]  && SF_EXCLUDE_MAPS="$_c_excl"
    [ -n "$_c_ts" ]    && SF_TIMESCALE="$_c_ts"
    [ -n "$_c_grace" ] && SF_PRE_COMBAT_DELAY="$_c_grace"
    [ -n "$_c_nmd" ]   && SF_NEXT_MATCH_DELAY="$_c_nmd"
    [ -n "$_c_vy" ]    && SF_VOID_Y="$_c_vy"
    [ -n "$_c_vz" ]    && SF_VOID_Z="$_c_vz"
    [ -n "$_c_hp" ]    && SF_STAGE_HP="$_c_hp"
    cat > "$PIDDIR/fleet.env" <<EOF
export SFGYM_BOT_SLOTS=${SFGYM_BOT_SLOTS:-0,1}
export SFGYM_RL_SLOTS=${SFGYM_RL_SLOTS:-}
export SF_FIXED_MAP=${SF_FIXED_MAP:-}
export SF_BOT_STALL_SECS=${SF_BOT_STALL_SECS:-}
export SF_EXCLUDE_MAPS=${SF_EXCLUDE_MAPS:-103}
export SF_TIMESCALE=${SF_TIMESCALE:-}
export SF_PRE_COMBAT_DELAY=${SF_PRE_COMBAT_DELAY:-}
export SF_NEXT_MATCH_DELAY=${SF_NEXT_MATCH_DELAY:-}
export SF_VOID_Y=${SF_VOID_Y:-}
export SF_VOID_Z=${SF_VOID_Z:-}
export SF_STAGE_HP=${SF_STAGE_HP:-}
EOF
    echo "[fleet] wrote $PIDDIR/fleet.env (SF_FIXED_MAP=${SF_FIXED_MAP:-<none>} RL_SLOTS=${SFGYM_RL_SLOTS:-<none>} TIMESCALE=${SF_TIMESCALE:-1}, restart-safe)"
    for i in $(seq 0 $((N-1))); do
      start_one "$i"
      sleep 8   # stagger so prefix clone + Proton boot don't thrash disk/CPU
    done
    echo "[fleet] start issued for $N instance(s). Use 'fleet.sh status'."
    ;;
  stop)
    echo "[fleet] stopping watchdog, orchestrators, and all instances..."
    # Kill the watchdog + any backgrounded fleet starts FIRST, else they
    # respawn instances faster than we can kill them.
    pkill -9 -f "watchdog.sh" 2>/dev/null
    pkill -9 -f "scripts/fleet.sh start" 2>/dev/null
    sleep 1
    # Game processes get orphaned (reparented to init) when their proton/xvfb
    # wrapper dies, so pattern-kill a few rounds then sweep by comm.
    for r in 1 2 3; do
      pkill -9 -f "StickFight" 2>/dev/null
      pkill -9 -f "Proton - Experimental" 2>/dev/null
      pkill -9 -f "wineserver" 2>/dev/null
      pkill -9 -f "xvfb-run" 2>/dev/null
      sleep 2
      [ "$(ps -eo comm | grep -c StickFight)" -eq 0 ] && break
    done
    # Final sweep: kill orphans by explicit PID (pattern-kill misses PPID=1).
    for pid in $(ps -eo pid,comm | grep -iE "StickFight|wineserver|wine64|winedevice|services.exe" | awk '{print $1}'); do
      kill -9 "$pid" 2>/dev/null
    done
    rm -f "$PIDDIR"/oracle*.pid
    echo "[fleet] stopped (remaining StickFight: $(ps -eo comm|grep -c StickFight))."
    ;;
  restart)
    "$0" stop; sleep 5; "$0" start "$N"
    ;;
  status)
    echo "==== fleet status $(date '+%H:%M:%S') ===="
    running=$(pgrep -f "StickFight.exe" | wc -l)
    echo "StickFight.exe processes: $running"
    free -m | awk '/Mem:/{printf "RAM: used=%dMB avail=%dMB\n",$3,$7}'
    echo "load:$(uptime | sed 's/.*load average://')"
    for pf in "$PIDDIR"/oracle*.pid; do
      [ -f "$pf" ] || continue
      i=$(basename "$pf" .pid | sed 's/oracle//')
      plog="$LOGS/oracle${i}-plugin.log"
      adv=$(grep -c "Round advance #" "$plog" 2>/dev/null || echo 0)
      deaths=$(grep -c "Steamworks-safe death" "$plog" 2>/dev/null || echo 0)
      live=$(pgrep -f "SFHEADLESS_PORT.*$((1337+i))" >/dev/null 2>&1 && echo up || echo "?")
      last=$(grep -E "bot-drive|Round advance" "$plog" 2>/dev/null | tail -1 | sed 's/\[Info[^]]*\]//' | cut -c1-70)
      printf "  inst %s (port %s): rounds=%s deaths=%s | %s\n" "$i" "$((1337+i))" "$adv" "$deaths" "$last"
    done
    ;;
  *)
    echo "usage: fleet.sh {start [N]|stop|status|restart [N]}"; exit 1;;
esac
