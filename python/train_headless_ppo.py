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

import numpy as np
import torch

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


class KickstartPPO(PPO):
    """PPO with a decaying behavior-cloning anchor ("kickstarting").

    Plain BC transfer failed twice here: a 97.8%-accurate clone of the
    scripted teacher still won ~0.05 because sampled per-step errors compound
    over 600-step episodes into states the demos never covered. Fix: keep the
    teacher in the LOSS during RL. After every PPO update we take a few
    minibatch gradient steps of  λ(t) * E_demo[-log π(a_teacher|s)]  on the
    teacher's (obs, action) pairs — RL repairs the off-distribution states
    while the anchor stops PPO from washing the teacher behavior out before
    it starts collecting reward with it. λ decays linearly to 0 so late
    training is pure RL.

    Demo arrays are attached post-construction via set_kickstart() (NOT
    __init__ kwargs) so SB3's save/load signature stays untouched; they are
    excluded from checkpoints.
    """

    KS_ATTRS = ("_ks_obs", "_ks_act", "_ks_coef", "_ks_anchor", "_ks_decay",
                "_ks_batch", "_ks_steps", "_ks_warmup_until", "_ks_lr_warmup",
                "_ks_frozen")

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._ks_obs = None
        self._ks_act = None
        self._ks_warmup_until = 0
        self._ks_lr_warmup = 50_000
        self._ks_frozen = False

    def set_kickstart(self, obs_raw, act, coef=0.5, anchor=-1,
                      decay=400_000, batch=512, steps=4, warmup_until=0):
        self._ks_obs = np.asarray(obs_raw, dtype=np.float32)   # RAW obs
        self._ks_act = torch.as_tensor(np.asarray(act, dtype=np.int64),
                                       device=self.device)
        self._ks_coef = float(coef)
        self._ks_anchor = self.num_timesteps if anchor < 0 else int(anchor)
        self._ks_decay = int(decay)
        self._ks_batch = int(batch)
        self._ks_steps = int(steps)
        self._ks_warmup_until = int(warmup_until)
        print(f"[train] kickstart: {len(self._ks_obs)} demo pairs, coef={coef} "
              f"anchor={self._ks_anchor} decay={decay} "
              f"critic-warmup-until={self._ks_warmup_until}")

    def _ks_lambda(self) -> float:
        if self._ks_obs is None:
            return 0.0
        frac = (self.num_timesteps - self._ks_anchor) / max(1, self._ks_decay)
        # min() clamp: num_timesteps < anchor must not INFLATE the coefficient
        # (a fresh run with a stale hardcoded anchor would otherwise start at
        # multiples of the intended BC weight).
        return self._ks_coef * min(1.0, max(0.0, 1.0 - frac))

    def _excluded_save_params(self):
        return super()._excluded_save_params() + list(self.KS_ATTRS)

    def _ks_policy_params(self):
        return list(self.policy.mlp_extractor.policy_net.parameters()) + \
               list(self.policy.action_net.parameters())

    def _ks_apply_phase(self) -> None:
        """Critic warmup (PIRLNav): for the first N steps after a BC re-seed
        the policy tower stays FROZEN while normal rollouts train the value
        head — the stale critic otherwise produces garbage advantages that
        shred the clone within a few updates (observed twice with plain BC
        init). On unfreeze: fresh optimizer (the 800k-step Adam moments are
        calibrated to a different policy) + LR warmup handled in
        _update_learning_rate."""
        if self._ks_obs is None or self._ks_warmup_until <= 0:
            return
        t = self.num_timesteps
        if t < self._ks_warmup_until and not self._ks_frozen:
            for p in self._ks_policy_params():
                p.requires_grad = False
            self._ks_frozen = True
            print(f"[train] kickstart phase A: policy tower FROZEN until "
                  f"{self._ks_warmup_until} (t={t}); training value head only")
        elif t >= self._ks_warmup_until and self._ks_frozen:
            for p in self._ks_policy_params():
                p.requires_grad = True
            self.policy.optimizer = self.policy.optimizer_class(
                self.policy.parameters(), lr=self.lr_schedule(1.0),
                **self.policy.optimizer_kwargs)
            self._ks_frozen = False
            print(f"[train] kickstart phase B: policy UNFROZEN at t={t}, "
                  f"fresh optimizer, LR warmup over {self._ks_lr_warmup} steps")

    def _update_learning_rate(self, optimizers) -> None:
        super()._update_learning_rate(optimizers)
        # Phase-B LR warmup: scale the (constant) LR linearly from ~0 to full
        # over _ks_lr_warmup steps after the critic-warmup window ends.
        if self._ks_obs is None or self._ks_warmup_until <= 0:
            return
        t = self.num_timesteps
        if t < self._ks_warmup_until or t >= self._ks_warmup_until + self._ks_lr_warmup:
            return
        scale = max(0.02, (t - self._ks_warmup_until) / self._ks_lr_warmup)
        opts = optimizers if isinstance(optimizers, list) else [optimizers]
        for opt in opts:
            for g in opt.param_groups:
                g["lr"] *= scale
        self.logger.record("train/ks_lr_scale", scale)

    def train(self) -> None:
        self._ks_apply_phase()
        super().train()
        if self._ks_frozen:
            return                      # phase A: no BC steps into frozen params
        lam = self._ks_lambda()
        self.logger.record("train/ks_lambda", lam)
        if lam <= 0.0:
            return
        vec_env = self.get_vec_normalize_env()
        bc_loss_val = 0.0
        for _ in range(self._ks_steps):
            idx = np.random.randint(0, len(self._ks_obs), self._ks_batch)
            ob = self._ks_obs[idx]
            # Normalize with the CURRENT obs running stats — exactly what the
            # policy sees from the env (stats are not updated here; VecNormalize
            # only updates them inside step()).
            if vec_env is not None:
                ob = vec_env.normalize_obs(ob)
            ob_t = torch.as_tensor(ob, device=self.device)
            _, log_prob, _ = self.policy.evaluate_actions(ob_t, self._ks_act[idx])
            loss = -lam * log_prob.mean()
            self.policy.optimizer.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(self.policy.parameters(), self.max_grad_norm)
            self.policy.optimizer.step()
            bc_loss_val = float(-log_prob.mean().detach())
        self.logger.record("train/ks_bc_loss", bc_loss_val)


