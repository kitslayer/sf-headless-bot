# SF Headless Bot Host

A headless, server-side **bot arena** for *Stick Fight: The Game*. It runs the
real game binary in batch mode (no rendering) and spawns N in-process scripted
bots that fight each other using the **stock game physics and combat code** —
no external client, no rendering, no human input. Built for generating
self-play / training data for a reinforcement-learning agent.

It's a BepInEx plugin that extends a Stick Fight dedicated-server plugin with a
bot layer. The defining design rule: **every spawn / move / revive / death step
mirrors stock Stick Fight 1:1**, so behavior matches the real game and trained
policies transfer.

## What it does

- Boots `StickFight.exe -batchmode -nographics` under a virtual display (xvfb).
- Spawns bots in player slots at the loaded map's real spawn points.
- Drives each bot every physics tick (walk toward nearest opponent + melee).
- Runs the stock match lifecycle: fight → death → round advance → new map →
  revive → fight again, indefinitely.
- Scales horizontally: launch multiple instances on different UDP ports, each
  in its own Wine prefix.

## Build

Requires the .NET SDK and a set of reference assemblies in `refs/`
(`UnityEngine.dll`, `Assembly-CSharp.dll`, `BepInEx.dll`, `0Harmony.dll`).
These are **not** committed — they're copyrighted game/engine binaries. Copy
them from your own Stick Fight install + BepInEx:

```
refs/UnityEngine.dll        <game>/StickFight_Data/Managed/UnityEngine.dll
refs/Assembly-CSharp.dll    <game>/StickFight_Data/Managed/Assembly-CSharp.dll
refs/BepInEx.dll            <BepInEx>/core/BepInEx.dll
refs/0Harmony.dll           <BepInEx>/core/0Harmony.dll
```

Then:

```bash
dotnet build SFHeadlessHost.csproj -c Release
cp bin/Release/SFHeadlessHost.dll <game>/BepInEx/plugins/
```

## Run

```bash
SFGYM_BOT_SLOTS=0,1 bash scripts/launch_oracle.sh 0     # instance 0, UDP 1337
SFGYM_BOT_SLOTS=0,1 bash scripts/launch_oracle.sh 1     # instance 1, UDP 1338
```

Each instance is independent — run as many as the box has CPU for.

### Environment

| Var | Default | Meaning |
|---|---|---|
| `SFGYM_BOT_SLOTS` | `0,1` | Comma-separated player slots (0–3) to fill with bots |
| `SFHEADLESS_PORT` | `1337` | UDP port (set per instance) |
| `SFHEADLESS_DEBUG` | `1` | Verbose logging |

## How the 1:1 pipeline works

```
AutoSpawnBots
  └ TrySpawnPlayer at currentMapInfo.spawnPoints[slot].localPosition
  └ ConfigureBotRig         (mHasControl, SetCollision(true), Standing/Fighting)
  └ RegisterRigWithControllerHandler   (adds Controller to the player list)
  └ ReviveSpawnedBot        (GameManager.RevivePlayer → health=100)
  └ MoveBotToSpawnPoint     (invokes the stock private MovePlayer coroutine)
DriveScriptedBots  (each FixedUpdate)
  └ DetectAndApplyDeath     (flip isDead + drop weapon when HP ≤ 0)
  └ walk toward nearest opponent + periodic swing
```

Death detection note: in a network match the stock death path broadcasts over
Steam P2P, which isn't initialized headless. So death is applied locally
(`isDead` + drop weapon) and the host's existing death monitor schedules the
round advance — the same outcome the stock local-match path produces.

## License

The bot/host code here is provided as-is. Stick Fight: The Game and its
assemblies are property of Landfall Games; nothing from the game is included
in this repo.
