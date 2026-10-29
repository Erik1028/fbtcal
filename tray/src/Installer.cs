using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FbtCal
{
    // The installer is this same exe: started as "fbtcalSetup.exe" (or with --setup) it copies itself
    // per user to %LOCALAPPDATA%\Programs\fbtcal together with what it carries (the SteamVR driver,
    // openvr_api.dll, fbtcal.vrmanifest), adds a Start menu shortcut and an "Apps" entry, and starts the
    // installed copy, which registers its driver and the start with SteamVR itself (Setup.cs).
    // "fbtcal.exe --uninstall" takes it all out again; the settings in %APPDATA%\fbtcal stay.
    // Orbit's installer is the model (one exe, per user, only its own files deleted).
    public static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\fbtcal";
        const string PayloadPrefix = "payload/";
        public const string ExeName = "fbtcal.exe";

        public static string DefaultDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "fbtcal");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "fbtcal.lnk");
        public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        // What the setup exe carries besides itself (embedded by build_setup.ps1: -p:FbtcalPayload=true).
        static IEnumerable<string> PayloadNames => Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(PayloadPrefix, StringComparison.Ordinal));
        public static bool HasPayload => PayloadNames.Any();
        static IEnumerable<string> PayloadFiles => PayloadNames.Select(n => n.Substring(PayloadPrefix.Length).Replace('/', '\\'));

        public sealed class Options
        {
            public string Dir = DefaultDir;
            public bool Shortcuts = true;   // Start menu + Apps entry (tests: --no-shortcuts)
            public bool Start = true;       // start the installed copy (tests: --no-start)
            public bool StopRunning = true; // close a running fbtcal first (tests: --no-stop)
            public bool SteamVR = true;     // uninstall: take the driver and the auto-start out (tests: --keep-steamvr)
            public bool Quiet;

            public static Options Parse(string[] args)
            {
                var o = new Options
                {
                    Shortcuts = !args.Contains("--no-shortcuts"),
                    Start = !args.Contains("--no-start"),
                    StopRunning = !args.Contains("--no-stop"),
                    SteamVR = !args.Contains("--keep-steamvr"),
                    Quiet = args.Contains("--quiet"),
                };
                int i = Array.IndexOf(args, "--install-dir");
                if (i >= 0 && i + 1 < args.Length) o.Dir = Path.GetFullPath(args[i + 1]);
                return o;
            }
        }

        // Setup: --setup, "setup" in the file name, or a setup build renamed (it carries the driver but
        // has none next to it, e.g. "fbtcal (1).exe" in Downloads).
        public static bool IsSetupMode(string[] args) =>
            args.Contains("--setup") ||
            (!args.Contains("--uninstall") &&
             (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase) ||
              (HasPayload && !File.Exists(Path.Combine(AppContext.BaseDirectory, "driver", "fbtcal", "bin", "win64", "driver_fbtcal.dll")))));

        public static bool SteamVRRunning() => Process.GetProcessesByName("vrserver").Length > 0;

        // An update while SteamVR runs can't replace the driver it has loaded.
        public static bool DriverInUse(string dir) =>
            SteamVRRunning() && File.Exists(Path.Combine(dir, "driver", "fbtcal", "bin", "win64", "driver_fbtcal.dll"));

        public static string InstalledVersion()
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k?.GetValue("DisplayVersion") as string;
        }

        // Closes running fbtcal copies (not this setup exe): a tray app has no window to close, and
        // its single-instance lock would keep the new copy from starting.
        static void StopRunning()
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("fbtcal"))
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                        Log.Info($"setup: stopped running fbtcal (pid {p.Id})");
                    }
                    catch (Exception ex) { Log.Warn($"setup: could not stop pid {p.Id}: {ex.Message}"); }
                }
            }
        }

        public static void Install(Options o)
        {
            if (!HasPayload) throw new InvalidOperationException("this build carries no driver (build it with build_setup.ps1)");
            if (DriverInUse(o.Dir)) throw new InvalidOperationException("Close SteamVR first: it has fbtcal's driver loaded.");
            Log.Info($"setup {Version}: install into {o.Dir} (shortcuts {o.Shortcuts}, start {o.Start})");
            if (o.StopRunning) StopRunning();
            Directory.CreateDirectory(o.Dir);
            // the files first: if one can't be written, the old exe still goes with the old files
            foreach (string name in PayloadNames)
            {
                string target = Path.Combine(o.Dir, name.Substring(PayloadPrefix.Length).Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            string exe = Path.Combine(o.Dir, ExeName);
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath), exe, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Environment.ProcessPath, exe, true);
                // a downloaded setup carries the "came from the internet" mark; the installed app should not ask again
                try { File.Delete(exe + ":Zone.Identifier"); } catch (Exception) { }
            }
            if (o.Shortcuts)
            {
                MakeShortcut(StartMenuLink, exe, o.Dir);
                using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
                k.SetValue("DisplayName", "fbtcal");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Erik1028");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("URLInfoAbout", TrayApp.Website);
                k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
            Log.Info("setup: installed");
            if (o.Start)
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = o.Dir, UseShellExecute = true });
        }

        // ---------------- uninstall ----------------

        static string _selfDelete;  // the cmd line that removes the exe and its folders, once this process ends

        // Returns null when done, else why not (for the window). The caller has asked the user, and
        // calls ScheduleSelfDelete after its last message box.
        public static string Uninstall(Options o)
        {
            string exe = Path.GetFullPath(Environment.ProcessPath);
            string dir = Path.GetDirectoryName(exe);
            string registeredDir;
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) registeredDir = k?.GetValue("InstallLocation") as string;
            if (!o.Shortcuts) registeredDir = dir;  // tests: a copy installed with --no-shortcuts has no Apps entry
            Log.Info($"setup: uninstall {dir} (registered {registeredDir})");
            // Another copy (the setup in Downloads, a dev build) must not take the installed one's Apps
            // entry with it. Without an entry, the default folder still counts as installed; any other
            // folder only gets its own SteamVR registration removed, no files deleted.
            if (registeredDir != null && !Setup.SamePath(registeredDir, dir))
                return $"This is not the installed copy (fbtcal is installed in {registeredDir}). Uninstall it from Windows Settings > Apps.";
            bool installedCopy = registeredDir != null || Setup.SamePath(dir, DefaultDir);
            if (o.SteamVR && SteamVRRunning()) return "Close SteamVR first: it has fbtcal's driver loaded.";
            if (o.StopRunning) StopRunning();

            if (o.SteamVR)
            {
                Log.Info(Setup.RemoveDriverRegistration(Path.Combine(dir, "driver", Setup.DriverName)));
                Log.Info(RemoveFromSteamVRApps(Path.Combine(dir, "fbtcal.vrmanifest")));
            }
            if (!installedCopy)
            {
                Log.Warn($"setup: not deleting files, {dir} is not the installed folder");
                return null;
            }
            if (o.Shortcuts)
            {
                if (LinkTarget(StartMenuLink) is string target && Setup.SamePath(target, exe))
                    try { File.Delete(StartMenuLink); } catch (Exception) { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            }

            // Only the files the installer wrote: an uninstall can't take other files with it. The exe
            // can't delete itself; ScheduleSelfDelete leaves a hidden cmd that removes it and the then
            // empty folders once this process has ended.
            foreach (string f in PayloadFiles)
                try { File.Delete(Path.Combine(dir, f)); } catch (Exception ex) { Log.Warn($"setup: delete {f}: {ex.Message}"); }
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // every folder under dir the payload made
            foreach (string f in PayloadFiles)
                for (string d = Path.GetDirectoryName(Path.Combine(dir, f)); d.Length > dir.Length; d = Path.GetDirectoryName(d))
                    folders.Add(d);
            string rmdirs = string.Concat(folders.OrderByDescending(d => d.Length).Select(d => $" & rmdir \"{d}\""));  // deepest first
            // retries for up to 30 s: the exe is locked until this process is gone, however long a dialog stays open
            _selfDelete = $"/c (for /l %i in (1,1,30) do @if exist \"{exe}\" (del /q \"{exe}\" 2>nul & ping -n 2 127.0.0.1 >nul)){rmdirs} & rmdir \"{dir}\"";
            Log.Info("setup: uninstalled");
            return null;
        }

        // The last thing an uninstall does, after its message boxes.
        public static void ScheduleSelfDelete()
        {
            if (_selfDelete == null) return;
            Process.Start(new ProcessStartInfo("cmd.exe", _selfDelete)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,  // not inside the folder it removes
            });
            _selfDelete = null;
        }

        // SteamVR is closed by now (Uninstall insists), so its app list is edited in its file:
        // the manifest path comes out of Steam\config\appconfig.json, backed up first.
        static string RemoveFromSteamVRApps(string manifest)
        {
            try
            {
                string steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
                if (string.IsNullOrEmpty(steam)) return "SteamVR apps: no Steam";
                string cfg = Path.Combine(steam.Replace('/', '\\'), "config", "appconfig.json");
                if (!File.Exists(cfg)) return "SteamVR apps: no appconfig.json";
                var root = JsonNode.Parse(File.ReadAllText(cfg));
                if (root?["manifest_paths"] is not JsonArray arr) return "SteamVR apps: no manifest_paths";
                // (junctions resolved: the manifest is still there, it is deleted after this)
                var hits = arr.Where(n => n is JsonValue v && v.TryGetValue(out string p) &&
                                          Setup.SamePath(p.Replace('/', '\\'), manifest)).ToList();
                if (hits.Count == 0) return "SteamVR apps: fbtcal not listed";
                string backup = cfg + ".fbtcal-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(cfg, backup, true);
                foreach (var h in hits) arr.Remove(h);
                Files.WriteAtomic(cfg, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return $"SteamVR apps: removed {manifest} (backup {backup})";
            }
            catch (Exception ex) { return "SteamVR apps: " + ex.Message; }
        }

        // ---------------- shortcuts ----------------

        static void MakeShortcut(string path, string target, string workDir)
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                dynamic lnk = sh.CreateShortcut(path);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.IconLocation = target + ",0";
                lnk.Description = "fbtcal - VRChat FBT calibration without posing";
                lnk.Save();
            }
            catch (Exception e) { Log.Warn($"setup: shortcut {path}: {e.Message}"); }
        }

        static string LinkTarget(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                return Path.GetFullPath((string)sh.CreateShortcut(path).TargetPath);
            }
            catch (Exception) { return null; }
        }
    }
}
