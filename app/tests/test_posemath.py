import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import slimevr  # noqa: E402
from posemath import (adjust_pose, apply_feet_forward, apply_stance, apply_toe_out, body_facing,  # noqa: E402
                      facing_yaw_deg, ideal_pose, matrix_from_quat, pitch_deg, quat_from_matrix, rest_offset,
                      slimevr_rest_pose, stance_m, symmetrize, toe_out_deg, yaw_frame)

X, Y, Z = np.eye(3)


def rot(axis, deg):
    a = np.radians(deg)
    x, y, z = np.asarray(axis, float) / np.linalg.norm(axis)
    K = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    return np.eye(3) + np.sin(a) * K + (1 - np.cos(a)) * K @ K


def head(yaw, pitch=0.0, roll=0.0):
    """HMD rotation: yaw about +Y, pitch up about the right axis (+X), roll about -Z."""
    return rot(Y, yaw) @ rot(X, pitch) @ rot(Z, roll)


class BodyFacing(unittest.TestCase):
    def facing_for(self, yaw):
        return rot(Y, yaw) @ np.array([0, 0, -1.0])

    def test_upright_head(self):
        for yaw in (0, 37, 90, -150):
            np.testing.assert_allclose(body_facing(head(yaw)), self.facing_for(yaw), atol=1e-12)

    def test_pitch_does_not_change_facing(self):
        for yaw in (0, 60, -120):
            for pitch in (-90, -89.9, -60, -10, 10, 60, 89.9, 90):
                np.testing.assert_allclose(body_facing(head(yaw, pitch)), self.facing_for(yaw), atol=1e-9,
                                           err_msg=f"yaw {yaw} pitch {pitch}")

    def test_lying_face_up_faces_the_feet(self):
        # Lying on the back, top of the head pointing +X (feet towards -X): the head looks
        # straight up and its up vector points along +X.
        R = np.column_stack([np.cross(X, -Y), X, -Y])  # columns right, up, back: forward = +Y, up = +X
        self.assertAlmostEqual(np.linalg.det(R), 1.0)
        np.testing.assert_allclose(body_facing(R), -X, atol=1e-12)
        self.assertAlmostEqual(pitch_deg(R), 90.0)

    def test_yaw_frame_is_a_rotation(self):
        for R in (head(20, 45, 10), head(-70, -89, 30), head(5)):
            F = yaw_frame(R)
            np.testing.assert_allclose(F.T @ F, np.eye(3), atol=1e-12)
            self.assertAlmostEqual(np.linalg.det(F), 1.0)
            np.testing.assert_allclose(F[:, 1], Y)


class RestPose(unittest.TestCase):
    def setUp(self):
        rng = np.random.default_rng(1)
        # A standing body: trackers fixed relative to an upright, slightly bobbing head.
        self.off = {
            "waist": (np.array([0.01, -0.72, 0.06]), rot(Y, 4) @ rot(X, -3)),
            "foot_L": (np.array([-0.11, -1.55, -0.04]), rot(Y, -8)),
        }
        n = 270
        yaw0 = 33.0
        self.hR = np.array([head(yaw0 + rng.normal(0, 0.5), rng.normal(0, 2)) for _ in range(n)])
        self.hp = np.array([0.4, 1.62, -1.1]) + rng.normal(0, 0.002, (n, 3))
        self.samples = {}
        for k, (p, R) in self.off.items():
            Fs = np.array([yaw_frame(h) for h in self.hR])
            self.samples[k] = (np.einsum("nij,jk->nik", Fs, R), self.hp + np.einsum("nij,j->ni", Fs, p))

    def test_capture_recovers_offsets(self):
        for k, (p, R) in self.off.items():
            pos, rr, ps, rs = rest_offset(self.hR, self.hp, *self.samples[k])
            np.testing.assert_allclose(pos, p, atol=1e-9)
            np.testing.assert_allclose(rr, R, atol=1e-9)
            self.assertLess(ps, 1e-6)
            self.assertLess(rs, 1e-3)

    def test_ideal_pose_follows_head_position_and_yaw_only(self):
        p, R = self.off["waist"]
        # Lying on the back on a bed: head 0.6 m up, looking at the ceiling, top of head towards -Z.
        hR = np.column_stack([np.cross(-Z, -Y), -Z, -Y])  # right = up x back
        hp = np.array([1.0, 0.6, 2.0])
        Ri, pi = ideal_pose(hR, hp, p, R)
        F = yaw_frame(hR)  # facing +Z (towards the feet)
        np.testing.assert_allclose(F[:, 2], -Z, atol=1e-12)
        np.testing.assert_allclose(pi, hp + F @ p)
        np.testing.assert_allclose(Ri, F @ R)
        self.assertAlmostEqual(pi[1], 0.6 - 0.72)  # straight down from the head, as if standing


