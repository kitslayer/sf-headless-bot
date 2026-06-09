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
| 2.66M | oscillating −1.08 ↔ −0.27 | 133 checkpoints; 0 new restarts (13 total). Fleet ~14.5h continuous, disk 85%. RAM ~5.5GB (resumed slow ~150–250MB/h drift; ~26h headroom, non-urgent). Steady-state unchanged. |
| 2.75M | oscillating −1.03 ↔ +0.03 | 137 checkpoints; 2nd clean watchdog auto-recovery (13→14) at ~01:55 (~9h after the 1st — recovered in one cycle, reclaimed RAM to ~6.0GB, training uninterrupted). Fleet ~15.5h continuous, disk 85%. Steady-state otherwise unchanged. |

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

## 2026-06-06 — BOX HARD-FROZE at ~2.8M steps (root-caused + fixed)

**What happened.** Healthy until 03:00 UTC (sar: 0.03% iowait, 58% mem, swap
unused). Between 03:00–03:10 the per-frame game exceptions went *runaway* on
instances 1/2/3 at once — each plugin log ballooned to ~3.5 GB in 90 s
(~39 MB/s). At ~03:10 the box froze hard; it stayed unresponsive ~13h until a
hard reset at 16:24. No OOM, no nvidia Xid, no MCE/thermal, no kernel panic.

**Mechanism.** The 3.5 GB logs were *sparse* afterward (3.5 GB apparent / 148 MB
actually on disk) → ~3.4 GB *per file* was dirty page-cache that never flushed.
Three instances × 39 MB/s overwhelmed writeback → uninterruptible-I/O pileup →
whole box wedged. journald couldn't write either, which is why there's no final
log line. The watchdog's 150 MB / 90 s truncation was far too slow to contain a
39 MB/s spam.

**The two spam sources (both game-side, every frame, with stack traces):**
1. `InvalidOperationException: Steamworks is not initialized` —
   `SyncableObjectManager.LateUpdate → ListenForPackages → IsP2PPacketAvailable`.
2. `NullReferenceException` — `OnlineRoom.Update → CheckSides →
   GetComponentInChildren<Torso>()` (the long-known "Torso NRE").

**Fixes shipped (this commit):**
- *Source:* `IsPacketAvailableHeadlessPrefix` now ALWAYS returns false in
  batchmode (no Steam = no P2P packets) — kills source #1 *and* the downstream
  ListenForPackages NullRef at the root.
- *Log layer:* `PerLobbyLogListener` drops the known benign per-frame blobs
  (needle list incl. both exceptions + their stack frames) and emits one
  rolled-up summary every 10 s — caps disk spam no matter what.
- *Watchdog:* truncation threshold 150 → 100 MB.
- *Host (operator runs w/ sudo):* `vm.dirty_background_bytes=256MB`,
  `vm.dirty_bytes=512MB` in `/etc/sysctl.d/99-sf-dirty.conf` — caps the dirty
  backlog so a writeback storm can never wedge the box again, from any source.
- *Restart-safety:* `fleet.sh start` was clobbering `SF_FIXED_MAP` to empty on
  supervisor restarts (it only re-exports `SFGYM_RL_SLOTS`) → random maps →
  hang/flap; that path was LIVE the night it froze. It now sources the prior
  `run/fleet.env` and only overrides with caller-set non-empty vars.

**Recovery.** Resumed cleanly from `ppo_headless_2800000_steps.zip` (lost ~10
min). Fleet of 4 back on scene 6, supervisor + watchdog up, all fixes deployed.

## 2026-06-06 — stage-0 v2 retune (FRESH run from 0)

stage-0 v1 plateaued: over 2.8M steps vs the stationary dummy the mean ep_rew
stayed **net-negative** (overall −0.72; first-15% −0.84 → last-15% −0.55; best
peaks Q1 +0.26 / Q2 +0.65 / Q3 +0.21 / Q4 +0.49 — not climbing). Verified the
sim is REAL first (live probe: both ents alive at y≈0 on Desert3 geometry, agent
strafing/jumping and dropping the dummy 55→32 hp; map confirmed = Desert3
buildIndex 6 reloaded every round, 1565/1579 rounds). So the plateau is genuine
PPO instability + reward variance, not broken data. The negative mean is mostly
**falling off Desert3's edges** (death == −1.0 == a full kill) drowning the
damage signal.

Three changes (commit pending), then a FRESH run (old run archived to
`models/archive_stage0_v1_lr3e4/`, 2.8M policy kept as `BEST_..._2800000`):
1. death/fall penalty −1.0 → −0.5 (kill +1.0 unchanged → a kill is worth 2 falls;
   biases toward finishing over edge-camping, cuts variance).
2. `VecNormalize(norm_reward=True, clip_reward=10, gamma=0.995)` — normalizes
   returns, directly taming the ±spike variance that drove the oscillation.
3. learning_rate 3e-4 → 1e-4.

Watching for: ep_rew_mean climbing AND **holding** positive (the v1 run never
held). First rollout (2k steps) was a noisy +2.0 over ~8 episodes — ignore until
~300k+ steps. ep_rew_mean is RAW (VecMonitor is inside VecNormalize) so it stays
comparable to v1.

## 2026-06-07 — v3: PERCEPTION UPGRADE (obs 22 → 48), ported from scripted bot

