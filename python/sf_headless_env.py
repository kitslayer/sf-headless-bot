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
import socket
import time

import numpy as np

try:
    import gymnasium as gym
    from gymnasium import spaces
except ImportError:  # fall back to classic gym
    import gym
    from gym import spaces


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

        # obs: self(9) + opp(9) + relative(4) = 22
        high = np.full(22, np.inf, dtype=np.float32)
        self.observation_space = spaces.Box(-high, high, dtype=np.float32)
        self.action_space = spaces.MultiDiscrete([3, 2, 2])

        self._steps = 0
        self._round = None
        self._prev_self_hp = 100.0
        self._prev_opp_hp = 100.0

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
        # Aim toward the opponent on the horizontal (z) axis by default.
        aimx = -mx if mx != 0 else 1.0
        self._send_nowait({"cmd": "setBotAction", "slot": self.my_slot,
                           "mx": mx, "my": 0.0, "aimx": aimx, "aimy": 0.0,
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
            if e is None:
                return [0.0] * 9
            return [float(e.get("x", 0)), float(e.get("y", 0)), float(e.get("z", 0)),
                    float(e.get("vx", 0)), float(e.get("vy", 0)), float(e.get("vz", 0)),
                    float(e.get("hp", 0)) / 100.0,
                    1.0 if e.get("alive", False) else 0.0,
                    1.0 if e.get("armed", False) else 0.0]

        mf, of = feats(me), feats(op)
        if me and op:
            dx = of[0] - mf[0]; dy = of[1] - mf[1]; dz = of[2] - mf[2]
            dist = (dx * dx + dy * dy + dz * dz) ** 0.5
            rel = [dx, dy, dz, dist]
        else:
            rel = [0.0, 0.0, 0.0, 0.0]
        obs = np.array(mf + of + rel, dtype=np.float32)
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
                    return obs, {}
            time.sleep(0.03)
        # Timed out — return whatever we have so training doesn't deadlock.
        snap = self._snapshot()
        obs, me, op = self._build_obs(snap)
        self._round = snap.get("round") if snap else None
        self._steps = 0
        self._prev_self_hp = float(me["hp"]) if me else 100.0
        self._prev_opp_hp = float(op["hp"]) if op else 100.0
        return obs, {"reset_timeout": True}

    def step(self, action):
        self._send_action(action)
        self._hold_opp()
        time.sleep(self.dt)
        snap = self._snapshot()
        obs, me, op = self._build_obs(snap)
        self._steps += 1

        self_hp = float(me["hp"]) if me else 0.0
        opp_hp = float(op["hp"]) if op else 0.0
        # Damage-based shaped reward.
        opp_dmg = max(0.0, self._prev_opp_hp - opp_hp)
        self_dmg = max(0.0, self._prev_self_hp - self_hp)
        reward = (opp_dmg - self_dmg) / 100.0
        self._prev_self_hp = self_hp
        self._prev_opp_hp = opp_hp

        terminated = False
        cur_round = snap.get("round") if snap else self._round
        self_dead = (me is not None and not me["alive"]) or self_hp <= 0
        opp_dead = (op is not None and not op["alive"]) or opp_hp <= 0
        if opp_dead and not self_dead:
            reward += 1.0; terminated = True
        elif self_dead:
            reward -= 1.0; terminated = True
        elif cur_round != self._round:
            terminated = True  # round advanced (stall/other) — episode boundary

        truncated = self._steps >= self.max_steps
        return obs, float(reward), terminated, truncated, {"round": cur_round}

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
