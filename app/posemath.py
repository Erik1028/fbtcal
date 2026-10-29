"""Pose maths: the HMD's body-yaw frame, the rest-pose capture and the ideal pose.

OpenVR raw universe: +Y up, -Z forward, +X right, metres. Rotations are 3x3 device->world.

The idea: VRChat's calibration pins an upright avatar to the headset (position + yaw). So
the "ideal" tracker poses are the ones a straight-standing body would have under that
headset: the rest pose captured once, rotated into the current HMD yaw frame.
"""
from __future__ import annotations

import numpy as np

UP = np.array([0.0, 1.0, 0.0])


def body_facing(R_hmd):
    """Unit horizontal direction a standing body under this headset would face.

    Plain forward projection breaks down when the head looks straight up or down (lying
    on the back or front). So the head's up vector is blended in: for a pure pitch both
    point the same way, and at +-90 deg pitch the up vector alone still gives it
    (towards the feet when facing up), like Unity's Euler yaw at the gimbal lock.
    """
    f, u = -R_hmd[:, 2], R_hmd[:, 1]
    sign = -1.0 if f[1] > 0 else 1.0
    d = np.array([f[0] + sign * u[0], 0.0, f[2] + sign * u[2]])
    n = np.linalg.norm(d)
    if n < 1e-6:  # head exactly on its side, pointing straight up: no defined facing
        return np.array([0.0, 0.0, -1.0])
    return d / n


def yaw_frame(R_hmd):
    """Rotation of the HMD yaw frame (columns: right, up, back) in the world."""
    d = body_facing(R_hmd)
    return np.column_stack([np.cross(d, UP), UP, -d])


def pitch_deg(R_hmd):
    """How far the headset looks up (+) or down (-) from the horizon."""
    return float(np.degrees(np.arcsin(np.clip(-R_hmd[1, 2], -1, 1))))


def mean_rotation(R):
    """Closest rotation to the average of (N,3,3) rotations."""
    U, _, Vt = np.linalg.svd(R.mean(axis=0))
    if np.linalg.det(U @ Vt) < 0:
        U[:, -1] *= -1
    return U @ Vt


def rotation_angle_deg(R):
    """Angle of each rotation in (N,3,3)."""
    cos = np.clip((np.trace(R, axis1=-2, axis2=-1) - 1) / 2, -1, 1)
    return np.degrees(np.arccos(cos))


def rest_offset(R_hmd, p_hmd, R_trk, p_trk):
    """A tracker's pose in the HMD yaw frame, averaged over the samples of a capture.

    Returns (pos (3,), rot (3,3), pos_spread_m, rot_spread_deg); the spreads show how
    still the body was."""
    Y = np.array([yaw_frame(R) for R in R_hmd])
    pos = np.einsum("nji,nj->ni", Y, p_trk - p_hmd)
    rot = np.einsum("nji,njk->nik", Y, R_trk)
    pm, rm = pos.mean(axis=0), mean_rotation(rot)
    pos_spread = float(np.percentile(np.linalg.norm(pos - pm, axis=1), 90))
    rot_spread = float(np.percentile(rotation_angle_deg(np.einsum("ji,njk->nik", rm, rot)), 90))
    return pm, rm, pos_spread, rot_spread


MIRROR = np.diag([-1.0, 1.0, 1.0])  # left <-> right in the HMD yaw frame (x = right)


def rot_y(deg):
    """Rotation about +Y (positive turns -Z forward towards -X, i.e. to the left)."""
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def facing_yaw_deg(R):
    """Which way a tracker's forward (-Z) points in the HMD yaw frame; + = turned right."""
    f = -R[:, 2]
    return float(np.degrees(np.arctan2(f[0], -f[2])))


def symmetrize(trackers):
    """Make a captured standing pose face straight ahead and be left/right symmetric.

    trackers: {serial: (pos, rot)} in the HMD yaw frame. The torso trackers (names without
    LEFT/RIGHT, i.e. SlimeVR's WAIST and CHEST) tell how far the body was turned against the
    head; that turn is undone. Then LEFT_x / RIGHT_x pairs become mirror images of their
    average, and the torso trackers move onto the midline. Returns (trackers, report).
    """
    torso = [s for s in trackers if "LEFT" not in s and "RIGHT" not in s]
    yaws = np.radians([facing_yaw_deg(trackers[s][1]) for s in torso])
    body_yaw = float(np.degrees(np.arctan2(np.sin(yaws).mean(), np.cos(yaws).mean()))) if len(yaws) else 0.0
    turn = rot_y(body_yaw)  # turned right by body_yaw = rot_y(-body_yaw); this undoes it
    t = {s: (turn @ p, turn @ R) for s, (p, R) in trackers.items()}

    out, mismatch = {}, 0.0
    for s, (p, R) in t.items():
        other = s.replace("LEFT", "RIGHT") if "LEFT" in s else s.replace("RIGHT", "LEFT")
        if s == other:  # torso: onto the midline, roll and yaw removed (pitch kept)
            out[s] = (np.array([0.0, p[1], p[2]]), mean_rotation(np.array([R, MIRROR @ R @ MIRROR])))
        elif other not in t:  # half of a pair: nothing to mirror against
            out[s] = (p, R)
        elif "LEFT" in s:
            po, Ro = t[other]
            pl = (p + MIRROR @ po) / 2
            Rl = mean_rotation(np.array([R, MIRROR @ Ro @ MIRROR]))
            out[s], out[other] = (pl, Rl), (MIRROR @ pl, MIRROR @ Rl @ MIRROR)
            mismatch = max(mismatch, float(np.linalg.norm(p - MIRROR @ po)))
    return out, {"body_yaw_deg": body_yaw, "pair_mismatch_m": mismatch}


