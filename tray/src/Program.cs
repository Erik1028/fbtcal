using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FbtCal
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // English UI: 0.4°, not the Windows locale's 0,4° (every thread: the worker formats the panel's text)
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            if (args.Length == 2 && args[0] == "--render-panel")
            {
                RenderPanelPreviews(args[1]);  // to look at the SteamVR panel without a headset
                return;
            }
            if (args.Length == 2 && args[0] == "--render-setup")  // the installer's window as PNG (website, checks)
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                using var w = new SetupWindow(new Installer.Options())
                {
                    StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), ShowInTaskbar = false,
                };
                w.Show();  // off screen: the child controls only draw once they exist
                Application.DoEvents();
                using var whole = new System.Drawing.Bitmap(w.Width, w.Height);
                w.DrawToBitmap(whole, new System.Drawing.Rectangle(0, 0, w.Width, w.Height));
                var client = w.RectangleToScreen(w.ClientRectangle);
                client.Offset(-w.Left, -w.Top);
                using var bmp = whole.Clone(client, whole.PixelFormat);
                bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                w.Close();
                return;
            }
            if (args.Contains("--uninstall") || Installer.IsSetupMode(args))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var o = Installer.Options.Parse(args);
                if (args.Contains("--uninstall")) Environment.ExitCode = RunUninstall(o);
                else if (o.Quiet)
                {
                    try { Installer.Install(o); }
                    catch (Exception ex)
                    {
                        Log.Warn("setup failed: " + ex);
                        Environment.ExitCode = 1;
                    }
                }
                else Application.Run(new SetupWindow(o));
                return;
            }

            // SteamVR's auto-launch and a manual start must not run two copies.
            using var single = new Mutex(true, @"Local\fbtcal_single_instance", out bool first);
            if (!first) return;

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Info($"start {Application.ProductVersion} {Environment.ProcessPath}");
            // Unexpected exits: log every way out instead of a dialog nobody sees in VR.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Warn("UI exception: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Warn("unhandled: " + e.ExceptionObject);
            Application.ApplicationExit += (s, e) => Log.Info("ApplicationExit\n" + Environment.StackTrace);
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => Log.Info("session ending: " + e.Reason);
            bool changed = Setup.EnsureDriverRegistered(out string setup);
            Log.Info(setup);
            string note = changed ? "The SteamVR driver is set up. If SteamVR is already running, restart it once." : null;
            Application.Run(new TrayApp(note));
            Log.Info("exit");
        }

        // "fbtcal.exe --uninstall" (Apps & features): asks, waits for SteamVR to be closed, removes.
        // Exit code 0 = removed, 1 = not (cancelled, or SteamVR still running in --quiet mode).
        static int RunUninstall(Installer.Options o)
        {
            const string Caption = "Uninstall fbtcal";
            if (!o.Quiet && MessageBox.Show("Remove fbtcal from this PC?\n\nIts SteamVR driver and the start with SteamVR go too. " +
                                            "Your pose and settings in %APPDATA%\\fbtcal are kept.",
                                            Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 1;
            while (true)
            {
                string notYet = Installer.Uninstall(o);
                if (notYet == null) break;
                Log.Warn("setup: uninstall refused: " + notYet);
                if (o.Quiet || MessageBox.Show(notYet, Caption, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                    return 1;
            }
            if (!o.Quiet) MessageBox.Show("fbtcal has been removed.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Installer.ScheduleSelfDelete();  // last: the exe is gone once this process has ended
            return 0;
        }

        // The pose figure's points, like VrWorker builds them.
        static PosePoint[] Points(System.Collections.Generic.IReadOnlyDictionary<string, (Vec3 pos, Mat3 rot)> trackers)
        {
            var list = new System.Collections.Generic.List<PosePoint> { new PosePoint("head", 0, 0, 0) };
            foreach (var kv in trackers)
                list.Add(new PosePoint(kv.Key.Replace("human://", ""), (float)kv.Value.pos.X, (float)kv.Value.pos.Y, (float)kv.Value.pos.Z));
            return list.ToArray();
        }

        // Every state of the dashboard tab as PNG, with made-up demo data (no one's real pose): to look
        // at the panel without a headset, and the website's screenshots.
        static void RenderPanelPreviews(string dir)
        {
            Directory.CreateDirectory(dir);
            // SlimeVR's default proportions for a 160 cm head height, through the real reader
            string fake = Path.Combine(Path.GetTempPath(), "fbtcal-preview-slimevr");
            Directory.CreateDirectory(Path.Combine(fake, "user"));
            File.WriteAllText(Path.Combine(fake, "user", "default.json"), "{\"userHeight\": 1.6, \"proportions\": {}}");
            var body = SlimeVrBody.Load(fake);
            var slimePose = PoseMath.SlimeVrRestPose(body.Bones);
            // a "captured" pose: the same body standing a little wider, feet turned out
            var captured = PoseMath.AdjustPose(slimePose, 0.28, 13, null);
            var normal = new PanelModel(WorkerState.Watching, "Watching", -1, -1, true, 6, 6, "", true, "16:41 · done",
                                        PoseSource.Capture, "Oct 27, 16:40", "symmetrized 0.4° · 2.1 cm", "",
                                        28, false, "as captured", 28, 13, false, 13, 0, false,
                                        Points(captured), true, "", "");
            var slime = normal with
            {
                Source = PoseSource.SlimeVr, PoseTitle = "proportions", PoseDetail = $"head–ankle {body.HeightM * 100:0} cm\nprofile: default",
                StanceCm = PoseMath.StanceM(slimePose) * 100, BaseStanceCm = PoseMath.StanceM(slimePose) * 100, StanceNote = "as in SlimeVR",
                ToeOutDeg = 0, BaseToeOutDeg = 0, Pose = Points(slimePose),
            };
            var settings = normal with
            {
                SettingsPage = true, StanceCm = 32, StanceCustom = true, ToeOutDeg = 18, ToeOutCustom = true, ForwardCm = 6,
                Pose = Points(PoseMath.AdjustPose(captured, 0.32, 18, 0.06)),
            };
            var shots = new (string name, PanelModel m, PanelAction hover)[]
            {
                ("01-main", normal, PanelAction.None),
                ("02-main-slimevr", slime, PanelAction.OpenSettings),
                ("03-saved", normal with { Toast = "Standing pose saved (6 trackers)." }, PanelAction.Capture),
                ("04-problem-vrchat-log", normal with { IkDebugSeen = false }, PanelAction.CopyIkFlag),
                ("05-problems-tracker-log", normal with { TrackersFound = 5, MissingTrackers = "left knee", IkDebugSeen = false }, PanelAction.None),
                ("06-problem-driver", normal with { DriverLoaded = false, TrackersFound = 0 }, PanelAction.None),
                ("07-no-pose", normal with { PoseTitle = "", PoseDetail = "", Pose = new PosePoint[0], StanceCm = double.NaN, ToeOutDeg = double.NaN,
                                             TrackersInProfile = 0, TrackersFound = 0, LastCalibration = "" }, PanelAction.None),
                ("08-no-slimevr", slime with { PoseTitle = "", Pose = new PosePoint[0], TrackersInProfile = 0, TrackersFound = 0,
                                               PoseProblem = "No SlimeVR body proportions (default.json). This needs SlimeVR server v22 or later." }, PanelAction.None),
                ("09-waiting", normal with { State = WorkerState.WaitingForSteamVR, StateText = "Waiting for SteamVR" }, PanelAction.None),
                ("10-settings", settings, PanelAction.ToeOutPlus),
                ("11-settings-test", settings with { State = WorkerState.Testing, StateText = "Test", TestLeftS = 14 }, PanelAction.None),
                ("12-test", normal with { State = WorkerState.Testing, StateText = "Test", TestLeftS = 14 }, PanelAction.None),
                ("13-calibrating", normal with { State = WorkerState.Calibrating, StateText = "Calibrating" }, PanelAction.None),
                ("14-countdown", normal with { State = WorkerState.Capturing, StateText = "Capturing", CountdownS = 7 }, PanelAction.Cancel),
                ("15-holding", normal with { State = WorkerState.Capturing, StateText = "Capturing" }, PanelAction.None),
                ("16-capture-problems", normal with { PendingProblems = "your head was tilted -27° (look straight ahead)\nleft foot: moved during the capture (4.1 cm, 7°)\nleft knee: not under the head (44 cm to the side, 58 cm down), this does not look like standing" }, PanelAction.Discard),
            };
            foreach (var (name, m, hover) in shots)
            {
                using var bmp = DashboardPanel.RenderBitmap(m, hover);
                bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
