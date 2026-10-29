using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Valve.VR;

namespace FbtCal
{
    public enum WorkerState { WaitingForSteamVR, Watching, Calibrating, Capturing, Testing }

    public sealed class CaptureResult
    {
        public Profile Profile;
        public List<string> Problems = new List<string>();
        public string Summary = "";
    }

    // Everything that talks to SteamVR runs on this one thread: connecting, watching the VRChat
    // log, the override loop, the capture and the dashboard panel. Same behaviour as
    // app/fbtcal.py (run / hold / capture).
    public sealed class VrWorker
    {
        const int CalibrationTimeoutMs = 90_000;  // failsafe if VRChat never reports the end
        const int TestMs = 20_000;
        const int EndGraceMs = 300;               // keep the ideal pose a moment after VRChat took it
        const int OverrideHz = 200;
        const int LogPollMs = 50;
        const int UiPumpMs = 20;
        const int ToastMs = 6000;
        public const int CaptureDelayS = 25;       // from the desktop tray: time to put the headset on
        public const int CapturePanelDelayS = 10;  // from the SteamVR panel: already wearing it
        const double CaptureSeconds = 3;
        const int CaptureHz = 90;
        const double StandingMaxSidewaysM = 0.30;  // same checks as app/fbtcal.py capture
        const double StandingMinDropM = 0.15;
        const string Watching = "Ready";  // (watching the VRChat log: calibrate whenever you like)
        const double StanceStepCm = 2, StanceMinCm = 10, StanceMaxCm = 60;
        const double ToeStepDeg = 3, ToeMinDeg = -15, ToeMaxDeg = 45;
        const double ForwardStepCm = 2, ForwardMaxCm = 20;

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        public event Action<WorkerState, string> StateChanged;  // all events fire on the worker thread
        public event Action<string, bool> Notify;               // text, is it a warning
        public event Action SteamVRQuit;
        public event Action<string> CopyText;                   // for the clipboard (the UI thread puts it there)

        readonly ConcurrentQueue<string> _requests = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<PanelAction> _windowActions = new ConcurrentQueue<PanelAction>();  // clicks in the desktop window
        volatile PanelModel _latest;  // what the tab shows, for the desktop window
        readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        volatile bool _stop, _cancel;
        volatile CaptureResult _pending;  // a capture with problems, waiting for "save anyway" / "discard"
        Thread _thread;
        CVRSystem _vr;
        OverrideWriter _writer;
        VrcLogWatcher _log;
        DashboardPanel _panel;

        // what the panel shows
        WorkerState _state = WorkerState.WaitingForSteamVR;
        string _stateText = "Waiting for SteamVR";
        int _countdown = -1, _testLeft = -1, _found, _inProfile;
        bool _driverLoaded, _autoStart;
        string _lastCal = "", _toast = "", _missing = "";
        long _toastUntil, _lastCacheRefresh, _lastUi;
        bool _cachesStale = true;  // refresh at the next UI pump (a sentinel like long.MinValue overflowed: now - MinValue < 0)
        Profile _profile;
        string _profileKey = "", _poseProblem = "";
        double? _stanceCm, _toeOutDeg, _forwardCm;  // the Settings page's values (null = the pose's own)
        double _baseStanceCm = double.NaN;  // the pose's own feet distance (captured, or SlimeVR's hips)
        double _baseToeOutDeg = double.NaN; // the pose's own toe-out
        bool _settingsPage;
        int _adjustVersion;  // +1 on every Settings page change: a running test or calibration picks it up
        PosePoint[] _pose = Array.Empty<PosePoint>();
        string _poseKey = "";

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "fbtcal-vr" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(3000);
        }

        public void RequestCapture(int delayS) => _requests.Enqueue("capture:" + delayS);
        public void RequestTest() => _requests.Enqueue("test");
        public void RequestAutoStart() => _requests.Enqueue("autostart");
        public bool HasPendingCapture => _pending != null;
        public void ResolveCapture(bool save) => _requests.Enqueue(save ? "save" : "discard");
        public void PostAction(PanelAction a) => _windowActions.Enqueue(a);  // (UI thread)
        public PanelModel LatestModel => _latest;                            // null until the worker's first pump

        void SetState(WorkerState s, string text)
        {
            _state = s;
            _stateText = text;
            StateChanged?.Invoke(s, text);
        }