def rot_z(deg):
    """Rotation about +Z (the yaw frame's back axis): positive moves a point below the origin to the right."""
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def _find(trackers, part):
    return next((s for s in trackers if part in s), None)


def stance_m(trackers):
    """Distance between the feet (centre to centre, sideways) in a standing pose, or None."""
    l, r = _find(trackers, "LEFT_FOOT"), _find(trackers, "RIGHT_FOOT")
    return None if l is None or r is None else float(abs(trackers[r][0][0] - trackers[l][0][0]))


def apply_stance(trackers, stance):
    """Spread or narrow the legs so the feet end up `stance` metres apart (None = as captured).

    Each leg turns about its hip in the front plane, like standing wider or narrower: the knee
    tracker moves less than the foot, and both turn with the leg (SlimeVR without foot IMUs
    takes the foot's rotation from the shin). The hip is where the foot->knee line reaches
    waist height. Torso trackers stay put.
    """
    out = dict(trackers)
    waist = _find(trackers, "WAIST")
    if stance is None or waist is None:
        return out
    waist_y = trackers[waist][0][1]
    for side, sign in (("LEFT", -1.0), ("RIGHT", 1.0)):
        knee, foot = _find(trackers, side + "_KNEE"), _find(trackers, side + "_FOOT")
        if knee is None or foot is None:
            continue
        (kp, kR), (fp, fR) = trackers[knee], trackers[foot]
        if kp[1] - fp[1] < 0.1:  # not a standing leg
            continue
        t = (waist_y - kp[1]) / (kp[1] - fp[1])
        hip = np.array([kp[0] + (kp[0] - fp[0]) * t, waist_y, 0.0])
        v = fp - hip
        length = float(np.hypot(v[0], v[1]))
        target = sign * stance / 2 - hip[0]
        if abs(target) >= length:
            continue
        turn = rot_z(np.degrees(np.arcsin(target / length) - np.arctan2(v[0], -v[1])))
        for key, (p, R) in ((knee, (kp, kR)), (foot, (fp, fR))):
            out[key] = (hip + turn @ (p - hip), turn @ R)
    return out


def rot_x(deg):
    """Rotation about +X (the yaw frame's right axis): positive moves a point below the origin forward (-Z)."""
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def _toe_yaw_deg(R):
    """Which way a leg tracker's toes point in the HMD yaw frame; + = turned right.

    The knee's forward is its -Z; SlimeVR's foot trackers are pitched 90 deg, there -Z points up
    and -Y forward, so the more horizontal of the two is taken."""
    f = -R[:, 2]
    if abs(f[1]) > 0.7:
        f = -R[:, 1]
    return float(np.degrees(np.arctan2(f[0], -f[2])))


def toe_out_deg(trackers):
    """How far the feet turn out (+) or in (-), averaged over both feet, or None."""
    out = []
    for side, sign in (("LEFT", -1.0), ("RIGHT", 1.0)):
        foot = _find(trackers, side + "_FOOT")
        if foot is not None:
            out.append(sign * _toe_yaw_deg(trackers[foot][1]))
    return float(np.mean(out)) if out else None


def apply_toe_out(trackers, deg):
    """Turn the feet out (or in) to `deg` (None = as is): each leg turns about the upright line through
    its knee, the knee tracker in place, the foot tracker (at the toes) around it. Both turn together,
    like turning the whole leg at the hip (SlimeVR without foot IMUs takes the foot from the shin)."""
    out = dict(trackers)
    if deg is None:
        return out
    for side, sign in (("LEFT", -1.0), ("RIGHT", 1.0)):
        knee, foot = _find(trackers, side + "_KNEE"), _find(trackers, side + "_FOOT")
        if knee is None or foot is None:
            continue
        (kp, kR), (fp, fR) = trackers[knee], trackers[foot]
        delta = deg - sign * _toe_yaw_deg(fR)
        turn = rot_y(-sign * delta)  # out = to the left for the left leg, to the right for the right one
        out[knee] = (kp, turn @ kR)
        out[foot] = (kp + turn @ (fp - kp), turn @ fR)
    return out


