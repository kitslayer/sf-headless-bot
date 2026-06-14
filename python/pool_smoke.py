"""Offline smoke test for the self-play LEAGUE POOL (no bridge/match needed).

Bypasses SFHeadlessEnv.__init__ (which binds sockets) via __new__, sets the
minimal opp-loading attrs, then exercises _load_selfplay_opponent / _select_opp
/ predict for both single-opp (backward compat) and 3-opp pool modes.

    python pool_smoke.py
"""
import os
import sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
ROOT = os.path.abspath(os.path.join(HERE, ".."))

CKPTS = [
    "models/ppo_headless_1104000_steps.zip",
    "models/ppo_headless_1327996_steps.zip",
    "models/ppo_headless_1487996_steps.zip",
]


def _fresh():
    from sf_headless_env import SFHeadlessEnv
    e = SFHeadlessEnv.__new__(SFHeadlessEnv)  # skip __init__ (no socket/bridge)
    e._opp_pool = []
    e._opp_model = None
    e._opp_obs_mean = None
    e._opp_obs_var = None
    e._opp_obs_eps = 1e-8
    e._opp_obs_clip = 50.0
    e._opp_name = ""
    return e


def test_single():
    os.environ.pop("SF_SELFPLAY_POOL", None)
    os.environ["SF_SELFPLAY_CKPT"] = CKPTS[1]
    e = _fresh()
    e._load_selfplay_opponent()
    assert len(e._opp_pool) == 1, f"single pool size {len(e._opp_pool)}"
    assert e._opp_model is not None, "single: model not active"
    assert e._opp_obs_mean is not None, "single: vecnorm not loaded"
    print(f"[single] OK pool=1 active={e._opp_name}")


def test_pool_via_ckpt_overload():
    """Deploy path used live: multi-entry SF_SELFPLAY_CKPT (the var the existing
    supervisor already exports) must be treated as a pool, no SF_SELFPLAY_POOL."""
    os.environ.pop("SF_SELFPLAY_POOL", None)
    os.environ["SF_SELFPLAY_CKPT"] = "\n".join(CKPTS)  # multi-line, as cat run/SELFPLAY_CKPT
    e = _fresh()
    e._load_selfplay_opponent()
    assert len(e._opp_pool) == 3, f"ckpt-overload pool size {len(e._opp_pool)}"
    print(f"[overload] OK multi-entry SF_SELFPLAY_CKPT -> pool=3")


def test_pool():
    os.environ.pop("SF_SELFPLAY_CKPT", None)
    os.environ["SF_SELFPLAY_POOL"] = " ".join(CKPTS)
    e = _fresh()
    e._load_selfplay_opponent()
    assert len(e._opp_pool) == 3, f"pool size {len(e._opp_pool)}"
    # each member must carry its OWN vecnorm stats
    for o in e._opp_pool:
        assert o["mean"] is not None, f"{o['name']} missing vecnorm"
    # uniform sampling must cover all 3 over many draws
    names = set()
    for _ in range(60):
        e._select_opp()
        names.add(e._opp_name)
    assert len(names) == 3, f"sampling only covered {names}"
    # predict path works end-to-end on a 78-dim obs (normalize -> predict)
    obs = np.zeros(78, dtype=np.float32)
    e._select_opp()
    a, _ = e._opp_model.predict(e._normalize_opp_obs(obs), deterministic=True)
    assert len(a) == 3, f"action shape {a}"
    print(f"[pool] OK pool=3 sampled={sorted(names)} predict={list(map(int, a))}")


if __name__ == "__main__":
    test_single()
    test_pool_via_ckpt_overload()
    test_pool()
    print("ALL POOL SMOKE TESTS PASSED")
