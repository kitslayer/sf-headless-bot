#!/bin/bash
# One babysit tick for the headless PPO run: sleep, then verify trainer /
# watchdog / supervisor liveness, 4 bridge pings, timestep progress (two-strike
# FROZEN2 via /tmp/sf_frozen_once), fps/RAM/disk, rollout win/fell/arms/hits,
# and stage-specific guards. Designed to be the body of a Monitor task:
#   Monitor: cd <bot> && bash scripts/sf_watch.sh
# Env knobs:
#   SF_WATCH_SLEEP   seconds to sleep before sampling (default 260)
#   SF_WATCH_STAGE   label for the line (default "PATROL")
#   SF_EVAL_TS       ts threshold that flips eval_due=YES (default 1035000)
set -u
cd "$HOME/stickfight-bot/sf-headless-bot" || exit 1
SLEEP="${SF_WATCH_SLEEP:-260}"
STAGE="${SF_WATCH_STAGE:-PATROL}"
EVAL_TS="${SF_EVAL_TS:-1035000}"
N="${SF_N:-4}"; export SF_N="$N"   # fleet instance count (bridges 1341..1340+N)
sleep "$SLEEP"

TS=$(grep -E '^\|    total_timesteps' logs/train.log | tail -1 | grep -oE '[0-9]+')
LAST=$(cat /tmp/sf_last_ts 2>/dev/null || echo 0)
det=$("$HOME/stickfight-bot/.venv/bin/python" -c "
import socket,json,os
r=[]
for p in range(1341,1341+int(os.environ.get('SF_N','4'))):
    s=socket.socket(2,2); s.settimeout(1.5)
    try:
        s.sendto(json.dumps({'cmd':'ping'}).encode(),('127.0.0.1',p)); s.recvfrom(999); r.append(str(p)+':ok')
    except: r.append(str(p)+':DOWN')
    s.close()
print(' '.join(r))" 2>/dev/null)
up=$(echo "$det" | grep -o ':ok' | wc -l)
gv(){ grep "$1" logs/train.log | tail -1 | grep -oE '[0-9.]+ *\|$' | grep -oE '^[0-9.]+'; }
W=$(gv win_mean); F=$(gv fell_mean); A=$(gv arms_mean); H=$(gv hits_mean)
FROZEN2=no
if [ -n "$TS" ] && [ "$TS" = "$LAST" ]; then
  [ -f /tmp/sf_frozen_once ] && FROZEN2=YES || touch /tmp/sf_frozen_once
else
  rm -f /tmp/sf_frozen_once
fi
if [ "$STAGE" = "SELFPLAY" ]; then
  SD=$(awk -v w="${W:-0}" 'BEGIN{print (w>0.75)?"learner>>frozen-REFRESH?":"ok"}')   # selfplay: high win = beating frozen opp, not a wedge
else
  SD=$(awk -v w="${W:-0}" 'BEGIN{print (w>0.55)?"SUSPECT-selfdestruct":"ok"}')
fi
EVD=$(awk -v t="${TS:-0}" -v e="$EVAL_TS" 'BEGIN{print (t>=e)?"YES":"no"}')
tr=$(pgrep -fc '[t]rain_headless_ppo.py'); sup=$(pgrep -fc '[t]rain_supervisor.sh')
echo "===== WATCH $(date '+%H:%M:%S') ($STAGE) ====="
echo "tr=$tr wd=$(ps -eo args|grep -c '^bash scripts/[w]atchdog') sup=$sup | $det"
echo "ts=$TS (prev $LAST) eval_due=$EVD fps=$(grep fps logs/train.log|tail -1|grep -oE '[0-9]+'|head -1) | load=$(cut -d' ' -f1-3 /proc/loadavg) RAM=$(free -m|awk '/Mem:/{print $7}')MB disk=$(df -h /home|tail -1|awk '{print $5}')"
echo "$STAGE: win=$W fell=$F arms=$A hits=$H wd_restarts=$(grep -c 'restarting instance' logs/watchdog.log) selfdestruct=$SD"
echo "ALERTS: trDead=$([ "${tr:-0}" -eq 0 ]&&echo YES||echo no) FROZEN2=$FROZEN2 bridges=$([ ${up:-0} -lt $N ]&&echo DEGRADED||echo ok) supDead=$([ "${sup:-0}" -eq 0 ]&&echo YES||echo no) selfdestruct=$SD eval_due=$EVD"
[ -n "$TS" ] && echo "$TS" > /tmp/sf_last_ts
