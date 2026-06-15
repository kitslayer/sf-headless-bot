"""Gymnasium environment over the SFHeadlessHost JSON bridge.

Drives one agent slot inside a running headless Stick Fight instance via the
loopback UDP bridge (no external game client). The opponent slot can be the
in-plugin scripted bot (RL-vs-scripted) or a second policy (self-play, by
running two envs on slots 0 and 1 of the same instance).

The headless host must be launched with the RL slot(s) excluded from the
scripted driver, e.g.:

    SFGYM_BOT_SLOTS=0,1 SFGYM_RL_SLOTS=0 bash scripts/launch_oracle.sh 0

Observation (Box, float32): self + opponent kinematics/state + relative geometry.
Action (MultiDiscrete [3,2,2]): move{left,none,right} x jump{0,1} x fire{0,1}.
Reward: (opp_dmg_dealt - self_dmg_taken)/100 per step, +1 on kill, -1 on death.
Episode ends when the round advances (someone died / stall timeout) or max_steps.
"""
from __future__ import annotations

import glob
import json
import os
import re
import socket
import time

import numpy as np

try:
    import gymnasium as gym
    from gymnasium import spaces
except ImportError:  # fall back to classic gym
    import gym
    from gym import spaces


# --- Void / edge sense (2026-06-07) -----------------------------------------
# Ported from the scripted bot's terrain awareness (docs/BOT_SENSING.md +
# mod ScriptedBotMaster.WaypointInVoid / SimulateVoidTime). The RL agent's obs
# was blind to map geometry, so it kept walking off Desert3's edges and could
# never beat even a stationary dummy. These give it the SAME edge/floor sense
# the scripted bot uses. SF playable box (Desert3 / general kill thresholds):
# kill floor at y<-11.5, horizontal edges at |z|>19.
def _fleet_cfg(name: str, default: float) -> float:
    """Map-geometry config: process env wins, then run/fleet.env (the fleet's
    single source of truth, same pattern as SF_TIMESCALE), then default."""
    v = os.environ.get(name)
    if v:
        try:
            return float(v)
        except ValueError:
            pass
    try:
        path = os.path.join(os.path.dirname(__file__), "..", "run", "fleet.env")
        with open(path) as f:
            for line in f:
                line = line.strip()
                if line.startswith(f"export {name}="):
                    val = line.split("=", 1)[1].strip()
                    if val:
                        return float(val)
    except (OSError, ValueError):
        pass
    return default


_VOID_Y = _fleet_cfg("SF_VOID_Y", -11.5)   # kill-floor Y (Desert3 default)
_VOID_Z = _fleet_cfg("SF_VOID_Z", 19.0)    # horizontal edge |z| (Desert3 default)
_VOID_SPAN = 2.0 * _VOID_Z
# SF's actual Physics.gravity is -20, NOT Unity's -9.81 default (verified by
# Miles against the real TimeManager/ProjectSettings dump, 2026-06-12 — the
# old docs were wrong; physics also ticks at 60Hz not 50). With 9.81 this
# predictor told the agent it had ~2x longer before void impact than reality.
_G = 20.0
_VOID_HORIZON = 1.2      # seconds for the predictive look-ahead


def _ballistic_void_time(z, y, vz, vy, horizon=_VOID_HORIZON):
    """Faithful port of ScriptedBotMaster.SimulateVoidTime: integrate pos+vel
    under gravity (dt=0.05) and return seconds until the point leaves the
    playable box (y<_VOID_Y or |z|>_VOID_Z), else `horizon`. Used for the
    airborne/knocked-off case."""
    dt = 0.05
    t = 0.0
    while t < horizon:
        vy -= _G * dt
        y += vy * dt
        z += vz * dt
        if y < _VOID_Y or abs(z) > _VOID_Z:
            return t
        t += dt
    return horizon


