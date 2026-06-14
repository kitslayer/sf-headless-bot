# SF Headless Bot Host

A headless, server-side **RL training arena** for *Stick Fight: The Game*. It
runs the real game binary in batch mode (no rendering), exposes a UDP JSON
bridge per instance, and trains a PPO agent against it — plus an in-process
scripted **teacher** used for imitation-learning bootstraps. Stock game physics
and combat code throughout — the defining design rule is that **every spawn /
move / revive / death step mirrors stock Stick Fight 1:1**, so behavior matches
the real game and trained policies transfer.

## Architecture

```
StickFight.exe -batchmode  (xvfb, Wine/Proton, N instances)
 └ SFHeadlessHost.dll (BepInEx plugin)
    ├ stock-faithful spawn/revive/round lifecycle
    ├ scripted driver: teacher core (weapon fetch, engage band,
    │   pulsed fire, two-band void veto) for non-RL slots
    ├ UDP JSON bridge (ports 1341+i): ping / snapshot / setBotAction /
    │   loadMap / inspect / ...  Snapshots carry per-slot obs fields
    │   (kinematics, hp, weapon state, 16-ray terrain fan, projectiles,
    │   ground weapons) and each slot's exact InputFrame ("in") — the
    │   demo label stream.
    └ curriculum knobs via env vars (fixed map, stage HP, void box, ...)

python/
 ├ sf_headless_env.py      Gym env, 78-dim obs, auto-aim, MultiDiscrete
 │                         [move(3), jump(2), fire(2)], slot-swap spatial
 │                         diversity, kill-biased reward (damage asym,
 │                         per-hit floor, pickup + armed trickle, fast-kill
 │                         bonus, fall/death penalty -1.0)
 ├ train_headless_ppo.py   PPO + KickstartPPO (decaying BC anchor on
 │                         teacher demos, critic-warmup phase, LR warmup,
 │                         frozen VecNormalize) with auto-resume
 ├ collect_demos.py        records (obs, exact teacher action) pairs from
 │                         teacher-driven instances; drops teacher deaths
 ├ bc_pretrain.py          behavior-clones a demo set into the latest
 │                         checkpoint (policy tower only)
 └ sf_viewer.py            live pygame viewer of any instance
```

Orchestration: `scripts/fleet.sh start|stop|restart N` (writes `run/fleet.env`,
the single source of truth the instances source), `scripts/watchdog.sh`
(restarts dead/hung instances, desync jitter), `scripts/train_supervisor.sh`
(keeps the trainer alive, resumes from checkpoints, stall detection).

## Training pipeline (current)

1. **Stage curriculum** (gates: deterministic-eval win ≥ 0.8, falls ≤ 0.1):
   stationary dummy at `SF_STAGE_HP=25` (the stock HP option scales damage,
   so kills are ~1 clip) → HP 100 → moving dummy → weakened scripted bot →
   self-play pool. Fixed map per stage.
2. **Current run = plain PPO** from the pre-BC `ppo_headless_800000` tip with
   `randomize_slot` spatial diversity + `SF_STAGE_HP=25`. The BC/Kickstart
   bootstrap (steps below) is BUILT but GATED OFF (`run/USE_KICKSTART`): BC
   from the fixed-spawn teacher collapsed to a constant cliff-march
   (deterministic fell 0.83). Re-enable only with diverse-spawn demos.
3. **Imitation bootstrap (dormant)**: `SFGYM_RL_SLOTS=1` → teacher drives
   slot 0, `collect_demos.py` (~100k pairs) → `bc_pretrain.py` → checkpoint,
   then `KickstartPPO` resumes with a decaying BC anchor + critic warmup.
   Recollect demos whenever the opponent stage changes.

