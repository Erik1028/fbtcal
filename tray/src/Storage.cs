using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FbtCal
{
    public static class Paths
    {
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fbtcal");
        public static string ProfileFile => Path.Combine(DataDir, "profile.json");
        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string LogFile => Path.Combine(DataDir, "fbtcal.log");
    }

    // Saves that survive a crash: the new content goes to a temp file, onto the disk, and then
    // replaces the old file in one step. A kill, a SteamVR quit or a driver crash mid-write leaves
    // the old file, never half of one. (app/fbtcal.py writes the same way.)
    public static class Files
    {
        const int Tries = 20, RetryMs = 50;  // a reader (the other app, the tray) may hold the file a moment

        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            Retry(() => File.Move(tmp, path, true));
        }

        public static string ReadText(string path)
        {
            string text = null;
            Retry(() => text = File.ReadAllText(path, Encoding.UTF8));
            return text;
        }

        static void Retry(Action io)
        {
            for (int i = 1; ; i++)
            {
                try
                {
                    io();
                    return;
                }
                catch (IOException) when (i < Tries) { }
                catch (UnauthorizedAccessException) when (i < Tries) { }
                System.Threading.Thread.Sleep(RetryMs);
            }
        }
    }

    public enum PoseSource { Capture, SlimeVr }

    // The standing pose: each tracker in the HMD yaw frame. Same JSON as app/fbtcal.py writes,
    // so a capture from either one works with both. Or, without a capture, SlimeVR's proportions.
    public sealed class Profile
    {
        public PoseSource Source = PoseSource.Capture;
        public SlimeVrBody SlimeVr;  // Source == SlimeVr: where it came from
        public string Captured = "";
        public double HmdPitchDeg;
        public Dictionary<string, (Vec3 pos, Mat3 rot)> Trackers = new Dictionary<string, (Vec3, Mat3)>();
        // Only written (for comparing later): the measured pose before PoseMath.Symmetrize.
        public Dictionary<string, (Vec3 pos, Mat3 rot)> Raw;
        public double BodyYawDeg, PairMismatchM;
        public bool HasSymmetry;  // captured by a version that symmetrizes

        public static Profile Load(string path = null)
        {
            path ??= Paths.ProfileFile;
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(Files.ReadText(path));
            var p = new Profile
            {
                Captured = (string)root["captured"] ?? "",
                HmdPitchDeg = (double?)root["hmd_pitch_deg"] ?? 0,
            };
            if (root["symmetry"] is JsonObject sym)
            {
                p.HasSymmetry = true;
                p.BodyYawDeg = (double?)sym["body_yaw_deg"] ?? 0;
                p.PairMismatchM = (double?)sym["pair_mismatch_m"] ?? 0;
            }
            foreach (var kv in root["trackers"].AsObject())
            {
                var pos = kv.Value["pos"].AsArray();
                var rot = kv.Value["rot"].AsArray();
                double R(int r, int c) => (double)rot[r][c];
                p.Trackers[kv.Key] = (new Vec3((double)pos[0], (double)pos[1], (double)pos[2]),
                                      new Mat3(R(0, 0), R(0, 1), R(0, 2), R(1, 0), R(1, 1), R(1, 2), R(2, 0), R(2, 1), R(2, 2)));
            }
            return p;
        }

        // Writes the capture and reads it back: throws unless the file on disk holds this pose.
        public void Save(string path = null)
        {
            path ??= Paths.ProfileFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))  // never lose an earlier capture
                File.Copy(path, path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            static JsonObject ToJson(Dictionary<string, (Vec3 pos, Mat3 rot)> trackers)
            {
                var o = new JsonObject();
                foreach (var kv in trackers)
                {
                    var (pos, rot) = kv.Value;
                    var rows = new JsonArray();
                    for (int r = 0; r < 3; r++)
                        rows.Add(new JsonArray(Math.Round(rot[r, 0], 6), Math.Round(rot[r, 1], 6), Math.Round(rot[r, 2], 6)));
                    o[kv.Key] = new JsonObject
                    {
                        ["pos"] = new JsonArray(Math.Round(pos.X, 5), Math.Round(pos.Y, 5), Math.Round(pos.Z, 5)),
                        ["rot"] = rows,
                    };
                }
                return o;
            }
            var root = new JsonObject
            {
                ["version"] = 1,
                ["captured"] = Captured,
                ["hmd_pitch_deg"] = double.IsFinite(HmdPitchDeg) ? Math.Round(HmdPitchDeg, 1) : null,
                ["trackers"] = ToJson(Trackers),
            };
            if (Raw != null)
            {
                root["trackers_raw"] = ToJson(Raw);
                root["symmetry"] = new JsonObject
                {
                    ["body_yaw_deg"] = Math.Round(BodyYawDeg, 2),
                    ["pair_mismatch_m"] = Math.Round(PairMismatchM, 4),
                };
            }
            Files.WriteAtomic(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var back = Load(path);
            bool same = back != null && back.Captured == Captured && back.Trackers.Count == Trackers.Count;
            foreach (var kv in Trackers)
                same &= back != null && back.Trackers.TryGetValue(kv.Key, out var t) && (t.pos - kv.Value.pos).Length < 1e-4;
            if (!same) throw new IOException("the saved profile reads back different");
        }

        public static Profile FromSlimeVr(SlimeVrBody body) => new Profile
        {
            Source = PoseSource.SlimeVr,
            SlimeVr = body,
            Trackers = PoseMath.SlimeVrRestPose(body.Bones),
        };

        // Which pose to use: chosen on the panel (settings.json pose_source, shared with
        // app/fbtcal.py); unset = the capture if there is one, else SlimeVR's proportions.
        public static PoseSource SourceInUse() =>
            Settings.PoseSource ?? (File.Exists(Paths.ProfileFile) ? PoseSource.Capture : PoseSource.SlimeVr);

        // The pose in use, or null with the reason in `problem` (for the user, Hungarian).
        public static Profile LoadInUse(out PoseSource source, out string problem)
        {
            source = SourceInUse();
            problem = "";
            try
            {
                if (source == PoseSource.SlimeVr) return FromSlimeVr(SlimeVrBody.Load());
                var p = Load();
                if (p == null) problem = "No standing pose captured yet.";
                return p;
            }
            catch (SlimeVrException ex) { problem = ex.Message; }
            catch (Exception ex) { problem = "Broken profile file: " + ex.Message; }
            return null;
        }

        public string Describe()
        {
            if (Source == PoseSource.SlimeVr) return SlimeVr.Describe();
            string when = DateTime.TryParse(Captured, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.ToString("yyyy-MM-dd HH:mm") : Captured;
            return $"{when}, {Trackers.Count} trackers";
        }
    }

    public static class Settings
    {
        // settings.json is shared with app/fbtcal.py; every write keeps the keys it does not touch.
        static readonly object Lock = new object();

        static JsonObject Read()  // for the getters: unreadable = defaults
        {
            try { return Read(Paths.SettingsFile, out _); }
            catch (Exception) { return new JsonObject(); }
        }

        // broken: the file is there but is no JSON object (a read error throws instead, so a busy
        // file never passes for an empty one).
        static JsonObject Read(string path, out bool broken)
        {
            broken = false;
            if (!File.Exists(path)) return new JsonObject();
            try
            {
                if (JsonNode.Parse(Files.ReadText(path)) is JsonObject o) return o;
            }
            catch (JsonException) { }
            broken = true;
            return new JsonObject();
        }

        static void Set(string key, JsonNode value) => Set(Paths.SettingsFile, key, value);

        // Every write keeps the other keys; a broken file is kept aside instead of being lost.
        public static void Set(string path, string key, JsonNode value)
        {
            lock (Lock)
            {
                var o = Read(path, out bool broken);
                if (broken) File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                o[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Files.WriteAtomic(path, o.ToJsonString());
            }
        }

        public static JsonNode Get(string path, string key) => Read(path, out _)[key];

        // Start together with SteamVR (its auto-launch list). On by default.
        public static bool AutoStart
        {
            get => (bool?)Read()["autostart"] ?? true;
            set => Set("autostart", value);
        }

        // Feet distance for the ideal pose, set on the SteamVR panel; null = as captured.
        public static double? StanceCm
        {
            get => (double?)Read()["stance_cm"];
            set => Set("stance_cm", value);
        }

        // How far the feet turn out, degrees (Settings page); null = as the pose has them.
        public static double? ToeOutDeg
        {
            get => (double?)Read()["toe_out_deg"];
            set => Set("toe_out_deg", value);
        }

        // Feet moved forward (+) or back (-), cm (Settings page); null = as the pose has them.
        public static double? FeetForwardCm
        {
            get => (double?)Read()["feet_forward_cm"];
            set => Set("feet_forward_cm", value);
        }

        // Where the standing pose comes from, chosen on the SteamVR panel; null = not chosen yet
        // (Profile.SourceInUse decides).
        public static PoseSource? PoseSource
        {
            get => (string)Read()["pose_source"] switch
            {
                "capture" => FbtCal.PoseSource.Capture,
                "slimevr" => FbtCal.PoseSource.SlimeVr,
                _ => null,
            };
            set => Set("pose_source", value switch
            {
                FbtCal.PoseSource.Capture => "capture",
                FbtCal.PoseSource.SlimeVr => "slimevr",
                _ => null,
            });
        }
    }

    public static class Log
    {
        static readonly object Lock = new object();

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);

        static void Write(string level, string msg)
        {
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 1_000_000)
                        File.Move(Paths.LogFile, Paths.LogFile + ".old", true);
                    File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
                }
                catch (Exception) { }
            }
        }
    }
}
