using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Valve.VR;

namespace FbtCal
{
    // What the panel shows, built by VrWorker. A record: "nothing changed" is a cheap Equals.
    public sealed record PanelModel(
        WorkerState State,
        string StateText,
        int CountdownS,          // capture countdown, -1 = none
        int TestLeftS,           // test seconds left, -1 = none
        bool DriverLoaded,
        int TrackersFound,
        int TrackersInProfile,
        string MissingTrackers,  // trackers of the pose SteamVR doesn't have, "left knee, right foot"; "" = none
        bool IkDebugSeen,
        string LastCalibration,  // "" = none yet
        PoseSource Source,       // where the standing pose comes from (the switch in the pose card)
        string PoseTitle,        // capture time or "proportions"; "" = no pose to use
        string PoseDetail,       // the capture's symmetry, or SlimeVR's height and profile
        string PoseProblem,      // no pose: why
        double StanceCm,         // feet distance of the pose in use; NaN = unknown
        bool StanceCustom,       // set on the panel (else: the pose's own)
        string StanceNote,       // where the pose's own values come from ("as captured")
        double BaseStanceCm,     // the pose's own feet distance (Reset goes back to it)
        double ToeOutDeg,        // feet turned out (+) / in (-) in the pose in use; NaN = unknown
        bool ToeOutCustom,
        double BaseToeOutDeg,    // the pose's own toe-out
        double ForwardCm,        // feet moved forward (+) / back (-), 0 = as the pose has them
        bool SettingsPage,       // the Settings page instead of the cards
        PosePoint[] Pose,        // the ideal pose in use, for the figure (same array until it changes)
        bool AutoStart,
        string PendingProblems,  // not empty: a capture waits for "save anyway" / "discard"
        string Toast)
    {
        public bool OnDesktop { get; init; }  // drawn in the desktop window (MainWindow.cs), not the SteamVR tab
    }

    // A tracker of the ideal pose in the HMD yaw frame (x right, y up, z back), metres.
    public readonly record struct PosePoint(string Part, float X, float Y, float Z);

    public enum PanelAction { None, Capture, Test, ToggleAutoStart, Cancel, SaveAnyway, Discard, StanceMinus, StancePlus, StanceReset,
                              SourceCapture, SourceSlimeVr, ToeOutMinus, ToeOutPlus, ToeOutReset, ForwardMinus, ForwardPlus, ForwardReset,
                              OpenSettings, CloseSettings, CopyIkFlag }

    // The FBTcal tab in SteamVR's dashboard: drawn with GDI+ (PanelDraw.cs) into one GPU texture
    // (PanelTexture.cs), clicked with the laser in mouse mode. Lives on the VR thread like every other
    // OpenVR call. Orbit's dashboard tab is the model for the details
    // (2.5 m wide, mouse scale = pixels, mouse y counted from the bottom).
    public sealed partial class DashboardPanel : IDisposable
    {
        public const int W = 1024, H = 640;
        const string Key = "erik.fbtcal.panel";

        ulong _main, _thumb;
        PanelTexture _gpu;  // null: no GPU texture, SetOverlayRaw (blinks on redraws) or PNG files
        bool _wasOpen, _dirty = true, _rawFailed;
        float _mx, _my;
        PanelAction _hover, _pressed;
        PanelModel _shown;
        long _lastDraw;
        readonly Bitmap _bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        byte[] _rgba = new byte[W * H * 4];

        public string Create(CVROverlay ov, int adapterIndex)
        {
            ulong main = 0, thumb = 0;
            var e = ov.CreateDashboardOverlay(Key, "FBTcal", ref main, ref thumb);
            if (e != EVROverlayError.None) return "dashboard panel: " + e;
            _main = main;
            _thumb = thumb;
            string gpu;
            try
            {
                _gpu = new PanelTexture(adapterIndex, W, H);
                gpu = $"GPU texture (adapter {adapterIndex})";
            }
            catch (Exception ex)
            {
                gpu = "no GPU texture, raw pixels: " + ex.Message;
            }
            ov.SetOverlayWidthInMeters(_main, 2.5f);
            ov.SetOverlayInputMethod(_main, VROverlayInputMethod.Mouse);
            var scale = new HmdVector2_t { v0 = W, v1 = H };
            ov.SetOverlayMouseScale(_main, ref scale);
            using (var icon = Res.Image("thumb.png"))  // the tab's icon: the tracker
            {
                string thumbResult = Upload(ov, _thumb, icon);  // (once: raw pixels are fine)
                return $"dashboard panel created, {gpu}" + (thumbResult.Length > 0 ? ", thumbnail: " + thumbResult : "");
            }
        }

        public void Dispose()  // after OpenVR.Shutdown: SteamVR has let go of the texture
        {
            _gpu?.Dispose();
            _gpu = null;
            _bmp.Dispose();
        }