class Symmetrize(unittest.TestCase):
    M = np.diag([-1.0, 1.0, 1.0])

    def body(self):
        """A symmetric standing body in the HMD yaw frame (x right, y up, z back)."""
        knee_R = rot(Y, -8) @ rot(X, 3)  # turned a bit outwards (right knee: to the right)
        foot_R = rot(Y, -15) @ rot(X, 88)  # SlimeVR feet: forward axis points up
        return {
            "human://WAIST": (np.array([0, -0.67, 0.08]), rot(X, -2)),
            "human://CHEST": (np.array([0, -0.26, 0.09]), rot(X, 3)),
            "human://RIGHT_KNEE": (np.array([0.095, -1.07, 0.09]), knee_R),
            "human://LEFT_KNEE": (np.array([-0.095, -1.07, 0.09]), self.M @ knee_R @ self.M),
            "human://RIGHT_FOOT": (np.array([0.092, -1.57, -0.03]), foot_R),
            "human://LEFT_FOOT": (np.array([-0.092, -1.57, -0.03]), self.M @ foot_R @ self.M),
        }

    def assertSame(self, a, b, tol=1e-9):
        self.assertEqual(set(a), set(b))
        for s in a:
            np.testing.assert_allclose(a[s][0], b[s][0], atol=tol, err_msg=s)
            np.testing.assert_allclose(a[s][1], b[s][1], atol=tol, err_msg=s)

    def test_symmetric_body_is_unchanged(self):
        b = self.body()
        out, rep = symmetrize(b)
        self.assertSame(out, b)
        self.assertAlmostEqual(rep["body_yaw_deg"], 0, places=9)
        self.assertAlmostEqual(rep["pair_mismatch_m"], 0, places=9)

    def test_body_turned_against_the_head_is_turned_back(self):
        b = self.body()
        turned = {s: (rot(Y, -4) @ p, rot(Y, -4) @ R) for s, (p, R) in b.items()}  # 4 deg to the right
        self.assertAlmostEqual(facing_yaw_deg(turned["human://WAIST"][1]), 4.0, places=6)
        out, rep = symmetrize(turned)
        self.assertAlmostEqual(rep["body_yaw_deg"], 4.0, places=6)
        self.assertSame(out, b)

    def test_left_right_mismatch_is_averaged(self):
        b = self.body()
        uneven = dict(b)
        p, R = b["human://LEFT_FOOT"]
        uneven["human://LEFT_FOOT"] = (p + np.array([-0.03, 0.01, -0.02]), rot(Y, 6) @ R)
        out, rep = symmetrize(uneven)
        pl, Rl = out["human://LEFT_FOOT"]
        pr, Rr = out["human://RIGHT_FOOT"]
        np.testing.assert_allclose(pr, self.M @ pl, atol=1e-12)
        np.testing.assert_allclose(Rr, self.M @ Rl @ self.M, atol=1e-12)
        np.testing.assert_allclose(pl, p + np.array([-0.015, 0.005, -0.01]), atol=1e-12)  # half way
        self.assertAlmostEqual(rep["pair_mismatch_m"], np.linalg.norm([0.03, 0.01, 0.02]), places=9)
        for R_ in (Rl, Rr):
            np.testing.assert_allclose(R_.T @ R_, np.eye(3), atol=1e-12)

    def test_torso_goes_to_the_midline(self):
        b = self.body()
        off = dict(b)
        off["human://WAIST"] = (np.array([0.02, -0.67, 0.08]), rot(Z, 5) @ rot(X, -2))  # leaning sideways
        out, _ = symmetrize(off)
        self.assertEqual(out["human://WAIST"][0][0], 0.0)
        R = out["human://WAIST"][1]
        np.testing.assert_allclose(R, self.M @ R @ self.M, atol=1e-12)  # no roll/yaw left


