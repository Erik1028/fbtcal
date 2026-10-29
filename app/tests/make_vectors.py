"""Writes reference results of posemath.py to JSON, so tray/tests can check the C# port against them.

Usage:  python make_vectors.py out.json
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from posemath import (adjust_pose, apply_feet_forward, apply_stance, apply_toe_out, body_facing,  # noqa: E402
                      ideal_pose, pitch_deg, quat_from_matrix, rest_offset, slimevr_rest_pose, stance_m, symmetrize,
                      toe_out_deg, yaw_frame)


def rot(axis, deg):
    a = np.radians(deg)
    x, y, z = np.asarray(axis, float) / np.linalg.norm(axis)
    K = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    return np.eye(3) + np.sin(a) * K + (1 - np.cos(a)) * K @ K


def main(out):
    rng = np.random.default_rng(7)
    X, Y, Z = np.eye(3)
    heads = [rot(Y, yaw) @ rot(X, pitch) @ rot(Z, roll)
             for yaw in (0, 45, -135) for pitch in (-90, -89, -30, 0, 30, 89, 90) for roll in (0, 20)]
    heads += [rot(rng.normal(size=3), rng.uniform(-180, 180)) for _ in range(40)]
    cases = []
    for R in heads:
        off_p, off_R = rng.normal(0, 0.5, 3), rot(rng.normal(size=3), rng.uniform(-60, 60))
        hp = rng.normal(0, 1, 3)
        iR, ip = ideal_pose(R, hp, off_p, off_R)
        cases.append({"hmd": R.tolist(), "facing": body_facing(R).tolist(), "yaw_frame": yaw_frame(R).tolist(),
                      "pitch": pitch_deg(R), "quat": quat_from_matrix(R).tolist(),
                      "hmd_p": hp.tolist(), "off_p": off_p.tolist(), "off_R": off_R.tolist(),
                      "ideal_R": iR.tolist(), "ideal_p": ip.tolist()})

    # A capture: a slightly bobbing head, a tracker with small noise.
    n = 270
    hR = np.array([rot(Y, 20 + rng.normal(0, 1)) @ rot(X, rng.normal(0, 3)) for _ in range(n)])
    hp = np.array([0.3, 1.6, -0.5]) + rng.normal(0, 0.003, (n, 3))
    off_p, off_R = np.array([0.02, -0.7, 0.05]), rot([1, 2, 0.5], 25)
    F = np.array([yaw_frame(R) for R in hR])
    tR = np.array([f @ off_R @ rot(rng.normal(size=3), rng.normal(0, 0.5)) for f in F])
    tp = hp + np.einsum("nij,j->ni", F, off_p) + rng.normal(0, 0.004, (n, 3))
    pos, rr, ps, rs = rest_offset(hR, hp, tR, tp)
    capture = {"hmd_R": hR.tolist(), "hmd_p": hp.tolist(), "trk_R": tR.tolist(), "trk_p": tp.tolist(),
               "pos": pos.tolist(), "rot": rr.tolist(), "pos_spread": ps, "rot_spread": rs}

    # A lopsided standing pose (body turned against the head, left/right uneven) to symmetrize.
    M = np.diag([-1.0, 1.0, 1.0])
    knee, foot = rot(Y, -8) @ rot(X, 3), rot(Y, -15) @ rot(X, 88)
    body = {"human://WAIST": (np.array([0.0, -0.67, 0.08]), rot(X, -2)),
            "human://CHEST": (np.array([0.0, -0.26, 0.09]), rot(X, 3)),
            "human://RIGHT_KNEE": (np.array([0.09, -1.07, 0.1]), knee),
            "human://LEFT_KNEE": (np.array([-0.11, -1.06, 0.08]), M @ rot(Y, 5) @ knee @ M),
            "human://RIGHT_FOOT": (np.array([0.08, -1.57, -0.02]), foot),
            "human://LEFT_FOOT": (np.array([-0.1, -1.56, -0.04]), M @ foot @ M @ rot(X, 4))}
    lopsided = {s: (rot(Y, -4.5) @ p, rot(Y, -4.5) @ R) for s, (p, R) in body.items()}
    sym, rep = symmetrize(lopsided)
    as_json = lambda d: {s: {"pos": p.tolist(), "rot": R.tolist()} for s, (p, R) in d.items()}
    symmetry = {"in": as_json(lopsided), "out": as_json(sym), **rep}

    # The symmetrized pose spread to 32 cm and narrowed to 12 cm.
    stance = {"in": as_json(sym), "captured": stance_m(sym),
              "wide": as_json(apply_stance(sym, 0.32)), "narrow": as_json(apply_stance(sym, 0.12))}

    # SlimeVR's proportions -> the standing pose, and that spread to 30 cm.
    bones = {"NECK": 0.1, "UPPER_CHEST": 0.17, "LOWER_CHEST": 0.15, "UPPER_WAIST": 0.11, "LOWER_WAIST": 0.09, "HIP": 0.06,
             "HIPS_WIDTH": 0.21, "UPPER_LEG": 0.42, "LOWER_LEG": 0.48, "FOOT_LENGTH": 0.12, "FOOT_SHIFT": -0.03}
    slime_pose = slimevr_rest_pose(bones)
    slime = {"bones": bones, "pose": as_json(slime_pose), "wide": as_json(apply_stance(slime_pose, 0.30))}

    # The panel's leg settings on both kinds of pose: each one alone, then all three.
    legs = {}
    for name, pose in (("slimevr", slime_pose), ("capture", sym)):
        legs[name] = {"in": as_json(pose), "toe_out": toe_out_deg(pose),
                      "toe_15": as_json(apply_toe_out(pose, 15)), "forward_4": as_json(apply_feet_forward(pose, 0.04)),
                      "all": as_json(adjust_pose(pose, 0.30, 12, -0.03))}

    Path(out).write_text(json.dumps({"cases": cases, "capture": capture, "symmetry": symmetry, "stance": stance,
                                     "slimevr": slime, "legs": legs}), encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1])
