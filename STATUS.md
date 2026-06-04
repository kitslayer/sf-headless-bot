# Status / Architecture Notes

_Living doc for the headless bot arena. Updated as the system evolves._

## What's running right now

- **Fleet of 4 headless instances** (ports 1337–1340, bridges 1341–1344), each
  with 2 bots. Launched with `SFGYM_RL_SLOTS=0,1` so the RL env owns both slots.
- **Watchdog** auto-restarts any instance that dies.
- **PPO training** (`python/train_headless_ppo.py`, pid in `logs/train.log`):
  curriculum **stage 0** — agent (slot 0) vs a **stationary dummy** (slot 1),
  learning to approach + attack. Reward = damage-diff + win/loss. Checkpoints to
  `models/ppo_headless_*_steps.zip` every ~20k steps, GPU, auto-resumes.
  **Pinned to scene 6 (Desert3) via SF_FIXED_MAP** — a consistent map is
  essential; random maps each episode made ep_rew flatline (agent fell off
  varied geometry). **Verified learning**: ep_rew_mean −1.5 → −0.65 in the
  first ~12k steps, agent landing many hits on the dummy.
- Supervisor (`train_supervisor.sh`) + watchdog (`watchdog.sh`) keep the fleet +
  trainer alive and rotate logs. **Launch long-lived helpers with `setsid`**
  (nohup alone dies with the launching shell).
- Host load ~13 (4 instances) leaves headroom for the docker media stacks.

## Operate it

```bash
cd ~/stickfight-bot/sf-headless-bot
# Build the plugin + deploy
dotnet build SFHeadlessHost.csproj -c Release
cp bin/Release/SFHeadlessHost.dll ~/.steam/steam/steamapps/common/StickFightTheGame/BepInEx/plugins/

# Scripted self-play fleet (2 scripted bots per instance)
bash scripts/fleet.sh start 4          # ports 1337+i, bridges 1341+i
bash scripts/fleet.sh status
bash scripts/fleet.sh stop
nohup bash scripts/watchdog.sh 4 90 0 &  # keep-alive

# RL training (slot 0 = agent, slot 1 = dummy/scripted)
SFGYM_RL_SLOTS=0,1 bash scripts/fleet.sh start 4   # free both slots for the env
cd python && python train_headless_ppo.py --instances 4 --base-bridge 1341

# Inspect a live instance
python scripts/bridge_probe.py 1341     # ping / enriched snapshot / setBotAction
```

Tunables (env): `SFGYM_BOT_SLOTS` (default 0,1), `SFGYM_RL_SLOTS` (default empty),
`SF_BOT_STALL_SECS` (default 30), `SF_EXCLUDE_MAPS` (default 103=LevelEditor).

## Pieces

| File | Role |
|---|---|
| `SFHeadlessHost.cs` | BepInEx plugin: scripted bots + RL bridge (snapshot/setBotAction) |
| `scripts/launch_oracle.sh` | one instance; per-instance wineprefix (OUTSIDE project dir) + log |
| `scripts/fleet.sh` | N-instance orchestrator (start/stop/status/restart) |
| `scripts/watchdog.sh` | restart dead/stalled instances |
| `scripts/bridge_probe.py` | validate the JSON bridge |
| `python/sf_headless_env.py` | Gymnasium env over the bridge |
| `python/train_headless_ppo.py` | SB3 PPO across the fleet |

## Bridge protocol (loopback UDP JSON, port 1341+i)

- `{"cmd":"snapshot"}` → `{tick,scene,inFight,round,ents:[{slot,x,y,z,vx,vy,vz,hp,alive,armed}]}`
- `{"cmd":"setBotAction","slot":N,"mx":f,"my":f,"aimx":f,"aimy":f,"buttons":i}` (bit0=jump, bit1=fire)
- `{"cmd":"ping"}`, `{"cmd":"loadMap","scene":N}`

## Hard-won gotchas (don't regress)

- **Never put wineprefixes inside the project dir** — the SDK globs their Wine
  `mscorlib.dll` into the net46 reference set and the build dies (missing
  IReadOnlyCollection). Prefixes live in `~/stickfight-bot/sf-bot-prefixes/`.
- **Mono/net35 reflection**: never `Type/FieldInfo/MethodInfo != null` — no
  `op_Inequality` overload, throws `MissingMethodException`. Use `(object)x != null`.
- **Bridge env**: actions are fire-and-forget on a separate UDP socket; sharing
  one socket lets setBotAction acks pollute snapshot reads.
- **Death in network match**: `HealthHandler.Die()` broadcasts over Steamworks
  (uninitialized headless → throws). We set `isDead` + drop weapon locally;
  canonical `TickAuthRigDeathCheck` schedules the round advance.
- **6 instances thrash** this 24-core host (load ~29). 4 is the sweet spot.

## Roadmap

- [x] 2 bots fighting, multi-round, self-healing, scaled fleet + watchdog
- [x] RL bridge (obs + action injection) + Gym env + PPO, stage-0 training live
- [ ] Stage 1: self-play vs frozen-policy snapshots (replace the held dummy)
- [ ] PFSP / league play; richer obs (raycasts, weapon type, aim angle)
- [ ] Curated combat-map whitelist for a cleaner training distribution