class Stance(unittest.TestCase):
    def setUp(self):
        self.body = Symmetrize().body()  # feet 18.4 cm apart

    def test_none_and_same_width_change_nothing(self):
        Symmetrize.assertSame(self, apply_stance(self.body, None), self.body)
        Symmetrize.assertSame(self, apply_stance(self.body, stance_m(self.body)), self.body, tol=1e-12)
        self.assertAlmostEqual(stance_m(self.body), 0.184)

    def test_wider_stance(self):
        out = apply_stance(self.body, 0.30)
        self.assertAlmostEqual(stance_m(out), 0.30, places=12)
        for side, sign in (("LEFT", -1), ("RIGHT", 1)):
            k0, f0 = self.body[f"human://{side}_KNEE"], self.body[f"human://{side}_FOOT"]
            k1, f1 = out[f"human://{side}_KNEE"], out[f"human://{side}_FOOT"]
            self.assertAlmostEqual(f1[0][0], sign * 0.15, places=12)
            moved_knee, moved_foot = sign * (k1[0][0] - k0[0][0]), sign * (f1[0][0] - f0[0][0])
            self.assertGreater(moved_knee, 0)  # outwards...
            self.assertLess(moved_knee, moved_foot * 0.6)  # ...but less than the foot
            self.assertGreater(f1[0][1], f0[0][1])  # a spread leg reaches less far down
            self.assertAlmostEqual(f1[0][2], f0[0][2], places=12)  # nothing moves forwards/back
            # knee and foot turn by the same angle, about the forward axis only
            dk, df = k1[1] @ k0[1].T, f1[1] @ f0[1].T
            np.testing.assert_allclose(dk, df, atol=1e-12)
            np.testing.assert_allclose(dk[:, 2], [0, 0, 1], atol=1e-12)
        M = np.diag([-1.0, 1.0, 1.0])  # still a mirror image
        np.testing.assert_allclose(out["human://LEFT_FOOT"][0], M @ out["human://RIGHT_FOOT"][0], atol=1e-12)
        for s in ("human://WAIST", "human://CHEST"):
            np.testing.assert_array_equal(out[s][0], self.body[s][0])

    def test_narrower_and_back(self):
        narrow = apply_stance(self.body, 0.12)
        self.assertAlmostEqual(stance_m(narrow), 0.12, places=12)
        Symmetrize.assertSame(self, apply_stance(narrow, 0.184), self.body, tol=1e-9)


