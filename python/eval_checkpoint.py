"""Deterministic (and stochastic) evaluation of a trained checkpoint.

The trainer's rollout win_mean is measured on the STOCHASTIC exploration
policy and swings ±0.06 from instance churn — useless for a real verdict
or a curriculum gate. This runs a checkpoint on a DEDICATED game instance
(its own bridge, NOT the training fleet's) for N full episodes and reports
the true win/fell/length/arms/hits, with --deterministic selecting argmax
vs sampled actions. The whole BC-transfer question (is the policy good but
sample-noisy, or genuinely stuck?) is answered by running this once with
and once without --deterministic.

Win/fell come from info[] (the env's own definition), NOT the reward sign —
under the dense kill-biased shaping a non-win episode easily clears any
reward threshold, so the old reward-sign classifier over-counts wins.

Usage (point at an idle bridge, e.g. a 5th instance on 1349):
    python eval_checkpoint.py --bridge 1349 --episodes 40 --deterministic
    python eval_checkpoint.py --bridge 1349 --episodes 40            # stochastic
    python eval_checkpoint.py models/BC_INIT_816000.zip --bridge 1349 --deterministic
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
MODELS = os.path.join(HERE, "..", "models")

from sf_headless_env import SFHeadlessEnv
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize


def latest_checkpoint():
    best_n, best = -1, None
    for z in glob.glob(os.path.join(MODELS, "ppo_headless_*_steps.zip")):
        m = re.search(r"ppo_headless_(\d+)_steps\.zip$", z)
        if m and int(m.group(1)) > best_n:
            best_n, best = int(m.group(1)), z
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint", nargs="?", default="",
                    help="checkpoint zip (default: latest ppo_headless_*_steps.zip)")
    ap.add_argument("--bridge", type=int, default=1349)
    ap.add_argument("--episodes", type=int, default=40)
    ap.add_argument("--vecnormalize", default="",
                    help="VecNormalize .pkl (default: matched by step number)")
    ap.add_argument("--deterministic", action="store_true", help="argmax actions (default: sampled)")
    ap.add_argument("--max-steps", type=int, default=600)
    ap.add_argument("--opp-mode", default="hold", choices=["hold", "patrol", "scripted", "selfplay"],
                    help="opponent the eval faces; use 'patrol' for a stage-1-faithful "
                         "number (must match how the checkpoint was trained)")
    args = ap.parse_args()

    ckpt = args.checkpoint or latest_checkpoint()
    assert ckpt and os.path.exists(ckpt), f"no checkpoint ({ckpt})"
    vn = args.vecnormalize
    if not vn:
        m = re.search(r"_(\d+)_steps\.zip$", ckpt)
        if m:
            vn = os.path.join(MODELS, f"ppo_headless_vecnormalize_{m.group(1)}_steps.pkl")
    mode = "DETERMINISTIC" if args.deterministic else "stochastic"
    print(f"[eval] {mode} | ckpt={os.path.basename(ckpt)} | bridge={args.bridge} | "
          f"eps={args.episodes}", flush=True)

    def _thunk():
        return SFHeadlessEnv(bridge_port=args.bridge, my_slot=0, opp_slot=1,
                             poll_hz=20.0, max_steps=args.max_steps, opp_mode=args.opp_mode)
    venv = DummyVecEnv([_thunk])
    if vn and os.path.exists(vn):
        venv = VecNormalize.load(vn, venv)
        venv.training = False          # freeze stats, don't update during eval
        venv.norm_reward = False
        print(f"[eval] loaded vecnormalize {os.path.basename(vn)}", flush=True)
    else:
        print(f"[eval] WARNING: no vecnormalize stats ({vn}) — obs unnormalized!", flush=True)

    model = PPO.load(ckpt, device="cpu")

    wins = fell = 0
    lengths, arms, hits = [], [], []
    obs = venv.reset()
    ep_len = 0
    done_count = 0
    while done_count < args.episodes:
        action, _ = model.predict(obs, deterministic=args.deterministic)
        obs, _, dones, infos = venv.step(action)
        ep_len += 1
        if dones[0]:
            info = infos[0]
            wins += int(float(info.get("win", 0.0)) >= 0.5)
            fell += int(float(info.get("fell", 0.0)) >= 0.5)
            arms.append(float(info.get("arms", 0.0)))
            hits.append(float(info.get("hits", 0.0)))
            lengths.append(ep_len)
            ep_len = 0
            done_count += 1
            if done_count % 5 == 0:
                # Include running arms/hits/len so a partial run (the eval
                # instance tends to wedge ~20 eps) still surfaces the
                # armed-vs-inefficient breakdown — the final summary block
                # never prints if the process is Killed mid-run.
                print(f"[eval] {done_count}/{args.episodes} "
                      f"win={wins/done_count:.3f} fell={fell/done_count:.3f} "
                      f"arms={np.mean(arms):.2f} hits={np.mean(hits):.2f} "
                      f"len={np.mean(lengths):.0f}", flush=True)
    venv.close()
    n = args.episodes
    print(f"\n===== EVAL RESULT ({mode}) =====")
    print(f"checkpoint : {os.path.basename(ckpt)}")
    print(f"episodes   : {n}")
    print(f"WIN rate   : {wins/n:.3f}  ({wins}/{n})")
    print(f"fell rate  : {fell/n:.3f}  ({fell}/{n})")
    print(f"ep length  : {np.mean(lengths):.0f} steps mean / {np.median(lengths):.0f} median")
    print(f"arms/ep    : {np.mean(arms):.2f}")
    print(f"hits/ep    : {np.mean(hits):.2f}")


if __name__ == "__main__":
    main()