class EpInfoLogger(BaseCallback):
    """Log win/fell episode means (VecMonitor info_keywords) each rollout —
    the real stage-gate metrics; ep_rew_mean alone is too muddy."""
    def _on_step(self) -> bool:
        return True

    def _on_rollout_end(self) -> None:
        buf = list(self.model.ep_info_buffer or [])
        for key in ("win", "fell", "arms", "hits"):
            vals = [e[key] for e in buf if key in e]
            if vals:
                self.logger.record(f"rollout/{key}_mean", sum(vals) / len(vals))


def make_env(bridge_port: int, opp_mode: str = "hold", poll_hz: float = 20.0):
    def _thunk():
        return SFHeadlessEnv(bridge_port=bridge_port, my_slot=0, opp_slot=1,
                             poll_hz=poll_hz, max_steps=600, opp_mode=opp_mode,
                             randomize_slot=True)   # 2026-06-13: spatial diversity
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
    ap.add_argument("--kickstart-demos", default="",
                    help="npz of teacher (obs, act) pairs; enables the decaying "
                         "BC anchor inside PPO (KickstartPPO)")
    ap.add_argument("--ks-coef", type=float, default=0.5)
    ap.add_argument("--ks-anchor", type=int, default=-1,
                    help="num_timesteps where the decay starts; -1 = at load. "
                         "Pass a fixed value so supervisor relaunches don't "
                         "restart the decay clock.")
    ap.add_argument("--ks-decay", type=int, default=400_000)
    ap.add_argument("--ks-warmup-until", type=int, default=0,
                    help="absolute num_timesteps until which the policy tower "
                         "is frozen (critic warmup); 0 = no warmup phase")
    args = ap.parse_args()

    os.makedirs(MODELS, exist_ok=True)
    os.makedirs(LOGS, exist_ok=True)

    ports = [args.base_bridge + i for i in range(args.instances)]
    ts = fleet_timescale()
    poll_hz = 20.0 * ts
    print(f"[train] envs on bridge ports {ports} opp_mode={args.opp_mode} "
          f"timescale={ts} poll_hz={poll_hz}")
    venv = SubprocVecEnv([make_env(p, args.opp_mode, poll_hz) for p in ports])
    venv = VecMonitor(venv, info_keywords=("win", "fell", "arms", "hits"))
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

    algo = KickstartPPO if args.kickstart_demos else PPO
    if ckpt:
        print(f"[train] resuming from {ckpt}")
        # n_epochs/target_kl override on resume (2026-06-12, research rec):
        # samples are ~100x dearer than gradient steps at this env's fps —
        # reuse each rollout harder, with the KL early-stop as the guardrail.
        model = algo.load(ckpt, env=venv, device="cuda",
                          custom_objects={"n_epochs": 10, "target_kl": 0.02})
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
        model = algo(
            "MlpPolicy", venv, device="cuda", verbose=1,
            n_steps=512, batch_size=512, n_epochs=10, target_kl=0.02,
            gamma=0.995, gae_lambda=0.95, ent_coef=0.01,
            learning_rate=1e-4, clip_range=0.2,   # 2026-06-06: 3e-4 oscillated; lower for stability
            policy_kwargs=dict(net_arch=[256, 256]),
            tensorboard_log=LOGS,
        )
        reset_num = True

    if args.kickstart_demos:
        d = np.load(args.kickstart_demos)
        model.set_kickstart(d["obs"], d["act"], coef=args.ks_coef,
                            anchor=args.ks_anchor, decay=args.ks_decay,
                            warmup_until=args.ks_warmup_until)
        # Freeze normalization stats: obs_rms is converged after 800k steps,
        # and a drifting obs_rms silently shifts what the demo anchor pulls
        # toward (demos are normalized with the LIVE stats each minibatch).
        env_ref = model.get_vec_normalize_env()
        if env_ref is not None:
            env_ref.training = False
            print("[train] VecNormalize stats FROZEN (kickstart mode)")

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
