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

## 2026-06-10 — v5 first hour: IT LEARNS

| steps | win_mean | fell_mean | ep_rew |
|---|---|---|---|
| 10k | 0.018 | 0.053 | −0.27 |
| 37k | 0.07 | 0.16 | −0.14 |
| 49k | 0.12 | 0.14 | −0.01 |
| 61k | 0.13 | **0.05** | **+0.40 ← first positive in project history** |

One hour of v5 (true aim + 72-dim perception + 2× timescale, ~25-34 fps) beat
~4M steps of v1–v4 combined. Falls collapsing (void-sense), wins climbing
(aim), episodes shortening (faster kills). Stage-0 gate: win ≥ 0.8, fell ≤ 0.1.

## 2026-06-10 — v6: WEAPONS BECOME REAL (the second "fundamentally wrong" bug)

Win plateaued ~12% on v5; probe showed agent armed **0%** with the dummy at
100hp at most round ends. Root-caused a STACK of headless breaks:
1. **No physical weapons ever existed**: stock `SpawnWeapon` only broadcasts a
   packet; a stock listen-server's own client handler instantiates it, but this
   host never looped the packet back. 1000+ "sky spawn" log lines, zero weapons
   in-world, ever. Fix: build the same payload + call `OnWeaponSpawned(byte[])`
   locally.
2. **Pickup chain dead**: BodyPart.OnCollisionEnter NREs at mNetworkPlayer and
   RequestWeaponPickUp's server path dies on empty registries. Fix:
   `TickAutoPickup` — unarmed+alive rig within 2.0m of a settled weapon arms
   via the replicated stock local branch (Fighting.PickUpWeapon + destroy).
3. Sky-drop cadence densified (first 1s, every 3-5s) for stage-0 practice.

Verified live: 52 pickups, agent armed ~5% of samples (brief windows = stock
ammo dynamics — empty gun auto-drops). **The complete game loop now exists for
the first time**: perceive (72-dim) → aim (100%) → move → pick up → shoot.
Fresh run from 0. (Commits 9ab810e..c89114b; subagent-reviewed.)

## 2026-06-10 ~03:45 — overnight steady state (v6 final config)

Config locked for the night: stock 1× physics (user timescale warning honored —
measurement showed the bottleneck is wall-clock reset time anyway), stall 15s
(8s tripled scene-reload churn → fleet-wide hang cycling), checkpoints every 8k,
weapons+pickup live, complete game loop. Learning at this point: **rew5 +0.285
(best ever), falls 2-5%, win 5-10% and climbing**, fps ~10-12 (reset-bound;
next lever = 6 instances, needs port-scheme change — deferred). Self-healing
stack: watchdog (instances) + stall-guard supervisor (trainer) + 8k checkpoints.

## 2026-06-10 05:34 — v6 crosses 100k (overnight, untouched)

103k steps on the final stable config. Reward has held positive most of the
night (rew5 band roughly −0.18 ↔ +0.44, centered ~+0.2 — vs v1-v4 which never
held positive at all), falls steady 2-7%, win oscillating 3-14%. Fleet: the
known batchmode hangs rotate through instances; watchdog auto-recovers each
(~30 restarts over the night), trainer never wedged since the 8k-checkpoint +
stall=15s pass. Zero manual interventions since 03:27.

## 2026-06-10 08:22 — 200k overnight

200k steps, zero manual interventions since 03:27. Reward firmly positive
(rew5 band +0.09 ↔ +0.44, typically ~+0.25), falls 1-6%, win 3-14% oscillating.
~55 watchdog instance-recoveries overnight, all clean; trainer never wedged
under the 8k-checkpoint + stall=15 config.

## 2026-06-10 — v7: weapon-obs, Ice11, and the clock wedge

- **v7 deployed** (~10:45): 78-dim obs (+2 nearest ground weapons rel to
  self), +0.05 pickup shaping with `arms` telemetry, CapGroundWeapons(24),
  forced regen=0 (DSF). Root cause of v6's 2-14% win cap: the agent was
  blind to weapon locations and idled unarmed. First rollout win 0.18,
  arms 0.30. Commits `92be454` + review fixes.
- **Ice11 (scene 57)** per Miles (flatter): switched ~12:10. Found the
  **clock wedge**: map-entry freeze (managerTime=0) whose restore
  coroutine dies headless → Time.timeScale stuck 0 forever, rigs pinned,
  reward flat 0 while fps *looks* great. Fixed via StartCountDown batch
  prefix (apply end state synchronously) + LateUpdate wedge-breaker
  (burst mode 2s). Rolled back the ~30k frozen-junk steps to the 48k
  checkpoint. Void box per map via SF_VOID_Y/Z in fleet.env.
- **Throughput/infra**: sky-drop cadence 2-3.5s (ice slides weapons off
  edges, supply was ~1 standing); watchdog orphan-kill fix (`4b70a3a`);
  supervisor stall-guard fixed twice (ts-reset re-anchor; 600s window —
  progress is rollout-quantized, 300s false-killed); SF_BOT_STALL_SECS
  15→30 (was ending rounds mid-weapon-fetch, halfway through the env's
  30s episode cap — win climbed 0.08→0.14 after).
- **State at 200k** (18:40): win ~0.10±0.04, fell ~0.12, arms ~0.35±0.1,
  rew oscillating ±0.2. Stage-0 gate (win≥0.8) still far; next levers:
  more steps (PPO needs 500k+), then finishing-incentive shaping or
  6-instance scaling (needs port-scheme change). Viewer:
  `python/sf_viewer.py` (pygame, DISPLAY=:0, keys 1-4/r/q).

## 2026-06-11 (overnight) — v7 at 500k

Run crossed 500k (~06:00) fully autonomous since the 23:00 watch. Band:
win 0.02-0.16 (oscillating, centered ~0.08), fell ~0.10-0.15, arms
0.25-0.54, rew ±0.3 oscillation. Notable events, all self-healed: one
trainer EOFError crash (silent worker death #3, supervisor relaunched
<60s, resumed from checkpoint), recurring instance hang-churn (~2-3
watchdog restarts/10min; fps 7→5-6 overnight from boot overhead), one
false-empty ts read (log line caught mid-write — watch now uses an
anchored grep + empty guard). No gate progress: win needs either far
more steps or the armed-time/HP-curriculum levers (SF_STAGE_HP knob is
in the tree, dormant; Miles deferred).

## 2026-06-11 — v7 at 600k

Crossed 600k ~10:05, still fully self-healing (no manual intervention
since 500k). Band unchanged: win 0.03-0.15 (centered ~0.10), fell
0.08-0.19, arms 0.30-0.47, rew5 -0.08..+0.25. Instance hang-churn
continues (~1430 cumulative watchdog restarts, fps steady at 5-6); two
single-tick ts-flat strikes self-cleared (slow rollouts under churn —
quantization, not freezes). Plateau verdict at HP=100 unchanged: <1
weapon pickup/episode vs the 1-3 clips a kill needs caps win mechanically;
SF_STAGE_HP=25 curriculum lever remains built+dormant pending Miles.

## 2026-06-11 (afternoon) — kill-biased reward shaping at 650-750k

Miles: no HP changes — shape rewards to make it kill more. Two passes,
both env-side only (commits a7e8b68, then the retune):
1. **650k:** dealt-damage 1.5x taken, +0.05/damaging-tick floor, kill
   pays 1.0 + 0.5*(time left/30s); added `hits` telemetry (damaging
   ticks/ep, the dense kill-path precursor).
2. **704k:** after 59k steps hits doubled (0.14→~0.29 plateau) but win
   stayed flat ~0.07 — arms ~0.25-0.3/ep showed gun ACQUISITION is the
   binding constraint. Pickup bonus 0.05→0.15 + 0.0005/tick armed
   trickle (max ~0.3/ep; camping stays dominated by hunting).

At 750k: hits holds ~0.2-0.4 (2x pre-shaping), arms 0.288→0.311 avg
with an upward tail (0.33-0.38 recent), fell unchanged-to-better
(0.05-0.17), win still 0.05-0.10 — the chain fetch→hit→kill is being
paid at every link now; wins lag because a kill at HP=100 needs a
full clip+ landed within the 30s cap. Both trainer bounces resumed
clean from checkpoints (648k, 704k; obs unchanged so no archive
needed). Next decision point: if arms/hits keep drifting up but win
stays <0.15 by ~850k, the remaining reward-side lever is a bigger
fast-kill bonus; beyond that it's HP curriculum (dormant) or longer
episodes.

## 2026-06-12 — BC from the scripted-bot teacher (650-808k)

Box was down Jun 11 18:43 → Jun 12 ~17:10 (power outage, intentional).
On resume, built the behavior-cloning pipeline per Miles ("teach it
directly from the scripted bot"): host DriveScriptedBots now runs the
teachable core of mod/StickFightGym/ScriptedBot (weapon fetch, engage
band, pulsed fire, void veto); snapshots emit exact per-slot inputs
('in'); collect_demos.py recorded 21,339 (obs,action) pairs over 35 min
on 4 instances (459 eps, teacher win 0.44 overall, 72% of kept eps won;
teacher-death eps dropped). bc_pretrain.py cloned them into the 800k
checkpoint (8 epochs, lr 2e-4: move acc 0.956, fire 0.892), saved as
ppo_headless_808000_steps.zip + BC_INIT_808000 archive. PPO resumed from
it (verified in train.log). Subagent-reviewed; ops trap confirmed live:
fleet.sh stop pattern-kills cost one wrapper shell (self-match again —
use separate calls). Watch win_mean: pre-BC band was 0.05-0.15.

## 2026-06-12 (evening) — BC round 2: 98k pairs from the safer teacher

First clone (21k pairs) didn't lift wins (0.02-0.07, fell up to ~0.21-0.27
— cloned the teacher's decisiveness without edge discipline; 85% of demo
moves were one direction). Per Miles: more data, not more epochs. Teacher
edge-safety patch (4f8b118): two-band void veto (soft 4.5m input veto,
hard 3m forced inward step) + skip weapons in the edge band. 150-min
collection: 98,057 pairs / 1,974 eps, teacher win 0.48 overall, 75% of
kept; re-cloned into 808k -> ppo_headless_816000_steps.zip (move acc
0.978) + BC_INIT_816000 archive. PPO resumed from it (verified). Pre-BC
win band for comparison: 0.05-0.15, fell ~0.13.

