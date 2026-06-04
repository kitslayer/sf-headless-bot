#!/bin/bash
# Launch ONE headless Stick Fight oracle running the bot-extended
# SFHeadlessHost plugin, with N in-process scripted bots fighting each other.
#
# Usage: launch_oracle.sh [INSTANCE]
#   INSTANCE 0 (default) → UDP 1337, bridge 1341, the main Steam wineprefix
#   INSTANCE i>0         → UDP 1337+i, bridge 1341+i, a persistent per-instance
#                          wineprefix under prefixes/ (cloned from main on first use)
#
# Env overrides: SFGYM_BOT_SLOTS (default "0,1"), SFHEADLESS_DEBUG (default 1),
#                SF_EXCLUDE_MAPS (passed through).
set -u
I="${1:-0}"
STEAM="$HOME/.steam/steam"
GAME="$STEAM/steamapps/common/StickFightTheGame"
PROTON="$STEAM/steamapps/common/Proton - Experimental/proton"
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"
MAIN_PFX="$STEAM/steamapps/compatdata/674940"
# Per-instance wineprefixes live OUTSIDE the project dir — their Wine
# mscorlib.dll otherwise pollutes the .NET build's reference set.
PFX_BASE="$HOME/stickfight-bot/sf-bot-prefixes"

mkdir -p "$BOTDIR/logs" "$PFX_BASE"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
if [ "$I" -eq 0 ]; then
  export STEAM_COMPAT_DATA_PATH="$MAIN_PFX"
else
  PFX="$PFX_BASE/$I"
  # Clone the (already-initialized + activated) main prefix on first use so
  # we don't pay Proton's slow first-run setup per instance.
  if [ ! -d "$PFX/pfx" ]; then
    echo "[launch_oracle] cloning main wineprefix → $PFX (first run for instance $I)"
    mkdir -p "$PFX"
    cp -a "$MAIN_PFX/." "$PFX/" 2>/dev/null
  fi
  export STEAM_COMPAT_DATA_PATH="$PFX"
fi
export WINEDLLOVERRIDES="winhttp=n,b"
export PROTON_USE_XALIA=0          # Xalia crashes without a real display
export WINEDEBUG=-all
export SFGYM_BOT_SLOTS="${SFGYM_BOT_SLOTS:-0,1}"
# Slots driven by an external RL policy (setBotAction) instead of the scripted
# driver. Empty = fully scripted self-play. Forwarded from the caller's env.
export SFGYM_RL_SLOTS="${SFGYM_RL_SLOTS:-}"
export SFGYM_BOT_AUTOSPAWN=false
export SFHEADLESS_PORT=$((1337 + I))
export SFHEADLESS_BRIDGEPORT=$((1341 + I))
export SFHEADLESS_DEBUG="${SFHEADLESS_DEBUG:-1}"
# Per-instance plugin log so instances sharing the game dir don't trample
# BepInEx/LogOutput.log (last-writer-wins).
export SFHEADLESS_LOGFILE="$BOTDIR/logs/oracle${I}-plugin.log"
# 103 = LevelEditor (no real gameplay/spawn setup — bots can't spawn there).
# Other non-combat scenes are routed around by AutoSpawnBots' self-heal
# (advance-round-on-spawn-fail), so we only hard-exclude the known-worst.
export SF_EXCLUDE_MAPS="${SF_EXCLUDE_MAPS:-103}"

ULOG="$BOTDIR/logs/oracle${I}-unity.log"
echo "[launch_oracle] instance=$I port=$SFHEADLESS_PORT bridge=$SFHEADLESS_BRIDGEPORT bots=$SFGYM_BOT_SLOTS pfx=$STEAM_COMPAT_DATA_PATH log=$SFHEADLESS_LOGFILE"
exec xvfb-run -a \
  "$PROTON" run "$GAME/StickFight.exe" \
  -batchmode -nographics -logFile "$ULOG"