Root insight (after reading the scripted-bot docs at `~/stickfight-bot/docs/` +
`BOT_SENSING.md`): v1/v2's 22-dim obs (pos/vel/hp only) was far poorer than the
scripted bot's BotContext AND the original FrameBuilder design (which fed the
Python policy a 64-ray spatial fan — the headless snapshot had dropped it). The
agent was effectively blind to map geometry → kept walking off Desert3's edges
(confirmed via live probe: agent y plunged to −13.6, falling 9/35 frames) → never
beat even a stationary dummy across 2.8M (v1) + 500k (v2) steps, smoothed mean
stuck ~−0.3 to −0.7. **Not a tuning problem — a perception problem.**

Ported the scripted bot's senses into the obs:
- **Tier 1 (env/Python):** void/edge sense — edge distances, kill-floor height,
  `SimulateVoidTime` ballistic time-to-void (bounds y<−11.5, |z|>19). The fix for
  falling. No mod rebuild (computed from snapshot pos+vel).
- **Tier 2/3 (mod snapshot + env):** per-ent self/opp state mirroring BotContext —
  grounded, ragdolled, swinging, blocking, jump-cd, wall-cd, sinceShot, ammo,
  aim z/y — plus opp predicted-pos (velocity lead). Added to `EmitStateSnapshotTo`
  (10 new JSON fields/ent; field names verified vs decompiled Assembly-CSharp,
  every read guarded). DLL rebuilt + deployed.
- **Tier 2 spatial + Tier 3 threat (added on request — "keep this"):** 16-ray YZ
  world-distance fan per ent (downsized from the original FrameBuilder's 64;
  RaycastAll + IsChildOf-rig filter so it senses terrain, not own ragdoll/opp —
  verified live: open above ~1.0, ground below ~0.15) + top-level `proj[]` of
  in-flight RayCastForward/ThrownWeapon as [z,y,dirz,diry] (empty at stage 0).
- No grab (unimplemented in vanilla).

obs is now **72-dim** (self 19 + opp 19 + rel 4 + opp-pred 2 + void 4 + rayfan 16
+ proj 8). Fresh run started clean (old 22/26/48-dim runs archived under
`models/archive_*`).

**fps cost (2026-06-07):** the 72-dim DLL halved throughput — **~12 fps** (was
~24) — because `EmitStateSnapshotTo` now does 16× `RaycastAll` + 2×
`FindObjectsOfType` per snapshot at 20 Hz. Verified the run is genuinely
ADVANCING at 12 fps (2048→6144 in 210s). Left as-is for the overnight babysit
(progress + liveness > speed; no risky rebuild while unattended). **MORNING TODO:
optimize** — throttle `FindObjectsOfType` to ~5 Hz with caching + use
`RaycastNonAlloc`; should restore ~24 fps without dropping the senses. One
transient freeze occurred during the chaotic restart/CLI-crash sequence
(unrelated Bun segfault crashed the CLI; setsid'd fleet survived) — recovered by
restarting the trainer; current run stable. Overnight monitor actively pings all
bridges + verifies timestep delta each tick.
Verdict pending the clean ~300k read; the key question is whether edge-awareness
finally stops the falling. (Commit 001b8f2.)

⚠ User flagged a *possible deeper issue with the headless build itself* — if the
perception upgrade still doesn't let it beat a dummy, investigate that next.

## 2026-06-09 — v5: THE AIM BUG (root cause of everything), + throughput

**The user's "something fundamentally wrong" instinct was right.** Strategy
review found and fixed the deepest bug of the project: **the agent could never
aim.** Chain: rigs are spawned `keyBoard=true` → stock `Controller.UserAim()`
calls `RotateTowardsMouse()` every frame for keyboard input → injected Aiming
axes were overridden by the position of the meaningless xvfb mouse. Measured
live: aim-at-opponent correlation ~50% (= chance) under the old env, 0% (perfect
anti-correlation) once the env sent opponent-relative values. Every prior run
(v1 22-dim through v4 72-dim, ~4M steps) trained with "fire" disconnected from
"hit" — kills happened only by accident. This explains the eternal negative
plateau vs a stationary dummy far better than tuning or perception ever could.

Fixes (subagent-reviewed, then verified live: **aim-at-opponent 50/50 = 100%**):
- host: skip-prefix `Controller.RotateTowardsMouse` in batchmode → the stock
  couch-gamepad path applies our injected axes (`LookRotation(0, AimY, −AimX)`).
- env: true auto-aim — `aimx=−dz, aimy=dy` (normalized, from latest snapshot).
- host: snapshot `aimz/aimy` now read `Controller.aimer` (written by stock in
  both unarmed + gradual branches; AimTargetHelper goes stale when unarmed).

Also in v5 (throughput + measurement):
- **SF_TIMESCALE=2** — TimeHandler-aware LateUpdate assert (stock slowmo/pause
  preserved exactly); trainer polls 40 Hz wall = constant 20 Hz game-time
  decisions. Physics identical per game-second.
- Dead-time trim: pre-combat grace 2.0→0.5 s, next-match delay 2.0→0.5 s (the
  fps ceiling was reset-bound: every episode end blocked the sync vec-env for
  the full round-advance; snapshot RPC itself measures ~0 ms).
- **win_mean / fell_mean** in the training log (VecMonitor info_keywords) — the
  real stage gates. Review fixes: missing-ent snapshots no longer fabricate
  wins/damage; VecNormalize resume filename now actually matches (every prior
  auto-resume silently reset normalization stats).

Stage-0 gate (advance when): win_mean ≥ 0.8 sustained AND fell_mean ≤ 0.1.
Then stage 0.5 (moving passive opponent) → stage 1 (scripted) → stage 2
(self-play league). Fresh run from 0 (prior runs archived in models/archive_*).
