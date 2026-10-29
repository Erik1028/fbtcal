// Checks the C# port without SteamVR:
//   dotnet run -c Release -- <vectors.json> <hooktest.exe> <driver_fbtcal.dll>
// vectors.json comes from app/tests/make_vectors.py, the Python reference.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace FbtCal.Tests
{
    static class Program
    {
        static int _failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }

        static Mat3 M(JsonNode n) => new Mat3(
            (double)n[0][0], (double)n[0][1], (double)n[0][2],
            (double)n[1][0], (double)n[1][1], (double)n[1][2],
            (double)n[2][0], (double)n[2][1], (double)n[2][2]);

        static Vec3 V(JsonNode n) => new Vec3((double)n[0], (double)n[1], (double)n[2]);

        static double Diff(Mat3 a, Mat3 b)
        {
            double d = 0;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a[r, c] - b[r, c]));
            return d;
        }

        static double Diff(Vec3 a, Vec3 b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

        static void PoseMathAgainstPython(string vectorsPath)
        {
            Console.WriteLine("PoseMath vs posemath.py");
            var root = JsonNode.Parse(File.ReadAllText(vectorsPath));
            var cases = root["cases"].AsArray();
            double facing = 0, frame = 0, pitch = 0, quat = 0, ideal = 0;
            foreach (var c in cases)
            {
                Mat3 h = M(c["hmd"]);
                facing = Math.Max(facing, Diff(PoseMath.BodyFacing(h), V(c["facing"])));
                frame = Math.Max(frame, Diff(PoseMath.YawFrame(h), M(c["yaw_frame"])));
                pitch = Math.Max(pitch, Math.Abs(PoseMath.PitchDeg(h) - (double)c["pitch"]));
                Quat q = PoseMath.QuatFromMatrix(h);
                var qr = c["quat"].AsArray();
                quat = Math.Max(quat, new[] { q.W - (double)qr[0], q.X - (double)qr[1], q.Y - (double)qr[2], q.Z - (double)qr[3] }.Max(Math.Abs));
                var (ir, ip) = PoseMath.IdealPose(h, V(c["hmd_p"]), V(c["off_p"]), M(c["off_R"]));
                ideal = Math.Max(ideal, Math.Max(Diff(ir, M(c["ideal_R"])), Diff(ip, V(c["ideal_p"]))));
            }
            Check(facing < 1e-9, $"BodyFacing, {cases.Count} heads incl. +-90 deg pitch (max diff {facing:E1})");
            Check(frame < 1e-9, $"YawFrame (max diff {frame:E1})");
            Check(pitch < 1e-9, $"PitchDeg (max diff {pitch:E1})");
            Check(quat < 1e-9, $"QuatFromMatrix (max diff {quat:E1})");
            Check(ideal < 1e-9, $"IdealPose (max diff {ideal:E1})");

            var cap = root["capture"];
            var hR = cap["hmd_R"].AsArray().Select(M).ToList();
            var hp = cap["hmd_p"].AsArray().Select(V).ToList();
            var tR = cap["trk_R"].AsArray().Select(M).ToList();
            var tp = cap["trk_p"].AsArray().Select(V).ToList();
            var off = PoseMath.ComputeRestOffset(hR, hp, tR, tp);
            Check(Diff(off.Pos, V(cap["pos"])) < 1e-9, "rest offset position");
            Check(Diff(off.Rot, M(cap["rot"])) < 1e-5, $"rest offset rotation (quaternion mean vs SVD: {Diff(off.Rot, M(cap["rot"])):E1})");
            Check(Math.Abs(off.PosSpreadM - (double)cap["pos_spread"]) < 1e-9, "position spread");
            Check(Math.Abs(off.RotSpreadDeg - (double)cap["rot_spread"]) < 1e-3, $"rotation spread ({off.RotSpreadDeg:F4} vs {(double)cap["rot_spread"]:F4} deg)");

            var sym = root["symmetry"];
            var input = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in sym["in"].AsObject()) input[kv.Key] = (V(kv.Value["pos"]), M(kv.Value["rot"]));
            var outp = PoseMath.Symmetrize(input, out double bodyYaw, out double mismatch);
            double worst = 0;
            foreach (var kv in sym["out"].AsObject())
                worst = Math.Max(worst, Math.Max(Diff(outp[kv.Key].pos, V(kv.Value["pos"])), Diff(outp[kv.Key].rot, M(kv.Value["rot"]))));
            Check(outp.Count == sym["out"].AsObject().Count && worst < 1e-9, $"Symmetrize, 6 trackers (max diff {worst:E1})");
            Check(Math.Abs(bodyYaw - (double)sym["body_yaw_deg"]) < 1e-9 && Math.Abs(mismatch - (double)sym["pair_mismatch_m"]) < 1e-12,
                  $"Symmetrize report (body yaw {bodyYaw:F3} deg, mismatch {mismatch * 100:F2} cm)");

            var st = root["stance"];
            var stIn = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
            foreach (var kv in st["in"].AsObject()) stIn[kv.Key] = (V(kv.Value["pos"]), M(kv.Value["rot"]));
            Check(Math.Abs(PoseMath.StanceM(stIn) - (double)st["captured"]) < 1e-12, $"StanceM ({PoseMath.StanceM(stIn) * 100:F1} cm)");
            foreach (var (name, width) in new[] { ("wide", 0.32), ("narrow", 0.12) })
            {
                var got = PoseMath.ApplyStance(stIn, width);
                double d = 0;
                foreach (var kv in st[name].AsObject())
                    d = Math.Max(d, Math.Max(Diff(got[kv.Key].pos, V(kv.Value["pos"])), Diff(got[kv.Key].rot, M(kv.Value["rot"]))));
                Check(d < 1e-9 && Math.Abs(PoseMath.StanceM(got) - width) < 1e-12, $"ApplyStance {width * 100:0} cm (max diff {d:E1})");
            }

            var sv = root["slimevr"];
            var bones = new Dictionary<string, double>();
            foreach (var kv in sv["bones"].AsObject()) bones[kv.Key] = (double)kv.Value;
            var slime = PoseMath.SlimeVrRestPose(bones);
            foreach (var (name, pose) in new[] { ("pose", slime), ("wide", PoseMath.ApplyStance(slime, 0.30)) })
            {
                double d = 0;
                foreach (var kv in sv[name].AsObject())
                    d = Math.Max(d, Math.Max(Diff(pose[kv.Key].pos, V(kv.Value["pos"])), Diff(pose[kv.Key].rot, M(kv.Value["rot"]))));
                Check(pose.Count == sv[name].AsObject().Count && d < 1e-12, $"SlimeVrRestPose, {name} (max diff {d:E1})");
            }

            foreach (var kv in root["legs"].AsObject())
            {
                var legs = kv.Value;
                var legIn = new Dictionary<string, (Vec3 pos, Mat3 rot)>();
                foreach (var t in legs["in"].AsObject()) legIn[t.Key] = (V(t.Value["pos"]), M(t.Value["rot"]));
                Check(Math.Abs(PoseMath.ToeOutDeg(legIn) - (double)legs["toe_out"]) < 1e-9, $"ToeOutDeg, {kv.Key} ({PoseMath.ToeOutDeg(legIn):F2} deg)");
                foreach (var (name, got) in new[] { ("toe_15", PoseMath.ApplyToeOut(legIn, 15)), ("forward_4", PoseMath.ApplyFeetForward(legIn, 0.04)),
                                                   ("all", PoseMath.AdjustPose(legIn, 0.30, 12, -0.03)) })
                {
                    double d = 0;
                    foreach (var t in legs[name].AsObject())
                        d = Math.Max(d, Math.Max(Diff(got[t.Key].pos, V(t.Value["pos"])), Diff(got[t.Key].rot, M(t.Value["rot"]))));
                    Check(d < 1e-9, $"leg settings {name}, {kv.Key} (max diff {d:E1})");
                }
            }
        }

        // Saving: the capture and settings.json in a temp folder (never the real %APPDATA%\fbtcal).
        static void Saving()
        {
            Console.WriteLine("Saving");
            string dir = Path.Combine(Path.GetTempPath(), "fbtcal-save-test-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "profile.json");
                var p = new Profile { Captured = "2026-10-29T01:00:00", HmdPitchDeg = 1.5 };
                p.Trackers["human://WAIST"] = (new Vec3(0, -0.67, 0.1), Mat3.Identity);
                p.Trackers["human://LEFT_FOOT"] = (new Vec3(-0.1, -1.56, 0), PoseMath.RotY(10));
                p.Save(file);
                var back = Profile.Load(file);
                Check(back != null && back.Captured == p.Captured && back.Trackers.Count == 2
                      && Diff(back.Trackers["human://LEFT_FOOT"].rot, PoseMath.RotY(10)) < 1e-6, "a capture reads back the same");
                p.Captured = "2026-10-29T02:00:00";
                p.Save(file);
                var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
                Check(Profile.Load(file).Captured == p.Captured && files.Count(f => f.StartsWith("profile.json.bak-")) == 1,
                      "a new capture keeps the old one as a .bak");
                Check(!files.Any(f => f.EndsWith(".tmp")), "no temp file left behind");

                // a reader holds the file (fbtcal.exe and app/fbtcal.py share it): the write waits for it
                var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = new Thread(() => { Thread.Sleep(200); reader.Dispose(); });
                release.Start();
                p.Captured = "2026-10-29T03:00:00";
                p.Save(file);
                release.Join();
                Check(Profile.Load(file).Captured == p.Captured, "saving while the file is being read waits and succeeds");

                string set = Path.Combine(dir, "settings.json");
                File.WriteAllText(set, "{\"autostart\":true,\"stance_cm\":32}");
                Settings.Set(set, "pose_source", "slimevr");
                Check((bool)Settings.Get(set, "autostart") && (double)Settings.Get(set, "stance_cm") == 32
                      && (string)Settings.Get(set, "pose_source") == "slimevr", "a setting is added, the others stay");
                File.WriteAllText(set, "{\"autostart\":tr");  // cut off by a crash in an older version
                Settings.Set(set, "stance_cm", 30);
                Check((double)Settings.Get(set, "stance_cm") == 30 && Directory.GetFiles(dir, "settings.json.broken-*").Length == 1,
                      "a broken settings.json is kept aside, not lost");
            }
            finally { Directory.Delete(dir, true); }
        }

        // The SlimeVR profile reader against app/slimevr.py's rules (temp folders, not the real one).
        static void SlimeVrProfile()
        {
            Console.WriteLine("SlimeVR profile");
            string root = Path.Combine(Path.GetTempPath(), "fbtcal-slimevr-test-" + Environment.ProcessId);
            Directory.CreateDirectory(Path.Combine(root, "user"));
            try
            {
                File.WriteAllText(Path.Combine(root, "global.json"), "{\"selectedUserProfile\": \"tall\"}");
                File.WriteAllText(Path.Combine(root, "user", "default.json"), "{\"userHeight\": 1.2, \"proportions\": {\"NECK\": 0.5}}");
                File.WriteAllText(Path.Combine(root, "user", "tall.json"), "{\"userHeight\": 1.884, \"proportions\": {\"NECK\": 0.11, \"HIPS_WIDTH\": 0.2}}");
                var b = SlimeVrBody.Load(root);
                Check(b.ProfileName == "tall" && b.Bones["NECK"] == 0.11 && b.Bones["HIPS_WIDTH"] == 0.2, "reads the selected profile");
                Check(Math.Abs(b.Bones["UPPER_LEG"] - 0.48) < 1e-12 && Math.Abs(b.Bones["FOOT_SHIFT"] + 0.024) < 1e-12
                      && b.Missing.Contains("UPPER_LEG") && !b.Missing.Contains("NECK"), "missing bones: SlimeVR's defaults scaled to the height");

                bool Fails(string r) { try { SlimeVrBody.Load(r); return false; } catch (SlimeVrException) { return true; } }
                Check(Fails(Path.Combine(root, "nincs")), "no SlimeVR folder: error");
                File.Delete(Path.Combine(root, "user", "tall.json"));
                Check(Fails(root), "no profile file (old SlimeVR): error");
                foreach (string broken in new[] { "{", "[1, 2]", "{\"proportions\": [1]}" })
                {
                    File.WriteAllText(Path.Combine(root, "user", "tall.json"), broken);
                    Check(Fails(root), $"broken profile {broken}: error");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        // Real lines from a VRChat log, in the order a calibration produces them.
        static readonly string[] Calibration =
        {
            "2026.10.25 20:44:59 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrate FBT and reload avatar...\n",
            "2026.10.25 20:44:59 Debug      -  [VRCTrackingManager] [IK Debug Log] Clearing all calibrated trackers...\n",
            "2026.10.25 20:45:00 Debug      -  [VRCTrackingManager] [IK Debug Log] Preparing for calibration...\n",
            "2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibration alignment... Position:(-0.96, 1.45, 0.08) yaw:90.52669\n",
            "2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Clearing all calibrated trackers...\n",
            "2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrating trackers...\n",
            "2026.10.25 20:45:07 Debug      -  [VRCTrackingSteam] [IK Debug Log] [Steam] Calibrating tracker... Serial number: human://WAIST\n",
            "2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Applying calibration to avatar... type: 8\n",
        };

        static void LogWatcher()
        {
            Console.WriteLine("VrcLogWatcher");
            string dir = Path.Combine(Path.GetTempPath(), "fbtcal-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                void Append(string name, string text) => File.AppendAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
                string Ev(List<string> e) => string.Join(",", e);
                const string a = "output_log_2026-10-25_20-38-23.txt";

                Append(a, "2026.10.25 20:38:23 Log - startup\n");
                var w = new VrcLogWatcher(dir);
                Check(!w.IkDebugSeen && Ev(w.Poll()) == "", "starts at the end of the existing log");
                Append(a, string.Concat(Calibration.Take(3)));
                Check(Ev(w.Poll()) == "start" && w.IkDebugSeen, "calibration start");
                Append(a, Calibration[3] + Calibration[4] + Calibration[5].Substring(0, 40));
                Check(Ev(w.Poll()) == "", "half-written line waits");
                Append(a, Calibration[5].Substring(40) + string.Concat(Calibration.Skip(6)));
                Check(Ev(w.Poll()) == "end,end", "calibration end");

                Append("output_log_2026-10-25_21-00-00.txt", Calibration[2]);
                Check(Ev(w.Poll()) == "newlog,start" && w.CurrentPath.EndsWith("21-00-00.txt"), "follows a new log after a VRChat restart");

                var w2 = new VrcLogWatcher(dir);
                Check(w2.IkDebugSeen, "notices IK debug lines already in the log");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void WriterToDriver(string hooktest, string dll)
        {
            Console.WriteLine("OverrideWriter -> driver_fbtcal.dll");
            using var w = new OverrideWriter(OverrideWriter.TestName);
            var pos = new Vec3(0.123456789, -0.75, 1.5);
            Quat q = PoseMath.QuatFromMatrix(new Mat3(0, 0, 1, 0, 1, 0, -1, 0, 0));
            bool stop = false;
            var t = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))  // like the app: keep the heartbeat fresh
                {
                    w.Write(new[] { (2u, new Vec3(9, 9, 9), new Quat(1, 0, 0, 0)), (5u, pos, q) });
                    Thread.Sleep(10);
                }
            });
            t.Start();
            string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo(hooktest) { UseShellExecute = false, RedirectStandardOutput = true };
            psi.Environment["FBTCAL_SHM_NAME"] = OverrideWriter.TestName;  // the DLL under test reads this name
            foreach (string arg in new[] { dll, "--external", "5", R(pos.X), R(pos.Y), R(pos.Z), R(q.W), R(q.X), R(q.Y), R(q.Z) })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Volatile.Write(ref stop, true);
            t.Join();
            Check(p.ExitCode == 0, "the driver applies what the C# writer wrote" + (p.ExitCode == 0 ? "" : "\n" + output));

            w.Clear();
            psi.ArgumentList[2] = "5";
            using var p2 = Process.Start(psi);
            string out2 = p2.StandardOutput.ReadToEnd();
            p2.WaitForExit();
            Check(p2.ExitCode != 0 && out2.Contains("[FAIL] app-written override applied"), "after Clear the driver overrides nothing");
        }

        static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("usage: <vectors.json> <hooktest.exe> <driver_fbtcal.dll>");
                return 2;
            }
            PoseMathAgainstPython(args[0]);
            SlimeVrProfile();
            Saving();
            LogWatcher();
            WriterToDriver(args[1], args[2]);
            Console.WriteLine(_failures == 0 ? "PASSED" : $"FAILED ({_failures})");
            return _failures == 0 ? 0 : 1;
        }
    }
}
