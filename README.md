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

**Status (2026-06-15 20:25):** STAGE — **SCRIPTED vs a WEAKENED bot** (`opp_mode="scripted"`,
`SFGYM_BOT_AGGRO=0.4 / AIM_NOISE=0.3 / REACTION=0.15`) + a REBALANCED reward, resumed from the
clean best **1879996**, HP25/Ice11. The fix for the plateau both self-play and full-scripted hit
(full arc in `TRAINING_LOG.md` **2026-06-15 11:40 → 20:25**). Three changes shipped together:
**(1) reward** — removed the farmable armed-trickle (it paid the agent to CAMP) + up-weighted the
KILL to +2..3 so winning dominates the per-step terms (passivity no longer safe); **(2) a weakened
scripted bot** — new C# difficulty knobs in `DriveScriptedBots` make the opponent BEATABLE (a
full-strength bot collapsed the argmax policy to passive: scripted-eval arms .12→.10→.00,
win→0.00); **(3) a corrected eval gate** — `--slot-swap` + stochastic + a low-variance `score`
(opp_died−self_died) so the selfplay mirror is interpretable (the OLD gate secretly measured a
hold-dummy — see below). GOAL: a beatable opponent + win-dominant reward → a real WIN climb; then
ramp AGGRO→1.0, →HP100, →selfplay (gated by the fixed `--slot-swap` eval). Revert to self-play:
`touch run/USE_SELFPLAY` + restart. Collapsed full-scripted ckpts in `models/archive-scripted-collapse/`.
**⚠ GATE BUG: every "deterministic win vs frozen opp" number in the self-play history
below was actually measured vs a STATIONARY hold-dummy** — `run_eval.sh` parsed `OPP_MODE`
but never forwarded `--opp-mode`, so `eval_checkpoint.py` always defaulted to `opp_mode=hold`.
So the 5 "refreshes" were dummy-gated, NOT a verified strength ladder. FIXED (run_eval
forwards `--opp-mode`, defaults selfplay, hard-fails if the frozen opp can't load).
Corrected first-ever mirror evals: learner **0.10** win / 0.45 fell vs frozen 1759996;
self-mirror (1759996 vs itself) only **0.15** (harness even-baseline ~0.15, NOT 0.50 —
deterministic-vs-sampled, single-slot, kill-and-survive). So self-play was a stuck
mutual-fall basin (learner ~even with its ancestor; bots barely arm ~0.05/ep or fight
~0.5 hits/ep; falls dominate). Switched to the planned-but-skipped SCRIPTED rung (a fixed
competent opponent that punishes not-arming). **REVERT: `touch run/USE_SELFPLAY` + restart
supervisor** (backups `run/{fleet.env,SELFPLAY_CKPT}.selfplay-bak`).

<details><summary>Pre-gate-fix self-play ladder history — ⚠ all "det win" numbers below were vs a hold-dummy, not the mirror</summary>

The ladder climbed
cleanly through 5 refreshes (1.49M→1.82M @ det win 0.65-0.80) then the 6th stage
REGRESSED — deterministic win dropped to 0.30 vs 1759996 AND 0.35 vs the weak 1104k,
with falls 0.05→0.30, while shaped ep_rew ROSE (reward/win divergence at high skill;
PPO metrics were healthy). Rolled back to known-good 1759996 (0.65 vs 1695996, fell
0.05); did NOT raise the fall penalty (memory: that backfired into passivity). If it
re-regresses, fix = rebalance reward toward kills (down-weight farmable chip/trickle). Single-opp ladder: refresh #1
(opp 1104k→1327996 @ det win 0.80 @ts1.33M), #2 (→1487996 @ 0.65 @ts1.49M), #3
(→1575996 @ 0.80 @ts1.58M), #4 (→1695996 @ 0.75 @ts1.70M), #5 (→1759996 @ 0.65 @ts1.76M). A ts1.49M forgetting-check (win vs OLD 1104k
0.80→0.70) prompted a fictitious-self-play POOL {1104000,1327996,1487996} — but ~150k
steps in the EQUAL-WEIGHT pool **CATASTROPHICALLY COLLAPSED the deterministic policy to
a passive no-op** (ts1.65M: win 0.00 / arms 0.00 / hits 0.00 vs all 3; A/B-confirmed
real — known-good 1487996 scored 0.55 under identical conditions). REVERTED to
single-opp, resumed from healthy 1487996, archived the collapsed ckpts
(`models/archive-pool-collapse/`). Pool code stays gated behind a multi-entry
`SF_SELFPLAY_CKPT` but needs PFSP weighting + kill-weighted reward before any retry
(TRAINING_LOG 2026-06-14 16:20). ("Flat 0.35" @1.17M/1.24M was just too-few-steps.)
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
POOL mechanics: the env (`sf_headless_env.py`) builds `self._opp_pool` from a
multi-entry `SF_SELFPLAY_CKPT` (or `SF_SELFPLAY_POOL`) and `_select_opp()` samples
one per episode in `reset()` (uniform = fictitious self-play; hot path unchanged).
Single-entry value = single-opp (backward compatible). Deploy/grow the pool = write
the ckpt paths into `run/SELFPLAY_CKPT` (one per line) + kill TRAINER ONLY
(supervisor relaunches ≤60s; no supervisor change). Offline check: `python/pool_smoke.py`.
VERIFY any opponent switch via the PROCESS ENVIRON (`python` NUL-split of `/proc/PID/environ`
for trainer+workers), NOT `train.log` (BLOCK-BUFFERED; `[selfplay]` lines lag & dying-trainer
buffers interleave). The pool collapse was invisible in rollout (win 0.1-0.17, det 0.00).

</details>

**Eval gate (corrected 2026-06-15):** `run_eval.sh [N] [CKPT] [OPP_MODE]` now FORWARDS
`--opp-mode` (default `selfplay`; hard-fails if the frozen opp can't load) — previously it
parsed but dropped this arg, so every eval silently ran vs a stationary `hold` dummy. A real
selfplay/scripted eval prints `[selfplay] loaded opponent …` / spawns the scripted bot on
slot 1; if that's absent, the eval is bogus. Gate on DETERMINISTIC eval, but the harness is
still asymmetric — a policy-vs-ITSELF sanity eval reads only ~**0.15** today (deterministic
learner vs sampled opp, single slot, kill-and-survive). TODO before trusting a refresh
number: make it interpretable (even ≈ 0.50) via stochastic-both-sides + balanced slot-swap +
a fixed map set + low-variance secondary metrics (opp_died−self_died, end-HP-diff); then set
the refresh bar ≈ 0.58. Scripted-stage difficulty knobs: `SFGYM_BOT_AGGRO` (default 0.7,
lower = weaker), `SFGYM_BOT_REACTION`, `SF_STAGE_HP` (25 now; DSF comp = 100).

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
