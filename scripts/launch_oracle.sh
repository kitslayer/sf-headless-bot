#!/bin/bash
# Launch one headless Stick Fight oracle running the bot-extended
# SFHeadlessHost plugin, with N in-process scripted bots fighting each other.
#
# Usage: launch_oracle.sh [INSTANCE]
#   INSTANCE 0 (default) → UDP 1337, bridge 1341, shared wineprefix
#   INSTANCE 1..N        → UDP 1337+I, bridge 1341+I, isolated wineprefix in /tmp
#
# Env overrides: SFGYM_BOT_SLOTS (default "0,1"), SFHEADLESS_DEBUG (default 1).
set -u
I="${1:-0}"
STEAM="$HOME/.steam/steam"
GAME="$STEAM/steamapps/common/StickFightTheGame"
PROTON="$STEAM/steamapps/common/Proton - Experimental/proton"
BOTDIR="$HOME/stickfight-bot/sf-headless-bot"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
if [ "$I" -eq 0 ]; then
  export STEAM_COMPAT_DATA_PATH="$STEAM/steamapps/compatdata/674940"
else
  export STEAM_COMPAT_DATA_PATH="/tmp/sf-oracle-pfx-$I"
  mkdir -p "$STEAM_COMPAT_DATA_PATH"
fi
export WINEDLLOVERRIDES="winhttp=n,b"
export PROTON_USE_XALIA=0          # Xalia crashes without a real display
export WINEDEBUG=-all
export SFGYM_BOT_SLOTS="${SFGYM_BOT_SLOTS:-0,1}"
export SFGYM_BOT_AUTOSPAWN=false
export SFHEADLESS_PORT=$((1337 + I))
export SFHEADLESS_BRIDGEPORT=$((1341 + I))
export SFHEADLESS_DEBUG="${SFHEADLESS_DEBUG:-1}"

ULOG="$BOTDIR/logs/oracle${I}-unity.log"
echo "[launch_oracle] instance=$I port=$SFHEADLESS_PORT bots=$SFGYM_BOT_SLOTS prefix=$STEAM_COMPAT_DATA_PATH"
exec xvfb-run -a \
  "$PROTON" run "$GAME/StickFight.exe" \
  -batchmode -nographics -logFile "$ULOG"
