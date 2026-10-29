using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Valve.VR;

namespace FbtCal
{
    // Self-install: the SteamVR driver next to the exe, and starting together with SteamVR.
    public static class Setup
    {
        public const string AppKey = "erik.fbtcal";
        public const string DriverName = "fbtcal";

        static string OpenVrPaths => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        static string AppDir => Norm(AppContext.BaseDirectory);
        static string DriverDir => Path.Combine(AppDir, "driver", DriverName);
        static string VrManifest => Path.Combine(AppDir, "fbtcal.vrmanifest");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

        // Full path with junctions resolved: Desktop\project\fbtcal is a junction to G:, and both
        // spellings must count as the same folder, or every start from the other one re-registers.
        static string Norm(string p)
        {
            string full = Path.GetFullPath(p).TrimEnd('\\', '/');
            using var h = CreateFileW(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);  // OPEN_EXISTING, BACKUP_SEMANTICS (folders too)
            if (h.IsInvalid) return full;
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return full;
            string real = sb.ToString();
            if (real.StartsWith(@"\\?\UNC\")) real = @"\\" + real.Substring(8);
            else if (real.StartsWith(@"\\?\")) real = real.Substring(4);
            return real.TrimEnd('\\', '/');
        }

        internal static bool SamePath(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        static string DriverNameOf(string dir)
        {
            try { return (string)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "driver.vrdrivermanifest")))?["name"]; }
            catch (Exception) { return null; }
        }

        // Makes SteamVR load the driver shipped next to the exe, replacing any other "fbtcal"
        // registration (e.g. the dev build). Returns true if the list changed: a running SteamVR
        // only picks that up after a restart. Works without SteamVR running.
        public static bool EnsureDriverRegistered(out string message)
        {
            try
            {
                if (!File.Exists(Path.Combine(DriverDir, "bin", "win64", "driver_fbtcal.dll")))
                {
                    message = "no driver next to the exe (" + DriverDir + "), not registering";
                    return false;
                }
                var root = JsonNode.Parse(File.ReadAllText(OpenVrPaths));
                var drivers = root["external_drivers"]?.AsArray().Select(n => (string)n).Where(s => s != null).ToList() ?? new List<string>();
                string runtime = root["runtime"]?.AsArray().Select(n => (string)n).FirstOrDefault(Directory.Exists);
                if (drivers.Any(d => SamePath(d, DriverDir)))
                {
                    message = "driver already registered: " + Norm(DriverDir);
                    return false;
                }
                string vrpathreg = runtime == null ? null : Path.Combine(runtime, "bin", "win64", "vrpathreg.exe");
                if (vrpathreg == null || !File.Exists(vrpathreg))
                {
                    message = "vrpathreg.exe not found, SteamVR runtime: " + runtime;
                    return false;
                }

                string backup = OpenVrPaths + ".fbtcal-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(OpenVrPaths, backup, true);
                var log = new StringBuilder("backup " + backup);
                foreach (string other in drivers.Where(d => DriverNameOf(d) == DriverName))
                    log.Append("; removedriver ").Append(other).Append(" -> ").Append(Run(vrpathreg, "removedriver", other));
                log.Append("; adddriver ").Append(Norm(DriverDir)).Append(" -> ").Append(Run(vrpathreg, "adddriver", Norm(DriverDir)));
                message = log.ToString();
                return true;
            }
            catch (Exception ex)
            {
                message = "driver registration failed: " + ex.Message;
                return false;
            }
        }

        // The uninstaller's half: takes the driver in `driverDir` out of SteamVR's list (backup first).
        // SteamVR must be closed, or it keeps the DLL loaded until it exits.
        public static string RemoveDriverRegistration(string driverDir)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(OpenVrPaths));
                var drivers = root["external_drivers"]?.AsArray().Select(n => (string)n).Where(s => s != null).ToList() ?? new List<string>();
                var mine = drivers.Where(d => SamePath(d, driverDir)).ToList();
                if (mine.Count == 0) return "driver: not registered " + driverDir;
                string runtime = root["runtime"]?.AsArray().Select(n => (string)n).FirstOrDefault(Directory.Exists);
                string vrpathreg = runtime == null ? null : Path.Combine(runtime, "bin", "win64", "vrpathreg.exe");
                if (vrpathreg == null || !File.Exists(vrpathreg)) return "driver: vrpathreg.exe not found, SteamVR runtime: " + runtime;
                string backup = OpenVrPaths + ".fbtcal-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(OpenVrPaths, backup, true);
                var log = new StringBuilder("driver: backup " + backup);
                foreach (string d in mine) log.Append("; removedriver ").Append(d).Append(" -> ").Append(Run(vrpathreg, "removedriver", d));
                return log.ToString();
            }
            catch (Exception ex) { return "driver: removal failed: " + ex.Message; }
        }

        static string Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            string output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(10000);
            return $"exit {p.ExitCode}{(output.Length > 0 ? " " + output : "")}";
        }

        // Registers fbtcal.vrmanifest and switches SteamVR's auto-launch on/off (needs a live
        // OpenVR connection). Re-registers if the exe moved since the last registration.
        // Same approach as Orbit's ApplyAutoStart.
        public static string ApplyAutoStart(bool wanted)
        {
            try
            {
                var apps = OpenVR.Applications;
                bool installed = apps.IsApplicationInstalled(AppKey);
                string path = "";
                if (installed)
                {
                    var sb = new StringBuilder(1024);
                    var pe = EVRApplicationError.None;
                    apps.GetApplicationPropertyString(AppKey, EVRApplicationProperty.BinaryPath_String, sb, (uint)sb.Capacity, ref pe);
                    path = sb.ToString();
                }
                string exe = Environment.ProcessPath ?? "";
                bool samePath = installed && path.Length > 0 && SamePath(path, exe);
                var e = EVRApplicationError.None;
                if (wanted)
                {
                    if (!samePath)
                    {
                        if (installed && path.Length > 0)  // registered from another folder before
                            apps.RemoveApplicationManifest(Path.Combine(Path.GetDirectoryName(path) ?? "", "fbtcal.vrmanifest"));
                        e = apps.AddApplicationManifest(VrManifest, false);
                    }
                    if (e == EVRApplicationError.None && !apps.GetApplicationAutoLaunch(AppKey))
                        e = apps.SetApplicationAutoLaunch(AppKey, true);
                }
                else if (installed)
                {
                    apps.SetApplicationAutoLaunch(AppKey, false);
                    e = apps.RemoveApplicationManifest(VrManifest);
                }
                return $"auto-start wanted={wanted} installed={installed} path='{path}' -> {e}, autolaunch={apps.GetApplicationAutoLaunch(AppKey)}";
            }
            catch (Exception ex)
            {
                return "auto-start failed: " + ex.Message;
            }
        }
    }
}
