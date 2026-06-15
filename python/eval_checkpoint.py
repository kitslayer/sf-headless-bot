"""Deterministic / stochastic evaluation of a trained checkpoint.

The trainer's rollout win_mean is measured on the STOCHASTIC exploration
policy and swings ±0.06 from instance churn — useless for a real verdict
or a curriculum gate. This runs a checkpoint on a DEDICATED game instance
(its own bridge, NOT the training fleet's) for N full episodes and reports
the true win/fell/length/arms/hits.

GATE (2026-06-15, the corrected interpretable gate): the SELF-PLAY mirror
harness is asymmetric — the evaluated side at argmax vs a SAMPLED opp on a
single fixed slot makes an EVEN match score only ~0.15, not 0.50 (so the old
0.65 refresh bar was unreachable on any fair mirror). The fixes here:
  * --slot-swap : run half the episodes with the learner on slot 0 and half on
    slot 1, then average. Removes the slot-0/1 spawn asymmetry → an even match
    (a policy vs ITSELF) reads ~0.50. selfplay-only (in other modes the HOST
    drives the non-RL slot, so the learner can't take slot 1).
  * stochastic by default for the gate (drop --deterministic) so BOTH sides
    sample, matching what training optimizes; --deterministic stays for the
    argmax DIAGNOSTIC (catches argmax mode-collapse, e.g. arms->0).
  * secondary low-variance metrics: score = mean(opp_died) - mean(self_died)
    and end HP-diff. Every episode contributes a graded value (not a mostly-0
    binary), so these move long before the noisy win rate and expose passivity
    (both ~0 = nobody's killing anybody). Read from env info (added 2026-06-15).

CALIBRATION: run `--opp-mode selfplay --slot-swap` of the frozen opp vs ITSELF
once per session; it MUST read ~0.50. If it drifts, something asymmetric
regressed. REFRESH BAR on this scale: promote when a candidate beats the frozen
opp at >= ~0.58 (two consecutive gates, or one 2N>=40 run).

Usage:
    python eval_checkpoint.py --bridge 1349 --episodes 24 --opp-mode selfplay --slot-swap
    python eval_checkpoint.py --bridge 1349 --episodes 20 --opp-mode scripted --deterministic
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


def eval_pass(model, vn, bridge, my_slot, opp_slot, opp_mode, n, max_steps, deterministic):
    """Run n episodes with the learner on my_slot; return aggregate dict."""
    def _thunk():
        return SFHeadlessEnv(bridge_port=bridge, my_slot=my_slot, opp_slot=opp_slot,
                             poll_hz=20.0, max_steps=max_steps, opp_mode=opp_mode)
    venv = DummyVecEnv([_thunk])
    if vn and os.path.exists(vn):
        venv = VecNormalize.load(vn, venv)
        venv.training = False          # freeze stats, don't update during eval
        venv.norm_reward = False
    wins = fell = oppdied = selfdied = 0
    lengths, arms, hits, hpdiff = [], [], [], []
    obs = venv.reset()
    ep_len = 0
    done_count = 0
    while done_count < n:
        action, _ = model.predict(obs, deterministic=deterministic)
        obs, _, dones, infos = venv.step(action)
        ep_len += 1
        if dones[0]:
            info = infos[0]
            wins += int(float(info.get("win", 0.0)) >= 0.5)
            fell += int(float(info.get("fell", 0.0)) >= 0.5)
            oppdied += int(float(info.get("opp_dead", 0.0)) >= 0.5)
            selfdied += int(float(info.get("self_dead", 0.0)) >= 0.5)
            arms.append(float(info.get("arms", 0.0)))
            hits.append(float(info.get("hits", 0.0)))
            hpdiff.append(float(info.get("hp_diff", 0.0)))
            lengths.append(ep_len)
            ep_len = 0
            done_count += 1
            if done_count % 5 == 0:
                # include score (opp_died-self_died) so a wedged run (the eval
                # instance wedges ~10-25 eps) still surfaces the low-variance metric.
                print(f"[eval] slot{my_slot} {done_count}/{n} "
                      f"win={wins/done_count:.3f} score={(oppdied-selfdied)/done_count:+.2f} "
                      f"fell={fell/done_count:.3f} arms={np.mean(arms):.2f} "
                      f"hits={np.mean(hits):.2f} len={np.mean(lengths):.0f}", flush=True)
    venv.close()
    return dict(wins=wins, fell=fell, oppdied=oppdied, selfdied=selfdied, n=n,
                arms=arms, hits=hits, hpdiff=hpdiff, lengths=lengths)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint", nargs="?", default="",
                    help="checkpoint zip (default: latest ppo_headless_*_steps.zip)")
    ap.add_argument("--bridge", type=int, default=1349)
    ap.add_argument("--episodes", type=int, default=40)
    ap.add_argument("--vecnormalize", default="",
                    help="VecNormalize .pkl (default: matched by step number)")
    ap.add_argument("--deterministic", action="store_true",
                    help="argmax DIAGNOSTIC (default: sampled — the gate uses sampled)")
    ap.add_argument("--slot-swap", action="store_true",
                    help="half eps on each slot + average (even match => ~0.5; selfplay only)")
    ap.add_argument("--max-steps", type=int, default=600)
    ap.add_argument("--opp-mode", default="hold", choices=["hold", "patrol", "scripted", "selfplay"],
                    help="opponent the eval faces; must match how the checkpoint trained")
    args = ap.parse_args()

    ckpt = args.checkpoint or latest_checkpoint()
    assert ckpt and os.path.exists(ckpt), f"no checkpoint ({ckpt})"
    vn = args.vecnormalize
    if not vn:
        m = re.search(r"_(\d+)_steps\.zip$", ckpt)
        if m:
            vn = os.path.join(MODELS, f"ppo_headless_vecnormalize_{m.group(1)}_steps.pkl")
    if vn and not os.path.exists(vn):
        print(f"[eval] WARNING: no vecnormalize stats ({vn}) — obs unnormalized!", flush=True)
        vn = ""

    if args.slot_swap and args.opp_mode != "selfplay":
        print("[eval] NOTE: --slot-swap is selfplay-only (host drives the non-RL slot in "
              "hold/patrol/scripted) — running single-slot", flush=True)
        args.slot_swap = False

    mode = "DETERMINISTIC" if args.deterministic else "stochastic"
    gate = "SLOT-SWAP" if args.slot_swap else "single-slot"
    print(f"[eval] {mode} {gate} | ckpt={os.path.basename(ckpt)} | bridge={args.bridge} | "
          f"eps={args.episodes} | opp={args.opp_mode}", flush=True)

    model = PPO.load(ckpt, device="cpu")

    if args.slot_swap:
        n0 = args.episodes // 2
        passes = [
            eval_pass(model, vn, args.bridge, 0, 1, args.opp_mode, n0, args.max_steps, args.deterministic),
            eval_pass(model, vn, args.bridge, 1, 0, args.opp_mode, args.episodes - n0, args.max_steps, args.deterministic),
        ]
    else:
        passes = [eval_pass(model, vn, args.bridge, 0, 1, args.opp_mode,
                            args.episodes, args.max_steps, args.deterministic)]

    W = sum(p["wins"] for p in passes); F = sum(p["fell"] for p in passes)
    OD = sum(p["oppdied"] for p in passes); SD = sum(p["selfdied"] for p in passes)
    N = sum(p["n"] for p in passes)
    A = [x for p in passes for x in p["arms"]]
    H = [x for p in passes for x in p["hits"]]
    HD = [x for p in passes for x in p["hpdiff"]]
    L = [x for p in passes for x in p["lengths"]]
    print(f"\n===== EVAL RESULT ({mode} {gate}) =====")
    print(f"checkpoint : {os.path.basename(ckpt)}")
    print(f"episodes   : {N}")
    print(f"WIN rate   : {W/N:.3f}  ({W}/{N})")
    print(f"fell rate  : {F/N:.3f}  ({F}/{N})")
    print(f"score      : {(OD-SD)/N:+.3f}  (opp_died {OD/N:.2f} - self_died {SD/N:.2f})  [low-var]")
    print(f"end HP diff: {np.mean(HD) if HD else 0.0:+.1f}  (self - opp, mean)")
    print(f"ep length  : {np.mean(L):.0f} steps mean / {np.median(L):.0f} median")
    print(f"arms/ep    : {np.mean(A):.2f}")
    print(f"hits/ep    : {np.mean(H):.2f}")
    if args.slot_swap:
        for i, p in enumerate(passes):
            sl = 0 if i == 0 else 1
            print(f"  slot{sl}: win {p['wins']}/{p['n']}={p['wins']/max(1,p['n']):.3f} "
                  f"fell={p['fell']/max(1,p['n']):.2f}")


if __name__ == "__main__":
    main()
