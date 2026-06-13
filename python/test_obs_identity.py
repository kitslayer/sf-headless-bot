"""Obs-identity test for the self-play refactor.

Proves _build_obs_for(snap, my_slot, opp_slot) reproduces the LEARNER's obs
byte-for-byte versus the PRE-refactor _build_obs. The pre-refactor function is
recovered from git (HEAD = the committed env before this worktree's edits),
bound onto the live env as _build_obs_ORIG, and compared on a hand-built mock
snapshot. Run: python test_obs_identity.py
"""
from __future__ import annotations

import os
import subprocess
import sys
import types

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from sf_headless_env import SFHeadlessEnv


# The PRE-refactor commit (parent of the self-play-v1 commit). The test pins
# the baseline here, NOT at HEAD: once the refactor is committed, HEAD IS the
# refactored file, so comparing against HEAD would be refactor-vs-refactor
# (trivially equal) and the test would stop guarding anything. Override with
# SF_OBS_BASELINE_REF=<rev> if the history is rewritten (rebase/squash).
ORIG_BASELINE_REF = os.environ.get("SF_OBS_BASELINE_REF", "285de2d")


def _load_orig_build_obs():
    """Extract the PRE-refactor _build_obs source from the baseline commit and
    compile it into a function we can bind onto the env as _build_obs_ORIG.
    This is the ground truth the refactored learner obs must match exactly."""
    src = subprocess.check_output(
        ["git", "-C", HERE, "show",
         f"{ORIG_BASELINE_REF}:python/sf_headless_env.py"],
        text=True,
    )
    lines = src.splitlines()
    # Grab the method body from `def _build_obs(self, snap):` up to the next
    # top-level method (`    def ` at 4-space indent) after it.
    start = next(i for i, l in enumerate(lines)
                 if l.strip() == "def _build_obs(self, snap):")
    end = start + 1
    while end < len(lines):
        l = lines[end]
        if l.startswith("    def ") and l.strip() != "def _build_obs(self, snap):":
            break
        end += 1
    method_lines = lines[start:end]
    # Dedent from 4 spaces to module level and rename to _build_obs_ORIG.
    method_src = "\n".join(ln[4:] if ln.startswith("    ") else ln
                           for ln in method_lines)
    method_src = method_src.replace("def _build_obs(self, snap):",
                                    "def _build_obs_ORIG(self, snap):", 1)
    # Exec in the env module's own namespace so module-level globals the body
    # references (_VOID_Z, _VOID_HORIZON, _ballistic_void_time, np, ...) resolve
    # exactly as they do for the live code.
    import sf_headless_env as _mod
    ns = dict(_mod.__dict__)
    code = compile(method_src, "<orig_build_obs>", "exec")
    exec(code, ns)
    return ns["_build_obs_ORIG"]


def make_mock_snap():
    """A realistic two-entity snapshot mirroring the real bridge schema: per-ent
    keys slot,x,y,z,vx,vy,vz,hp,alive,armed,grnd,rag,sw,sws,blk,bl,sj,ss,aimz,
    aimy,rays(list); top-level inFight,round,proj(list),wps(list)."""
    ent0 = {
        "slot": 0, "x": 1.5, "y": 2.25, "z": 3.5,
        "vx": -0.5, "vy": 1.2, "vz": -2.3,
        "hp": 73.0, "alive": True, "armed": True,
        "grnd": True, "rag": False, "sw": 0.4, "sws": True, "blk": False,
        "bl": 7.0, "sj": 0.2, "ss": 1.0, "aimz": 0.8, "aimy": -0.3,
        "rays": [3.1, 5.2, 19.9, 0.7, 12.0, 8.8, 1.1, 4.4,
                 6.6, 2.2, 9.9, 11.1, 0.3, 7.7, 13.3, 18.0],
    }
    ent1 = {
        "slot": 1, "x": -4.0, "y": 6.5, "z": -8.25,
        "vx": 0.9, "vy": -3.4, "vz": 5.1,
        "hp": 41.0, "alive": True, "armed": False,
        "grnd": False, "rag": True, "sw": 9.0, "sws": False, "blk": True,
        "bl": -1.0, "sj": 9.0, "ss": 9.0, "aimz": -0.6, "aimy": 0.2,
        "rays": [10.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0,
                 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0],
    }
    return {
        "inFight": True,
        "round": 5,
        "ents": [ent0, ent1],
        # 3 projectiles (pos_z, pos_y, dir_z, dir_y) — exercises the 2-nearest sort.
        "proj": [[2.0, 3.0, -1.0, 0.2],
                 [10.0, 9.0, 0.5, -0.5],
                 [0.5, 1.5, 0.0, 1.0]],
        # 3 ground weapons (z, y) — exercises the 2-nearest sort + clamp.
        "wps": [[5.0, 4.0], [-30.0, 2.0], [3.6, 2.3]],
    }


