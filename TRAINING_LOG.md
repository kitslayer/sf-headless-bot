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
| 1.31M | oscillating, peaks still ~0 to +0.6 | stable run, 65 checkpoints |
| 1.37M | oscillating −1.2 ↔ −0.05 | 68 checkpoints; watchdog auto-recovered 5 batchmode hangs, all clean |
| 1.44M | oscillating −1.2 ↔ +0.15 | 71 checkpoints; ~7 hang auto-recoveries (steady ~1/20min, all clean) |
| 1.54M | oscillating −1.0 ↔ −0.03 | 77 checkpoints; FIXED instance-2 flapping (watchdog was relaunching it on RANDOM maps due to missing SF_FIXED_MAP in its env → hang-prone scenes → flap; now persisted via run/fleet.env). Wiped+re-cloned inst2 prefix. Fleet now consistently scene 6. |
| 1.60M | oscillating −1.08 ↔ −0.285 (this hour's window) | 80 checkpoints; **0 watchdog restarts this hour** (restarts steady at 12 — instance-2 fix holding ~1.5h+). Fleet rock-solid, RAM stable ~6.9–7.5GB, disk 85%. Oscillation band has tightened vs the early −1.5↔+0.6 range but still no held-positive convergence (same PPO-instability story). |
| 1.67M | oscillating −1.02 ↔ −0.20 | 83 checkpoints; **2nd consecutive hour with 0 watchdog restarts** (steady at 12 total). Period ~25–35 min, amplitude ~−1.0↔−0.2. A 3-sample cluster near −0.2 at 12:55 looked like convergence but cratered back to −1.1 by 13:05 — confirmed still oscillating, not converging. Infra: RAM slow-drifting 6.8GB (plateaued, no leak), disk 85% flat. Fleet stable ~2.5h straight. |
| 1.74M | oscillating −1.32 ↔ −0.166 | 87 checkpoints; **3rd consecutive hour, 0 watchdog restarts** (12 total). Same regular oscillation (period ~25–35 min). Infra: RAM 6.6GB (drifts ~150MB/h, ~30h+ headroom before the 1.5GB lowRAM alert, reclaimed on any restart), disk 85% flat, no orphan growth. Fleet stable ~3.5h straight on scene 6. Policy plateau persists — no autonomous hyperparameter changes (user's tuning domain). |
| 1.81M | oscillating −1.43 ↔ −0.286 | 90 checkpoints; **4th consecutive hour, 0 watchdog restarts** (12 total). Confirmed: RAM drift is non-monotonic — fell to 6.57GB then reclaimed to ~7.0GB without a restart (internal page reclaim), so lowRAM is a non-issue. Fleet stable ~4.5h straight on scene 6. Steady-state holding; same oscillation, same plateau. |
| 1.89M | oscillating −1.13 ↔ +0.19 | 94 checkpoints; **5th consecutive hour, 0 watchdog restarts** (12 total). ep_rew briefly touched **+0.19** at 16:14 (first positive window in this run's recent stretch) then cratered back to −1.1 within ~10 min — still the same oscillation, peaks just clip positive occasionally (all-time peak remains +0.61 at ~1.22M). Fleet stable ~5.5h straight on scene 6, RAM ~6.7GB, disk 85%. |
| 1.96M | oscillating −1.14 ↔ −0.133 | 98 checkpoints; one clean watchdog auto-recovery (12→13 total) at ~16:50 — first hang in ~5.5h, recovered in one cycle, bridge back + steps kept advancing (self-healing validated again). RAM reclaimed to ~7.4GB on the restart. Otherwise same oscillation/plateau. ~6.5h continuous, approaching 2M steps. |
| **2.04M** | oscillating −0.83 ↔ −0.207 | **crossed 2M steps; 102 checkpoints**. 0 new watchdog restarts this hour (13 total). Fleet ~7.5h continuous on scene 6, RAM steady ~7.1GB, disk 85%. Same plateau — the policy has been oscillating in roughly the same band (~−1.1 ↔ ~0) for ~800k steps now without breaking through to held-positive. Pipeline + infra fully proven; further progress is a tuning problem (LR/reward-variance/curriculum — user's domain), not an infra one. |
| 2.13M | oscillating −1.37 ↔ −0.40 | 106 checkpoints; 0 new watchdog restarts (13 total). Fleet ~8.5h continuous. No change to the steady-state — same oscillation band, RAM ~6.8GB, disk 85% flat. Logging tersely now; nothing new until the band shifts or an alert fires. |
| 2.22M | oscillating −1.17 ↔ −0.10 | 110 checkpoints; 0 new restarts (13 total). Fleet ~9.5h continuous, RAM ~6.6GB, disk 85%. Steady-state unchanged. |
| 2.30M | oscillating −1.06 ↔ −0.44 | 115 checkpoints; 0 new restarts (13 total). Fleet ~10.5h continuous, RAM ~6.4GB, disk 85%. Steady-state unchanged. |
| 2.39M | oscillating −1.04 ↔ −0.40 | 119 checkpoints; 0 new restarts (13 total). Fleet ~11.5h continuous, disk 85%. RAM slow-drifting (~6.1GB, ~250MB/h this stretch with no reclaim event in ~2h) — still ~18h of headroom before the 1.5GB alert, and a watchdog restart would reclaim it; watching but non-urgent. Steady-state otherwise unchanged. |
| 2.48M | **first all-positive window: +0.069 ↔ +0.24** | 123 checkpoints; 0 new restarts (13 total). Fleet ~12.5h continuous. At 23:30 all 3 ep_rew samples were positive (0.24/0.069/0.188) — first time the whole window cleared 0 (prior best was a lone +0.19 at 16:14 that cratered immediately). Most convincing improvement signal yet, but NOT yet called convergence — needs to hold positive next window (oscillation period ~25–35 min vs 10 min sampling). RAM ~6.0GB, disk 85%. Watching closely. |
| 2.57M | oscillating −1.05 ↔ +0.24 | 128 checkpoints; 0 new restarts (13 total). Fleet ~13.5h continuous. The +0.24 all-positive window did NOT hold — reverted to the −1.0 trough within ~20 min, confirming it was a (wide) oscillation peak, not convergence. Band-top now occasionally clears 0; troughs unchanged ~−1.0. RAM stabilized ~5.7GB, disk 85%. |

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
