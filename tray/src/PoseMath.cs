using System;
using System.Collections.Generic;

namespace FbtCal
{
    // Port of app/posemath.py - the Python file is the reference, tray/tests compares the two.
    // OpenVR raw universe: +Y up, -Z forward, +X right, metres. Rotations are 3x3 device->world.

    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(s * a.X, s * a.Y, s * a.Z);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    public readonly struct Quat
    {
        public readonly double W, X, Y, Z;
        public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }
        public double Dot(Quat b) => W * b.W + X * b.X + Y * b.Y + Z * b.Z;
    }

    // Row-major 3x3.
    public readonly struct Mat3
    {
        public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public Mat3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static readonly Mat3 Identity = new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static Mat3 FromColumns(Vec3 a, Vec3 b, Vec3 c) => new Mat3(a.X, b.X, c.X, a.Y, b.Y, c.Y, a.Z, b.Z, c.Z);

        public double this[int r, int c] => (r * 3 + c) switch
        {
            0 => M00, 1 => M01, 2 => M02,
            3 => M10, 4 => M11, 5 => M12,
            6 => M20, 7 => M21, _ => M22,
        };

        public Vec3 Col(int c) => new Vec3(this[0, c], this[1, c], this[2, c]);
        public Mat3 T => new Mat3(M00, M10, M20, M01, M11, M21, M02, M12, M22);
        public double Trace => M00 + M11 + M22;

        public static Mat3 operator *(Mat3 a, Mat3 b) => new Mat3(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public static Vec3 operator *(Mat3 a, Vec3 v) => new Vec3(
            a.M00 * v.X + a.M01 * v.Y + a.M02 * v.Z,
            a.M10 * v.X + a.M11 * v.Y + a.M12 * v.Z,
            a.M20 * v.X + a.M21 * v.Y + a.M22 * v.Z);
    }

    public sealed class RestOffset
    {
        public Vec3 Pos;
        public Mat3 Rot;
        public double PosSpreadM;   // how still the body was during the capture
        public double RotSpreadDeg;
    }

    public static class PoseMath
    {
        const double Deg = 180.0 / Math.PI;

        // Unit horizontal direction a standing body under this headset would face. Plain forward
        // projection breaks down when the head looks straight up or down (lying on the back or front),
        // so the head's up vector is blended in: for a pure pitch both point the same way, and at
        // +-90 deg pitch the up vector alone still gives it (towards the feet when facing up).
        public static Vec3 BodyFacing(Mat3 hmd)
        {
            Vec3 f = -hmd.Col(2), u = hmd.Col(1);
            double sign = f.Y > 0 ? -1.0 : 1.0;
            var d = new Vec3(f.X + sign * u.X, 0, f.Z + sign * u.Z);
            double n = d.Length;
            return n < 1e-6 ? new Vec3(0, 0, -1) : (1 / n) * d;
        }

        // Rotation of the HMD yaw frame (columns: right, up, back) in the world.
        public static Mat3 YawFrame(Mat3 hmd)
        {
            Vec3 d = BodyFacing(hmd);
            return Mat3.FromColumns(d.Cross(Vec3.Up), Vec3.Up, -d);
        }

        // How far the headset looks up (+) or down (-) from the horizon.
        public static double PitchDeg(Mat3 hmd) => Math.Asin(Math.Clamp(-hmd.M12, -1, 1)) * Deg;

        // Facing as a Unity-style yaw (0 = OpenVR -Z, clockwise seen from above), for the log:
        // compare with VRChat's "Calibration alignment ... yaw" (that one has a per-world offset).
        public static double FacingYawDeg(Mat3 hmd)
        {
            Vec3 d = BodyFacing(hmd);
            double y = Math.Atan2(d.X, -d.Z) * Deg;
            return y < 0 ? y + 360 : y;
        }

        // Left <-> right in the HMD yaw frame (x = right).
        static readonly Mat3 Mirror = new Mat3(-1, 0, 0, 0, 1, 0, 0, 0, 1);

        // Rotation about +Y (positive turns -Z forward towards -X, i.e. to the left).
        public static Mat3 RotY(double deg)
        {
            double a = deg / Deg, c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, 0, s, 0, 1, 0, -s, 0, c);
        }

        // Which way a tracker's forward (-Z) points in the HMD yaw frame; + = turned right.
        public static double TrackerYawDeg(Mat3 r)
        {
            Vec3 f = -r.Col(2);
            return Math.Atan2(f.X, -f.Z) * Deg;
        }

        // Makes a captured standing pose face straight ahead and be left/right symmetric (port of
        // posemath.symmetrize). The torso trackers (names without LEFT/RIGHT: SlimeVR's WAIST and
        // CHEST) tell how far the body was turned against the head; that turn is undone. Then
        // LEFT_x / RIGHT_x pairs become mirror images of their average, and the torso moves onto
        // the midline.
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> Symmetrize(
            IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, out double bodyYawDeg, out double pairMismatchM)
        {
            double sin = 0, cos = 0;
            int torso = 0;
            foreach (var kv in trackers)
            {
                if (kv.Key.Contains("LEFT") || kv.Key.Contains("RIGHT")) continue;
                double a = TrackerYawDeg(kv.Value.rot) / Deg;
                sin += Math.Sin(a);
                cos += Math.Cos(a);
                torso++;
            }
            bodyYawDeg = torso > 0 ? Math.Atan2(sin / torso, cos / torso) * Deg : 0;
            Mat3 turn = RotY(bodyYawDeg);  // turned right by bodyYaw = RotY(-bodyYaw); this undoes it
            var t = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in trackers) t[kv.Key] = (turn * kv.Value.pos, turn * kv.Value.rot);

            var result = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            pairMismatchM = 0;
            foreach (var kv in t)
            {
                string s = kv.Key;
                string other = s.Contains("LEFT") ? s.Replace("LEFT", "RIGHT") : s.Replace("RIGHT", "LEFT");
                var (p, r) = kv.Value;
                if (s == other)  // torso: onto the midline, roll and yaw removed (pitch kept)
                    result[s] = (new Vec3(0, p.Y, p.Z), MeanRotation(new[] { r, Mirror * r * Mirror }));
                else if (!t.ContainsKey(other))  // half of a pair: nothing to mirror against
                    result[s] = (p, r);
                else if (s.Contains("LEFT"))
                {
                    var (po, ro) = t[other];
                    Vec3 pl = 0.5 * (p + Mirror * po);
                    Mat3 rl = MeanRotation(new[] { r, Mirror * ro * Mirror });
                    result[s] = (pl, rl);
                    result[other] = (Mirror * pl, Mirror * rl * Mirror);
                    pairMismatchM = Math.Max(pairMismatchM, (p - Mirror * po).Length);
                }
            }
            return result;
        }

        // Rotation about +Z (the yaw frame's back axis): positive moves a point below the origin to the right.
        public static Mat3 RotZ(double deg)
        {
            double a = deg / Deg, c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, -s, 0, s, c, 0, 0, 0, 1);
        }

        static string Find(IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, string part)
        {
            foreach (var kv in trackers)
                if (kv.Key.Contains(part)) return kv.Key;
            return null;
        }

        // Distance between the feet (centre to centre, sideways), or NaN.
        public static double StanceM(IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers)
        {
            string l = Find(trackers, "LEFT_FOOT"), r = Find(trackers, "RIGHT_FOOT");
            return l == null || r == null ? double.NaN : Math.Abs(trackers[r].pos.X - trackers[l].pos.X);
        }

        // Spreads or narrows the legs so the feet end up `stance` metres apart (null = as captured;
        // port of posemath.apply_stance). Each leg turns about its hip in the front plane: the knee
        // moves less than the foot, and both turn with the leg (SlimeVR without foot IMUs takes the
        // foot's rotation from the shin). The hip is where the foot->knee line reaches waist height.
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> ApplyStance(
            IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, double? stance)
        {
            var result = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in trackers) result[kv.Key] = kv.Value;
            string waist = Find(trackers, "WAIST");
            if (stance == null || waist == null) return result;
            double waistY = trackers[waist].pos.Y;
            foreach (var (side, sign) in new[] { ("LEFT", -1.0), ("RIGHT", 1.0) })
            {
                string knee = Find(trackers, side + "_KNEE"), foot = Find(trackers, side + "_FOOT");
                if (knee == null || foot == null) continue;
                var (kp, kr) = trackers[knee];
                var (fp, fr) = trackers[foot];
                if (kp.Y - fp.Y < 0.1) continue;  // not a standing leg
                double t = (waistY - kp.Y) / (kp.Y - fp.Y);
                var hip = new Vec3(kp.X + (kp.X - fp.X) * t, waistY, 0);
                Vec3 v = fp - hip;
                double length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
                double target = sign * stance.Value / 2 - hip.X;
                if (Math.Abs(target) >= length) continue;
                Mat3 turn = RotZ((Math.Asin(target / length) - Math.Atan2(v.X, -v.Y)) * Deg);
                result[knee] = (hip + turn * (kp - hip), turn * kr);
                result[foot] = (hip + turn * (fp - hip), turn * fr);
            }
            return result;
        }

        // Rotation about +X (the yaw frame's right axis): positive moves a point below the origin forward (-Z).
        public static Mat3 RotX(double deg)
        {
            double a = deg / Deg, c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(1, 0, 0, 0, c, -s, 0, s, c);
        }

        // Which way a leg tracker's toes point (+ = turned right). The knee's forward is its -Z;
        // SlimeVR's foot trackers are pitched 90 deg, there -Z points up and -Y forward.
        static double ToeYawDeg(Mat3 r)
        {
            var f = new Vec3(-r.M02, -r.M12, -r.M22);
            if (Math.Abs(f.Y) > 0.7) f = new Vec3(-r.M01, -r.M11, -r.M21);
            return Math.Atan2(f.X, -f.Z) * Deg;
        }

        // How far the feet turn out (+) or in (-), averaged over both feet, or NaN.
        public static double ToeOutDeg(IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers)
        {
            double sum = 0;
            int n = 0;
            foreach (var (side, sign) in new[] { ("LEFT", -1.0), ("RIGHT", 1.0) })
            {
                string foot = Find(trackers, side + "_FOOT");
                if (foot == null) continue;
                sum += sign * ToeYawDeg(trackers[foot].rot);
                n++;
            }
            return n == 0 ? double.NaN : sum / n;
        }

        // Turns the feet out (or in) to `deg` (null = as is; port of posemath.apply_toe_out): each leg
        // turns about the upright line through its knee, the knee tracker in place, the foot tracker
        // (at the toes) around it, both together like turning the whole leg at the hip.
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> ApplyToeOut(
            IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, double? deg)
        {
            var result = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in trackers) result[kv.Key] = kv.Value;
            if (deg == null) return result;
            foreach (var (side, sign) in new[] { ("LEFT", -1.0), ("RIGHT", 1.0) })
            {
                string knee = Find(trackers, side + "_KNEE"), foot = Find(trackers, side + "_FOOT");
                if (knee == null || foot == null) continue;
                var (kp, kr) = trackers[knee];
                var (fp, fr) = trackers[foot];
                double delta = deg.Value - sign * ToeYawDeg(fr);
                Mat3 turn = RotY(-sign * delta);  // out = to the left for the left leg, to the right for the right one
                result[knee] = (kp, turn * kr);
                result[foot] = (kp + turn * (fp - kp), turn * fr);
            }
            return result;
        }

        // Swings the legs forward (+) or back (-) so the feet move `forward` metres (port of
        // posemath.apply_feet_forward): each leg turns about the waist tracker's height and depth (the
        // hip, which does not move) in the side plane; the knee moves less, both pitch with the leg.
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> ApplyFeetForward(
            IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, double? forward)
        {
            var result = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in trackers) result[kv.Key] = kv.Value;
            string waist = Find(trackers, "WAIST");
            if (forward == null || forward.Value == 0 || waist == null) return result;
            var pivot = new Vec3(0, trackers[waist].pos.Y, trackers[waist].pos.Z);  // (x does not matter)
            foreach (string side in new[] { "LEFT", "RIGHT" })
            {
                string knee = Find(trackers, side + "_KNEE"), foot = Find(trackers, side + "_FOOT");
                if (knee == null || foot == null) continue;
                var (kp, kr) = trackers[knee];
                var (fp, fr) = trackers[foot];
                Vec3 v = fp - pivot;
                double length = Math.Sqrt(v.Y * v.Y + v.Z * v.Z);
                if (v.Y > -0.1 || Math.Abs(v.Z - forward.Value) >= length) continue;  // not a hanging leg, or too far
                Mat3 turn = RotX((Math.Atan2(v.Z, -v.Y) - Math.Asin((v.Z - forward.Value) / length)) * Deg);
                result[knee] = (pivot + turn * (kp - pivot), turn * kr);
                result[foot] = (pivot + turn * (fp - pivot), turn * fr);
            }
            return result;
        }

        // The panel's settings on a standing pose (port of posemath.adjust_pose): toe-out first (it moves
        // the toes sideways), then the stance makes the feet distance exact, then the forward swing.
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> AdjustPose(
            IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers, double? stanceM, double? toeOutDeg, double? forwardM) =>
            ApplyFeetForward(ApplyStance(ApplyToeOut(trackers, toeOutDeg), stanceM), forwardM);

        public const double SlimeVrHmdOffsetM = 0.10;  // SlimeVR: the head centre sits 10 cm behind the headset
        static readonly Mat3 SlimeVrFootRest = new Mat3(1, 0, 0, 0, 0, -1, 0, 1, 0);  // pitched 90 deg

        // The standing pose SlimeVR's own skeleton has right after a full reset, from its v22 bone
        // lengths (port of posemath.slimevr_rest_pose). The spine and legs hang straight down from
        // the head centre; its SteamVR driver puts each tracker at the tail of a bone: CHEST = upper
        // chest, WAIST = hip, KNEE = upper leg, FOOT = foot (the toes; FOOT_SHIFT moves the ankle back).
        public static Dictionary<string, (Vec3 pos, Mat3 rot)> SlimeVrRestPose(IReadOnlyDictionary<string, double> b)
        {
            double chest = b["NECK"] + b["UPPER_CHEST"];
            double waist = chest + b["LOWER_CHEST"] + b["UPPER_WAIST"] + b["LOWER_WAIST"] + b["HIP"];
            double knee = waist + b["UPPER_LEG"];
            double ankle = knee + b["LOWER_LEG"];
            double z = SlimeVrHmdOffsetM;
            double toeZ = z - b["FOOT_SHIFT"] - b["FOOT_LENGTH"];
            var pose = new Dictionary<string, (Vec3 pos, Mat3 rot)>
            {
                ["human://CHEST"] = (new Vec3(0, -chest, z), Mat3.Identity),
                ["human://WAIST"] = (new Vec3(0, -waist, z), Mat3.Identity),
            };
            foreach (var (side, sign) in new[] { ("LEFT", -1.0), ("RIGHT", 1.0) })
            {
                double x = sign * b["HIPS_WIDTH"] / 2;
                pose[$"human://{side}_KNEE"] = (new Vec3(x, -knee, z), Mat3.Identity);
                pose[$"human://{side}_FOOT"] = (new Vec3(x, -ankle, toeZ), SlimeVrFootRest);
            }
            return pose;
        }

        // Where a tracker would be if the body stood straight under this headset.
        public static (Mat3 rot, Vec3 pos) IdealPose(Mat3 hmdR, Vec3 hmdP, Vec3 offPos, Mat3 offRot)
        {
            Mat3 y = YawFrame(hmdR);
            return (y * offRot, hmdP + y * offPos);
        }

        // A tracker's pose in the HMD yaw frame, averaged over the samples of a capture.
        public static RestOffset ComputeRestOffset(IReadOnlyList<Mat3> hmdR, IReadOnlyList<Vec3> hmdP,
                                                   IReadOnlyList<Mat3> trkR, IReadOnlyList<Vec3> trkP)
        {
            int n = hmdR.Count;
            var pos = new Vec3[n];
            var rot = new Mat3[n];
            Vec3 sum = Vec3.Zero;
            for (int i = 0; i < n; i++)
            {
                Mat3 yt = YawFrame(hmdR[i]).T;
                pos[i] = yt * (trkP[i] - hmdP[i]);
                rot[i] = yt * trkR[i];
                sum = sum + pos[i];
            }
            Vec3 mean = (1.0 / n) * sum;
            Mat3 meanRot = MeanRotation(rot);
            var posDev = new double[n];
            var rotDev = new double[n];
            for (int i = 0; i < n; i++)
            {
                posDev[i] = (pos[i] - mean).Length;
                rotDev[i] = AngleDeg(meanRot.T * rot[i]);
            }
            return new RestOffset { Pos = mean, Rot = meanRot, PosSpreadM = Percentile(posDev, 90), RotSpreadDeg = Percentile(rotDev, 90) };
        }

        // Average of nearby rotations: sign-aligned quaternion mean (Python uses an SVD; same result
        // for the near-identical samples of a capture).
        public static Mat3 MeanRotation(IReadOnlyList<Mat3> rots)
        {
            Quat first = QuatFromMatrix(rots[0]);
            double w = 0, x = 0, y = 0, z = 0;
            foreach (Mat3 r in rots)
            {
                Quat q = QuatFromMatrix(r);
                double s = q.Dot(first) < 0 ? -1 : 1;
                w += s * q.W; x += s * q.X; y += s * q.Y; z += s * q.Z;
            }
            double n = Math.Sqrt(w * w + x * x + y * y + z * z);
            return MatrixFromQuat(new Quat(w / n, x / n, y / n, z / n));
        }

        public static double AngleDeg(Mat3 r) => Math.Acos(Math.Clamp((r.Trace - 1) / 2, -1, 1)) * Deg;

        // numpy.percentile's default (linear interpolation).
        public static double Percentile(double[] values, double p)
        {
            var v = (double[])values.Clone();
            Array.Sort(v);
            double pos = (v.Length - 1) * p / 100.0;
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, v.Length - 1);
            return v[lo] + (v[hi] - v[lo]) * (pos - lo);
        }

        // Rotation matrix -> quaternion (w, x, y, z), w >= 0.
        public static Quat QuatFromMatrix(Mat3 m)
        {
            double t = m.Trace, w, x, y, z;
            if (t > 0)
            {
                double s = Math.Sqrt(t + 1.0) * 2;
                w = 0.25 * s; x = (m.M21 - m.M12) / s; y = (m.M02 - m.M20) / s; z = (m.M10 - m.M01) / s;
            }
            else if (m.M00 > m.M11 && m.M00 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M00 - m.M11 - m.M22) * 2;
                w = (m.M21 - m.M12) / s; x = 0.25 * s; y = (m.M01 + m.M10) / s; z = (m.M02 + m.M20) / s;
            }
            else if (m.M11 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M11 - m.M00 - m.M22) * 2;
                w = (m.M02 - m.M20) / s; x = (m.M01 + m.M10) / s; y = 0.25 * s; z = (m.M12 + m.M21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m.M22 - m.M00 - m.M11) * 2;
                w = (m.M10 - m.M01) / s; x = (m.M02 + m.M20) / s; y = (m.M12 + m.M21) / s; z = 0.25 * s;
            }
            double n = Math.Sqrt(w * w + x * x + y * y + z * z);
            double sg = w >= 0 ? 1 / n : -1 / n;
            return new Quat(w * sg, x * sg, y * sg, z * sg);
        }

        public static Mat3 MatrixFromQuat(Quat q)
        {
            double w = q.W, x = q.X, y = q.Y, z = q.Z;
            return new Mat3(
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
        }
    }
}