def main():
    orig = _load_orig_build_obs()
    env = SFHeadlessEnv.__new__(SFHeadlessEnv)  # no socket/IO, just need fields
    # Minimal field setup the obs path reads.
    env.my_slot = 0
    env.opp_slot = 1
    env._last_opp_z = 0.0
    env._aim_dz = 1.0
    env._aim_dy = 0.0
    env._build_obs_ORIG = types.MethodType(orig, env)

    snaps = [make_mock_snap()]
    # Also test slot-swapped (my_slot=1) to confirm the wrapper tracks self.*.
    failures = 0
    for label, my, opp in [("slots 0/1", 0, 1), ("slots 1/0 (swapped)", 1, 0)]:
        env.my_slot, env.opp_slot = my, opp
        env._aim_dz, env._aim_dy, env._last_opp_z = 1.0, 0.0, 0.0
        for snap in snaps:
            orig_obs, _, _ = env._build_obs_ORIG(snap)
            # snapshot the side-effect caches the ORIG produced
            orig_aim = (env._aim_dz, env._aim_dy)
            orig_zcache = env._last_opp_z
            env._aim_dz, env._aim_dy, env._last_opp_z = 1.0, 0.0, 0.0
            new_obs, _, _ = env._build_obs(snap)            # == _build_obs_for(snap, my, opp)
            new_aim = (env._aim_dz, env._aim_dy)
            new_zcache = env._last_opp_z
            try:
                np.testing.assert_array_equal(new_obs, orig_obs)
                assert new_aim == orig_aim, (new_aim, orig_aim)
                assert new_zcache == orig_zcache, (new_zcache, orig_zcache)
                print(f"PASS [{label}]: obs identical ({new_obs.shape}), "
                      f"aim cache {new_aim}, opp-z cache {new_zcache:.4f}")
            except AssertionError as e:
                failures += 1
                print(f"FAIL [{label}]: {e}")

    # Sanity: the OPPONENT-perspective obs (me=opp_slot) must be DIFFERENT from
    # the learner obs (proves the refactor actually flips perspective) AND must
    # NOT perturb the learner's aim cache.
    env.my_slot, env.opp_slot = 0, 1
    env._aim_dz, env._aim_dy, env._last_opp_z = 0.111, 0.222, 12.5
    learner_obs, _, _ = env._build_obs(make_mock_snap())          # me=0
    aim_after_learner = (env._aim_dz, env._aim_dy)
    opp_obs, _, _ = env._build_obs_for(make_mock_snap(), 1, 0)    # me=1, opp=0
    aim_after_opp = (env._aim_dz, env._aim_dy)
    if np.array_equal(learner_obs, opp_obs):
        failures += 1
        print("FAIL: opp-perspective obs equals learner obs (perspective not flipped)")
    else:
        print(f"PASS: opp-perspective obs differs from learner obs "
              f"(max|Δ|={np.max(np.abs(learner_obs - opp_obs)):.3f})")
    if aim_after_opp != aim_after_learner:
        failures += 1
        print(f"FAIL: opp obs build mutated learner aim cache "
              f"{aim_after_learner} -> {aim_after_opp}")
    else:
        print(f"PASS: opp obs build left learner aim cache untouched {aim_after_opp}")

    print()
    if failures:
        print(f"RESULT: {failures} FAILURE(S)")
        sys.exit(1)
    print("RESULT: ALL PASS — learner obs is byte-identical post-refactor")


if __name__ == "__main__":
    main()
