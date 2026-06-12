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

import json
import os
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
                 reset_timeout: float = 30.0, opp_mode: str = "hold"):
        super().__init__()
        self.addr = ("127.0.0.1", bridge_port)
        self.my_slot = my_slot
        self.opp_slot = opp_slot
        # opp_mode: "hold"  -> env pins the opponent slot stationary (curriculum
        #                      stage 0: learn to approach + attack a dummy).
        #           "scripted" -> leave the opponent to the in-plugin scripted
        #                      bot (do NOT free its slot via SFGYM_RL_SLOTS).
        # When "hold", launch with SFGYM_RL_SLOTS=<my_slot>,<opp_slot> so the
        # scripted driver yields both slots to us.
        self.opp_mode = opp_mode
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
        # Pin the opponent slot stationary (curriculum stage 0 dummy).
        if self.opp_mode == "hold":
            self._send_nowait({"cmd": "setBotAction", "slot": self.opp_slot,
                               "mx": 0.0, "my": 0.0, "aimx": 1.0, "aimy": 0.0, "buttons": 0})

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
        me = self._ent(snap, self.my_slot)
        op = self._ent(snap, self.opp_slot)

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
            # refresh the auto-aim direction (normalized YZ toward opponent)
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
            reward += 0.05
            self._hit_ticks += 1
        self._prev_self_hp = self_hp
        self._prev_opp_hp = opp_hp
        # Pickup shaping: one-time bonus on unarmed→armed. The walk-to-weapon →
        # damage credit chain is seconds long; this densifies it. Not
        # exploitable: re-arming requires having lost the gun (emptied = it
        # auto-drops), which already costs time and forgone damage.
        # 2026-06-11 retune: 0.05 → 0.15, plus +0.0005/tick while armed (max
        # +0.3 over a full 600-step episode). 59k steps of hit-shaping doubled
        # hits/ep but win stayed flat at ~0.07 — arms ~0.25/ep showed gun
        # ACQUISITION was the binding constraint, not per-hit credit. Camping
        # with a gun (trickle ~0.3) stays strictly dominated by hunting with
        # it (hits + 1.5x damage + kill 1.0-1.5).
        armed_now = bool(me.get("armed", False)) if me else self._prev_armed
        if armed_now and not self._prev_armed:
            reward += 0.15
            self._arm_count += 1
        if armed_now:
            reward += 0.0005
        self._prev_armed = armed_now

        terminated = False
        cur_round = snap.get("round") if snap else self._round
        # Death requires the ent to be PRESENT (review fix 2026-06-09): a
        # snapshot timeout (snap=None) or a momentarily missing ent used to
        # zero both hp's and fabricate a loss — or a +1.0 WIN — polluting both
        # the reward and the win_mean stage gate.
        self_dead = me is not None and ((not me["alive"]) or self_hp <= 0)
        opp_dead = op is not None and ((not op["alive"]) or opp_hp <= 0)
        if opp_dead and not self_dead:
            #   * fast-kill bonus: +1.0 base, plus up to +0.5 decaying
            #     linearly over the 30s cap — time pressure toward hunting
            #     WITHOUT a per-step idle penalty (which would make suicide
            #     cheaper than waiting and inflate fell_mean).
            reward += 1.0 + 0.5 * max(0.0, 1.0 - self._steps / self.max_steps)
            terminated = True
        elif self_dead:
            # 2026-06-06 tuning: death (vs a stationary dummy this is almost
            # always a fall off Desert3's edge) was -1.0 == a full kill, so the
            # high-variance fall spike dominated and destabilized PPO. Halve it
            # so a kill (+1.0) is worth 2 falls — biases toward finishing the
            # opponent over timid edge-camping, and cuts the reward variance.
            reward -= 0.5; terminated = True
        elif cur_round != self._round:
            terminated = True  # round advanced (stall/other) — episode boundary

        truncated = self._steps >= self.max_steps
        # Telemetry (read by VecMonitor info_keywords at episode end):
        #   win  = killed the opponent and survived
        #   fell = our death was a fall (y well below ground level) — vs being
        #          killed, which matters from stage 1 onward.
        my_y = float(me.get("y", 0.0)) if me else 0.0
        info = {"round": cur_round,
                "win": 1.0 if (opp_dead and not self_dead) else 0.0,
                "fell": 1.0 if (self_dead and my_y < -3.0) else 0.0,
                # arms = pickups this episode; watches both learning progress
                # and the +0.05 dump-and-refetch farming surface.
                "arms": float(self._arm_count),
                # hits = damaging ticks this episode — the dense precursor to
                # win_mean; shows whether the kill-biased shaping is working
                # long before kills become common.
                "hits": float(self._hit_ticks)}
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