def apply_feet_forward(trackers, forward):
    """Swing the legs forward (+) or back (-) so the feet move `forward` metres (0/None = as is).

    Each leg turns about the waist tracker's height and depth (the hip, which does not move), in the
    side plane: the foot moves exactly that far, the knee less, and both trackers pitch with the leg.
    Torso trackers stay put."""
    out = dict(trackers)
    waist = _find(trackers, "WAIST")
    if not forward or waist is None:
        return out
    pivot = np.array([0.0, trackers[waist][0][1], trackers[waist][0][2]])  # (x does not matter)
    for side in ("LEFT", "RIGHT"):
        knee, foot = _find(trackers, side + "_KNEE"), _find(trackers, side + "_FOOT")
        if knee is None or foot is None:
            continue
        (kp, kR), (fp, fR) = trackers[knee], trackers[foot]
        v = fp - pivot
        length = float(np.hypot(v[1], v[2]))
        if v[1] > -0.1 or abs(v[2] - forward) >= length:  # not a hanging leg, or too far
            continue
        turn = rot_x(np.degrees(np.arctan2(v[2], -v[1]) - np.arcsin((v[2] - forward) / length)))
        for key, (p, R) in ((knee, (kp, kR)), (foot, (fp, fR))):
            out[key] = (pivot + turn @ (p - pivot), turn @ R)
    return out


def adjust_pose(trackers, stance=None, toe_out=None, forward=None):
    """The panel's settings on a standing pose: stance (m), toe-out (deg), feet forward (m).

    Toe-out goes first: it moves the toes (where SlimeVR's foot trackers sit) sideways, and the stance
    after it then makes the feet distance exactly what the panel shows. The forward swing keeps both."""
    return apply_feet_forward(apply_stance(apply_toe_out(trackers, toe_out), stance), forward)


SLIMEVR_HMD_OFFSET_M = 0.10  # SlimeVR: the head centre sits 10 cm behind the headset
SLIMEVR_FOOT_REST = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])  # pitched 90 deg


def slimevr_rest_pose(b):
    """The standing pose SlimeVR's own skeleton has right after a full reset, from its body proportions.

    b: SlimeVR v22 bone lengths in metres (NECK, UPPER_CHEST, ..., FOOT_LENGTH, FOOT_SHIFT). Same chain
    as the SlimeVR server: the spine and legs hang straight down from the head centre, and its SteamVR
    driver puts each tracker at the tail of a bone: CHEST = upper chest, WAIST = hip, KNEE = upper leg,
    FOOT = foot (at the toes; FOOT_SHIFT moves the ankle back). Rotations as SlimeVR has them at rest:
    upright, the foot trackers pitched 90 deg. In the HMD yaw frame, like a capture.
    """
    chest = b["NECK"] + b["UPPER_CHEST"]
    waist = chest + b["LOWER_CHEST"] + b["UPPER_WAIST"] + b["LOWER_WAIST"] + b["HIP"]
    knee = waist + b["UPPER_LEG"]
    ankle = knee + b["LOWER_LEG"]
    z = SLIMEVR_HMD_OFFSET_M
    toe_z = z - b["FOOT_SHIFT"] - b["FOOT_LENGTH"]
    pose = {"human://CHEST": (np.array([0.0, -chest, z]), np.eye(3)),
            "human://WAIST": (np.array([0.0, -waist, z]), np.eye(3))}
    for side, sign in (("LEFT", -1.0), ("RIGHT", 1.0)):
        x = sign * b["HIPS_WIDTH"] / 2
        pose[f"human://{side}_KNEE"] = (np.array([x, -knee, z]), np.eye(3))
        pose[f"human://{side}_FOOT"] = (np.array([x, -ankle, toe_z]), SLIMEVR_FOOT_REST.copy())
    return pose


def ideal_pose(R_hmd, p_hmd, off_pos, off_rot):
    """Where a tracker would be if the body stood straight under this headset."""
    Y = yaw_frame(R_hmd)
    return Y @ off_rot, p_hmd + Y @ off_pos


def quat_from_matrix(R):
    """Rotation matrix -> quaternion (w, x, y, z), w >= 0."""
    m = R
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        q = [0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [(m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s]
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s]
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s]
    q = np.array(q)
    q /= np.linalg.norm(q)
    return q if q[0] >= 0 else -q


def matrix_from_quat(q):
    w, x, y, z = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
