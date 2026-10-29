using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FbtCal
{
    // The proportions can't be read; the message is for the user (Hungarian, like the UI).
    public sealed class SlimeVrException : Exception
    {
        public SlimeVrException(string message) : base(message) { }
    }

    // SlimeVR's body proportions, for a standing pose without standing up to capture it (port of
    // app/slimevr.py). SlimeVR server v22 keeps them in %APPDATA%\dev.slimevr.SlimeVR: global.json
    // names the selected user profile, user\<name>.json has its userHeight and proportions (metres).
    // Older servers (vrconfig.yml) are not read.
    public sealed class SlimeVrBody
    {
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.slimevr.SlimeVR");

        // SlimeVR's defaults (skeleton/proportions.kt) for bones missing from a profile. SlimeVR scales
        // them to the profile's height; for adults that is a plain ratio of the default height.
        static readonly (string bone, double length)[] Defaults =
        {
            ("NECK", 0.1), ("UPPER_CHEST", 0.16), ("LOWER_CHEST", 0.16), ("UPPER_WAIST", 0.1), ("LOWER_WAIST", 0.1),
            ("HIP", 0.05), ("UPPER_LEG", 0.4), ("LOWER_LEG", 0.5),
            ("HIPS_WIDTH", 0.26), ("FOOT_LENGTH", 0.115), ("FOOT_SHIFT", -0.02),
        };
        const double DefaultHeight = 1.57;  // NECK .. LOWER_LEG

        public Dictionary<string, double> Bones = new Dictionary<string, double>();
        public string ProfileName = "";
        public string File = "";  // the user profile it came from
        public double HeightM;
        public List<string> Missing = new List<string>();

        // The selected profile's file, also when it does not exist (to notice when it appears).
        public static string ProfilePath(string root = null)
        {
            root ??= DefaultDir;
            string name = "default";
            try
            {
                if (JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(root, "global.json"), Encoding.UTF8)) is JsonObject g
                    && g["selectedUserProfile"] is JsonValue v && v.TryGetValue(out string s) && s.Length > 0)
                    name = s;
            }
            catch (Exception) { }
            return Path.Combine(root, "user", name + ".json");
        }

        public static SlimeVrBody Load(string root = null)
        {
            root ??= DefaultDir;
            if (!Directory.Exists(root))
                throw new SlimeVrException("Can't find SlimeVR's settings (%APPDATA%\\dev.slimevr.SlimeVR).");
            string path = ProfilePath(root);
            string file = Path.GetFileName(path);
            JsonNode data;
            try { data = JsonNode.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8)); }
            catch (IOException) { throw new SlimeVrException($"No SlimeVR body proportions ({file}). This needs SlimeVR server v22 or later."); }
            catch (UnauthorizedAccessException) { throw new SlimeVrException($"No SlimeVR body proportions ({file}). This needs SlimeVR server v22 or later."); }
            catch (JsonException ex) { throw new SlimeVrException($"The SlimeVR profile is broken ({file}): {ex.Message}"); }
            if (!(data is JsonObject o) || !(o["proportions"] is JsonObject stored))
                throw new SlimeVrException($"The SlimeVR profile ({file}) has no body proportions.");

            var body = new SlimeVrBody
            {
                ProfileName = Path.GetFileNameWithoutExtension(path),
                File = path,
                HeightM = Number(o["userHeight"]) is double h && h > 0 ? h : DefaultHeight,
            };
            foreach (var (bone, length) in Defaults)
            {
                if (Number(stored[bone]) is double v) body.Bones[bone] = v;
                else
                {
                    body.Bones[bone] = length * body.HeightM / DefaultHeight;
                    body.Missing.Add(bone);
                }
            }
            return body;
        }

        static double? Number(JsonNode n) =>
            n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

        public string Describe() => $"SlimeVR body proportions ({ProfileName}, head–ankle {HeightM * 100:0} cm)" +
                                    (Missing.Count > 0 ? ", defaults: " + string.Join(", ", Missing) : "");
    }
}