## 2026-06-12 (night) — KickstartPPO + HP-25 curriculum stage live

Research pass (Kickstarting/DAPG/PIRLNav/VPT/JSRL + fighting-game DRL):
plain BC died twice for textbook reasons — stale critic shreds the clone
(no warmup) and nothing anchors the teacher during RL. Deployed, after
subagent review caught the stale-supervisor trap (plain PPO had already
overwritten the 816k BC seed at 21:25 — restored from BC_INIT_816000):

- **KickstartPPO** (commit 8579a1b): phase A 808k→858k = policy tower
  frozen, value-head-only recalibration vs the clone's own rollouts;
  phase B = unfreeze + fresh Adam + 50k LR warmup + BC cross-entropy
  anchor λ=0.5 → 0 linearly by 1.258M. Absolute-ts anchors survive
  relaunches; λ clamped above; n_epochs=10 + target_kl=0.02;
  VecNormalize stats frozen.
- **Stage 0a: SF_STAGE_HP=25** (fleet.env): stock HP option scales
  damage 4x, so a kill = ~1 clip — the win signal PPO could never credit
  at 100 HP becomes dense. Demos stay on-distribution (health display
  is unchanged; only damage scales). Ramp 25→(50)→100 on gate
  win≥0.8 ∧ fell≤0.1, measured by DETERMINISTIC eval (rolling means
  swing ±0.06 from churn — research trap #6; eval script TODO).
- Curriculum (research (c)): 0a HP25 dummy → 0b HP100 dummy → 1 moving
  dummy → 2 weakened scripted (AIM_NOISE/REACTION anneal) → 3 self-play
  pool + scripted ~20% → 4 self-play + gradual map pool. Recollect
  teacher demos at each opponent change.

Verified at boot: resume from restored 816k, 98k-pair kickstart banner,
vecnorm frozen, fleet asserting OptionsHolder.HP=25, slots RL-driven.

**23:50 — kickstart phase B engaged on schedule** (ts 859k): policy
unfrozen, fresh Adam, LR warmup over 50k, λ=0.496 decaying. bc_loss=0.285
≈ the clone's final pretrain loss → phase A preserved the teacher
behavior while the critic recalibrated. Now watching win/fell as RL
starts moving the policy at HP-25.

## 2026-06-13 02:40 — deterministic eval kills the BC/kickstart approach; reverted to pre-BC + HP-25

Built eval_checkpoint.py (rewritten — old one classified wins by reward
SIGN, which the dense shaping breaks) and ran a DETERMINISTIC 40-ep eval
on a dedicated 5th instance (bridge 1349, no fleet collision) against the
920k kickstart checkpoint:
  **deterministic win=0.067, fell=0.83** (stochastic was win~0.05 fell~0.23)
The argmax policy MARCHES OFF THE VOID EDGE in 83% of episodes — the
stochastic noise was the only thing holding fell at ~0.2.

Root cause: the teacher demos were ~85% one move-direction (fixed map +
fixed spawns → the teacher's movement is near-constant), so BC cloned a
near-constant "walk toward target" policy that argmaxes off the cliff, and
the kickstart anchor (target = those demos) PINNED the policy there. Net:
BC/kickstart REGRESSED vs pre-BC pure PPO (win 0.08->0.05, fell 0.13->0.23)
and phase-B win was DECLINING (0.11->0.03 over 76k steps) — the anchor was
winning the tug-of-war toward a worse policy, and wouldn't free RL until
~1.2M (10+h away).

ACTION (reverted, all archived not deleted):
- archived 34 files >800k (BC clones + kickstart ckpts) to models/archive-kickstart/
- kickstart DISABLED in train_supervisor.sh (gated behind run/USE_KICKSTART;
  machinery intact in train_headless_ppo.py)
- resumed PLAIN PPO from ppo_headless_800000 (last pre-BC tip), now with the
  three clean new levers it never had: HP-25 (dense kills), corrected
  gravity -20 in the void predictor (was 9.81 -> agent thought it had 2x
  longer before void impact), n_epochs 10 + target_kl 0.02 (sample reuse).
- VecNormalize unfrozen (trains normally; no anchor to protect).

OPEN STRATEGIC QUESTION for Miles: BC from a scripted teacher COLLAPSES on a
fixed-spawn map (movement becomes a constant). To make imitation work it
needs spawn randomization OR a flat/edgeless map for the movement+combat
skill (your "learn movement first" intuition). If pure-PPO-from-800k +
HP-25 re-plateaus at ~0.08, the next move is that pivot — flagging rather
than doing it unprompted since it's a bigger redo + needs fresh demos.

## 2026-06-13 02:45 — spatial diversity via slot-swap (Miles' idea)

Added `randomize_slot` to SFHeadlessEnv: each episode the policy drives a
random slot (0 or 1); they spawn at different ~mirrored points, so the
agent sees both start positions / facing directions instead of always
marching the same way off the same edge (the fixed-spawn collapse that
broke BC and caps PPO). Obs is ego-relative so one policy keyed on dz/dy
produces opposite global movement — the state-conditioned navigation we
want. Pure stock spawns, NO teleport (honors the 1:1 rule). Enabled in the
trainer (make_env randomize_slot=True); eval stays fixed-slot for
comparable gates. Live on the pure-PPO-from-800k + HP-25 run. First
post-revert tick already showed fell 0.23->0.15.

## 2026-06-13 05:00 — PLATEAU BROKEN: win 0.13-0.18 (slot-swap + HP-25)

First real breakout in the project. After reverting BC and running pure
PPO from the pre-BC 800k tip with HP-25 + corrected gravity + slot-swap
diversity, win climbed off the long ~0.08 plateau: rollout win_mean
0.10->0.11->0.13->0.18->0.15 over ~870-872k, with fell holding 0.07-0.10
(at/below the 0.1 gate) and arms/hits up (0.36-0.38 / 0.29-0.36). The
slot-swap (policy drives a random spawn each episode) was the unlock — it
forced state-conditioned navigation the fixed-spawn setup never allowed.
Plan: deterministic eval at ~900k for the true number; if it confirms
~0.15+ win / <0.1 fell sustained, this is the first stage-gate-credible
policy — then consider HP-50 step.

## 2026-06-13 05:58 — DETERMINISTIC EVAL: win ~0.45, fell ~0.05 (the rolling metric was lying)

eval_checkpoint.py (argmax) on the 888k ckpt, dedicated bridge 1349, 20
episodes before the unwatched eval instance hung: **win 0.45, fell 0.05** —
vs the stochastic rollout win_mean of ~0.08. Exploration noise was
undercounting ~5x: the GREEDY policy already wins ~45% vs the HP-25 dummy
and almost never falls. Opposite of the BC clone (worse deterministically,
0.83 falls) — this clean pure-PPO + slot-swap policy is genuinely
competent and just samples sub-optimally in training. 20-ep CI ~±0.22 but
the conclusion is robust.

Gate (deterministic win>=0.8 & fell<=0.1): FELL PASSES (0.05); WIN 0.45 ->
0.8 needed. So keep training at HP-25, let greedy win climb, re-eval
~1.0M+. Do NOT advance HP yet. NOTE for future evals: add the 5th instance
to the watchdog or accept ~20-ep samples (it hung at 20/40 here).

## 2026-06-13 07:45 — 2nd deterministic eval: win PLATEAUED ~0.50 at HP-25

eval #2 (944k ckpt, 20 eps before the unwatched 5th instance hung again):
**win 0.55, fell 0.30**. Combined with eval #1 (888k): win 0.45 -> 0.55
(deterministic win is ~0.50 and roughly flat across 888k->944k, NOT
climbing toward the 0.8 gate). Falls noisy across the two 20-ep samples
(0.05 vs 0.30) — and notably the GREEDY policy falls more than the
stochastic one (training fell ~0.09): argmax marches more decisively
toward targets and occasionally off the edge (mild residual of the
BC-clone failure; slot-swap reduced but didn't eliminate it).

INTERPRETATION: "kill a stationary HP-25 dummy in 30s" looks RNG-capped
near ~0.5 win (when no weapon is reachable + melee positioning fails, the
episode times out as a non-win). The 0.8 stage-0 gate is likely
unreachable on a stationary dummy regardless of more training — this is a
degenerate task. The policy IS competent (fetches gun, fires, ~0.5 kills,
mostly avoids edges).

DECISION POINT (for Miles): stage 0 (dummy) has plateaued. Real options:
(1) graduate to stage 1 — a MOVING/scripted opponent (the actual task) and
treat ~0.5-on-dummy as "stage 0 passed" since 0.8 is RNG-capped; (2) push
the residual greedy falls down first (small fall-penalty bump or stronger
void shaping — reversible); (3) keep grinding HP-25 (diminishing returns).
Leaning (2) then (1). NOT forcing a stage change unilaterally — training
continues at HP-25, watched, pending Miles. Also: add the 5th eval
instance to the watchdog so evals don't cap at ~20 eps.

## 2026-06-13 09:00 — autonomous decision: subagent-reviewed, FALLS-FIX first (data-backed)

Miles is away under the standing directive ("apply each stage, dont stop till
im back... use best judgement and when needed use subagents to review
decisions"). The dummy plateau had sat ~3h, so I exercised that authority
instead of idling the watch loop — with an independent subagent review of the
stage-advance decision (the review the directive calls for).

**Subagent review verdict (general-purpose, read the code):** fix the greedy
falls FIRST, then advance to a moving dummy. Reasons: (a) advancing the
opponent now CONFOUNDS the plateau diagnosis (is 0.50 RNG-capped, or are we
throwing away winnable episodes off the edge?); (b) the fall defect rides into
stage 1 and gets WORSE there (a moving target can bait the agent toward
edges); (c) falls-fix is the cheapest reversible lever. It also KILLED the
"weakened scripted" option: grepped the host — `SFGYM_BOT_AGGRO/REACTION/
AIM_NOISE` do NOT exist in `SFHeadlessHost.DriveScriptedBots` (those belong to
the *other* mod, `mod/StickFightGym/ScriptedBot`). And it confirmed the moving
dummy is env-only + void-safe (per-slot `rays` exist in the snapshot; port the
host's two-band void veto at runtime `SF_VOID_Z=17`, NOT the stale 19 in code
comments).

**Diagnostic baseline (deterministic, 984k tip, 20 eps before the 5th instance
wedged; eval_checkpoint.py now prints arms/hits/len every 5 eps so partial runs
are usable):**
  win 0.50 | fell 0.25 | arms 1.05 | hits 1.05 | len 198
Loss-bucket breakdown that this finally exposed:
  - **win 0.50** robust (eval#1 0.45, #2 0.55, this 0.50) — plateau is real.
  - **fell 0.25** is the TRUE greedy fall rate. eval#1's 0.05 was an unlucky-low
    draw, the first 5 eps here (0.00) a lucky one; it settles ~0.25. 1-in-4
    episodes the argmax policy marches off the void edge.
  - **arms 1.05** — the greedy policy DOES fetch a weapon nearly every episode
    (stochastic arms ~0.23 was just exploration noise, same ~5x understatement
    as win_mean). So 0.50 is NOT "no reachable weapon / RNG-capped" — it's
    falls (0.25) + fail-to-finish (only ~1 damaging tick/ep converts).
  Mechanism: the stationary dummy sits ~3u from the void edge; the agent
  closing to melee/point-blank overshoots into the void. Greedy commits harder
  → overshoots more (greedy fell 0.25 >> stochastic ~0.09).

**Action (single lever, reversible):** `sf_headless_env.py` death penalty
-0.5 → -1.0. The -0.5 was a 2026-06-06 halving to tame fall-spike variance in
the PRE-`target_kl` era; `target_kl=0.02` + `n_epochs=10` now absorb that, so
the stronger "don't die" signal is affordable. At this stage every death IS a
fall (dummy can't kill), so this is exactly a fall penalty; when stage 1 adds a
fighting opponent, SPLIT into fall(-1.0) vs combat-death(-0.5) so the agent
isn't timid about earned deaths. Resume boundary recorded at the deploy below.

**Watch for:** (timidity) deterministic win dropping >0.05 below 0.50 while
fell improves → too strong, try a shaped edge-proximity term instead; (no
help) fell still ~0.2 at the next eval → escalate to edge shaping. Re-eval at
~next checkpoints. If fell drops toward ≤0.1 and win holds/climbs, advance to
the moving dummy (stage 1, env-only `opp_mode="patrol"`, void-safe via the
opp's own rays). Rolling win_mean stays the noisy stochastic score; gate on
deterministic eval only. Infra note: the "two supervisors" flagged in review is
benign — one supervisor (pid 1519342) + its trainer-holding `( )&` subshell
(1527181), confirmed by parent-child ppid.

## 2026-06-13 10:18 — FALLS-FIX BACKFIRED (timidity); reverted + advanced to MOVING DUMMY (stage 1)

The -1.0 fall penalty was a clean, confirmed FAILURE. Post-fix deterministic
eval at 1016k (24k steps post-doubling), 25 eps:
  win 0.04 | fell 0.00 | arms 0.04 | hits 0.04 | len 92   (vs baseline
  984k: win 0.45 | fell 0.22 | arms 0.95 | hits 1.05 | len 198)
Falls went to ZERO — but the greedy policy went fully PASSIVE: it stopped
approaching the dummy, stopped arming, stopped fighting. Tell: deterministic
crashed while the STOCHASTIC rollout held ~baseline (win ~0.08, arms ~0.20),
i.e. the penalty shifted the policy's *argmax* (mode) to the safe "don't
approach" action. Root cause is GEOMETRIC: the stationary dummy spawns ~3u
from the void edge, so "don't die" == "don't approach the dummy." On a
stationary-near-edge target the fall penalty and the kill objective are in
direct tension — falls are NOT fixable by a death penalty here. Important
negative result; don't retry penalty-tuning on the stationary dummy.

ACTIONS (all reversible; -0.5/hold + 992k base intact, timid ckpts archived):
- Reverted death penalty -1.0 → -0.5 (restored the engaging policy).
- Implemented the MOVING DUMMY as `opp_mode="patrol"` (env-only, NO host/C#
  change): the env drives the opp slot with a back-and-forth walk (flip every
  ~1.75s), NEVER firing/aiming. Void-SAFE by a two-band veto on the dummy's
  OWN cached z (hard-correct inward past |z|>14, soft-veto outward past 12.5;
  runtime SF_VOID_Z=17). Unit-tested every band; LIVE snapshots confirm the
  patrolled slots stay |z|≤~14 with the veto actively pushing inward (never
  near the 17 void edge), so the dummy can't self-destruct (which would hand
  free wins). Sign verified vs the env's own obs comment (MoveX>0 → -z).
- Plumbing: `--opp-mode patrol` added to the trainer choices + the supervisor
  launch; `_last_opp_z` cached in `_build_obs`; patrol state in `__init__`.
- Archived the timid 1000k/1008k/1016k checkpoints to models/archive-timid/
  and RESUMED FROM 992k (the last engaging -0.5 policy) — NOT the timid tip,
  so the agent keeps its weapon-fetch/aim skill while adapting to a target
  that now MOVES. Killed+relaunched the supervisor (setsid) so it re-read the
  edited launch line; verified opp_mode=patrol, resume@992k, 1 trainer.

WHY the moving dummy should also fix falls (the original goal): a patrolling
target stays AWAY from the edge (|z|<14), so the agent engages it in safer
central positions — relieving the kill-vs-fall tension that broke both -0.5
(falls 0.22) and -1.0 (timid) on the stationary-near-edge dummy. Watch the
patrol-stage deterministic eval for BOTH win (target ≥0.4, harder than a
stationary dummy so expect a dip-then-climb) and fell. Self-destruct guard:
if rollout win_mean spikes >0.55 (implausible for a moving target), suspect
the dummy is suiciding → check the veto. eval_checkpoint.py still uses
opp_mode="hold"; for a patrol-faithful eval, point it at patrol later.

## 2026-06-13 12:09 — STAGE 1 VALIDATED: moving dummy SOLVES falls while keeping engagement

First patrol-faithful deterministic eval (1032k, ~40k patrol steps,
`run_eval.sh 40 "" patrol`, 20 eps before the 5th instance wedged):
  win 0.35 | **fell 0.000** | arms 0.60 | hits 0.45 | len 153
  (per-5-ep: win 0.40/0.50/0.467/0.35, fell 0.00 EVERY checkpoint)
vs stationary baseline (984k): win 0.45 | fell 0.22 | arms 0.95.

THE HEADLINE: **fell 0.22 → 0.00.** The moving dummy completely eliminated the
self-destruct-off-the-edge problem — and did it while KEEPING engagement (win
0.35 competent, NOT the timid 0.04 the -1.0 penalty produced). This is the
payoff of the whole chain: falls weren't fixable by a death penalty on the
stationary-near-edge dummy (penalty → timidity), but they ARE fixed by a
curriculum/environment change — a target that patrols away from the edge, so
the agent engages in safe central positions. 1:1 with the game (dummy moves via
stock setBotAction; no teleport/clamp). win 0.35 < stationary 0.45 because a
moving target is harder to corner (arms 0.60 vs 0.95) — expected, and win
should climb with more patrol training (was 0.50 at 10 eps; ~0.4±0.15 on 20).

NEXT: keep training patrol (track deterministic win climbing); auto re-eval at
ts 1.085M. When patrol win plateaus/climbs to ~0.55+, advance to STAGE 2 — the
natural env-only step is a MOVING+SHOOTING dummy (extend patrol to aim at the
learner + pulse fire): adds incoming fire (agent must dodge/block) and lets the
opp KILL the agent, so combat-deaths finally appear → THEN split the death
penalty into fall(-1.0) vs combat-death(-0.5). (A "weakened scripted" opp would
need new C# in DriveScriptedBots; moving+shooting is the cheaper rung.) Then →
self-play pool. Falls SOLVED is a real unblock toward the real fighting task.

## 2026-06-13 15:30 — SELF-PLAY built + verified, DEPLOYED, then REVERTED (host round-reset bug)

Jumped straight to self-play (stage 2) instead of building a moving+shooting
dummy — self-play is the real path AND auto-balanced (frozen copy of the bot =
its exact equal → ~50% → dense gradient from any skill). Built via worktree
subagent: `opp_mode="selfplay"` drives the opp slot with a FROZEN PPO snapshot
(obs refactor `_build_obs_for(me,opp)` keeping the learner obs BYTE-IDENTICAL —
unit-tested vs 285de2d; opp obs normalized with the frozen model's own
VecNormalize stats; opp aims at the learner). Reward split fall(-1.0)/
combat-death(-0.5). Offline tests PASS. LIVE sign-test (drove the selfplay env
on instance 8): opp_aim·(opp→learner)=+1.00, learner hp 100→-28 — the frozen
opp aims at + KILLS the learner. Sign correct, end-to-end verified.

DEPLOYED to the fleet (frozen opp pinned via run/SELFPLAY_CKPT=1104k, learner
resumes 1104k → symmetric). **It destabilized the fleet.** With BOTH bots
fighting+dying (hold/patrol NEVER had the opp die — even when the learner
killed the patrol dummy it was a single death, never MUTUAL), the instances
went into a wedged state: rounds advance WITHOUT players respawning (snapshots
show round counter climbing but ents=0 for 9s+), HP overkilled to ~-1900, ts
stalled then regressed (1108k→1106k = crash-resume), ep_len collapsed 153→56,
only 1/4 instances kept players. Critically these wedged instances stay
bridge-RESPONSIVE, so the watchdog (which only checks bridge ping) never
restarts them — they just spin empty.

REVERTED: opp_mode-aware death penalty (hold/patrol = known-good -0.5; the
split only for selfplay/scripted), supervisor → patrol, FULL fleet restart
(fleet.sh stop + fresh start) to clear the wedge. Confirmed recovered: fresh
instances spawn players, patrol trainer resumed the clean 1104k (self-play
saved NO checkpoint — never reached the 1112k save — so nothing corrupted).

ROOT-CAUSE HYPOTHESIS (needs host investigation): the headless round-reset/
respawn doesn't handle BOTH bots dying (mutual kill / both fall) and/or the
rapid round cycling of fast HP-25 duels — the respawn (CreatePlayer/Revive)
doesn't fire, leaving empty rounds. Self-play CODE stays in tree, SHELVED.
NEXT: investigate SFHeadlessHost round-end/respawn for the both-die case (does
opp_mode="scripted", also a both-can-die fighting opp, hit the same wedge? —
that localizes it to the host vs the selfplay env code). Fix the host respawn,
THEN re-deploy self-play. A fighting opponent is the critical path to beating
#1; the round-reset is the blocker. Ops gotcha (hit twice today): a bash
command whose OWN cmdline contains an unbracketed process pattern
(train_supervisor.sh / watchdog.sh / train_headless_ppo.py) self-matches its
own pgrep/pkill and kills itself — use bracketed `[t]` patterns AND exclude $$.

## 2026-06-13 16:03 — HOST ROUND-RESET FIXED → SELF-PLAY LIVE (fighting opponent)

Subagent root-caused the both-die wedge: the headless host's `AdvanceRound` is
re-entrant — it fires on EACH death with no survivor gate (stock SF only ends a
round at ≤1 survivor), so death #2 in a duel restarts the ~12.5s respawn chain
from zero → round counter climbs, players never respawn, corpses overkill to
-1900 (`TestProjectileHit` didn't skip dead rigs). Localization: it's the HOST,
not the selfplay env (the selfplay commit was Python-only; it just created the
both-die CONDITION for the first time — `opp_mode="scripted"` was NEVER actually
run on this host since the fleet uses SFGYM_RL_SLOTS=0,1).

FIX (SFHeadlessHost.cs, 4 coordinated changes, built 0-warn/0-err): (a)
`_roundAdvanceInFlight` latch makes AdvanceRound non-re-entrant (TrySchedule
early-returns while held; cleared when AutoSpawnBots completes) — the core fix;
(b) survivor-count gate in TickAuthRigDeathCheck (advance only ≤1 alive),
single-death path unchanged; (c) TestProjectileHit skips dead rigs (no more
-1900); (d) play-gate cleared only when RoundMinPlaySec≤0. Deployed with the
working DLL backed up (SFHeadlessHost.dll.bak-working-*).

**LIVE-VALIDATED:** re-deployed self-play on the fix DLL — fleet now SUSTAINS
rounds (4/4 bridges keep players, normal round cadence r3→r4 with ents cycling
2↔0 on clean transitions) vs the prior empty-round wedge. Trainer resumes 1104k
vs frozen 1104k opp (symmetric ~50%), opp_mode=selfplay, reward split active
(fall -1.0 / combat-death -0.5). Memory: si/so=0 (no thrash) but free tighter
(~1.7GB; 4 frozen-opp models) — WATCH; drop to 3 instances if it thrashes.

Pushed f13a79f. SELF-PLAY IS THE REAL FIGHTING OPPONENT — first time the bot
trains against something that shoots back. NEXT: babysit (rounds-sustain +
memory + ts), periodic deterministic eval vs selfplay, then league-refresh v2
(update run/SELFPLAY_CKPT to a newer snapshot + restart once the learner
reliably beats the frozen opp) so it climbs past one snapshot toward superhuman.
Also flagged today (teammate): box "OOM-restarting" — investigated, NOT OOM (no
kills, si/so≈0, sparse trainer restarts); it's Proton instance flapping
(tolerable, self-recovers) + moderate mem pressure (relieved). dmesg is
restricted (dmesg_restrict=1) so kernel-OOM unconfirmable directly.

## 2026-06-13 17:24 — SELF-PLAY CPU TUNING: 2 instances is the sweet spot

Self-play's frozen-opp inference is CPU-heavy (~3 cores per worker: torch
PPO.predict every step IN the SubprocVecEnv worker). At 4 instances load hit 28
(over 24 cores); at 3 it crept to 24+ (1-min 32-37) AND triggered a vicious
cycle (CPU starvation → instances flap → watchdog restarts → DXVK shader
recompiles spike CPU → more flap). Dropped to **2 instances**: load settled to
~12-16, RAM 8GB free, **fps DOUBLED 5→10** (at 3-4 the box was so contended
every env crawled — fewer envs = higher AGGREGATE throughput), and wd_restarts
went FLAT (flapping stopped). So on this 24-core box (shared w/ idle Docker
media), **self-play = 2 instances** is both healthier and faster than 3-4.
Memory was never the issue (si/so=0 throughout); it was pure CPU oversubscription
from the opp inference. sf_watch.sh got an SF_N knob (instance count) so the
bridge/DEGRADED checks match the fleet size. FUTURE: to scale instances back up,
cheapen the opp (predict every ~3 steps, reuse action + fresh aim — realistic
~6.7Hz reaction, ~3x less CPU); deferred (2 instances is fine for now). The
README "Run" still shows `fleet.sh start 4` — for SELF-PLAY use 2.

## 2026-06-13 19:08 — FIRST SELF-PLAY EVAL: healthy (greedy win 0.35 vs frozen, training stable)

The rollout win_mean looked alarming in self-play (declined 0.15→0.02, fell
rose to 0.41) — but that's MISLEADING. A deterministic eval (1168k learner vs
frozen 1104k opp, 20 eps): **win 0.35 / fell 0.20 / arms 0.70 / hits 0.50.** The
GREEDY learner is a competent fighter (~35% vs the frozen snapshot, arms+hits
fine) — slightly behind the fixed frozen policy, exactly as expected this early
(learner is being perturbed by training; it'll climb and eventually beat the
frozen opp → league-refresh). PPO metrics confirm HEALTHY training: approx_kl
0.002-0.008 (<<0.02 target), entropy_loss STABLE ~-1.8 (moderate exploration,
not collapsing/exploding), explained_variance 0.4-0.69 (critic learning),
ep_rew_mean POSITIVE ~1.2-2.0. The huge greedy(0.35)-vs-stochastic(0.02) gap is
just the stable ~1.8 entropy — the SAMPLED policy spreads actions so it flails
(low stochastic win, high stochastic fell), while argmax is competent.

LESSON: **in self-play the rollout win_mean is a poor metric (entropy-depressed)
— gate on DETERMINISTIC eval + ep_rew_mean, NOT stochastic win.** The sf_watch
"win/fell/selfdestruct" fields read low/scary under self-play; that's expected,
not a fault. NO intervention — self-play is learning fine. NEXT: periodic
deterministic eval vs frozen (track greedy win climbing 0.35→...); LEAGUE-REFRESH
(overwrite run/SELFPLAY_CKPT + restart) when greedy win vs frozen sustains
~>0.65 (learner reliably beats the snapshot). Falls (greedy 0.20) acceptable for
now; if they don't drop as it learns edge-aware dodging, add edge-proximity
shaping. Box: 2 instances, load ~15, healthy.

## 2026-06-13 22:23 — SELF-PLAY EVAL #2 (1240k, ~133k steps): falls SOLVED, win FLAT 0.35

Eval #2 vs frozen 1104k (20 eps): win 0.35 / **fell 0.05** / arms 0.35 / hits
0.50 / len 119. vs eval #1 (1168k): win 0.35 / fell 0.20 / arms 0.70 / len 170.
- **Falls SOLVED** (0.20→0.05): the learner developed edge-aware dodging through
  training — the -1.0 fall penalty worked WITHOUT timidity (because on a moving/
  fighting opp it can dodge inward, unlike the stationary-near-edge dummy). Good.
- **Win FLAT 0.35** (not climbing) + **arms halved** (0.70→0.35) + shorter eps:
  the learner traded "arm more, sometimes fall" for "arm less, never fall" — net
  win unchanged, and notably <0.5 vs a near-snapshot of itself (started ~symmetric
  by construction, now ~0.35). So self-play is HEALTHY (real sub-skill gained, PPO
  stable, ep_rew positive) but NOT yet producing a win gain vs the frozen opp —
  possibly a cautious low-arming equilibrium, or just needs many more steps (130k
  is little for self-play). The lower arming may be the shooting-opp making
  weapon-fetches risky (exposes the learner to fire) — the +0.15 arm bonus is
  outweighed by fall/combat risk.
DECISION: continue self-play; re-eval at ts ~1.33M (~3.5h, +90k steps). If win
is STILL ~0.35 (no climb), add LEAGUE DIVERSITY — a pool of past frozen snapshots
sampled as opponents (PFSP) instead of one fixed opp — the standard fix for
single-opponent self-play stagnation (the league-pool task #26 machinery from an
earlier engine is the reference; needs porting to the headless selfplay env: load
N frozen ckpts, sample one per episode). Also reconsider the arm incentive then.
NOT intervening yet (2 noisy 20-ep evals at ~0.35 ≠ conclusive stagnation; falls-
solved shows the learner CAN improve sub-skills). gate = deterministic eval +
ep_rew_mean, NOT rollout win_mean (entropy-depressed in self-play).

## 2026-06-14 01:25 — flap diagnosis (autonomous, no intervention)
Investigated 3 single-bridge flaps in ~20 min during self-play (ts ~1.315M).
Root cause is NOT a new fault — it's the known 2-instance steady state:
- `oracle{0,1}-plugin.log` grow ~1MB/min from STOCK SF `Debug.Log`
  ("Writing GroundWeapon With Pos ... sync index N"), amplified by weapons=HIGH
  + self-play. Watchdog truncates at ~100MB/min so it's bounded; disk steady 87%.
- Watchdog correctly restarts each hung instance within its 2-strike window
  ("unresponsive 2 cycles (hung)"); ts keeps climbing at normal cadence;
  trainer self-recovers BrokenPipe via supervisor.
- 148 NullReferenceException/min in oracle0 are CAUGHT (Warning-level via
  `EnsurePreSpawned` try/catch, mono TargetInvocationException path) and have
  coexisted with healthy training for 200k+ steps.
Decision: keep babysitting; do NOT fleet-restart to raise log level or rebuild
host mid-run (costs more than the flaps; eval uses separate bridge 1349).
FOLLOW-UP (daylight, low-pri): trace the `EnsurePreSpawned` NullRef and consider
raising BepInEx LogLevel to drop the GroundWeapon spew (needs fleet restart).
Escalate only if: wd_restarts >+5/tick, both bridges down simultaneously, or FROZEN2.

## 2026-06-14 02:24 — SELF-PLAY BREAKTHROUGH + LEAGUE-REFRESH #1 (autonomous)
**Deterministic eval at ts~1.332M (ckpt 1327996) vs frozen opp 1104000: WIN 0.800
(16/20)**, fell 0.150, arms/ep 1.15, hits/ep 1.60, len 146. Prior evals were
0.35 @1.168M and 0.35 @1.240M — the "stagnant at 0.35" read was WRONG; self-play
just needed more steps. The learner now decisively beats the frozen 1104k opp,
arming reliably (1.15/ep) and landing 1.6 hits/ep. 0.80 ≫ the 0.65 league-refresh
gate, so refreshed:
- `run/SELFPLAY_CKPT`: 1104000 → **1327996** (old saved to `.prev` for a
  regression/forgetting check). Learner resumes from latest (1335996) → faces a
  near-mirror ~50% matchup → dense gradient again.
- Mechanism: supervisor re-reads run/SELFPLAY_CKPT + re-exports SF_SELFPLAY_CKPT
  on every trainer (re)launch (line 65), so refresh = edit file + kill trainer
  only (NO supervisor restart; fleet untouched). Supervisor relaunches ≤60s.
- **GOTCHA (cost ~2 rekills to nail):** after the kill, `train.log` (append mode,
  shared) showed `[selfplay] loaded frozen opponent: 1104000` even though the LIVE
  trainer+workers' `/proc/PID/environ` said 1327996. Those 1104000 lines were
  STALE buffered output from the killed trainer's dying workers, interleaving into
  the log right before the new trainer's resume line during a bridge-1342 flap.
  Verify a refresh by (a) `/proc/PID/environ` of the live trainer AND its workers
  (`pgrep -P`), and (b) reading ONLY post-kill bytes (`tail -c +OFFSET`), NOT a
  raw `tail`. Clean confirmation restart showed both workers loaded 1327996. ✓
- Post-refresh expectation: rollout win DROPS (stronger opp) — NOT a regression.
  Next eval gate ts 1.49M: eval vs BOTH 1327996 (gate; refresh again if >0.65)
  and 1104000 (forgetting check; should still win ≥0.7). 1104000 path in
  run/SELFPLAY_CKPT.prev.

## 2026-06-14 09:12 — LEAGUE-REFRESH #2 + PFSP POOL deployed (autonomous)
**Two-opponent deterministic eval at ts~1.49M (learner ckpt 1487996):**
- vs CURRENT frozen opp 1327996 (gate): WIN 0.650 (13/20), fell 0.15, arms 1.10, hits 1.10
- vs OLD opp 1104000 (forgetting check): WIN 0.700 (14/20), fell 0.10, arms 0.90, hits 1.30

Read: 0.65 passed the refresh gate (healthy tightening vs the near-mirror, 0.80->0.65
across refreshes = approaching equilibrium). BUT vs 1104k the learner dropped 0.80
(@ckpt1327996) -> 0.70 (@ckpt1487996) = the single-opp ladder's NON-TRANSITIVE
forgetting, the pre-committed trigger for a pool.

Actions:
1. **League-refresh #2**: run/SELFPLAY_CKPT 1327996 -> 1487996 (verified live via
   worker environ; the train.log "loaded 1327996" lines were dying-worker
   interleave again).
2. **PFSP POOL built + deployed** (supersedes #2, includes 1487996 so no regression):
   - sf_headless_env.py: _load_selfplay_opponent now builds self._opp_pool from
     a multi-entry SF_SELFPLAY_CKPT (or SF_SELFPLAY_POOL); _load_one_opp factored
     out (per-opp model+vecnorm); _select_opp() samples one per episode in reset()
     (FICTITIOUS SELF-PLAY). Single-entry = single-opp (backward compatible).
     Hot path (_opp_action/_normalize_opp_obs) UNCHANGED.
   - Deploy with NO supervisor change: wrote 3 paths into run/SELFPLAY_CKPT (the
     var the supervisor already exports) + trainer-only restart. Pool =
     {1104000, 1327996, 1487996}. (run/SELFPLAY_CKPT.single_prepool = revert point.)
   - Validated: python/pool_smoke.py (3 tests: single backward-compat, multi-entry
     ckpt overload, explicit pool — all pass) + LIVE confirm (trainer+2 workers all
     have 3-path SF_SELFPLAY_CKPT via Python NUL-split /proc/environ read).
**VERIFICATION LESSON:** train.log is BLOCK-BUFFERED (not a TTY) — post-restart
`[selfplay]`/`POOL mode` lines lag minutes, and dying-trainer buffers flush late and
interleave. The reliable refresh/pool verification is the PROCESS ENVIRON via Python
NUL-split (`open('/proc/PID/environ','rb').read().split(b'\0')`), NOT the log and NOT
`grep -z` (misfires on multi-line values).
NEXT: eval at ts~1.54M vs ALL 3 pool members — expect forgetting FIXED (beats
1104k/1327k again) + competitive vs 1487k. Grow the league (add newer snapshots) as
the learner climbs. Pool runs at 2 instances; 3 models/worker ~+300MB RAM (fine).

## 2026-06-14 11:16 — first POOL eval (ts~1.54M, ~40k post-deploy) — INCONCLUSIVE, keep going
Deterministic 20-ep evals, latest learner vs pool extremes:
- vs OLD 1104000 (forgetting check): WIN 0.60 (12/20), fell 0.10, arms 0.70, hits 1.10
  — BIMODAL (1.0 first 10 eps, ~0.3 last 10); ckpt 1535996.
- vs strongest 1487996 (competitive): WIN 0.50 (10/20), fell 0.00, arms 0.55, hits 0.50
  — clean ~50% equilibrium vs strongest past self; ckpt 1543996.
Read: NOT the hoped "forgetting fixed -> 0.80 vs old". Instead a robust generalist
(~0.5-0.6 across the spectrum, no catastrophic loss to anyone = what league play is)
BUT arming ~halved (pre-pool 1.10/0.90 -> 0.70/0.55), likely caution learned vs the
strong 1487k generalizing to all opps. Only ~40k steps post-pool-deploy (too early)
+ noisy (20 eps, bimodal, A/B used different ckpts). DECISION: keep the pool, +110k
steps, re-eval CLEANLY at ts~1.65M vs all 3 (one ckpt). If arming stays suppressed
AND win-vs-1104k hasn't recovered by then -> intervene: PFSP weighting (sample
competitive opps more / weak less) or trim strong-opp frequency; possibly a small
arming-reward nudge. NOT reverting on 40k of noisy data. Pool itself is healthy
(falls ~0, ts climbing, load settled ~13-16, double-wedge at 10:09 self-healed).

## 2026-06-14 16:20 — POOL CAUSED DETERMINISTIC COLLAPSE -> REVERTED to single-opp
Re-eval at ts~1.65M (~150k post-pool) vs all 3 pool members: WIN 0.00 / 0.00 / 0.00,
arms ~0.1, hits ~0.0. The argmax policy became a PASSIVE NO-OP (never arms, never hits).
A/B disambiguation (same opp 1104000, identical contended conditions) CONFIRMED it's
real, not an eval artifact:
  - known-good 1487996: win 0.55, arms 0.70, hits 0.95  (eval infra sound)
  - current   1655996: win 0.00, arms 0.00, hits 0.00  (collapsed)
ROOT CAUSE (hypothesis): equal-weight FSP pool with the near-mirror STRONG opp (1487k)
1/3 of episodes + the dense survival-shaped reward made "don't engage" locally optimal
for the ARGMAX -> mode collapse to passivity. Rollout (stochastic) still explored combat
(hits ~0.3), MASKING the collapse in win_mean -> only the DETERMINISTIC eval caught it.
At 1.54M (40k post-pool) it was still 0.60/0.50 w/ arms halving (the early warning);
by 1.65M it was total. REVERTED:
  - run/SELFPLAY_CKPT -> SINGLE 1327996 (multi-entry->single auto-disables pool mode)
  - archived 22 collapsed ckpts (1495996..1663996) -> models/archive-pool-collapse/
  - trainer resumes from healthy 1487996 (now latest); ts rewound 1.66M->1.489M and
    progressing; single-opp 1327996 (learner has ~0.65 edge -> rebuilds/keeps aggression).
  - verified live via /proc/PID/environ (npaths=1) + resume-from-1487996 + ts=1489020.
Pool CODE retained (gated behind multi-entry SELFPLAY_CKPT) but DO NOT re-enable without:
(a) PFSP weighting (down-weight opps the learner loses to, so the strong near-mirror
doesn't dominate), and/or (b) kill-weighted reward so passivity isn't safe, and/or
(c) exclude the near-mirror until the learner is strong enough. LESSON: equal-weight FSP
+ survival reward => passive-argmax collapse; ALWAYS gate on deterministic eval (rollout
win hides argmax mode-collapse); eval within ~40k of any opponent/curriculum change.
NEXT: single-opp ladder resumes (the proven-good path: refresh #1 hit 0.80, #2 0.65);
recovery-confirm eval at ts~1.52M, then continue the ladder.

## 2026-06-14 17:43 — REVERT RECOVERY CONFIRMED (single-opp healthy)
Recovery-confirm det eval (learner 1519996, ~32k single-opp steps post-revert) vs
training opp 1327996: WIN 0.65 (13/20), arms 0.90, hits 1.25, fell 0.25. Fully
COMBATIVE again (vs the pool-collapse 0.00/0.00/0.00) — the revert to single-opp +
resume-from-1487996 worked. Back on the proven ladder (matches refresh-#2-era 0.65).
Plan: continue single-opp vs 1327996; re-eval at ts~1.58M; refresh the frozen opp to
a recent snapshot only if solidly >0.65 (avoid borderline churn post-disaster). Pool
stays shelved pending PFSP weighting + kill-weighted reward. (3 double-wedges today
[10:09/15:55/17:30] all watchdog-self-healed in ~2-3min; ~1 per 2.5hr, tolerable;
root-cause [oracle plugin-log GC spew?] is a daylight follow-up.)

## 2026-06-14 20:10 — LEAGUE-REFRESH #3 (post-revert ladder healthy, hit 0.80)
After the pool revert, the single-opp ladder recovered fast: det eval at ts~1.58M
(learner 1575996) vs frozen 1327996 = WIN 0.80 (16/20), arms 1.10, hits 1.75 (best
of session), fell 0.15 — up from the 0.65 recovery eval @1.52M. 0.80 >> 0.65 gate ->
refresh #3: run/SELFPLAY_CKPT 1327996 -> 1575996 (.prev=1327996). Learner resumes
~1583k vs near-self -> ~50%. Verified live via worker /proc/environ (npaths=1,
1575996; the buffered log's "resuming 1487996"/"loaded 1327996" lines are stale).
NEXT ladder-decision eval ts~1.64M; refresh again when it reliably clears 0.65.
Pool stays shelved (needs PFSP weighting). Infra steady: ~4 double-wedges today all
watchdog-self-healed; single-bridge flaps routine.

## 2026-06-14 22:20 — refresh #4 gate: NO refresh (0.60 < 0.65), keep training
Det eval (learner 1639996) vs frozen 1575996: WIN 0.60 (12/20), arms 1.10, hits 0.95,
fell 0.30. Below the 0.65 refresh threshold -> KEEP training vs 1575996 (matchup tightened
vs the strong near-self, as expected near self-play equilibrium; policy healthy/combative
but not yet dominant). WATCH fell 0.30 (up from 0.15 @1.58M — likely aggressive
edge-exposure vs the strong opp); re-eval ts~1.70M; add edge-shaping only if fell keeps
rising while win stalls. Rollout has been strong this stage (win 0.14-0.23, arms 0.33-0.40).

## 2026-06-15 00:36 — LEAGUE-REFRESH #4 (ladder climbing steadily)
Refresh #4 decision eval (learner 1695996) vs frozen 1575996: WIN 0.75 (15/20),
arms 0.90, hits 1.30, fell 0.20 (the 0.30 spike @1.64M resolved through training —
no edge-shaping needed). 0.75 >> 0.65 -> refresh #4: run/SELFPLAY_CKPT 1575996 ->
1695996 (.prev=1575996). Learner resumes ~1702k vs near-self. Verified live via
worker /proc/environ (trainer 1561910 npaths=1 -> 1695996, 2 workers).
Ladder so far (all post-pool-revert, single-opp): #1 ->1327996 (0.80), #2 ->1487996
(0.65), #3 ->1575996 (0.80), #4 ->1695996 (0.75). Cadence: eval ~60k, refresh when
det win >0.65. NEXT gate ts~1.76M. Infra: 6 double-wedges today, all watchdog-healed.

## 2026-06-15 02:58 — LEAGUE-REFRESH #5 (opp -> 1759996)
Refresh #5 decision eval (learner 1759996) vs frozen 1695996: WIN 0.65 (13/20),
arms 1.15, hits 1.50, fell 0.05. The post-refresh-#4 rollout-falls spike (peaked ~0.42)
was EXPLORATION noise — deterministic fell is 0.05 (barely falls); the policy dominates
combat (1.5 hits/ep) and converts 0.65 in a high-variance near-mirror. 0.65 meets the
gate -> refresh #5: run/SELFPLAY_CKPT 1695996 -> 1759996 (.prev=1695996). Verified live
via worker /proc/environ (trainer 279221 npaths=1 -> 1759996, 2 workers).
Ladder (post-revert, single-opp): #1->1327996(0.80), #2->1487996(0.65), #3->1575996(0.80),
#4->1695996(0.75), #5->1759996(0.65). Matchups tightening (~0.65-0.80) as the frozen opp
strengthens each rung = healthy self-play; absolute skill climbing. NEXT gate ts~1.82M.

## 2026-06-15 06:05 — LADDER REGRESSION at refresh-#6 gate -> ROLLBACK to 1759996
First non-improving stage after 5 clean refreshes. Refresh-#6 decision eval (learner
1815996) vs frozen 1759996: WIN 0.30 (6/20) — the learner started this stage AS
1759996 and trained DOWN to 0.30 vs it. Regression-check vs the WEAK opp 1104000
(every prior rung beat it >=0.55): WIN 0.35 (7/20), fell 0.30 -> ABSOLUTE regression,
not matchup-specific. Smoking gun = FALLS: deterministic fell 0.05->0.30 across BOTH
opponents; the policy drifted into a fall-prone, reward-FARMING state (ep_rew_mean
ROSE 1.5->3.0 while win-ability + falls degraded). PPO metrics healthy (kl 0.005-0.015,
entropy -1.6/-1.8, value_loss normal) — NOT a training crash; it's REWARD/WIN
DIVERGENCE at high skill (dense shaping farmable without winning).
ACTION: rolled back — archived 8 regressed ckpts (1767996..1823996) ->
models/archive-regress-1.82M/; learner resumes from 1759996 vs frozen 1759996 (mirror
restart). Verified trainer 4020235 npaths=1 -> 1759996, clean "loaded 1759996" log.
**Did NOT increase the fall penalty** — memory records a prior fall-penalty bump
BACKFIRED into passive collapse (win 0.04). Evidence-driven plan (mirrors pool revert):
resume clean, re-eval at ts~1.81M vs 1759996 + 1104000.
  - If recovered (fell<=0.15, vs1104k>=0.7) -> stochastic drift, continue ladder.
  - If RE-REGRESSES (fell up, win down again) -> SYSTEMATIC reward misalignment; fix by
    REBALANCING reward toward kills/wins (down-weight the farmable chip-damage/armed-
    trickle terms) — NOT by raising the fall penalty. Possibly cap the ladder here
    (~1759996 is a strong policy: 0.65 vs 1695996, fell 0.05) + harden reward before
    pushing further.

## 2026-06-15 08:25 — RECOVERY CONFIRMED (regression was stochastic, not reward)
Recovery re-eval (learner 1807996, re-trained ~48k from 1759996): vs mirror 1759996
WIN 0.60 (was 0.30 regressed), vs weak 1104000 WIN 0.60 (was 0.35), fell 0.20-0.25
(was 0.30), arms ~1.1 hits ~1.2 (was 0.75/0.65). Combat restored, pulling ahead of
the mirror again. KEY: same 1759996 start + SAME reward, a fresh trajectory recovered
to 0.60 -> the refresh-#6 regression was a BAD STOCHASTIC TRAJECTORY, not systematic
reward misalignment (a broken reward would have re-regressed this trajectory too). So
NO reward rebalance needed; the rollback fixed it. fell 0.20-0.25 slightly elevated vs
1759996's 0.05 — WATCH it. DECISION: continue ladder; 0.60 vs mirror is recovered but
<0.65 refresh bar (like refresh-#4's first gate 0.60->0.75), keep training vs 1759996,
re-eval ts~1.86M, refresh #6 when >0.65. Lesson reinforced: a single regression eval
warranted ROLLBACK (stop bleeding) + RE-EVAL (not a blind reward change) — the re-eval
distinguished stochastic drift from systematic, saving an unneeded reward rework.

## 2026-06-15 11:40 — ⚠ EVAL-GATE BUG (all ladder evals were vs a DUMMY) → switched to SCRIPTED stage
**The single most important finding of this run.** `scripts/run_eval.sh` parsed its
3rd arg (`OPP_MODE`) but **never forwarded `--opp-mode` to `eval_checkpoint.py`**,
which defaults to `opp_mode="hold"` (a STATIONARY dummy). So for the ENTIRE run,
every "deterministic win vs frozen opp" number — incl. the 0.65–0.80 that drove all
5 ladder refreshes, the refresh-#6 "regression," and the "recovery to 0.60" above —
was actually **win vs a stationary hold-dummy**, NOT the frozen opponent. Confirmed 3
ways: run_eval.sh line 50 had no `--opp-mode`; eval_checkpoint default = `hold`; and
NO eval log ever contains the env's `[selfplay] loaded opponent` line. The historical
ladder lineage (1104000→…→1759996) is therefore **NOT a verified strength ladder** —
it was selected on dummy-kill rate, orthogonal to head-to-head skill.

FIX (committed): run_eval.sh now forwards `--opp-mode "$OPP"`, defaults to `selfplay`,
and HARD-FAILS if a selfplay eval can't resolve the frozen opp (never silently a dummy
again). `launch_oracle.sh` got caller-env-wins precedence (capture overrides before
sourcing fleet.env) so a scripted/selfplay eval can set `SFGYM_RL_SLOTS` without the
persisted fleet config clobbering it.

FIRST-EVER true mirror evals (deterministic learner vs frozen opp driving slot 1,
HP25/Ice11, the ACTUAL training conditions):
- learner 1863996 vs frozen 1759996: WIN **0.10**, fell **0.45**, arms 0.05, hits 0.50, len 48
- SELF-MIRROR 1759996 vs ITSELF: WIN **0.15**, fell 0.20, arms 0.05, hits 0.30, len 56
KEY: a policy vs ITSELF scores only ~0.15, not 0.50 — the harness is asymmetric
(evaluated side deterministic/argmax, opp sampled; single fixed slot, no swap; random
spawn; kill-AND-survive win def; falls+draws compress both sides). So **~0.15 is the
"even match" baseline** and the old 0.65 refresh bar was UNREACHABLE on any fair mirror
(which is why refreshes only ever fired off the dummy mirage). The learner is at best
~even with its frozen ancestor. And the rollout win_mean (~0.07–0.10) AGREED with the
mirror all along — it was NEVER ~5× entropy-understated as believed; the "0.80 self-play
breakthrough" was a dummy artifact.

ROBUST diagnosis (consistent across both mirror evals AND training rollouts): in mutual
play the bots **barely fight** (hits ~0.5/ep), **almost never arm** (~0.05/ep vs 1.15
vs a dummy), and episodes end fast (~56 steps) dominated by FALLS. Self-play vs frozen
copies of itself can't fix this — both sides share the deficiency (a copy that doesn't
grab guns never punishes you for not grabbing guns). Two subagent reviews concurred.

DECISION — switch training to the SCRIPTED stage (the planned-but-skipped
dummy→patrol→**scripted**→selfplay rung; `opp_mode=scripted` was NEVER run here because
the fleet always used `SFGYM_RL_SLOTS=0,1`). Decisive scripted-bot eval (learner vs the
in-plugin scripted bot, a FIXED competent opponent): WIN 0.08, fell **0.04**, arms 0.12,
hits 0.17, len 74 — wins little, BUT falls far less (0.04 vs 0.20–0.45) and arms more
(0.12–0.20 vs 0.05) and fights longer than vs a self-copy → healthier dynamic + a
non-zero gradient to climb. Switched the fleet+trainer to scripted:
- `train_supervisor.sh`: flag-toggleable opp (`OPP_MODE`/`FLEET_RL_SLOTS`); default
  scripted (`SFGYM_RL_SLOTS=0`, slot1=scripted bot); **`touch run/USE_SELFPLAY` reverts**.
- `train_headless_ppo.py` make_env: `randomize_slot=(opp_mode!="scripted")` — scripted
  needs the learner pinned to slot 0 (host drives slot 1).
- Backups for rollback: `run/fleet.env.selfplay-bak`, `run/SELFPLAY_CKPT.selfplay-bak`
  (frozen 1759996). Selfplay checkpoints ≤1879996 preserved.
- `.training_paused` set (OpenClaw stand-down) for the duration.
LIVE-VERIFIED 11:49: resumed from 1879996, ts advancing (1881020→1882044), both rigs
spawn (slot0 RL learner / slot1 scripted via TakeLocalControl), ep_len **88.8** (vs ~56
selfplay — more sustained), win 0.07 rollout, fps 10. 1 supervisor + 1 watchdog + 1
trainer + 2 bridges.

DEVIATION + WATCH: the documented curriculum used a **weakened** scripted bot at **HP
100**; this is running at the current stage's **HP 25 + full-strength** bot (minimal
deviation from the live setup — didn't guess tuning values, the user's domain). If the
learner stays crushed (~0.08, no climb) over the next hours, WEAKEN via `SFGYM_BOT_AGGRO`
(default 0.7) / raise `SFGYM_BOT_REACTION`, and/or move toward HP 100.

TODO (eval-gate methodology, per subagent): the gate is now wired correctly but still
asymmetric. Make it interpretable (even=~0.50): both sides STOCHASTIC, balanced
SLOT-SWAP, a small FIXED map set, +secondary low-variance metrics (opp_died−self_died,
end-HP-diff). Self-mirror must then read ~0.50. Refresh bar → ~0.58 (drop 0.65).

## 2026-06-15 14:55 — SCRIPTED stage also PLATEAUS (no climb in ~90k steps) → bottleneck is reward/difficulty
Three deterministic scripted evals over ~90k steps of scripted training:
- 1879996 (pre-scripted tip): win 0.083, arms 0.12, hits 0.17
- 1919996 (+40k): win 0.067, arms 0.13, hits 0.07
- 1967996 (+90k): win 0.050, arms 0.10, hits 0.10
WIN is flat-to-slightly-down (all within ±0.18 CI at n=20) and **arms is FLAT ~0.10–0.13
(NOT collapsing)** — so it's a PLATEAU, not the pool-style passive collapse. Falls stay
~0.00–0.04 (the scripted stage DID kill the self-play fall problem — good). Rollout
briefly looked like decay (arms 0.14→0.05) but that was exploration noise; the
deterministic policy is stable. So a fixed competent opponent does NOT break the plateau
either. **CONCLUSION: both self-play (stuck mutual-fall basin) and full-strength scripted
(stable plateau ~0.05–0.08 win) fail to climb — the bottleneck is NOT which opponent, it's
the REWARD DESIGN + opponent DIFFICULTY (the user's tuning domain), and/or policy capacity.**
DECISION: did NOT revert (within-CI evidence doesn't justify a disruptive fleet restart;
deterministic policy is stable, not collapsing; best ckpt 1879996 is preserved on disk
regardless). KEPT scripted running (low falls + arms~0.10 = behaviorally healthy hold
state), lowered eval cadence, will revert to selfplay@1879996 ONLY if a clear DETERMINISTIC
collapse (arms→0) appears. **For the user (tuning domain) — options to break the plateau,
in rough order of leverage:** (1) REBALANCE reward toward decisive wins — the survival/chip
terms make "don't engage a bot you can't beat" locally optimal; down-weight armed-trickle
+ per-tick chip, up-weight terminal KILL, so passivity isn't safe. (2) WEAKEN the scripted
bot — but the headless `DriveScriptedBots` has NO `SFGYM_BOT_AGGRO/REACTION` knob (those
are in `mod/StickFightGym/ScriptedBot`), so this needs new C# (a difficulty scalar on the
host bot). (3) HP curriculum — the documented plan had scripted at HP100 with a *weakened*
bot; current is HP25 full-strength. (4) Build the corrected slot-swap eval gate first so
any change is measured against an interpretable (even=0.5) number, not the asymmetric ~0.15
harness. The eval-gate FIX + this diagnosis are the night's real deliverables; the policy
itself is plateaued pending a tuning decision.

## 2026-06-15 19:10 — scripted DETERIORATED to argmax passive collapse → REVERTED to selfplay@1879996
Continued scripted past the plateau to watch for a late climb. Deterministic scripted evals:
1879996→0.083 (arms .12), 1967996→0.050 (arms .10), **2095996→0.000 (arms .00, hits .00,
ALL 20 eps fully passive, ep_len 72)**. So the argmax policy COLLAPSED to doing nothing —
the arms trend .12→.10→.00 is monotone to zero. Classic argmax mode-collapse: ROLLOUT
(stochastic) still showed win ~0.10 / hits ~0.13 (exploration noise) the whole time, totally
MASKING the deterministic collapse — exactly the trap from the pool-collapse (2026-06-14) and
why we gate on DETERMINISTIC eval. Mechanism: full-strength scripted bot is unbeatable for
this policy, so under the survival-weighted reward "do nothing / don't engage" is the locally
optimal argmax action; PPO slid the greedy policy there over ~215k steps while ep_rew looked
fine. REVERTED (this is the pre-stated revert trigger = clear deterministic arms→0): 
- `touch run/USE_SELFPLAY`; restored `run/SELFPLAY_CKPT`→1759996; archived **27** collapsed
  scripted ckpts (1887996..2103996) → `models/archive-scripted-collapse/`; latest is again the
  pre-scripted **1879996**.
- Full clean teardown (kill by PID — supervisor+subshell+watchdog+trainer, then fleet; no
  pkill-self-match, no foreground sleep) + relaunch. LIVE-VERIFIED 19:18: opp_mode=selfplay,
  resumed from 1879996, frozen opp 1759996 loaded, 2 bridges, supervisor+watchdog up.
NET for the night: the scripted experiment ANSWERED its question — a fixed competent opponent
does NOT break the plateau; at full strength it actively collapses the policy. Back on the
stable selfplay state @1879996 (mirror ~0.10, harness even-baseline ~0.15), best policy
preserved. **The bottleneck is confirmed REWARD/DIFFICULTY (user tuning domain): both
self-play (stuck) and full-scripted (collapses) fail. Highest-leverage next (for the user):
rebalance reward so passivity isn't safe (down-weight survival/armed-trickle/chip, up-weight
terminal KILL) — that's the common root of BOTH the scripted collapse AND the self-play
no-arm basin — and/or a *weakened* scripted bot (needs new C# AGGRO knob in the headless
DriveScriptedBots) at HP100, gated by the corrected slot-swap eval.** Lesson reinforced: a
fixed-but-too-hard opponent + survival reward is a passivity trap just like equal-weight FSP;
gate on DETERMINISTIC eval (rollout masked a full collapse for ~120k steps here).

## 2026-06-15 20:25 — implemented ALL THREE plateau-breakers (reward + weakened bot + gate) → SCRIPTED@AGGRO0.4
Per Miles ("do 1-3 all of them"), implemented + deployed the three levers from the 19:10 analysis:

**#1 Reward rebalance (sf_headless_env.py) — make passivity unsafe, kills dominate:**
- REMOVED the `+0.0005/tick` armed-trickle — it literally paid the agent to CAMP holding a
  gun, the direct incentive behind both the scripted argmax collapse and the self-play no-arm
  basin. Kept the one-time `+0.15` acquisition bonus.
- KILL up-weighted: base 1.0→2.0, fast-kill 0.5→1.0 (a win is now +2.0..+3.0). Vs a strong
  opp the dense HP-trade nets negative while fighting, so the only way "engage to win" beats
  "do nothing" is a terminal kill big enough to overcome it. Pairs with a BEATABLE opp.
- Chip 0.05→0.03 (down-weight the farmable per-tick floor). No per-step idle penalty (the
  known suicide trap).

**#2 Weakened scripted bot (SFHeadlessHost.cs DriveScriptedBots) — a BEATABLE opponent:**
- Added `SFGYM_BOT_AGGRO` (0..1, <1 throttles fire+approach), `SFGYM_BOT_AIM_NOISE` (rad,
  rotates aim → misses), `SFGYM_BOT_REACTION` (s, re-serves last frame → laggy). Defaults
  (1.0/0/0) are EXACT no-ops; applied AFTER the RlControlledSlots guard so they touch ONLY
  the scripted opp slot (never the learner or self-play); void-veto stays outside gating
  (hazard safety preserved). Built clean (0 err), DLL backed up + deployed. Subagent-drafted,
  reviewed + applied by hand. Persisted via fleet.sh (writes the knobs into run/fleet.env)
  so watchdog restarts keep them. train_supervisor sets AGGRO=0.4/AIM=0.3/REACT=0.15 for
  opp_mode=scripted (override by exporting the vars).

**#3 Corrected eval gate (eval_checkpoint.py + run_eval.sh + env info) — interpretable:**
- `--slot-swap` (half eps each slot, averaged) removes the slot-0/1 spawn bias; stochastic by
  default (both sides sample, matching training); selfplay-only swap. Added low-variance
  secondary metrics to env info (`opp_dead`,`self_dead`,`hp_diff`) → eval prints
  `score = opp_died - self_died` (symmetric → 0 for a self-mirror) which moves long before the
  noisy win. run_eval: selfplay→GATE(stochastic,slot-swap), other modes→deterministic
  diagnostic; `SF_EVAL_DET=1` forces argmax (collapse check). CALIBRATION CAVEAT: a fall-prone
  policy DRAWS a lot (both fall) so even slot-swapped WIN compresses well below 0.5 (self-mirror
  win ~0.10, not 0.50) — the SCORE metric is the clean ~0-centered one. (The eval instance still
  wedges ~10-25 eps; score is in the per-5-ep partial so a wedged run still yields it.)

**NEW TRAINING (live 20:26):** opp_mode=scripted, WEAKENED bot (Config-log verified
`BotAggro=0.40 BotReaction=0.15s BotAimNoise=0.30`) on slot 1, NEW reward, resumed from the
clean best **1879996**, HP25/Ice11. Goal: a beatable opponent + win-dominant reward should
finally produce a measurable WIN climb (break passivity). REVERT to self-play: `touch
run/USE_SELFPLAY` + restart. NEXT: babysit; eval vs the weakened bot (`run_eval.sh 20 "" scripted`
→ deterministic diagnostic + score) ~every 60-90k steps — if win climbs, ramp AGGRO toward 1.0
(curriculum) then →HP100 →selfplay; if it collapses again, the reward needs more work. Once a
candidate clearly beats the frozen selfplay opp on the corrected `--slot-swap` gate (score>0),
resume the selfplay ladder with the fixed gate.

## 2026-06-16 00:21 — PLATEAU-BREAKERS WORK: greedy policy escaped passivity (arms 0.55, win 0.15 vs weakened bot)
Restart note: the whole stack got killed ~00:0x (system event — was healthy at win 0.22/ts 1944956);
cleaned orphan launch_oracle/xvfb-run wrappers + relaunched supervisor+watchdog, resumed latest
(1943996, win 0.22 intact). Then the first DETERMINISTIC eval of the new config (1943996 vs the
WEAKENED bot @AGGRO0.4): **win 0.15 (3/20), arms 0.55, hits 0.40, fell 0.15, score -0.60 (opp_died
0.20 / self_died 0.80), len 116.** vs full-strength scripted's COLLAPSE (0.00 win / 0.00 arms) this
is a decisive turnaround: the new reward (no camping-trickle + KILL→+2..3) + a BEATABLE opponent
broke the passivity/no-arm basin — the GREEDY policy now arms ~every other episode (0.55 vs the
basin's 0.05) and wins 15%. Rollout win climbed 0.11→0.22 over ~60k steps, falls 0.22→0.05.
`score -0.60` = AGGRO0.4 still wins the matchup ~80/20, so it's a LEARNABLE, not-yet-mastered rung.
(end-HP-diff -399 = overkill artifact; ignore, use SCORE.) PLAN: keep training @AGGRO0.4 (still
climbing); re-eval @2.05M; ramp AGGRO→0.6→0.8→1.0 once the learner DOMINATES (score→0+ / win up),
then →HP100 →selfplay (gated by the corrected `--slot-swap` eval). If it stalls at -0.6, weaken to
AGGRO~0.25-0.3 to let it master an easier rung first. The night's full chain — gate-bug fix →
plateau diagnosis → 3 plateau-breakers → escape — is now empirically validated.

## 2026-06-16 09:30 — double-instance hang root-caused: orphan Xvfb leak → watchdog now reaps them
While babysitting AGGRO-0.6 toward the 2.25M gate, the watch flagged BOTH bridges
DOWN at 09:26 (vs the usual single flap). Diagnosis: NOT a code crash —
- Both StickFight procs alive + both ports listening; the games were HUNG (oracle1
  plugin-log frozen, bridge no-reply). Watchdog correctly 2-struck both and restarted
  (inst 0 @09:27:43, inst 1 @09:28:02); trainer froze at ts 2248700 during the double
  restart, then RESUMED to 2249724 once bridges returned. No data loss.
- **Root cause** (oracle1-combined.log): `XIO: fatal IO error 110 (Connection timed out)
  on X server :189` — the instance's Xvfb display died, breaking the game's X conn and
  triggering a ~100MB/min error-loop spew (the watchdog was truncating oracle1-plugin.log
  every minute). WHY the Xvfb died: **leaked orphan Xvfb servers**. Found 6 stale Xvfb
  (displays :132/:137/:143/:149/:161/:174, ages 1.5–6h, all reparented to ppid==1, none
  used by a live game) — xvfb-run's SIGKILL teardown on each watchdog restart doesn't
  reliably reap its Xvfb child, so they pile up (~570MB + fd pressure over 6h / 2559
  restarts) and live X servers start timing out.
- **Fix**: killed the 6 orphans by PID + cleaned their stale /tmp/.X*-lock; added a
  periodic **orphan-Xvfb sweep** to watchdog.sh's main loop (each 60s cycle: kill any
  Xvfb with ppid==1 — a live instance's Xvfb always has a live xvfb-run parent, so
  ppid==1 is a definitive orphan; display-agnostic, no instance→display map needed).
  Restarted the watchdog to load it. README watchdog note updated.