class SlimeVrPose(unittest.TestCase):
    # A real SlimeVR v22 profile, and a symmetrized capture of the same body for comparison
    bones = {"NECK": 0.099655464, "UPPER_CHEST": 0.15944874, "LOWER_CHEST": 0.15944874, "UPPER_WAIST": 0.099655464,
             "LOWER_WAIST": 0.099655464, "HIP": 0.049827732, "HIPS_WIDTH": 0.19, "UPPER_LEG": 0.39862186,
             "LOWER_LEG": 0.49827728, "FOOT_LENGTH": 0.12456932, "FOOT_SHIFT": -0.019931093}
    captured = {"human://CHEST": [0, -0.26411, 0.10623], "human://WAIST": [0, -0.67223, 0.12489],
                "human://LEFT_KNEE": [-0.12178, -1.06808, 0.08661], "human://LEFT_FOOT": [-0.13447, -1.56636, -0.00801]}

    def test_chain(self):
        pose = slimevr_rest_pose(self.bones)
        self.assertEqual(len(pose), 6)
        np.testing.assert_allclose(pose["human://CHEST"][0], [0, -0.25910, 0.1], atol=1e-5)
        np.testing.assert_allclose(pose["human://WAIST"][0], [0, -0.66769, 0.1], atol=1e-5)
        np.testing.assert_allclose(pose["human://RIGHT_KNEE"][0], [0.095, -1.06631, 0.1], atol=1e-5)
        # the foot tracker sits at the toes: ankle 2 cm back (FOOT_SHIFT), then a foot length forward
        np.testing.assert_allclose(pose["human://RIGHT_FOOT"][0], [0.095, -1.56459, -0.00464], atol=1e-5)
        self.assertAlmostEqual(stance_m(pose), 0.19, places=12)
        for s, (p, R) in pose.items():
            np.testing.assert_allclose(R @ R.T, np.eye(3), atol=1e-12)
            if "FOOT" in s:  # toes forward: the tracker's +Z points down, its +Y back
                np.testing.assert_allclose(R @ Z, [0, -1, 0], atol=1e-12)
                np.testing.assert_allclose(R @ Y, [0, 0, 1], atol=1e-12)
            else:
                np.testing.assert_array_equal(R, np.eye(3))

    def test_matches_the_real_capture(self):
        # Same heights within 1 cm; sideways the capture stood 27 cm wide, back/forth it leaned a little.
        pose = slimevr_rest_pose(self.bones)
        for s, p in self.captured.items():
            self.assertLess(abs(pose[s][0][1] - p[1]), 0.01, s)
            self.assertLess(abs(pose[s][0][2] - p[2]), 0.03, s)

    def test_stance_turns_the_legs(self):
        pose = slimevr_rest_pose(self.bones)
        wide = apply_stance(pose, 0.27)
        self.assertAlmostEqual(stance_m(wide), 0.27, places=12)
        np.testing.assert_allclose(wide["human://WAIST"][0], pose["human://WAIST"][0])
        self.assertGreater(wide["human://RIGHT_KNEE"][0][0], 0.095)  # the knee moves out less than the foot
        self.assertLess(wide["human://RIGHT_KNEE"][0][0], 0.135)


class LegSettings(unittest.TestCase):
    pose = slimevr_rest_pose(SlimeVrPose.bones)
    M = np.diag([-1.0, 1.0, 1.0])

    def test_toe_out(self):
        self.assertAlmostEqual(toe_out_deg(self.pose), 0.0, places=9)
        out = apply_toe_out(self.pose, 15)
        self.assertAlmostEqual(toe_out_deg(out), 15.0, places=9)
        lk, lf = out["human://LEFT_KNEE"], out["human://LEFT_FOOT"]
        np.testing.assert_allclose(lk[0], self.pose["human://LEFT_KNEE"][0])  # the knee turns in place
        self.assertLess(lf[0][0], self.pose["human://LEFT_FOOT"][0][0])  # the left toes go further left
        self.assertAlmostEqual(lf[0][1], self.pose["human://LEFT_FOOT"][0][1], places=12)
        np.testing.assert_allclose(lk[1] @ Y, Y, atol=1e-12)  # turned about the upright only
        np.testing.assert_allclose(out["human://LEFT_FOOT"][0], self.M @ out["human://RIGHT_FOOT"][0], atol=1e-12)
        Symmetrize.assertSame(self, apply_toe_out(out, 0), self.pose, tol=1e-9)
        self.assertEqual(apply_toe_out(self.pose, None), self.pose)

    def test_toe_out_of_a_capture(self):
        # a real symmetrized capture: feet turned 12.6 deg out (the foot trackers pitched 90 deg)
        foot = np.array([[0.975981, 0.217457, 0.01318], [0.012092, 0.006329, -0.999907], [-0.217521, 0.976049, 0.003548]])
        trackers = {"human://LEFT_FOOT": (np.zeros(3), foot), "human://RIGHT_FOOT": (np.zeros(3), self.M @ foot @ self.M)}
        self.assertAlmostEqual(toe_out_deg(trackers), 12.56, places=1)

    def test_feet_forward(self):
        out = apply_feet_forward(self.pose, 0.05)
        for side in ("LEFT", "RIGHT"):
            k0, f0 = self.pose[f"human://{side}_KNEE"][0], self.pose[f"human://{side}_FOOT"][0]
            k1, f1 = out[f"human://{side}_KNEE"][0], out[f"human://{side}_FOOT"][0]
            self.assertAlmostEqual(f1[2], f0[2] - 0.05, places=12)  # the foot exactly 5 cm forward
            self.assertTrue(f0[2] > k1[2] - k0[2] + f0[2] > f1[2])  # the knee less
            self.assertLess(k1[2], k0[2])
            self.assertAlmostEqual(f1[0], f0[0], places=12)
            pivot = np.array([f0[0], *self.pose["human://WAIST"][0][1:]])
            self.assertAlmostEqual(np.linalg.norm(f1 - pivot), np.linalg.norm(f0 - pivot), places=12)  # leg length kept
        np.testing.assert_array_equal(out["human://WAIST"][0], self.pose["human://WAIST"][0])
        Symmetrize.assertSame(self, apply_feet_forward(out, -0.05), self.pose, tol=1e-9)
        self.assertEqual(apply_feet_forward(self.pose, 0), self.pose)

    def test_all_three(self):
        out = adjust_pose(self.pose, 0.30, 12, -0.03)
        self.assertAlmostEqual(stance_m(out), 0.30, places=2)  # the forward swing leaves the width
        self.assertAlmostEqual(toe_out_deg(out), 12, places=0)


