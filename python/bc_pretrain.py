"""Behavior-clone the teacher demos into the current PPO checkpoint.

Loads the latest models/ppo_headless_<N>_steps.zip + matching VecNormalize
stats, normalizes the RAW demo observations with those stats (exactly what
the policy sees in training), then minimizes -log pi(teacher_action | obs)
over the policy tower only (mlp_extractor.policy_net + action_net — the
value tower is left alone and recalibrates during the PPO resume).

Saves models/ppo_headless_<N+8000>_steps.zip (+ copied vecnormalize pkl)
so the train supervisor's resume glob picks the cloned policy up
automatically on next trainer launch.

Usage:
    python bc_pretrain.py --demos demos/teacher_demos.npz [--epochs 10]
"""
import argparse
import glob
import os
import pickle
import re
import shutil

import numpy as np
import torch

BOTDIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS = os.path.join(BOTDIR, "models")


def latest_checkpoint():
    best_n, best_zip = -1, None
    for z in glob.glob(os.path.join(MODELS, "ppo_headless_*_steps.zip")):
        m = re.search(r"ppo_headless_(\d+)_steps\.zip$", z)
        if m and int(m.group(1)) > best_n:
            best_n, best_zip = int(m.group(1)), z
    return best_n, best_zip


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--demos", default=os.path.join(BOTDIR, "demos/teacher_demos.npz"))
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--batch", type=int, default=1024)
    # Modest LR/epochs on purpose: the value tower + Adam moments stay stale
    # relative to the cloned policy, and the first PPO rollouts after resume
    # can partially undo an over-fit clone.
    ap.add_argument("--lr", type=float, default=2e-4)
    args = ap.parse_args()

    from stable_baselines3 import PPO

    n, zip_path = latest_checkpoint()
    assert zip_path, "no ppo_headless_*_steps.zip checkpoint found"
    pkl_path = os.path.join(MODELS, f"ppo_headless_vecnormalize_{n}_steps.pkl")
    assert os.path.exists(pkl_path), f"missing vecnormalize stats {pkl_path}"
    print(f"checkpoint: {zip_path}")

    model = PPO.load(zip_path, device="cpu")
    with open(pkl_path, "rb") as f:
        vecnorm = pickle.load(f)
    rms = vecnorm.obs_rms
    clip = float(getattr(vecnorm, "clip_obs", 10.0))

    d = np.load(args.demos)
    obs_raw, act = d["obs"].astype(np.float64), d["act"].astype(np.int64)
    assert obs_raw.shape[1] == model.observation_space.shape[0], \
        f"obs dim mismatch demos={obs_raw.shape[1]} model={model.observation_space.shape[0]}"
    obs = np.clip((obs_raw - rms.mean) / np.sqrt(rms.var + 1e-8), -clip, clip).astype(np.float32)
    print(f"demos: {obs.shape[0]} pairs | move dist={np.bincount(act[:,0], minlength=3)} "
          f"jump={act[:,1].mean():.3f} fire={act[:,2].mean():.3f}")

    policy = model.policy
    policy.train()
    params = list(policy.mlp_extractor.policy_net.parameters()) + \
             list(policy.action_net.parameters())
    opt = torch.optim.Adam(params, lr=args.lr)

    obs_t = torch.as_tensor(obs)
    act_t = torch.as_tensor(act)
    n_pairs = obs_t.shape[0]
    for ep in range(args.epochs):
        perm = torch.randperm(n_pairs)
        tot_loss, tot_acc, nb = 0.0, np.zeros(3), 0
        for i in range(0, n_pairs, args.batch):
            idx = perm[i:i + args.batch]
            bo, ba = obs_t[idx], act_t[idx]
            _, log_prob, _ = policy.evaluate_actions(bo, ba)
            loss = -log_prob.mean()
            opt.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(params, 1.0)
            opt.step()
            tot_loss += float(loss)
            with torch.no_grad():
                dist = policy.get_distribution(bo)
                for h, cat in enumerate(dist.distribution):
                    pred = cat.probs.argmax(dim=-1)
                    tot_acc[h] += float((pred == ba[:, h]).float().mean())
            nb += 1
        acc = tot_acc / max(1, nb)
        print(f"epoch {ep+1}/{args.epochs} loss={tot_loss/max(1,nb):.4f} "
              f"acc move={acc[0]:.3f} jump={acc[1]:.3f} fire={acc[2]:.3f}")

    out_n = n + 8000
    out_zip = os.path.join(MODELS, f"ppo_headless_{out_n}_steps.zip")
    out_pkl = os.path.join(MODELS, f"ppo_headless_vecnormalize_{out_n}_steps.pkl")
    model.save(out_zip)
    shutil.copyfile(pkl_path, out_pkl)
    # Archive copies under names the resume glob / CheckpointCallback can't
    # touch: the supervisor's first post-resume save lands at exactly N+8000
    # and would overwrite the BC artifact ~minutes into the run.
    shutil.copyfile(out_zip, os.path.join(MODELS, f"BC_INIT_{out_n}.zip"))
    shutil.copyfile(out_pkl, os.path.join(MODELS, f"BC_INIT_vecnormalize_{out_n}.pkl"))
    print(f"saved BC-initialized checkpoint -> {out_zip} (+ vecnormalize copy, "
          f"+ BC_INIT_{out_n} archive)")


if __name__ == "__main__":
    main()