class SFHeadlessEnv(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, bridge_port: int = 1341, my_slot: int = 0, opp_slot: int = 1,
                 poll_hz: float = 20.0, max_steps: int = 600,
                 reset_timeout: float = 30.0, opp_mode: str = "hold",
                 randomize_slot: bool = False):
        super().__init__()
        self.addr = ("127.0.0.1", bridge_port)
        self.my_slot = my_slot
        self.opp_slot = opp_slot
        # Slot swap (2026-06-13, Miles): on a fixed-spawn map the learner always
        # starting at slot 0's spawn collapses movement to a near-constant
        # action (always march the same way → off the same edge — this is what
        # broke BC and caps PPO). Slots 0 and 1 spawn at different, ~mirrored
        # points, so randomizing which slot the policy drives each episode makes
        # it experience both start positions / facing directions. The obs is
        # ego-relative (dz/dy to opp, rays from self) so the SAME policy keyed on
        # relative state produces the opposite global movement — exactly the
        # state-conditioned navigation we want. Pure stock spawns, no teleport.
        self.randomize_slot = randomize_slot
        # opp_mode: "hold"  -> env pins the opponent slot stationary (curriculum
        #                      stage 0: learn to approach + attack a dummy).
        #           "scripted" -> leave the opponent to the in-plugin scripted
        #                      bot (do NOT free its slot via SFGYM_RL_SLOTS).
        # When "hold", launch with SFGYM_RL_SLOTS=<my_slot>,<opp_slot> so the
        # scripted driver yields both slots to us.
        self.opp_mode = opp_mode
        # Moving-dummy (opp_mode=="patrol") state: phase counter, current walk
        # direction (+1/-1), the dummy's last-seen z (for the void veto), and
        # the direction-flip cadence (~1.75s at 20Hz poll).
        self._opp_phase = 0
        self._opp_dir = 1
        self._last_opp_z = 0.0
        self._patrol_period = 35
        self.dt = 1.0 / poll_hz
        self.max_steps = max_steps
        self.reset_timeout = reset_timeout

        # Two sockets: one request/reply socket for snapshots, and a separate
        # fire-and-forget socket for actions. The bridge acks setBotAction; if
        # actions and snapshots shared a socket, a snapshot recv could grab a
        # stale action-ack (no ents/round) and the agent would look dead.
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.5)
        self.asock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.asock.bind(("127.0.0.1", 0))

        # obs: self(19)+opp(19)+rel(4)+opp-pred(2)+void(4)+rayfan(16)+proj(8)
        #      +weapons(6) = 78
        # self/opp 19 = kinematics(6)+hp/alive/armed(3)+state(10: grounded,
        # ragdolled, swinging, blocking, jump-cd, wall-cd, sinceShot, ammo, aim z/y)
        # rayfan = 16 YZ-plane world-distance rays; proj = 2 nearest projectiles
        # weapons = 2 nearest ground weapons rel to self (dz/20, dy/10, present)
        high = np.full(78, np.inf, dtype=np.float32)
        self.observation_space = spaces.Box(-high, high, dtype=np.float32)
        self.action_space = spaces.MultiDiscrete([3, 2, 2])

        self._steps = 0
        self._round = None
        self._prev_self_hp = 100.0
        self._prev_opp_hp = 100.0
        self._prev_armed = False   # pickup-shaping edge detector
        self._arm_count = 0        # pickups this episode (telemetry)
        self._hit_ticks = 0        # damaging ticks this episode (telemetry)
        # Normalized aim-at-opponent direction, refreshed from each snapshot in
        # _build_obs and sent with every action (aimx==world-z 1:1, verified).
        self._aim_dz = 1.0
        self._aim_dy = 0.0
        # Most recent snapshot the LEARNER obs was built from. _opp_action
        # (selfplay) uses it so the frozen opponent reacts to the same frame the
        # learner just saw — same cache-driven pattern as the patrol void-veto.
        self._last_snap = None

        # --- selfplay frozen opponent -------------------------------------
        # opp_mode=="selfplay": a FROZEN PPO snapshot drives the opp slot. The
        # learner's obs is normalized OUTSIDE the env by the trainer's
        # VecNormalize wrapper, so the frozen opp (trained on normalized obs)
        # needs its raw obs normalized the SAME way IN HERE before predict.
        self._opp_model = None
        self._opp_obs_mean = None
        self._opp_obs_var = None
        self._opp_obs_eps = 1e-8
        self._opp_obs_clip = 50.0
        # League pool: list of loaded opponents {model,mean,var,eps,clip,name}.
        # Single-opp = a 1-element pool. reset() samples one per episode when >1.
        self._opp_pool = []
        self._opp_name = ""
        if self.opp_mode == "selfplay":
            self._load_selfplay_opponent()

    # ---- selfplay opponent loading ----
    def _load_selfplay_opponent(self):
        """Populate self._opp_pool with one or more frozen PPO opponents, then
        activate one via _select_opp().

        - POOL (>1 path in SF_SELFPLAY_POOL, OR a multi-entry SF_SELFPLAY_CKPT —
          comma/space/newline-separated). Each path loads with its matching
          VecNormalize stats and reset() samples one per episode (FICTITIOUS
          SELF-PLAY) so the learner must beat the DISTRIBUTION of past selves —
          fixes the single-opp ladder's non-transitive forgetting (eval B
          0.80->0.70 vs old opp). Overloading SF_SELFPLAY_CKPT means the pool
          deploys via run/SELFPLAY_CKPT + a trainer restart, NO supervisor change.
        - SINGLE (default, backward compatible): a single-path SF_SELFPLAY_CKPT,
          else the latest models/ppo_headless_<N>_steps.zip.
        If nothing loads, the opp slot stays STATIONARY (like opp_mode='hold')."""
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
        models_dir = os.path.join(repo_root, "models")
        # Pool source: explicit SF_SELFPLAY_POOL, else a MULTI-entry
        # SF_SELFPLAY_CKPT. Overloading SF_SELFPLAY_CKPT lets the existing
        # supervisor (already exports it from run/SELFPLAY_CKPT) switch to pool
        # mode with NO supervisor change — just write several paths into that
        # file. A single-entry value stays single-opp (backward compatible).
        spec = (os.environ.get("SF_SELFPLAY_POOL", "").strip()
                or os.environ.get("SF_SELFPLAY_CKPT", "").strip())
        paths = [q for q in re.split(r"[,\s]+", spec) if q] if spec else []
        if len(paths) > 1:
            for p in paths:
                if not os.path.isabs(p):
                    p = os.path.join(repo_root, p)
                opp = self._load_one_opp(p)
                if opp is not None:
                    self._opp_pool.append(opp)
            if self._opp_pool:
                print(f"[selfplay] POOL mode: {len(self._opp_pool)} opponents "
                      f"{[o['name'] for o in self._opp_pool]} (uniform/episode)")
            else:
                print("[selfplay] WARNING: pool spec set but none loaded; "
                      "falling back to latest.")
        if not self._opp_pool:
            ckpt = paths[0] if paths else ""
            if ckpt and not os.path.isabs(ckpt):
                ckpt = os.path.join(repo_root, ckpt)
            if not ckpt:
                # latest models/ppo_headless_<N>_steps.zip (eval_checkpoint parity)
                best_n, best = -1, None
                for z in glob.glob(os.path.join(models_dir, "ppo_headless_*_steps.zip")):
                    m = re.search(r"ppo_headless_(\d+)_steps\.zip$", z)
                    if m and int(m.group(1)) > best_n:
                        best_n, best = int(m.group(1)), z
                ckpt = best
            opp = self._load_one_opp(ckpt, allow_vn_override=True) if ckpt else None
            if opp is not None:
                self._opp_pool = [opp]
        if not self._opp_pool:
            print(f"[selfplay] WARNING: no opponent checkpoint found "
                  f"(spec={spec!r}, models={models_dir}). Falling back to "
                  f"STATIONARY opp (behaves like opp_mode='hold').")
        self._select_opp()

    def _load_one_opp(self, ckpt, allow_vn_override=False):
        """Load one frozen PPO + its matching VecNormalize stats. Returns a dict
        {model, mean, var, eps, clip, name} or None if the ckpt is missing. The
        vecnorm pkl is unpickled DIRECTLY (not VecNormalize.load, which calls
        set_venv() and needs a live venv) to read obs_rms.mean/var + eps + clip."""
        from stable_baselines3 import PPO  # local import: only needed for selfplay
        if not ckpt or not os.path.exists(ckpt):
            print(f"[selfplay] WARNING: opponent checkpoint missing: {ckpt!r}")
            return None
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
        models_dir = os.path.join(repo_root, "models")
        model = PPO.load(ckpt, device="cpu")
        mean = var = None
        eps, clip = 1e-8, 50.0
        # VecNormalize: env override applies to SINGLE-opp only (each pool member
        # uses its OWN step-matched pkl).
        vn_path = os.environ.get("SF_SELFPLAY_VECNORM", "") if allow_vn_override else ""
        if vn_path and not os.path.isabs(vn_path):
            vn_path = os.path.join(repo_root, vn_path)
        if not vn_path:
            m = re.search(r"_(\d+)_steps\.zip$", ckpt)
            if m:
                vn_path = os.path.join(
                    models_dir, f"ppo_headless_vecnormalize_{m.group(1)}_steps.pkl")
        if vn_path and os.path.exists(vn_path):
            import pickle
            with open(vn_path, "rb") as f:
                vn = pickle.load(f)
            mean = np.asarray(vn.obs_rms.mean, dtype=np.float32)
            var = np.asarray(vn.obs_rms.var, dtype=np.float32)
            eps = float(vn.epsilon)
            clip = float(vn.clip_obs)
            print(f"[selfplay] loaded opponent {os.path.basename(ckpt)} + vecnorm "
                  f"{os.path.basename(vn_path)} (clip={clip}, eps={eps})")
        else:
            print(f"[selfplay] loaded opponent {os.path.basename(ckpt)} WARNING "
                  f"no vecnorm ({vn_path!r}); feeding RAW obs (may act weakly).")
        return {"model": model, "mean": mean, "var": var, "eps": eps,
                "clip": clip, "name": os.path.basename(ckpt)}

    def _select_opp(self):
        """Activate one pool opponent as the current frozen opp. Uniform sample
        (fictitious self-play); single-opp pools are a no-op. np.random is
        per-process-seeded by SB3 so workers sample independently each episode."""
        if not self._opp_pool:
            self._opp_model = None
            return
        opp = (self._opp_pool[0] if len(self._opp_pool) == 1
               else self._opp_pool[int(np.random.randint(len(self._opp_pool)))])
        self._opp_model = opp["model"]
        self._opp_obs_mean = opp["mean"]
        self._opp_obs_var = opp["var"]
        self._opp_obs_eps = opp["eps"]
        self._opp_obs_clip = opp["clip"]
        self._opp_name = opp["name"]

    def _normalize_opp_obs(self, obs):
        """Normalize the opponent's raw obs with its frozen VecNormalize stats,
        exactly mirroring SB3 VecNormalize.normalize_obs. Identity if no stats
        were loaded (warned at construction)."""
        if self._opp_obs_mean is None:
            return obs
        return np.clip(
            (obs - self._opp_obs_mean) / np.sqrt(self._opp_obs_var + self._opp_obs_eps),
            -self._opp_obs_clip, self._opp_obs_clip,
        ).astype(np.float32)

    # ---- bridge I/O ----
    def _rpc(self, obj, wait=0.4):
        try:
            self.sock.sendto(json.dumps(obj).encode(), self.addr)
            self.sock.settimeout(wait)
            data, _ = self.sock.recvfrom(65535)
            return json.loads(data.decode(errors="replace"))
        except (socket.timeout, ValueError, OSError):
            return None

    def _send_nowait(self, obj):
        # Fire-and-forget on the action socket (acks are ignored, never read).
        try:
            self.asock.sendto(json.dumps(obj).encode(), self.addr)
        except OSError:
            pass

    def _snapshot(self):
        return self._rpc({"cmd": "snapshot"})

    def _hold_opp(self):
        # Drive the opponent slot for curriculum stages where the ENV (not the
        # in-host scripted bot) controls it.
        #   "hold"     -> stationary dummy (stage 0).
        #   "patrol"   -> MOVING dummy (stage 1): walks back and forth, NEVER
        #                 fires or aims at us. Void-SAFE by construction — a
        #                 two-band veto on the dummy's OWN z keeps |z| < ~14 so
        #                 it can't self-destruct (a fallen opp = opp_dead = a
        #                 FREE win that would corrupt win_mean).
        #   "selfplay" -> a FROZEN PPO snapshot drives the opp (real opponent,
        #                 aims at + fights the learner). See _opp_action.
        if self.opp_mode == "selfplay":
            self._opp_action(self._last_snap)
        elif self.opp_mode == "hold":
            self._send_nowait({"cmd": "setBotAction", "slot": self.opp_slot,
                               "mx": 0.0, "my": 0.0, "aimx": 1.0, "aimy": 0.0, "buttons": 0})
        elif self.opp_mode == "patrol":
            self._opp_phase += 1
            if self._opp_phase >= self._patrol_period:
                self._opp_phase = 0
                self._opp_dir = -self._opp_dir
            mx = float(self._opp_dir)
            # Sign convention (env obs comment ~ln 258 + CLAUDE.md): MoveX>0 ->
            # Right() -> -z, i.e. mx>0 DECREASES z (toward the -z void edge),
            # mx<0 increases z (toward +z edge). Two-band veto on the dummy's
            # cached z (runtime SF_VOID_Z, default 17): hard-correct inward
            # past |z|>14, soft-veto further-outward past |z|>12.5.
            z = self._last_opp_z
            hard = _VOID_Z - 3.0
            soft = _VOID_Z - 4.5
            if z < -hard:
                mx = -1.0            # near -z edge -> move +z
            elif z > hard:
                mx = 1.0             # near +z edge -> move -z
            elif z < -soft and mx > 0:
                mx = 0.0             # in -z soft band, don't head further -z
            elif z > soft and mx < 0:
                mx = 0.0             # in +z soft band, don't head further +z
            self._send_nowait({"cmd": "setBotAction", "slot": self.opp_slot,
                               "mx": mx, "my": 0.0, "aimx": 1.0, "aimy": 0.0, "buttons": 0})

    def _opp_action(self, snap):
        """opp_mode=="selfplay": a frozen PPO snapshot drives the OPPONENT slot.
        Mirrors _send_action exactly, but for self.opp_slot and with the opp
        aiming at the LEARNER. v1 = STATIC (loaded once at construction).

        TODO(selfplay refresh, trainer-side): for league/auto-refresh, the
        trainer should periodically (a) save the current policy to a snapshot
        zip + vecnormalize pkl, then (b) tell each subproc env to reload its
        frozen opponent (e.g. a `setSelfplayCkpt` env method invoked via
        VecEnv.env_method, or simply re-`_load_selfplay_opponent()`), sampling
        from a pool of past snapshots for stability. Nothing in THIS file needs
        to change for that beyond a public reload hook. Left out of v1.
        """
        # If the model failed to load (no checkpoint), behave like a stationary
        # dummy so training still proceeds with a clear warning already printed.
        if self._opp_model is None:
            self._send_nowait({"cmd": "setBotAction", "slot": self.opp_slot,
                               "mx": 0.0, "my": 0.0, "aimx": 1.0, "aimy": 0.0,
                               "buttons": 0})
            return
        # Build the opp's obs from ITS perspective (me=opp_slot, opp=my_slot),
        # normalize with the frozen stats, sample an action.
        obs, op_me, op_op = self._build_obs_for(snap, self.opp_slot, self.my_slot)
        obs_n = self._normalize_opp_obs(obs)
        action, _ = self._opp_model.predict(obs_n, deterministic=False)
        move, jump, fire = int(action[0]), int(action[1]), int(action[2])
        mx = (-1.0 if move == 0 else (1.0 if move == 2 else 0.0))
        buttons = (1 if jump else 0) | (2 if fire else 0)
        # Aim at the LEARNER. Same convention as _send_action: world aim
        # z = -AimX, y = +AimY, so to point from the opp at the learner we send
        # aimx = -dz, aimy = dy where (dz,dy) is the normalized direction
        # opp->learner. op_me = opp ent (shooter), op_op = learner ent (target).
        aimx, aimy = 1.0, 0.0
        if op_me is not None and op_op is not None:
            dz = float(op_op.get("z", 0.0)) - float(op_me.get("z", 0.0))
            dy = float(op_op.get("y", 0.0)) - float(op_me.get("y", 0.0))
            mag = (dz * dz + dy * dy) ** 0.5
            if mag > 1e-3:
                aimx, aimy = -dz / mag, dy / mag
        self._send_nowait({"cmd": "setBotAction", "slot": self.opp_slot,
                           "mx": mx, "my": 0.0, "aimx": aimx, "aimy": aimy,
                           "buttons": buttons})

    def _send_action(self, action):
        move, jump, fire = int(action[0]), int(action[1]), int(action[2])
        mx = (-1.0 if move == 0 else (1.0 if move == 2 else 0.0))
        buttons = (1 if jump else 0) | (2 if fire else 0)
        # AUTO-AIM AT THE OPPONENT (2026-06-09 fix). The old heuristic
        # (aimx=-mx while moving, +1.0 idle) never aimed at the target; worse,
        # keyboard-typed rigs had ALL aim input overridden every frame by stock
        # UserAim()'s RotateTowardsMouse (xvfb mouse = meaningless) — fixed by
        # a batchmode skip-patch in the host. With that patch, stock applies
        # LookRotation(0, AimY, -AimX): world aim z = -AimX, y = +AimY
        # (Controller.cs:474). So to aim at the opponent: aimx = -dz, aimy = dy
        # (normalized direction cached from the previous snapshot).
        aimx, aimy = -self._aim_dz, self._aim_dy
        self._send_nowait({"cmd": "setBotAction", "slot": self.my_slot,
                           "mx": mx, "my": 0.0, "aimx": aimx, "aimy": aimy,
                           "buttons": buttons})

    # ---- obs construction ----
    @staticmethod
    def _ent(snap, slot):
        if not snap:
            return None
        for e in snap.get("ents", []):
            if e.get("slot") == slot:
                return e
        return None

    def _build_obs(self, snap):
        # Learner-perspective obs. Thin wrapper over _build_obs_for so the
        # learner's observation is byte-identical to the pre-selfplay code.
        return self._build_obs_for(snap, self.my_slot, self.opp_slot)

    def _build_obs_for(self, snap, me_slot, opp_slot):
        # Build the 78-dim ego-relative obs from `me_slot`'s perspective, with
        # `opp_slot` as the opponent. Used both for the LEARNER (me=my_slot) and,
        # under opp_mode="selfplay", for the FROZEN opponent (me=opp_slot). The
        # math is slot-symmetric; only the cached per-learner state (aim
        # direction, last opp z, last snapshot) is updated, and ONLY when this
        # call is for the learner — guarded by `is_learner` so the opponent's
        # obs build can never perturb the learner's _send_action aim or the
        # patrol void-veto.
        is_learner = (me_slot == self.my_slot)
        me = self._ent(snap, me_slot)
        op = self._ent(snap, opp_slot)
        # Cache the opponent's z every LEARNER obs build (reset + step) so the
        # patrol void-veto in _hold_opp always has a fresh position to clamp
        # against. Also stash the whole snap so _opp_action (selfplay) can build
        # the opp obs against the most recent frame without re-polling.
        if is_learner:
            self._last_snap = snap
            if op is not None:
                self._last_opp_z = float(op.get("z", self._last_opp_z))

        def feats(e):
            # 19 per-ent features: kinematics(6) + hp/alive/armed(3) + Tier-2/3
            # state(10): grounded, ragdolled, swinging, blocking, jump-cd,
            # wall-cd, sinceShot, bulletsLeft, aim z/y. Bounded so VecNormalize
            # has sane inputs even before its running stats warm up.
            if e is None:
                return [0.0] * 19
            sj = float(e.get("sj", 9.0)); sw = float(e.get("sw", 9.0))
            ss = float(e.get("ss", 9.0)); bl = float(e.get("bl", -1))
            return [float(e.get("x", 0)), float(e.get("y", 0)), float(e.get("z", 0)),
                    float(e.get("vx", 0)), float(e.get("vy", 0)), float(e.get("vz", 0)),
                    float(e.get("hp", 0)) / 100.0,
                    1.0 if e.get("alive", False) else 0.0,
                    1.0 if e.get("armed", False) else 0.0,
                    1.0 if e.get("grnd", False) else 0.0,
                    1.0 if e.get("rag", False) else 0.0,
                    1.0 if e.get("sws", False) else 0.0,
                    1.0 if e.get("blk", False) else 0.0,
                    min(sj, 1.0), min(sw, 1.0), min(ss, 2.0) / 2.0,
                    max(bl, 0.0) / 30.0,
                    float(e.get("aimz", 0)), float(e.get("aimy", 0))]

        mf, of = feats(me), feats(op)
        if me and op:
            dx = of[0] - mf[0]; dy = of[1] - mf[1]; dz = of[2] - mf[2]
            dist = (dx * dx + dy * dy + dz * dz) ** 0.5
            rel = [dx, dy, dz, dist]
            # opp predicted relative position at 0.3s (velocity lead — Tier-3
            # aim help). of[4]=opp vy, of[5]=opp vz; mf[4/5]=self vy/vz.
            pred = [dz + (of[5] - mf[5]) * 0.3, dy + (of[4] - mf[4]) * 0.3]
            # refresh the LEARNER's auto-aim direction (normalized YZ toward its
            # opponent). Only for the learner's call — the opponent's aim is
            # computed independently in _opp_action, so this cache must never be
            # touched by the opp obs build or _send_action would aim wrong.
            if is_learner:
                mag = (dz * dz + dy * dy) ** 0.5
                if mag > 1e-3:
                    self._aim_dz = dz / mag
                    self._aim_dy = dy / mag
        else:
            rel = [0.0, 0.0, 0.0, 0.0]
            pred = [0.0, 0.0]

        # void/edge sense (ported from the scripted bot) — encodes the map
        # geometry (edges at |z|>19, kill floor y<-11.5) the agent was blind to.
        if me is not None:
            sz = float(me.get("z", 0.0)); sy = float(me.get("y", 0.0))
            svz = float(me.get("vz", 0.0)); svy = float(me.get("vy", 0.0))
            d_negz = min(1.0, max(0.0, sz + _VOID_Z) / _VOID_SPAN)  # margin to -z edge (MoveX>0 walks here)
            d_posz = min(1.0, max(0.0, _VOID_Z - sz) / _VOID_SPAN)  # margin to +z edge
            h_floor = min(1.0, max(0.0, sy - _VOID_Y) / 13.0)       # height above kill floor
            # predictive time-to-void: soonest my current motion leaves the box.
            t_horiz = _VOID_HORIZON
            if svz < -0.1:                                          # drifting toward -z edge
                t_horiz = max(0.0, sz + _VOID_Z) / -svz
            elif svz > 0.1:                                         # drifting toward +z edge
                t_horiz = max(0.0, _VOID_Z - sz) / svz
            t_void = min(t_horiz, _VOID_HORIZON)
            if svy < -1.0:   # actually falling — ballistic sim catches floor/edge crossing
                t_void = min(t_void, _ballistic_void_time(sz, sy, svz, svy))
            t_void = max(0.0, t_void) / _VOID_HORIZON               # 1=safe, ->0 = about to leave box
            void = [d_negz, d_posz, h_floor, t_void]
        else:
            void = [0.0, 0.0, 0.0, 0.0]

        # Tier-2 spatial fan: 16 normalized ray distances around self (1=clear to 20m).
        rays = (me.get("rays") if me else None) or []
        rays = [float(r) for r in rays[:16]] + [1.0] * max(0, 16 - len(rays))

        # Tier-3 threat sense: the 2 nearest in-flight projectiles, relative to
        # self (rel_z, rel_y, dir_z, dir_y). Zeros when none (e.g. stage 0).
        proj_feats = [0.0] * 8
        if me is not None and snap:
            sz = float(me.get("z", 0.0)); sy = float(me.get("y", 0.0))
            scored = []
            for pr in (snap.get("proj") or []):
                if len(pr) >= 4:
                    rz = float(pr[0]) - sz; ry = float(pr[1]) - sy
                    scored.append((rz * rz + ry * ry, rz, ry, float(pr[2]), float(pr[3])))
            scored.sort(key=lambda t: t[0])
            for k, (_, rz, ry, fz, fy) in enumerate(scored[:2]):
                proj_feats[k * 4:k * 4 + 4] = [rz, ry, fz, fy]

        # Ground-weapon sense: 2 nearest pickup-able weapons relative to self,
        # (dz/20, dy/10, present) each. Without these the agent is blind to
        # weapons and only ever arms by chance — win rate was hard-capped by
        # rigs idling unarmed while sky-drops landed elsewhere (2026-06-10).
        wp_feats = [0.0] * 6
        if me is not None and snap:
            sz = float(me.get("z", 0.0)); sy = float(me.get("y", 0.0))
            wscored = []
            for wp in (snap.get("wps") or []):
                if len(wp) >= 2:
                    wdz = float(wp[0]) - sz; wdy = float(wp[1]) - sy
                    wscored.append((wdz * wdz + wdy * wdy, wdz, wdy))
            wscored.sort(key=lambda t: t[0])
            for k, (_, wdz, wdy) in enumerate(wscored[:2]):
                wp_feats[k * 3:k * 3 + 3] = [
                    max(-1.0, min(1.0, wdz / 20.0)),
                    max(-1.0, min(1.0, wdy / 10.0)),
                    1.0,
                ]

        obs = np.array(mf + of + rel + pred + void + rays + proj_feats + wp_feats,
                       dtype=np.float32)
        return obs, me, op

    # ---- gym API ----
    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        # Slot swap: pick which rig the policy drives THIS episode (the other is
        # the held dummy). Slots 0/1 spawn at different points → spatial +
        # facing diversity. Done at the top of reset so all of reset's own
        # hold/obs use the chosen slots.
        if self.randomize_slot:
            self.my_slot = int(self.np_random.integers(0, 2))
            self.opp_slot = 1 - self.my_slot
        # League pool: sample which frozen opponent drives the opp slot THIS
        # episode (fictitious self-play vs the pool — prevents single-opp
        # cycling). No-op for a 1-element pool. Safe here: reset's hold loop
        # sends only neutral holds, not _opp_action, so the swap commits before
        # the first step's _opp_action uses self._opp_model.
        if self.opp_mode == "selfplay" and len(self._opp_pool) > 1:
            self._select_opp()
        # The host auto-cycles rounds on death/stall. We sync the episode to a
        # round boundary: note the current round, then wait until the round
        # NUMBER advances and both bots are present & alive (a freshly-spawned
        # round). This guarantees catching the start window even when the
        # untrained agent dies fast and rounds churn. We drive a neutral hold
        # on our slot while waiting so its SlotInputs stay fresh.
        t0 = time.time()
        start_snap = self._snapshot()
        base_round = start_snap.get("round") if start_snap else None
        while time.time() - t0 < self.reset_timeout:
            self._send_nowait({"cmd": "setBotAction", "slot": self.my_slot,
                               "mx": 0.0, "my": 0.0, "aimx": 1.0, "aimy": 0.0, "buttons": 0})
            self._hold_opp()
            snap = self._snapshot()
            obs, me, op = self._build_obs(snap)
            if snap and snap.get("inFight") and me and op and me["alive"] and op["alive"] \
                    and float(me.get("hp", 0)) > 50 and float(op.get("hp", 0)) > 50:
                cur = snap.get("round")
                # Accept either a new round, or (first reset) any clean frame.
                if base_round is None or cur != base_round or time.time() - t0 > 3.0:
                    self._round = cur
                    self._steps = 0
                    self._prev_self_hp = float(me["hp"])
                    self._prev_opp_hp = float(op["hp"])
                    self._prev_armed = bool(me.get("armed", False))
                    self._arm_count = 0
                    self._hit_ticks = 0
                    return obs, {}
            time.sleep(0.03)
        # Timed out — return whatever we have so training doesn't deadlock.
        snap = self._snapshot()
        obs, me, op = self._build_obs(snap)
        self._round = snap.get("round") if snap else None
        self._steps = 0
        self._prev_self_hp = float(me["hp"]) if me else 100.0
        self._prev_opp_hp = float(op["hp"]) if op else 100.0
        self._prev_armed = bool(me.get("armed", False)) if me else False
        self._arm_count = 0
        self._hit_ticks = 0
        return obs, {"reset_timeout": True}

    def step(self, action):
        self._send_action(action)
        self._hold_opp()
        time.sleep(self.dt)
        snap = self._snapshot()
        obs, me, op = self._build_obs(snap)
        self._steps += 1

        # Missing ent / timed-out snapshot = carry the previous hp (reward-
        # neutral) instead of 0.0, which fabricated ±1.0 damage swings.
        self_hp = float(me["hp"]) if me else self._prev_self_hp
        opp_hp = float(op["hp"]) if op else self._prev_opp_hp
        # Damage-based shaped reward, kill-biased (2026-06-11): a full day at
        # 100 HP showed win_mean pinned ~0.08 — the kill path (fetch gun, land
        # 3-15 hits, finish) earned barely more than idling safely, so PPO
        # never committed to it. Three changes, all reward-side (game HP
        # untouched per Miles):
        #   * damage DEALT weighs 1.5x damage taken — favors aggressive trades
        #   * +0.05 flat per damaging tick — each landed hit gets a clear
        #     floor even when per-hit HP damage is small (uzi pellets etc.),
        #     keeping the shoot-at-opponent signal dense at 100 HP
        opp_dmg = max(0.0, self._prev_opp_hp - opp_hp)
        self_dmg = max(0.0, self._prev_self_hp - self_hp)
        reward = (1.5 * opp_dmg - self_dmg) / 100.0
        if opp_dmg > 0.0:
            reward += 0.03   # 2026-06-15: 0.05→0.03 — down-weight farmable chip vs the kill
            self._hit_ticks += 1
        self._prev_self_hp = self_hp
        self._prev_opp_hp = opp_hp
        # Pickup shaping: one-time bonus on unarmed→armed. The walk-to-weapon →
        # damage credit chain is seconds long; this densifies it. Not
        # exploitable: re-arming requires having lost the gun (emptied = it
        # auto-drops), which already costs time and forgone damage.
        # 2026-06-11: +0.15 one-time on unarmed→armed. The +0.0005/tick
        # armed-trickle that used to follow was REMOVED 2026-06-15: it paid the
        # agent to CAMP holding a gun, and vs an opponent it can't beat that made
        # "arm + do nothing" locally optimal — a direct contributor to BOTH the
        # scripted argmax passive collapse and the self-play no-arm basin. Keep
        # ONLY the acquisition bonus (arming is the binding constraint); WINNING,
        # not holding, is what pays now (see the up-weighted kill below).
        armed_now = bool(me.get("armed", False)) if me else self._prev_armed
        if armed_now and not self._prev_armed:
            reward += 0.15
            self._arm_count += 1
        self._prev_armed = armed_now

        terminated = False
        cur_round = snap.get("round") if snap else self._round
        # Our y, computed BEFORE the death branches so the death penalty can
        # distinguish a FALL from a combat-death (also reused by the `fell`
        # telemetry below).
        my_y = float(me.get("y", 0.0)) if me else 0.0
        # Death requires the ent to be PRESENT (review fix 2026-06-09): a
        # snapshot timeout (snap=None) or a momentarily missing ent used to
        # zero both hp's and fabricate a loss — or a +1.0 WIN — polluting both
        # the reward and the win_mean stage gate.
        self_dead = me is not None and ((not me["alive"]) or self_hp <= 0)
        opp_dead = op is not None and ((not op["alive"]) or opp_hp <= 0)
        if opp_dead and not self_dead:
            #   * KILL up-weighted 2026-06-15: base 1.0→2.0, fast-kill 0.5→1.0
            #     (a win is now +2.0..+3.0). The win must DOMINATE the per-step
            #     terms: vs a strong opp the dense HP-trade nets NEGATIVE while
            #     fighting, so "engage to win" only beats "do nothing" if the
            #     terminal kill is big enough to overcome that — the core
            #     anti-passivity fix (pairs with a BEATABLE opp; against an
            #     unbeatable one even this can't make engaging positive-EV).
            #     Still NO per-step idle penalty (suicide-cheaper-than-waiting trap).
            reward += 2.0 + 1.0 * max(0.0, 1.0 - self._steps / self.max_steps)
            terminated = True
        elif self_dead:
            # Death penalty history: -1.0 (orig) -> -0.5 (2026-06-06, fall
            # variance) -> -1.0 (2026-06-13, to cut fell 0.25) -> -0.5 AGAIN
            # (2026-06-13). The flat -1.0 BACKFIRED on a near-edge STATIONARY
            # dummy: it collapsed into "don't approach the dummy at all" (falls
            # and the kill objective are geometrically entangled there).
            # Self-play (2026-06-13) decouples them: the opponent can actually
            # KILL us, so a death is now either UNFORCED (we fell into the void —
            # purely our own bad navigation, penalize hard: -1.0) or EARNED (the
            # opponent shot us — a normal combat loss, lighter: -0.5). This
            # split is only meaningful because a selfplay opp can deal lethal
            # damage; against hold/patrol nearly all deaths are falls so it
            # mostly reads -1.0.) 2026-06-13: made opp_mode-AWARE after the
            # self-play deploy was reverted — on hold/patrol (opp can't kill, so
            # every death IS a fall) the uniform -0.5 is the KNOWN-GOOD value
            # that let patrol train cleanly; -1.0 there risks the stationary-
            # dummy timidity. The fall(-1.0) vs combat-death(-0.5) split applies
            # only when the opponent can deal lethal damage (selfplay/scripted).
            if self.opp_mode in ("selfplay", "scripted"):
                reward -= 1.0 if my_y < -3.0 else 0.5
            else:
                reward -= 0.5
            terminated = True
        elif cur_round != self._round:
            terminated = True  # round advanced (stall/other) — episode boundary

        truncated = self._steps >= self.max_steps
        # Telemetry (read by VecMonitor info_keywords at episode end):
        #   win  = killed the opponent and survived
        #   fell = our death was a fall (y well below ground level) — vs being
        #          killed, which matters from stage 1 onward. (my_y computed
        #          above, before the death branches.)
        info = {"round": cur_round,
                "win": 1.0 if (opp_dead and not self_dead) else 0.0,
                "fell": 1.0 if (self_dead and my_y < -3.0) else 0.0,
                # arms = pickups this episode; watches both learning progress
                # and the +0.05 dump-and-refetch farming surface.
                "arms": float(self._arm_count),
                # hits = damaging ticks this episode — the dense precursor to
                # win_mean; shows whether the kill-biased shaping is working
                # long before kills become common.
                "hits": float(self._hit_ticks),
                # 2026-06-15 low-variance gate metrics (read by eval_checkpoint):
                # opp_dead/self_dead split the binary win into a graded score
                # (opp_died - self_died); hp_diff (self - opp end HP) moves long
                # before win does, so it exposes passivity early (both ~0).
                "opp_dead": 1.0 if opp_dead else 0.0,
                "self_dead": 1.0 if self_dead else 0.0,
                "hp_diff": float(self_hp - opp_hp)}
        return obs, float(reward), terminated, truncated, info

    def close(self):
        for s in (getattr(self, "sock", None), getattr(self, "asock", None)):
            try:
                if s is not None:
                    s.close()
            except OSError:
                pass


if __name__ == "__main__":
    # Smoke test: random policy for a few episodes.
    import sys
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 1341
    env = SFHeadlessEnv(bridge_port=port)
    for ep in range(3):
        obs, _ = env.reset()
        ret, steps = 0.0, 0
        done = False
        while not done:
            a = env.action_space.sample()
            obs, r, term, trunc, info = env.step(a)
            ret += r; steps += 1
            done = term or trunc
        print(f"episode {ep}: steps={steps} return={ret:.3f} round={info.get('round')}")
    env.close()