        void Toast(string text)
        {
            _toast = text;
            _toastUntil = Environment.TickCount64 + ToastMs;
        }

        void Run()
        {
            try
            {
                _log = new VrcLogWatcher(VrcLogWatcher.DefaultDir);
                Log.Info($"VRChat log: {_log.CurrentPath ?? "none yet"}, IK debug lines: {_log.IkDebugSeen}");
                while (!_stop)
                {
                    if (!Connect())
                    {
                        SetState(WorkerState.WaitingForSteamVR, "Waiting for SteamVR");
                        for (int i = 0; i < 30 && !_stop; i++)
                        {
                            PumpUi();  // the desktop window works without SteamVR
                            Thread.Sleep(100);
                        }
                        continue;
                    }
                    bool quit = Session();
                    _writer?.Clear();
                    OpenVR.Shutdown();  // (also removes the dashboard panel)
                    _vr = null;
                    _panel?.Dispose();
                    _panel = null;
                    if (quit)
                    {
                        Log.Info("SteamVR quit");
                        SteamVRQuit?.Invoke();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("worker crashed: " + ex);
                Notify?.Invoke("Something went wrong, details in the log: " + ex.Message, true);
            }
            finally
            {
                _writer?.Dispose();
            }
        }

        static bool SteamVRRunning()
        {
            var found = Process.GetProcessesByName("vrserver");
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }

        bool Connect()
        {
            // An overlay app (needed for the dashboard panel) would start SteamVR itself: only
            // connect to one that already runs.
            if (!SteamVRRunning()) return false;
            var err = EVRInitError.None;
            _vr = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
            if (err != EVRInitError.None)
            {
                _vr = null;
                return false;
            }
            _driverLoaded = _writer != null || OverrideWriter.MappingExists();
            Log.Info($"connected to SteamVR, driver loaded: {_driverLoaded}");
            Log.Info(Setup.ApplyAutoStart(Settings.AutoStart));
            int adapter = -1;  // the panel's texture goes on the headset's GPU
            _vr.GetDXGIOutputInfo(ref adapter);
            _panel = new DashboardPanel();
            Log.Info(_panel.Create(OpenVR.Overlay, adapter));
            if (!_driverLoaded)
            {
                // At the first start SteamVR may never have run, so there was no driver list to join:
                // now there is (a running SteamVR has written it).
                bool registered = Setup.EnsureDriverRegistered(out string reg);
                Log.Info(reg);
                Notify?.Invoke(registered
                    ? "FBTcal's SteamVR driver is set up now: restart SteamVR once."
                    : "The FBTcal driver is not loaded, so the trackers won't move. SteamVR Settings > " +
                      "Startup / Shutdown > Manage Add-ons > fbtcal: On, then restart SteamVR.", true);
            }
            if (LoadProfile(out var source, out string problem) == null)
            {
                Log.Warn("no standing pose: " + problem);
                Notify?.Invoke(source == PoseSource.SlimeVr
                    ? problem + " Capture a standing pose: SteamVR menu > FBTcal tab, or right-click the tray icon > Capture standing pose."
                    : "No standing pose captured yet. SteamVR menu > FBTcal tab, or right-click the tray icon > Capture standing pose.", true);
            }
            _cachesStale = true;
            return true;
        }

        // Returns true when SteamVR quit.
        bool Session()
        {
            SetState(WorkerState.Watching, Watching);
            while (!_stop)
            {
                if (QuitRequested()) return true;
                while (_requests.TryDequeue(out string r))
                {
                    bool alive = true;
                    if (r.StartsWith("capture:")) alive = DoCapture(int.Parse(r.Substring(8), CultureInfo.InvariantCulture));
                    else if (r == "test") alive = DoOverride("test", TestMs, watchLog: false);
                    else if (r == "autostart") Log.Info(Setup.ApplyAutoStart(Settings.AutoStart));
                    else if (r == "save" || r == "discard") Resolve(r == "save");
                    if (!alive) return true;
                    SetState(WorkerState.Watching, Watching);
                }
                var events = _log.Poll();
                if (events.Contains(VrcLogWatcher.NewLog)) Log.Info($"new VRChat log {_log.CurrentPath}, IK debug lines: {_log.IkDebugSeen}");
                int start = events.LastIndexOf(VrcLogWatcher.Start), end = events.LastIndexOf(VrcLogWatcher.End);
                if (start >= 0 && end > start)
                {
                    // Both arrived in one read: VRChat already took the calibration with the real poses.
                    Log.Warn("calibration start and end arrived together, too late to help");
                    Notify?.Invoke("The VRChat log came late, this calibration used your real pose. Try again.", true);
                }
                else if (start >= 0)
                {
                    if (!DoOverride("calibration", CalibrationTimeoutMs, watchLog: true)) return true;
                    SetState(WorkerState.Watching, Watching);
                }
                PumpUi(force: true);
                Thread.Sleep(LogPollMs);
            }
            return false;
        }

        // The pose in use: the capture or SlimeVR's proportions (the panel's switch).
        Profile LoadProfile(out PoseSource source, out string problem)
        {
            var p = Profile.LoadInUse(out source, out problem);
            if (p == null) Log.Warn($"pose ({source}): {problem}");
            return p;
        }

        // ------------------------------------------------------------ dashboard panel

        // The tab's laser and the desktop window's mouse, then the picture for both.
        void PumpUi(bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastUi < UiPumpMs) return;
            _lastUi = now;
            var ov = _panel != null ? OpenVR.Overlay : null;
            if (_panel != null) Handle(_panel.Pump(ov), window: false);
            while (_windowActions.TryDequeue(out var a)) Handle(a, window: true);
            RefreshCaches(now);
            var model = Model(now);
            _latest = model;
            _panel?.Render(ov, model);
        }

        void Handle(PanelAction action, bool window)
        {
            if (action == PanelAction.None) return;
            Log.Info((window ? "window: " : "panel: ") + action);
            if (_vr == null && action is PanelAction.Capture or PanelAction.Test)
                return;  // clicked just as SteamVR closed: no capture or test may start at the next connect
            switch (action)
            {
                // from the desktop there is the headset to put on first
                case PanelAction.Capture: RequestCapture(window ? CaptureDelayS : CapturePanelDelayS); break;
                case PanelAction.Test: RequestTest(); break;
                case PanelAction.Cancel: _cancel = true; break;
                case PanelAction.SaveAnyway: ResolveCapture(true); break;
                case PanelAction.Discard: ResolveCapture(false); break;
                case PanelAction.ToggleAutoStart:
                    Settings.AutoStart = !Settings.AutoStart;
                    if (_vr != null) Log.Info(Setup.ApplyAutoStart(Settings.AutoStart));  // else at the next connect
                    _cachesStale = true;
                    break;
                case PanelAction.StanceMinus:
                case PanelAction.StancePlus:
                    double from = _stanceCm ?? Math.Round(_baseStanceCm);
                    if (double.IsNaN(from)) break;
                    double to = Math.Clamp(Math.Round(from) + (action == PanelAction.StancePlus ? StanceStepCm : -StanceStepCm), StanceMinCm, StanceMaxCm);
                    Settings.StanceCm = to;
                    Log.Info($"stance {from:0} -> {to:0} cm (pose's own {_baseStanceCm:0.0})");
                    Adjusted();
                    break;
                case PanelAction.StanceReset:
                    Settings.StanceCm = null;
                    Log.Info($"stance back to the pose's own {_baseStanceCm:0.0} cm");
                    Adjusted();
                    break;
                case PanelAction.ToeOutMinus:
                case PanelAction.ToeOutPlus:
                    double toeFrom = _toeOutDeg ?? Math.Round(_baseToeOutDeg);
                    if (double.IsNaN(toeFrom)) break;
                    double toeTo = Math.Clamp(Math.Round(toeFrom) + (action == PanelAction.ToeOutPlus ? ToeStepDeg : -ToeStepDeg), ToeMinDeg, ToeMaxDeg);
                    Settings.ToeOutDeg = toeTo;
                    Log.Info($"toe-out {toeFrom:0} -> {toeTo:0} deg (pose's own {_baseToeOutDeg:0.0})");
                    Adjusted();
                    break;
                case PanelAction.ToeOutReset:
                    Settings.ToeOutDeg = null;
                    Log.Info($"toe-out back to the pose's own {_baseToeOutDeg:0.0} deg");
                    Adjusted();
                    break;
                case PanelAction.ForwardMinus:
                case PanelAction.ForwardPlus:
                    double fwdFrom = _forwardCm ?? 0;
                    double fwdTo = Math.Clamp(fwdFrom + (action == PanelAction.ForwardPlus ? ForwardStepCm : -ForwardStepCm), -ForwardMaxCm, ForwardMaxCm);
                    Settings.FeetForwardCm = fwdTo == 0 ? null : fwdTo;
                    Log.Info($"feet forward {fwdFrom:0} -> {fwdTo:0} cm");
                    Adjusted();
                    break;
                case PanelAction.ForwardReset:
                    Settings.FeetForwardCm = null;
                    Log.Info("feet forward back to 0");
                    Adjusted();
                    break;
                case PanelAction.CopyIkFlag:
                    CopyText?.Invoke("--enable-ik-debug-logging");
                    Toast("Copied. Paste it into VRChat's launch options.");
                    break;
                case PanelAction.OpenSettings: _settingsPage = true; break;
                case PanelAction.CloseSettings: _settingsPage = false; break;
                case PanelAction.SourceCapture:
                case PanelAction.SourceSlimeVr:
                    var source = action == PanelAction.SourceCapture ? PoseSource.Capture : PoseSource.SlimeVr;
                    Settings.PoseSource = source;
                    Log.Info($"pose source -> {source}");
                    _cachesStale = true;
                    break;
            }
        }

        void Adjusted()
        {
            _adjustVersion++;
            _cachesStale = true;
        }

        // The pose with the Settings page's values on it (stance, toe-out, feet forward).
        static Dictionary<string, (Vec3 pos, Mat3 rot)> Adjust(Dictionary<string, (Vec3 pos, Mat3 rot)> pose,
                                                               double? stanceCm, double? toeOutDeg, double? forwardCm) =>
            PoseMath.AdjustPose(pose, stanceCm / 100.0, toeOutDeg, forwardCm / 100.0);

        static string DescribeAdjust(double? stanceCm, double? toeOutDeg, double? forwardCm) =>
            (stanceCm != null ? $", stance {stanceCm:0} cm" : "") + (toeOutDeg != null ? $", toe-out {toeOutDeg:0} deg" : "") +
            (forwardCm != null ? $", feet forward {forwardCm:+0;-0} cm" : "");

        // Things that take a file read or 64 SteamVR calls: once a second is plenty.
        void RefreshCaches(long now)
        {
            if (!_cachesStale && now - _lastCacheRefresh < 1000) return;
            _cachesStale = false;
            _lastCacheRefresh = now;
            _autoStart = Settings.AutoStart;
            try
            {
                // reloaded when the source is switched or its file changes (SlimeVR saves its profile too)
                var source = Profile.SourceInUse();
                string file = source == PoseSource.SlimeVr ? SlimeVrBody.ProfilePath() : Paths.ProfileFile;
                string key = $"{source}|{file}|{(File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0)}";
                if (key != _profileKey)
                {
                    _profileKey = key;
                    _profile = LoadProfile(out _, out _poseProblem);
                    if (_profile != null) Log.Info("panel shows pose: " + _profile.Describe());
                }
            }
            catch (Exception) { }
            _inProfile = _profile?.Trackers.Count ?? 0;
            var present = _vr != null ? Trackers() : new Dictionary<string, uint>();  // (the desktop window without SteamVR)
            _found = _profile == null ? 0 : present.Keys.Count(_profile.Trackers.ContainsKey);
            var missing = _profile == null ? new List<string>() : _profile.Trackers.Keys.Where(k => !present.ContainsKey(k)).ToList();
            if (_profile?.Source == PoseSource.SlimeVr && _found > 0)
            {
                _inProfile = _found;  // SlimeVR's pose has every tracker it can have; one not worn is no problem
                missing.Clear();
            }
            _missing = string.Join(", ", missing.Select(TrackerName));

            // the figure: the pose in use, rebuilt only when the profile or a setting changed
            _stanceCm = Settings.StanceCm;
            _toeOutDeg = Settings.ToeOutDeg;
            _forwardCm = Settings.FeetForwardCm;
            string poseKey = $"{_profileKey}|{_stanceCm}|{_toeOutDeg}|{_forwardCm}";
            if (poseKey == _poseKey) return;
            _poseKey = poseKey;
            if (_profile == null)
            {
                _pose = Array.Empty<PosePoint>();
                _baseStanceCm = _baseToeOutDeg = double.NaN;
                return;
            }
            _baseStanceCm = PoseMath.StanceM(PoseMath.ApplyToeOut(_profile.Trackers, _toeOutDeg)) * 100;  // (toe-out moves the toes)
            _baseToeOutDeg = PoseMath.ToeOutDeg(_profile.Trackers);
            var points = new List<PosePoint> { new PosePoint("head", 0, 0, 0) };
            foreach (var kv in Adjust(_profile.Trackers, _stanceCm, _toeOutDeg, _forwardCm))
            {
                string part = kv.Key.StartsWith("human://") ? kv.Key.Substring(8) : kv.Key;
                points.Add(new PosePoint(part, (float)kv.Value.pos.X, (float)kv.Value.pos.Y, (float)kv.Value.pos.Z));
            }
            _pose = points.ToArray();
        }

        PanelModel Model(long now)
        {
            string title = "", detail = "", note = "";
            if (_profile?.Source == PoseSource.SlimeVr)
            {
                var b = _profile.SlimeVr;
                title = "proportions";
                detail = $"head–ankle {b.HeightM * 100:0} cm\n" + (b.Missing.Count > 0 ? "partly defaults" : $"profile: {b.ProfileName}");
                note = "as in SlimeVR";
            }
            else if (_profile != null)
            {
                title = DateTime.TryParse(_profile.Captured, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t.ToString("MMM d, HH:mm") : _profile.Captured;
                detail = _profile.HasSymmetry
                    ? $"symmetrized\n{Math.Abs(_profile.BodyYawDeg):0.0}° · {_profile.PairMismatchM * 100:0.0} cm"
                    : "old capture,\nnot symmetrized";
                note = "as captured";
            }
            double stance = _stanceCm ?? _baseStanceCm;
            return new PanelModel(_state, _stateText, _countdown, _testLeft, _driverLoaded, _found, _inProfile, _missing,
                                  _log?.IkDebugSeen ?? false, _lastCal, _profile?.Source ?? Profile.SourceInUse(),
                                  title, detail, _profile == null ? _poseProblem : "",
                                  stance, _stanceCm != null, note, _baseStanceCm,
                                  _toeOutDeg ?? _baseToeOutDeg, _toeOutDeg != null, _baseToeOutDeg, _forwardCm ?? 0,
                                  _settingsPage, _pose, _autoStart,
                                  _pending == null ? "" : string.Join("\n", _pending.Problems),
                                  now < _toastUntil ? _toast : "");
        }

        // ------------------------------------------------------------ override

        // Shows the ideal pose until VRChat reports the end (watchLog), the time runs out or the
        // panel's cancel button. Returns false if SteamVR quit meanwhile.
        bool DoOverride(string what, int timeoutMs, bool watchLog)
        {
            var profile = LoadProfile(out var source, out string problem);
            if (profile == null || profile.Trackers.Count == 0)
            {
                Log.Warn($"{what}: no standing pose, nothing to show");
                Sound.Problem();
                if (source == PoseSource.SlimeVr)
                {
                    Toast("Can't read the SlimeVR proportions.");
                    Notify?.Invoke(problem + " So I can't help this time. SteamVR menu > FBTcal tab: switch to the captured pose.", true);
                }
                else
                {
                    Toast("Capture the standing pose first.");
                    Notify?.Invoke("No standing pose captured, so I can't help this time. SteamVR menu > FBTcal tab > Capture standing pose.", true);
                }
                return true;
            }
            // the Settings page's values apply to every calibration; changed on the panel meanwhile
            // (a test, to see it on the trackers), they apply at once
            var basePose = profile.Trackers;
            int adjustSeen = _adjustVersion;
            double? stanceCm = Settings.StanceCm, toeOutDeg = Settings.ToeOutDeg, forwardCm = Settings.FeetForwardCm;
            profile.Trackers = Adjust(basePose, stanceCm, toeOutDeg, forwardCm);
            _writer ??= new OverrideWriter();
            var map = MatchTrackers(profile);
            Log.Info($"{what}: pose {profile.Describe()}" + DescribeAdjust(stanceCm, toeOutDeg, forwardCm));
            Log.Info($"{what} start: {DescribeHmd()}; trackers {string.Join(", ", map.Select(kv => $"{kv.Key}=#{kv.Value}"))}");
            var missing = profile.Trackers.Keys.Except(map.Keys).ToList();
            if (missing.Count > 0) Log.Warn("not in SteamVR: " + string.Join(", ", missing));
            SetState(watchLog ? WorkerState.Calibrating : WorkerState.Testing, watchLog ? "Calibrating" : "Test");
            Sound.On();

            _cancel = false;
            string outcome = "done";
            var sw = Stopwatch.StartNew();
            long endAt = -1, lastRefresh = 0, lastLogPoll = 0;
            timeBeginPeriod(1);
            try
            {
                while (!_stop)
                {
                    long now = sw.ElapsedMilliseconds;
                    if (now - lastRefresh >= 1000)
                    {
                        lastRefresh = now;
                        map = MatchTrackers(profile);
                        if (QuitRequested()) return false;
                    }
                    if (watchLog && now - lastLogPoll >= LogPollMs)
                    {
                        lastLogPoll = now;
                        foreach (string ev in _log.Poll())
                        {
                            if (ev == VrcLogWatcher.Start)
                            {
                                Log.Info("calibration restarted");
                                sw.Restart();
                                now = lastRefresh = lastLogPoll = 0;
                                endAt = -1;
                            }
                            else if (ev == VrcLogWatcher.End && endAt < 0)
                            {
                                endAt = now + EndGraceMs;
                                Log.Info($"calibration taken: {DescribeHmd()}");
                            }
                        }
                    }
                    if (endAt >= 0 && now >= endAt) break;
                    if (_cancel)
                    {
                        Log.Info($"{what} cancelled on the panel");
                        outcome = "cancelled";
                        break;
                    }
                    if (now >= timeoutMs)
                    {
                        if (watchLog)
                        {
                            Log.Warn("calibration timeout");
                            outcome = "timed out";
                            Notify?.Invoke("VRChat never reported the end of the calibration, the ideal pose is off again.", true);
                        }
                        break;
                    }
                    if (adjustSeen != _adjustVersion)
                    {
                        adjustSeen = _adjustVersion;
                        stanceCm = Settings.StanceCm;
                        toeOutDeg = Settings.ToeOutDeg;
                        forwardCm = Settings.FeetForwardCm;
                        profile.Trackers = Adjust(basePose, stanceCm, toeOutDeg, forwardCm);
                        Log.Info($"{what}: settings now" + DescribeAdjust(stanceCm, toeOutDeg, forwardCm));
                    }
                    WriteIdeal(profile, map);
                    if (!watchLog) _testLeft = (int)Math.Ceiling((timeoutMs - now) / 1000.0);
                    PumpUi();
                    Thread.Sleep(1000 / OverrideHz);
                }
            }
            finally
            {
                timeEndPeriod(1);
                _writer.Clear();
                _testLeft = -1;
                _cancel = false;
            }
            if (watchLog) _lastCal = $"{DateTime.Now:HH:mm} · {outcome}";
            Log.Info($"{what} done ({outcome})");
            Sound.Off();
            return true;
        }

        void WriteIdeal(Profile profile, Dictionary<string, uint> map)
        {
            Sample();
            var (hR, hP, ok) = Pose(OpenVR.k_unTrackedDeviceIndex_Hmd);
            if (!ok) return;  // headset lost: no write, the heartbeat lapses and the real poses return
            var entries = new List<(uint, Vec3, Quat)>(map.Count);
            foreach (var kv in map)
            {
                var (offPos, offRot) = profile.Trackers[kv.Key];
                var (r, p) = PoseMath.IdealPose(hR, hP, offPos, offRot);
                entries.Add((kv.Value, p, PoseMath.QuatFromMatrix(r)));
            }
            _writer.Write(entries);
        }

        // ------------------------------------------------------------ capture

        // Returns false if SteamVR quit meanwhile.
        bool DoCapture(int delayS)
        {
            var trackers = Trackers();
            if (trackers.Count == 0)
            {
                Sound.Problem();
                Toast("I can't see any tracker in SteamVR.");
                Notify?.Invoke("I can't see any tracker in SteamVR.", true);
                return true;
            }
            Log.Info($"capture in {delayS} s: trackers " + string.Join(", ", trackers.Keys));
            _pending = null;
            _cancel = false;
            try
            {
                for (int s = delayS; s > 0; s--)
                {
                    if (s <= 3) Sound.Ready();
                    else if (s % 5 == 0) Sound.Tick();
                    _countdown = s;
                    SetState(WorkerState.Capturing, "Capturing");
                    for (int i = 0; i < 10; i++)
                    {
                        if (_stop) return true;
                        if (_cancel)
                        {
                            Log.Info("capture cancelled on the panel");
                            Sound.Off();
                            Toast("Capture cancelled, the old pose stays.");
                            return true;
                        }
                        if (QuitRequested()) return false;
                        PumpUi(force: true);
                        Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                _countdown = -1;
                _cancel = false;
            }
            Sound.Go();
            SetState(WorkerState.Capturing, "Capturing");
            PumpUi(force: true);  // "stay still" on the panel; no drawing while sampling

            int n = (int)(CaptureSeconds * CaptureHz);
            var hmd = new List<(Mat3 R, Vec3 p, bool ok)>(n);
            var trk = trackers.Keys.ToDictionary(k => k, k => new List<(Mat3 R, Vec3 p, bool ok)>(n));
            timeBeginPeriod(1);
            try
            {
                var sw = Stopwatch.StartNew();
                for (int k = 0; k < n; k++)
                {
                    Sample();
                    hmd.Add(Pose(OpenVR.k_unTrackedDeviceIndex_Hmd));
                    foreach (var kv in trackers) trk[kv.Key].Add(Pose(kv.Value));
                    int wait = (int)((k + 1) * 1000.0 / CaptureHz - sw.ElapsedMilliseconds);
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally { timeEndPeriod(1); }
            Sound.Done();

            var result = Evaluate(hmd, trk, n);
            if (result.Problems.Count == 0) Save(result);
            else
            {
                _pending = result;
                Sound.Problem();
                Notify?.Invoke("Problem with the capture, not saved. Decide: SteamVR menu > FBTcal tab, " +
                               "or right-click the tray icon (Save anyway / Discard).", true);
            }
            return true;
        }

        void Resolve(bool save)
        {
            var pending = _pending;
            _pending = null;
            if (pending == null) return;
            if (save) Save(pending);
            else
            {
                Log.Info("capture discarded");
                Toast("Discarded, the old pose stays.");
            }
        }

        void Save(CaptureResult r)
        {
            try
            {
                r.Profile.Save();
                Log.Info("profile saved and read back: " + Paths.ProfileFile);
                string switched = "";
                if (Settings.PoseSource == PoseSource.SlimeVr)  // a fresh capture is meant to be used
                {
                    Settings.PoseSource = PoseSource.Capture;
                    Log.Info("pose source -> Capture (new capture)");
                    switched = ", using it from now on";
                }
                Toast($"Standing pose saved ({r.Profile.Trackers.Count} trackers){switched}.");
                Notify?.Invoke($"Standing pose saved ({r.Profile.Trackers.Count} trackers){switched}.", false);
            }
            catch (Exception ex)
            {
                Log.Warn("profile save: " + ex);
                Toast("Could not save.");
                Notify?.Invoke("Could not save: " + ex.Message, true);
            }
            _cachesStale = true;
        }

        static CaptureResult Evaluate(List<(Mat3 R, Vec3 p, bool ok)> hmd, Dictionary<string, List<(Mat3 R, Vec3 p, bool ok)>> trk, int n)
        {
            var result = new CaptureResult { Profile = new Profile { Captured = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss") } };
            var pitches = hmd.Where(h => h.ok).Select(h => PoseMath.PitchDeg(h.R)).OrderBy(v => v).ToList();
            double pitch = pitches.Count > 0 ? pitches[pitches.Count / 2] : double.NaN;
            result.Profile.HmdPitchDeg = pitch;
            if (!(Math.Abs(pitch) <= 20)) result.Problems.Add($"your head was tilted {pitch:+0;-0}° (look straight ahead)");

            var sb = new StringBuilder("Trackers against the head (cm: right / up / back):\n");
            foreach (var kv in trk)
            {
                var idx = Enumerable.Range(0, n).Where(i => hmd[i].ok && kv.Value[i].ok).ToList();
                if (idx.Count < 0.8 * n)
                {
                    result.Problems.Add($"{TrackerName(kv.Key)}: too few valid samples");
                    continue;
                }
                var off = PoseMath.ComputeRestOffset(idx.Select(i => hmd[i].R).ToList(), idx.Select(i => hmd[i].p).ToList(),
                                                     idx.Select(i => kv.Value[i].R).ToList(), idx.Select(i => kv.Value[i].p).ToList());
                bool moved = off.PosSpreadM > 0.03 || off.RotSpreadDeg > 5;
                if (moved) result.Problems.Add($"{TrackerName(kv.Key)}: moved during the capture ({off.PosSpreadM * 100:F1} cm, {off.RotSpreadDeg:F0}°)");
                sb.AppendLine($"{kv.Key}: {off.Pos.X * 100:+0.0;-0.0} / {off.Pos.Y * 100:+0.0;-0.0} / {off.Pos.Z * 100:+0.0;-0.0}{(moved ? "  <- mozgott" : "")}");
                result.Profile.Trackers[kv.Key] = (off.Pos, off.Rot);
            }
            // Standing = every tracker hangs under the head. Catches a capture started by accident
            // while sitting or lying (knees and feet forward), which would spoil every calibration.
            foreach (var kv in result.Profile.Trackers)
            {
                Vec3 p = kv.Value.pos;
                double sideways = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (sideways > StandingMaxSidewaysM || p.Y > -StandingMinDropM)
                    result.Problems.Add($"{TrackerName(kv.Key)}: not under the head ({sideways * 100:F0} cm to the side, {-p.Y * 100:F0} cm down), this does not look like standing");
            }
            // The ideal pose faces straight ahead and is left/right symmetric; the measured one is kept too.
            var profile = result.Profile;
            profile.Raw = profile.Trackers;
            profile.Trackers = PoseMath.Symmetrize(profile.Raw, out profile.BodyYawDeg, out profile.PairMismatchM);
            profile.HasSymmetry = true;
            sb.AppendLine($"Symmetrized: your body was turned {profile.BodyYawDeg:+0.0;-0.0}° against your head, " +
                          $"left and right differed by {profile.PairMismatchM * 100:F1} cm at most.");
            result.Summary = sb.ToString();
            Log.Info($"capture: pitch {pitch:F1}\n{result.Summary}problems: {(result.Problems.Count == 0 ? "none" : string.Join("; ", result.Problems))}");
            return result;
        }

        // "human://LEFT_KNEE" -> "left knee", for the messages people read.
        static string TrackerName(string serial)
        {
            string s = serial.StartsWith("human://") ? serial.Substring(8) : serial;
            return s switch
            {
                "WAIST" => "waist",
                "CHEST" => "chest",
                "LEFT_KNEE" => "left knee",
                "RIGHT_KNEE" => "right knee",
                "LEFT_FOOT" => "left foot",
                "RIGHT_FOOT" => "right foot",
                "LEFT_ELBOW" => "left elbow",
                "RIGHT_ELBOW" => "right elbow",
                _ => s,
            };
        }

        // ------------------------------------------------------------ SteamVR access

        void Sample() =>  // raw universe: the same space the driver poses live in
            _vr.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, _poses);

        (Mat3 R, Vec3 p, bool ok) Pose(uint i)
        {
            var pose = _poses[i];
            var m = pose.mDeviceToAbsoluteTracking;
            return (new Mat3(m.m0, m.m1, m.m2, m.m4, m.m5, m.m6, m.m8, m.m9, m.m10), new Vec3(m.m3, m.m7, m.m11),
                    pose.bPoseIsValid && pose.eTrackingResult == ETrackingResult.Running_OK);
        }

        string DescribeHmd()
        {
            Sample();
            var (r, p, ok) = Pose(OpenVR.k_unTrackedDeviceIndex_Hmd);
            return ok ? $"hmd pitch {PoseMath.PitchDeg(r):F0} facing yaw {PoseMath.FacingYawDeg(r):F1} pos {p}" : "hmd not tracking";
        }

        Dictionary<string, uint> Trackers()
        {
            var d = new Dictionary<string, uint>();
            var sb = new StringBuilder(256);
            for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                if (_vr.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;
                var err = ETrackedPropertyError.TrackedProp_Success;
                sb.Clear();
                _vr.GetStringTrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String, sb, (uint)sb.Capacity, ref err);
                if (err == ETrackedPropertyError.TrackedProp_Success && sb.Length > 0) d[sb.ToString()] = i;
            }
            return d;
        }

        Dictionary<string, uint> MatchTrackers(Profile profile) =>
            Trackers().Where(kv => profile.Trackers.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        bool QuitRequested()
        {
            var ev = new VREvent_t();
            uint size = (uint)Marshal.SizeOf(typeof(VREvent_t));
            while (_vr.PollNextEvent(ref ev, size))
            {
                if (ev.eventType == (uint)EVREventType.VREvent_Quit)
                {
                    _vr.AcknowledgeQuit_Exiting();
                    return true;
                }
            }
            return false;
        }
    }
}