**Status (2026-06-14):** STAGE 2 — SELF-PLAY (`opp_mode="selfplay"`), a real
fighting opponent. **League-refresh #1 DONE: learner beat frozen opp 1104k at
deterministic win 0.80 (16/20) @ ts~1.33M (arms 1.15, hits 1.60), so the frozen
opp was advanced to ckpt 1327996 — near-mirror ~50%, now climbing vs the stronger
snapshot.** ("Flat 0.35" @1.17M/1.24M was just too-few-steps, not stagnation.)
Path here: stage 0 stationary dummy (det win ~0.45, capped
by falls 0.22 + fail-to-finish) → a −0.5→−1.0 fall-penalty experiment that
BACKFIRED (falls→0 but went passive, win 0.04, because the dummy camped ~3u from
the void edge) → reverted to −0.5 and advanced to a void-safe MOVING dummy
(`patrol`), which SOLVED falls (det fell 0.22→0.00, win 0.35) by relieving the
kill-vs-fall tension → plateaued, so advanced to SELF-PLAY. Self-play deploy
first WEDGED the fleet (host `AdvanceRound` was re-entrant — fired per-death with
no survivor gate, so both-bots-dying restarted the respawn chain → empty rounds);
fixed in `SFHeadlessHost.cs` (in-flight latch + survivor-count gate +
skip-damage-on-corpse + play-gate guard) and LIVE-VALIDATED (fleet sustains
rounds, 4/4 bridges keep players). Now: a frozen PPO snapshot (`run/SELFPLAY_CKPT`)
drives the opponent slot — it moves/arms/shoots/kills; the learner trains against
its own equal (~50% symmetric → dense gradient). Reward death-penalty is
opp_mode-aware (hold/patrol −0.5; selfplay/scripted split fall −1.0 / combat −0.5).
NEXT: next eval gate ts 1.49M — eval vs BOTH the current frozen opp (1327996;
refresh again if win >0.65) and the old 1104k (forgetting check, expect ≥0.7,
path `run/SELFPLAY_CKPT.prev`); repeat the refresh ladder toward superhuman, or
escalate to a PFSP pool if cycling/forgetting appears across refreshes. Refresh =
edit `run/SELFPLAY_CKPT` + kill TRAINER ONLY (supervisor re-reads the file +
relaunches ≤60s w/ the new opp; no sup restart, fleet untouched). VERIFY a refresh
via live `/proc/PID/environ` of trainer+workers AND post-kill `tail -c +OFFSET`
(append-mode `train.log` interleaves stale "loaded frozen opponent" lines from the
killed trainer's dying workers — a raw `tail` misleads). Runs at 2 instances
(frozen-opp inference is CPU-heavy). Gate on deterministic eval (`run_eval.sh N "" selfplay`).

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
# RL training fleet (4 instances, slots 0+1 driven by Python):
SFGYM_RL_SLOTS=0,1 bash scripts/fleet.sh start 4
bash scripts/watchdog.sh 4 60 300 &
bash scripts/train_supervisor.sh 4 50000000 &

# demo collection mode (slot 0 = teacher):
SFGYM_RL_SLOTS=1 bash scripts/fleet.sh restart 4   # ALWAYS restart, not start
python python/collect_demos.py --minutes 120
python python/bc_pretrain.py
```

### Environment (via `run/fleet.env`, written by `fleet.sh start`)

| Var | Default | Meaning |
|---|---|---|
| `SFGYM_BOT_SLOTS` | `0,1` | Slots the host spawns and lifecycle-manages |
| `SFGYM_RL_SLOTS` | — | Subset driven externally via `setBotAction` (others get the scripted teacher) |
| `SF_FIXED_MAP` | — | Pin every round to one scene (curriculum) |
| `SF_STAGE_HP` | 100 | Stock HP option (scales damage; 25 = ~1-clip kills) |
| `SF_VOID_Y` / `SF_VOID_Z` | per-map | Kill-floor / edge coordinates for the obs void features |
| `SF_BOT_STALL_SECS` | — | Round-advance timer when no HP changes |
| `SFHEADLESS_PORT` | `1337` | Game UDP port (bridge is `port+4`) |

## Physics notes (corrected 2026-06-12)

The real game ticks at **60 Hz** (`fixedDeltaTime 0.01667`) and runs
**gravity -20** — not Unity's 50 Hz / -9.81 defaults that older docs claimed.
Raycasts do not hit triggers. All simulation constants in this repo use the
corrected values.

Death detection note: in a network match the stock death path broadcasts over
Steam P2P, which isn't initialized headless. So death is applied locally
(`isDead` + drop weapon) and the host's existing death monitor schedules the
round advance — the same outcome the stock local-match path produces.

## License

The bot/host code here is provided as-is. Stick Fight: The Game and its
assemblies are property of Landfall Games; nothing from the game is included
in this repo.
