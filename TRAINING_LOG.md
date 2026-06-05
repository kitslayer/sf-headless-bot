# Training Log — stage-0 (agent vs stationary dummy, scene 6 / Desert3)

Autonomous run on the 4-instance fleet (PPO, GPU, SubprocVecEnv over bridges
1341–1344). This log captures what the long run actually did, so the run is
reproducible and you can pick up tuning without re-deriving it.

## Run progression (ep_rew_mean, rolling)

| steps | ep_rew_mean (peak in window) | note |
|---|---|---|
| 0     | −1.8  | fresh / random policy |
| ~20k  | −1.5  | learning starts |
| ~120k | −0.05 | first near-break-even (checkpoint preserved) |
| ~390k | +0.08 | first positive blip (crashed back) |
| ~1.18M| +0.25 | higher positive swing (crashed back) |
| ~1.21M| +0.51 | higher still |
| ~1.22M| +0.61 | **highest swing so far** |
| 1.26M+| oscillating −1.5 ↔ +0.6 | never holds positive |

**Key finding:** the policy **oscillates** rather than converging — it reaches
progressively higher positive peaks (+0.08 → +0.61 over ~1.1M steps, so the
underlying policy IS slowly improving) but the troughs stay around −1.0 to −1.5.
This is PPO instability, not a broken pipeline (the pipeline demonstrably learns).

**Likely fixes (your call — not applied autonomously):**
- lower `learning_rate` 3e-4 → 1e-4 (primary suspect for the oscillation)
- reduce reward variance: smaller fall penalty than the −1 cliff, and/or
  `VecNormalize(norm_reward=True)`
- larger `n_steps`/`batch_size` for lower-variance updates
- the dummy is a trivial opponent; once stable, advance to stage-1 (vs scripted:
  `SFGYM_RL_SLOTS=0` + `--opp-mode scripted`) then stage-2 self-play.

## Best checkpoints (eval before trusting)

Checkpoints saved every ~20k steps in `models/` (never overwritten). Peaks are
oscillation tops, so eval a few:
- `BEST_stage0_scene6_120000.zip` — first near-0 era
- `BEST_stage0_scene6_1200000.zip` — +0.5/+0.6 peak era (likely strongest)
- eval with: `python eval_checkpoint.py models/BEST_stage0_scene6_1200000.zip --bridge <free port> --vecnormalize models/BEST_stage0_scene6_1200000_vecnormalize.pkl`
  (use a DEDICATED instance bridge, not a training one).

## Infrastructure proven in this run

- **Self-healing validated in production:** watchdog auto-detected a HUNG
  instance (inst 2, bridge 1343 unresponsive — the dead-process check misses
  this) via bridge-ping and auto-restarted it cleanly. No manual intervention.
- RAM is bounded: slow per-instance growth (~150–200 MB/h) is reclaimed by the
  periodic instance restarts; stays ~5.5–8.8 GB available.
- Logs bounded by the watchdog's 150 MB truncation (game spams a benign
  per-frame Torso NRE in batchmode).
- Stable for many hours / 1.26M+ steps with supervisor + watchdog, 0 crashes.