        // Reads the laser's events; returns what was clicked (a press and release on the same button).
        public PanelAction Pump(CVROverlay ov)
        {
            if (_main == 0) return PanelAction.None;
            if (!ov.IsDashboardVisible())
            {
                if (_wasOpen)
                {
                    _wasOpen = false;
                    _hover = _pressed = PanelAction.None;
                }
                return PanelAction.None;
            }
            if (!_wasOpen)
            {
                _wasOpen = true;
                _dirty = true;
            }
            var clicked = PanelAction.None;
            var ev = new VREvent_t();
            uint size = (uint)Marshal.SizeOf<VREvent_t>();
            while (ov.PollNextOverlayEvent(_main, ref ev, size))
            {
                switch ((EVREventType)ev.eventType)
                {
                    case EVREventType.VREvent_MouseMove:
                        _mx = ev.data.mouse.x;
                        _my = H - ev.data.mouse.y;
                        SetHover(HitTest());
                        break;
                    case EVREventType.VREvent_MouseButtonDown:
                        if (ev.data.mouse.button != (uint)EVRMouseButton.Left) break;
                        _pressed = HitTest();
                        _dirty = true;
                        break;
                    case EVREventType.VREvent_MouseButtonUp:
                        if (ev.data.mouse.button != (uint)EVRMouseButton.Left) break;
                        var up = HitTest();
                        if (up != PanelAction.None && up == _pressed) clicked = up;
                        _pressed = PanelAction.None;
                        _dirty = true;
                        break;
                    case EVREventType.VREvent_FocusLeave:
                        _hover = _pressed = PanelAction.None;
                        _dirty = true;
                        break;
                }
            }
            return clicked;
        }

        void SetHover(PanelAction a)
        {
            if (a == _hover) return;
            _hover = a;
            _dirty = true;
        }

        PanelAction HitTest()
        {
            if (_shown == null) return PanelAction.None;
            foreach (var b in Buttons(_shown))
                if (b.Kind != Kind.Disabled && b.Rect.Contains(_mx, _my)) return b.Action;
            return PanelAction.None;
        }

        // Redraws only while our tab is on screen and something changed (at most ~25 times a second).
        public void Render(CVROverlay ov, PanelModel m)
        {
            if (_main == 0 || !_wasOpen || !ov.IsOverlayVisible(_main)) return;
            if (!_dirty && m == _shown) return;
            long now = Environment.TickCount64;
            if (now - _lastDraw < 40) return;  // stays dirty: drawn on a later call
            _lastDraw = now;
            _dirty = false;
            _shown = m;
            using (var g = Graphics.FromImage(_bmp)) Draw(g, m, _hover, _pressed);
            string err = Upload(ov, _main, _bmp);
            if (err.Length > 0) Log.Warn("dashboard panel: " + err);
        }

        // SetOverlayRaw takes RGBA bytes; if SteamVR ever refuses that (its docs only say there is an
        // upper bound), fall back to a PNG file once and for all.
        string Upload(CVROverlay ov, ulong handle, Bitmap bmp)
        {
            if (handle == _main && _gpu != null)
            {
                var px = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                EVROverlayError e;
                try { e = _gpu.Upload(ov, handle, px.Scan0, px.Stride); }
                finally { bmp.UnlockBits(px); }
                if (e == EVROverlayError.None) return "";
                Log.Warn($"SetOverlayTexture refused ({e}), raw pixels from now on");
                _gpu.Dispose();
                _gpu = null;
            }
            if (!_rawFailed)
            {
                int w = bmp.Width, h = bmp.Height;
                if (_rgba.Length < w * h * 4) _rgba = new byte[w * h * 4];
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, _rgba, y * w * 4, w * 4);
                }
                finally { bmp.UnlockBits(data); }
                for (int i = 0; i < w * h * 4; i += 4)  // BGRA -> RGBA
                    (_rgba[i], _rgba[i + 2]) = (_rgba[i + 2], _rgba[i]);
                var pin = GCHandle.Alloc(_rgba, GCHandleType.Pinned);
                try
                {
                    var e = ov.SetOverlayRaw(handle, pin.AddrOfPinnedObject(), (uint)w, (uint)h, 4);
                    if (e == EVROverlayError.None) return "";
                    _rawFailed = true;
                    Log.Warn($"SetOverlayRaw refused ({e}), using PNG files from now on");
                }
                finally { pin.Free(); }
            }
            string file = Path.Combine(Path.GetTempPath(), "fbtcal", $"overlay_{handle}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            bmp.Save(file, ImageFormat.Png);
            var e2 = ov.SetOverlayFromFile(handle, file);
            return e2 == EVROverlayError.None ? "" : "SetOverlayFromFile: " + e2;
        }
    }
}