class SlimeVrProfile(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "user").mkdir()

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, data):
        (self.root / name).write_text(json.dumps(data), encoding="utf-8")

    def test_selected_profile_and_scaled_defaults(self):
        self.write("global.json", {"selectedUserProfile": "tall"})
        self.write("user/default.json", {"userHeight": 1.2, "proportions": {"NECK": 0.5}})
        self.write("user/tall.json", {"userHeight": 1.884, "proportions": {"NECK": 0.11, "HIPS_WIDTH": 0.2}})
        bones, info = slimevr.load(self.root)
        self.assertEqual(info["profile"], "tall")
        self.assertEqual(bones["NECK"], 0.11)
        self.assertEqual(bones["HIPS_WIDTH"], 0.2)
        self.assertAlmostEqual(bones["UPPER_LEG"], 0.4 * 1.2, places=12)  # 1.884 = 1.2 x SlimeVR's default height
        self.assertAlmostEqual(bones["FOOT_SHIFT"], -0.02 * 1.2, places=12)
        self.assertNotIn("NECK", info["missing"])
        self.assertIn("UPPER_LEG", info["missing"])

    def test_no_global_json_means_default(self):
        self.write("user/default.json", {"userHeight": 1.57, "proportions": {}})
        bones, info = slimevr.load(self.root)
        self.assertEqual(info["profile"], "default")
        self.assertAlmostEqual(bones["LOWER_LEG"], 0.5, places=12)

    def test_errors(self):
        with self.assertRaises(slimevr.SlimeVrError):
            slimevr.load(self.root / "nincs")
        with self.assertRaises(slimevr.SlimeVrError):  # old SlimeVR (vrconfig.yml only)
            slimevr.load(self.root)
        for broken in ("{", "[1, 2]", '{"proportions": [1]}'):
            (self.root / "user/default.json").write_text(broken, encoding="utf-8")
            with self.assertRaises(slimevr.SlimeVrError, msg=broken):
                slimevr.load(self.root)


class Quaternions(unittest.TestCase):
    def test_round_trip(self):
        rng = np.random.default_rng(3)
        for _ in range(200):
            R = rot(rng.normal(size=3), rng.uniform(-180, 180))
            q = quat_from_matrix(R)
            self.assertGreaterEqual(q[0], 0)
            np.testing.assert_allclose(matrix_from_quat(q), R, atol=1e-12)
        for R in (np.eye(3), rot(X, 180), rot(Y, 180), rot(Z, 180)):
            np.testing.assert_allclose(matrix_from_quat(quat_from_matrix(R)), R, atol=1e-12)


if __name__ == "__main__":
    unittest.main()
