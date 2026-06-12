"""Collect behavior-cloning demos from the in-host teacher driver.

Run with the fleet up in collection mode (SFGYM_RL_SLOTS=1): slot 0 is
driven by the host's teacher (the ScriptedBot decision core ported into
DriveScriptedBots), slot 1 stays externally drivable and we pin it
stationary exactly like the trainer's opp_mode="hold". We observe slot 0
through the same SFHeadlessEnv obs pipeline the PPO policy uses, and the
snapshot's per-ent "in" field gives the teacher's EXACT input each tick —
no inverse-dynamics guessing.

Output: demos/teacher_demos.npz with
    obs  float32 [N, 78]   RAW observations (pre-VecNormalize)
    act  int8    [N, 3]    MultiDiscrete labels (move, jump, fire)
Episodes where the teacher DIED are dropped (it occasionally falls into
the void; the student shouldn't clone that ending). Wins and timeouts
are kept — both show fetch/engage/fire behavior.

Usage:
    python collect_demos.py --bridges 1341,1342,1343,1344 \
        --minutes 35 --out demos/teacher_demos.npz
"""
import argparse
import os
import sys
import threading
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sf_headless_env import SFHeadlessEnv


def label_from_in(in_field):
    """Map the snapshot 'in' echo [StickX, Buttons] -> MultiDiscrete[3,2,2].

    Env convention (SFHeadlessEnv._send_action): move 0 -> mx=-1,
    move 2 -> mx=+1; buttons bit0=jump, bit1=fire.
    """
    sx = float(in_field[0]); buttons = int(in_field[1])
    move = 2 if sx > 0.5 else (0 if sx < -0.5 else 1)
    jump = 1 if (buttons & 1) else 0
    fire = 1 if (buttons & 2) else 0
    return [move, jump, fire]


def collect_on_bridge(port, deadline, out, lock, stats):
    env = SFHeadlessEnv(bridge_port=port, my_slot=0, opp_slot=1,
                        poll_hz=20.0, max_steps=600, opp_mode="hold")
    # Hard guarantee we never write slot-0 inputs (env.reset() pins my_slot
    # with a neutral frame while waiting — harmless given the host's Update
    # ordering, but the teacher owns slot 0 and we don't rely on luck).
    _orig_send = env._send_nowait
    env._send_nowait = lambda msg: (None if (msg.get("cmd") == "setBotAction" and
                                             msg.get("slot") == env.my_slot)
                                    else _orig_send(msg))
    ep_obs, ep_act = [], []
    try:
        obs, _ = env.reset()
        steps = 0
        while time.time() < deadline:
            # Drive ONLY the opponent pin; slot 0 belongs to the teacher.
            env._hold_opp()
            time.sleep(env.dt)
            snap = env._snapshot()
            obs, me, op = env._build_obs(snap)
            steps += 1

            self_hp = float(me["hp"]) if me else env._prev_self_hp
            opp_hp = float(op["hp"]) if op else env._prev_opp_hp
            env._prev_self_hp, env._prev_opp_hp = self_hp, opp_hp

            in_field = me.get("in") if me else None
            in_fight = bool(snap.get("inFight")) if snap else False
            if me and op and in_field and in_fight and me["alive"]:
                ep_obs.append(np.asarray(obs, dtype=np.float32))
                ep_act.append(np.asarray(label_from_in(in_field), dtype=np.int8))

            # Episode boundaries — mirror SFHeadlessEnv.step's logic.
            self_dead = me is not None and ((not me["alive"]) or self_hp <= 0)
            opp_dead = op is not None and ((not op["alive"]) or opp_hp <= 0)
            cur_round = snap.get("round") if snap else env._round
            ep_end = self_dead or opp_dead or cur_round != env._round or steps >= env.max_steps
            if ep_end:
                with lock:
                    stats["episodes"] += 1
                    if self_dead:
                        stats["teacher_died"] += 1   # drop the episode
                    else:
                        if opp_dead:
                            stats["teacher_won"] += 1
                        out["obs"].extend(ep_obs)
                        out["act"].extend(ep_act)
                ep_obs, ep_act = [], []
                obs, _ = env.reset()
                steps = 0
    except Exception as e:
        print(f"[{port}] collector error: {e}", flush=True)
    finally:
        env.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bridges", default="1341,1342,1343,1344")
    ap.add_argument("--minutes", type=float, default=35.0)
    ap.add_argument("--out", default="demos/teacher_demos.npz")
    args = ap.parse_args()

    ports = [int(p) for p in args.bridges.split(",")]
    deadline = time.time() + args.minutes * 60.0
    out = {"obs": [], "act": []}
    stats = {"episodes": 0, "teacher_died": 0, "teacher_won": 0}
    lock = threading.Lock()

    threads = [threading.Thread(target=collect_on_bridge,
                                args=(p, deadline, out, lock, stats), daemon=True)
               for p in ports]
    for t in threads:
        t.start()
    t0 = time.time()
    while any(t.is_alive() for t in threads):
        time.sleep(30)
        with lock:
            n = len(out["obs"])
            print(f"[{int(time.time()-t0)}s] pairs={n} eps={stats['episodes']} "
                  f"won={stats['teacher_won']} died={stats['teacher_died']}", flush=True)
    for t in threads:
        t.join(timeout=5)

    obs = np.stack(out["obs"]) if out["obs"] else np.zeros((0, 78), np.float32)
    act = np.stack(out["act"]) if out["act"] else np.zeros((0, 3), np.int8)
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    np.savez_compressed(args.out, obs=obs, act=act)
    kept = stats["episodes"] - stats["teacher_died"]
    wr = stats["teacher_won"] / max(1, stats["episodes"])
    print(f"saved {obs.shape[0]} pairs from {kept}/{stats['episodes']} episodes "
          f"(teacher win rate {wr:.2f}, died {stats['teacher_died']}) -> {args.out}")


if __name__ == "__main__":
    main()
