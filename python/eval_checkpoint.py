"""Evaluate a trained PPO checkpoint against the headless env (deterministic).

Reports win rate (agent kills opponent), loss/fall rate, and mean reward over N
episodes — use it to pick the best checkpoint (training ep_rew is noisy and the
"latest" checkpoint isn't necessarily the best).

IMPORTANT: point this at a DEDICATED instance bridge, NOT one the trainer is
using — deterministic eval actions would corrupt that env's training data. e.g.
stop training (or spin up an extra instance on another port) first.

Usage:
    python eval_checkpoint.py models/ppo_headless_120000_steps.zip --bridge 1341 --episodes 20
    # optional: --vecnormalize models/ppo_headless_vecnormalize_120000_steps.pkl
"""
from __future__ import annotations

import argparse
import os

import numpy as np
from sf_headless_env import SFHeadlessEnv

from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint")
    ap.add_argument("--bridge", type=int, default=1341)
    ap.add_argument("--episodes", type=int, default=20)
    ap.add_argument("--opp-mode", choices=["hold", "scripted"], default="hold")
    ap.add_argument("--vecnormalize", default=None,
                    help="VecNormalize .pkl saved alongside the checkpoint (recommended)")
    ap.add_argument("--stochastic", action="store_true", help="sample actions instead of argmax")
    args = ap.parse_args()

    def mk():
        return SFHeadlessEnv(bridge_port=args.bridge, my_slot=0, opp_slot=1,
                             poll_hz=20.0, max_steps=600, opp_mode=args.opp_mode)

    venv = DummyVecEnv([mk])
    # Match training obs normalization if provided (eval must use the same stats).
    if args.vecnormalize and os.path.exists(args.vecnormalize):
        venv = VecNormalize.load(args.vecnormalize, venv)
        venv.training = False
        venv.norm_reward = False
        print(f"[eval] loaded VecNormalize {args.vecnormalize}")

    model = PPO.load(args.checkpoint, device="cpu")
    print(f"[eval] {args.checkpoint} | {args.episodes} eps | opp={args.opp_mode} "
          f"| {'stochastic' if args.stochastic else 'deterministic'}")

    wins = losses = timeouts = 0
    returns = []
    for ep in range(args.episodes):
        obs = venv.reset()
        done = np.array([False])
        ret = 0.0
        info = {}
        while not done[0]:
            action, _ = model.predict(obs, deterministic=not args.stochastic)
            obs, r, done, infos = venv.step(action)
            ret += float(r[0]); info = infos[0]
        returns.append(ret)
        # Classify by terminal reward sign (env: +1 kill, -1 self-death).
        if ret > 0.5:
            wins += 1; tag = "WIN"
        elif ret < -0.5:
            losses += 1; tag = "loss/fall"
        else:
            timeouts += 1; tag = "draw/timeout"
        print(f"  ep {ep:2d}: return={ret:+.2f} [{tag}]")

    n = max(1, args.episodes)
    print(f"\n[eval] win={wins}/{n} ({100*wins/n:.0f}%)  loss/fall={losses}/{n}  "
          f"draw={timeouts}/{n}  mean_return={np.mean(returns):+.3f}")
    venv.close()


if __name__ == "__main__":
    main()
