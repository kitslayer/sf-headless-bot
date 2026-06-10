"""PPO self-play(-vs-scripted) trainer over the headless Stick Fight fleet.

Each parallel env drives slot 0 (the RL agent) of one headless instance via the
loopback bridge; slot 1 is the in-plugin scripted bot. Reward is damage-diff +
win/loss (see sf_headless_env). Runs on GPU, checkpoints regularly, and
auto-resumes from the latest checkpoint (so a crash/restart continues training).

Prereqs: launch the fleet with the RL slot freed, e.g.
    SFGYM_RL_SLOTS=0 bash scripts/fleet.sh start 4

Usage:
    python train_headless_ppo.py --instances 4 --base-bridge 1341 --steps 5_000_000
"""
from __future__ import annotations

import argparse
import glob
import os
import time

from sf_headless_env import SFHeadlessEnv

from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback, CallbackList, CheckpointCallback
from stable_baselines3.common.vec_env import SubprocVecEnv, VecMonitor, VecNormalize

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "..", "models")
LOGS = os.path.join(HERE, "..", "logs", "tb")
FLEET_ENV = os.path.join(HERE, "..", "run", "fleet.env")


def fleet_timescale() -> float:
    """Single source of truth: SF_TIMESCALE from run/fleet.env (the same file
    the instances source). The env polls at 20*timescale Hz wall so the agent
    keeps a 20 Hz GAME-time decision rate regardless of the host timescale."""
    try:
        with open(FLEET_ENV) as f:
            for line in f:
                line = line.strip()
                if line.startswith("export SF_TIMESCALE="):
                    v = line.split("=", 1)[1].strip()
                    if v:
                        return max(1.0, min(5.0, float(v)))
    except (OSError, ValueError):
        pass
    return 1.0


class EpInfoLogger(BaseCallback):
    """Log win/fell episode means (VecMonitor info_keywords) each rollout —
    the real stage-gate metrics; ep_rew_mean alone is too muddy."""
    def _on_step(self) -> bool:
        return True

    def _on_rollout_end(self) -> None:
        buf = list(self.model.ep_info_buffer or [])
        for key in ("win", "fell", "arms"):
            vals = [e[key] for e in buf if key in e]
            if vals:
                self.logger.record(f"rollout/{key}_mean", sum(vals) / len(vals))


def make_env(bridge_port: int, opp_mode: str = "hold", poll_hz: float = 20.0):
    def _thunk():
        return SFHeadlessEnv(bridge_port=bridge_port, my_slot=0, opp_slot=1,
                             poll_hz=poll_hz, max_steps=600, opp_mode=opp_mode)
    return _thunk


def latest_checkpoint():
    cks = glob.glob(os.path.join(MODELS, "ppo_headless_*_steps.zip"))
    if not cks:
        return None
    cks.sort(key=lambda p: int(p.split("_")[-2]))
    return cks[-1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instances", type=int, default=4)
    ap.add_argument("--base-bridge", type=int, default=1341)
    ap.add_argument("--steps", type=int, default=5_000_000)
    ap.add_argument("--save-every", type=int, default=20_000)
    ap.add_argument("--opp-mode", choices=["hold", "scripted"], default="hold",
                    help="hold = stationary dummy (stage 0, needs SFGYM_RL_SLOTS=0,1); "
                         "scripted = in-plugin scripted opponent (stage 1, SFGYM_RL_SLOTS=0)")
    args = ap.parse_args()

    os.makedirs(MODELS, exist_ok=True)
    os.makedirs(LOGS, exist_ok=True)

    ports = [args.base_bridge + i for i in range(args.instances)]
    ts = fleet_timescale()
    poll_hz = 20.0 * ts
    print(f"[train] envs on bridge ports {ports} opp_mode={args.opp_mode} "
          f"timescale={ts} poll_hz={poll_hz}")
    venv = SubprocVecEnv([make_env(p, args.opp_mode, poll_hz) for p in ports])
    venv = VecMonitor(venv, info_keywords=("win", "fell", "arms"))
    # 2026-06-06 tuning: norm_reward=True normalizes returns by a running std,
    # which directly tames the high-variance ±1/kill/fall reward spikes that
    # were destabilizing PPO (oscillated for ~2.8M steps, never converged).
    venv = VecNormalize(venv, norm_obs=True, norm_reward=True, clip_obs=50.0,
                        clip_reward=10.0, gamma=0.995)

    ckpt = latest_checkpoint()
    # save_freq is per-env; convert desired total-step cadence.
    save_freq = max(1, args.save_every // args.instances)
    ckpt_cb = CheckpointCallback(save_freq=save_freq, save_path=MODELS,
                                 name_prefix="ppo_headless",
                                 save_vecnormalize=True)

    if ckpt:
        print(f"[train] resuming from {ckpt}")
        model = PPO.load(ckpt, env=venv, device="cuda")
        # CheckpointCallback writes ppo_headless_vecnormalize_<N>_steps.pkl
        # (NOT <ckpt>_vecnormalize.pkl — the old path here never matched, so
        # every auto-resume silently reset normalization stats; review fix
        # 2026-06-09).
        step_n = ckpt.split("_")[-2]
        vn = os.path.join(MODELS, f"ppo_headless_vecnormalize_{step_n}_steps.pkl")
        if os.path.exists(vn):
            print(f"[train] loading vecnormalize stats {vn}")
            venv = VecNormalize.load(vn, venv.venv if hasattr(venv, "venv") else venv)
            model.set_env(venv)
        else:
            print(f"[train] WARNING: no vecnormalize stats at {vn} — fresh normalization")
        reset_num = False
    else:
        print("[train] fresh model")
        model = PPO(
            "MlpPolicy", venv, device="cuda", verbose=1,
            n_steps=512, batch_size=512, n_epochs=4,
            gamma=0.995, gae_lambda=0.95, ent_coef=0.01,
            learning_rate=1e-4, clip_range=0.2,   # 2026-06-06: 3e-4 oscillated; lower for stability
            policy_kwargs=dict(net_arch=[256, 256]),
            tensorboard_log=LOGS,
        )
        reset_num = True

    t0 = time.time()
    model.learn(total_timesteps=args.steps,
                callback=CallbackList([ckpt_cb, EpInfoLogger()]),
                reset_num_timesteps=reset_num, progress_bar=False)
    final = os.path.join(MODELS, "ppo_headless_final.zip")
    model.save(final)
    venv.save(os.path.join(MODELS, "vecnormalize_final.pkl"))
    print(f"[train] done in {time.time()-t0:.0f}s; saved {final}")


if __name__ == "__main__":
    main()
