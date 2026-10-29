using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FbtCal
{
    // The whole UI: a tray icon (colour = state) and its menu.
    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly Control _ui;  // marshals the worker's events onto the UI thread
        readonly VrWorker _worker = new VrWorker();
        readonly ToolStripMenuItem _status, _profile, _autostart, _saveAnyway, _discard, _sourceCapture, _sourceSlimeVr;
        // the tracker icon; its case colour is the state (art\make_icons.py)
        readonly Icon _iconWatching = Res.TrayIcon("tray-watching.ico");
        readonly Icon _iconActive = Res.TrayIcon("tray-active.ico");
        readonly Icon _iconWaiting = Res.TrayIcon("tray-waiting.ico");
        public const string Website = "https://erik1028.github.io/fbtcal/";
        const long StrayClickMs = 600;
        long _menuOpenedAt;
        bool _quitting;

        public TrayApp(string startupNote)
        {
            _ui = new Control();
            _ui.CreateControl();

            _status = new ToolStripMenuItem("Starting…") { Enabled = false };
            _profile = new ToolStripMenuItem("") { Enabled = false };
            _autostart = new ToolStripMenuItem("Start with SteamVR") { Checked = Settings.AutoStart };
            _autostart.Click += Guarded("autostart", () =>
            {
                _autostart.Checked = !_autostart.Checked;
                Settings.AutoStart = _autostart.Checked;
                _worker.RequestAutoStart();
            });
            // A capture with problems waits here or on the SteamVR panel, whichever answers first.
            _saveAnyway = new ToolStripMenuItem("Save the capture anyway", null, Guarded("save anyway", () => _worker.ResolveCapture(true)));
            _discard = new ToolStripMenuItem("Discard the capture", null, Guarded("discard", () => _worker.ResolveCapture(false)));
            // same switch as on the SteamVR panel; the worker picks it up within a second
            _sourceCapture = new ToolStripMenuItem("Captured (standing)", null, Guarded("source capture", () => Settings.PoseSource = PoseSource.Capture));
            _sourceSlimeVr = new ToolStripMenuItem("From SlimeVR's body proportions", null, Guarded("source slimevr", () => Settings.PoseSource = PoseSource.SlimeVr));
            var source = new ToolStripMenuItem("Standing pose from");
            source.DropDownItems.Add(_sourceCapture);
            source.DropDownItems.Add(_sourceSlimeVr);
            var menu = new ContextMenuStrip();
            menu.Items.Add(_status);
            menu.Items.Add(_profile);
            menu.Items.Add(_saveAnyway);
            menu.Items.Add(_discard);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Capture standing pose (in {VrWorker.CaptureDelayS} s)…", null,
                           Guarded("capture", () => _worker.RequestCapture(VrWorker.CaptureDelayS)));
            menu.Items.Add("Test: ideal pose for 20 s", null, Guarded("test", _worker.RequestTest));
            menu.Items.Add(source);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_autostart);
            menu.Items.Add("Open the log", null, Guarded("log", OpenLog));
            menu.Items.Add($"Website and help (fbtcal {Application.ProductVersion.Split('+')[0]})", null, Guarded("website", () =>
                Process.Start(new ProcessStartInfo(Website) { UseShellExecute = true })));
            menu.Items.Add("Quit…", null, Guarded("quit", ConfirmQuit));
            menu.Opening += (s, e) =>
            {
                _menuOpenedAt = Environment.TickCount64;
                RefreshProfileLine();
                _saveAnyway.Visible = _discard.Visible = _worker.HasPendingCapture;
                _autostart.Checked = Settings.AutoStart;  // (the SteamVR panel can switch it too)
            };

            _tray = new NotifyIcon { Icon = _iconWaiting, Text = "fbtcal", ContextMenuStrip = menu, Visible = true };
            RefreshProfileLine();

            _worker.StateChanged += (st, text) => OnUi(() => ShowState(st, text));
            _worker.Notify += (text, warn) => OnUi(() => Balloon(text, warn));
            _worker.CopyText += text => OnUi(() =>  // the clipboard wants the UI (STA) thread
            {
                try { Clipboard.SetText(text); }
                catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
            });
            _worker.SteamVRQuit += () => OnUi(() => Quit("SteamVR quit"));
            if (!string.IsNullOrEmpty(startupNote)) Balloon(startupNote, false);
            _worker.Start();
        }

        void OnUi(Action a)
        {
            if (_quitting || _ui.IsDisposed) return;
            _ui.BeginInvoke(() =>
            {
                if (!_quitting) a();  // the tray may be gone by the time this runs
            });
        }

        // A VR desktop view once sent clicks nobody made: a menu opened from the tray got Quit and
        // Capture clicked in the same instant. Clicks right after the menu opens are ignored, and every
        // accepted click is logged.
        EventHandler Guarded(string name, Action action) => (s, e) =>
        {
            long sinceOpen = Environment.TickCount64 - _menuOpenedAt;
            if (sinceOpen < StrayClickMs)
            {
                Log.Warn($"menu: ignored a click on '{name}' {sinceOpen} ms after the menu opened");
                return;
            }
            Log.Info($"menu: {name} ({sinceOpen} ms after opening)");
            action();
        };

        void ConfirmQuit()
        {
            if (MessageBox.Show("Quit fbtcal? While it is not running, it can't help with VRChat calibration.\n" +
                                "It starts again by itself the next time SteamVR starts.",
                                "fbtcal", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Quit("menu");
            else
                Log.Info("quit cancelled");
        }

        void ShowState(WorkerState st, string text)
        {
            _status.Text = text;
            string tip = "fbtcal: " + text;
            _tray.Text = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            _tray.Icon = st switch
            {
                WorkerState.WaitingForSteamVR => _iconWaiting,
                WorkerState.Watching => _iconWatching,
                _ => _iconActive,
            };
        }

        void Balloon(string text, bool warn) =>
            _tray.ShowBalloonTip(8000, "fbtcal", text, warn ? ToolTipIcon.Warning : ToolTipIcon.Info);

        void RefreshProfileLine()
        {
            try
            {
                var p = Profile.LoadInUse(out var source, out string problem);
                _profile.Text = "Standing pose: " + (p != null ? p.Describe() : source == PoseSource.SlimeVr ? problem : "NOT captured");
                _sourceCapture.Checked = source == PoseSource.Capture;
                _sourceSlimeVr.Checked = source == PoseSource.SlimeVr;
            }
            catch (Exception)
            {
                _profile.Text = "Standing pose: broken profile file";
            }
        }

        static void OpenLog()
        {
            if (File.Exists(Paths.LogFile))
                Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
        }

        void Quit(string why)
        {
            if (_quitting) return;
            _quitting = true;
            Log.Info($"quit requested: {why}");
            _worker.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }
}
