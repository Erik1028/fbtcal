using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace FbtCal
{
    // The dashboard tab's picture, direction "Instrument" (picked from three mockups; their numbers
    // are the ones here). Near-black, one accent - the tracker
    // purple from the icon - which only means a tracker, "on / selected" or the laser's hover; amber
    // only means a problem. No cards: a three-column grid (status 32-312, figure 344-640, pose
    // 672-992), one header rule and one bottom row of buttons. Bahnschrift throughout, text placed by
    // its baseline. One pass both lays out and paints, so the laser hits exactly what is drawn.
    public sealed partial class DashboardPanel
    {
        // ------------------------------------------------------------ palette and type

        static readonly Color Bg = C(0x0E0E10), Tx = C(0xEDEDEF), Tx2 = C(0xA2A2A9), Tx3 = C(0x8A8A92),
                              Rule = C(0x2A2A30), BtnLine = C(0x5A5A63), BtnBg = C(0x16161A), DisLine = C(0x2C2C32),
                              DisTx = C(0x55555D), Acc = C(0xA743E8), SelBg = C(0x4A1F70), Amber = C(0xE8A33A),
                              Bone = C(0x5E5E67), GhostLine = C(0x44444C), GhostRing = C(0x7A7A83), Floor = C(0x3A3A41),
                              Plumb = C(0x55555F), Head = C(0xE4E4E8), Field = C(0x1E1E23), Off = C(0x6A6A72),
                              PrimarySub = C(0x45454D), SelSub = C(0xE2C8F5);

        static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

        enum Wt { Light, Regular, Semibold, Mono }

        static readonly Dictionary<(float, Wt), Font> Fonts = new Dictionary<(float, Wt), Font>();
        static readonly Dictionary<Wt, float> Ascent = new Dictionary<Wt, float>();
        static readonly StringFormat Typo = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };

        static string Family(Wt w) => w switch
        {
            Wt.Light => "Bahnschrift Light",
            Wt.Semibold => "Bahnschrift SemiBold",
            Wt.Mono => "Consolas",
            _ => "Bahnschrift",
        };

        static Font Font(float px, Wt w)
        {
            lock (Fonts)
            {
                if (Fonts.TryGetValue((px, w), out var f)) return f;
                try { f = new Font(Family(w), px, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch (ArgumentException) { f = new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel); }  // no Bahnschrift
                if (!Ascent.ContainsKey(w))
                    Ascent[w] = (float)f.FontFamily.GetCellAscent(FontStyle.Regular) / f.FontFamily.GetEmHeight(FontStyle.Regular);
                return Fonts[(px, w)] = f;
            }
        }

        // ------------------------------------------------------------ buttons

        enum Kind { Secondary, Primary, Selected, Unselected, Disabled, CheckOn, CheckOff, Minus, Plus }
        enum Chev { None, Left, Right }

        record struct Btn(RectangleF Rect, PanelAction Action, string Title, string Sub, Kind Kind, Chev Chev = Chev.None, bool Center = false);

        static readonly Bitmap MeasureBmp = new Bitmap(1, 1);

        // Where the buttons are (the laser's hit test): the same layout pass, without painting.
        static List<Btn> Buttons(PanelModel m)
        {
            lock (MeasureBmp)
            {
                using var g = Graphics.FromImage(MeasureBmp);
                var cv = new Canvas(g, paint: false);
                Compose(cv, m);
                return cv.Buttons;
            }
        }

        public static Bitmap RenderBitmap(PanelModel m, PanelAction hover = PanelAction.None)
        {
            var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) Draw(g, m, hover, PanelAction.None);
            return bmp;
        }

        static void Draw(Graphics g, PanelModel m, PanelAction hover, PanelAction pressed)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;  // no ClearType fringes in the headset
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Bg);
            var cv = new Canvas(g, paint: true);
            Compose(cv, m);
            foreach (var b in cv.Buttons) cv.Button(b, hover == b.Action && b.Kind != Kind.Disabled, pressed == b.Action);
        }

        // ------------------------------------------------------------ the screens

        enum Screen { Main, Problems, Settings, Countdown, Holding, Test, Calibrating, Pending, Waiting }
        enum Prob { Driver, Trackers, IkLog, NoPose, NoSlimeVr }

        const float L0 = 32, L1 = 312, C0 = 344, C1 = 640, R0 = 672, R1 = 992, BottomY = 548, BottomH = 72;

        static Screen Which(PanelModel m)
        {
            if (m.PendingProblems.Length > 0) return Screen.Pending;
            if (m.CountdownS >= 0) return Screen.Countdown;
            if (m.State == WorkerState.Capturing) return Screen.Holding;
            if (m.State == WorkerState.Calibrating) return Screen.Calibrating;
            if (m.SettingsPage && m.PoseTitle.Length > 0 && (m.State == WorkerState.Watching || m.TestLeftS >= 0)) return Screen.Settings;
            if (m.TestLeftS >= 0) return Screen.Test;
            if (m.State == WorkerState.WaitingForSteamVR) return Screen.Waiting;
            return ProblemsOf(m).Count > 0 ? Screen.Problems : Screen.Main;
        }

        // What keeps fbtcal from helping, worst first. At most two fit: a missing tracker with the
        // VRChat log; the others alone (the status column still marks the rest).
        static List<Prob> ProblemsOf(PanelModel m)
        {
            var p = new List<Prob>();
            if (!m.DriverLoaded) { p.Add(Prob.Driver); return p; }
            if (m.PoseTitle.Length == 0) p.Add(m.Source == PoseSource.SlimeVr ? Prob.NoSlimeVr : Prob.NoPose);
            else if (m.MissingTrackers.Length > 0) p.Add(Prob.Trackers);
            if (!m.IkDebugSeen && (p.Count == 0 || p[0] == Prob.Trackers)) p.Add(Prob.IkLog);
            return p;
        }

        static void Compose(Canvas cv, PanelModel m)
        {
            switch (Which(m))
            {
                case Screen.Main: MainScreen(cv, m); break;
                case Screen.Problems: ProblemScreen(cv, m); break;
                case Screen.Waiting: MainScreen(cv, m); break;
                case Screen.Settings: SettingsScreen(cv, m); break;
                case Screen.Test: TestScreen(cv, m); break;
                case Screen.Calibrating: CalibratingScreen(cv, m); break;
                case Screen.Countdown: CountdownScreen(cv, m); break;
                case Screen.Holding: HoldingScreen(cv, m); break;
                case Screen.Pending: PendingScreen(cv, m); break;
            }
        }

        // ------------------------------------------------------------ header

        enum Lamp { Ok, Active, Problem, Wait }

        static void Header(Canvas cv, PanelModel m, string state, Lamp lamp, string page = null)
        {
            if (page != null)
            {
                cv.Buttons.Add(new Btn(new RectangleF(32, 8, 132, 56), PanelAction.CloseSettings, "Back", null, Kind.Secondary, Chev.Left));
                cv.Text(page, 188, 47, 30, Tx, Wt.Semibold);
            }
            else
            {
                cv.Image(AppIcon, 26, 10, 52, 52);
                cv.Text("fbtcal", 88, 47, 31, Tx, Wt.Semibold);
            }
            float wx = 992 - cv.Measure(state, 27, Wt.Regular);
            cv.Text(state, 992, 47, 27, lamp == Lamp.Problem ? Amber : Tx, Wt.Regular, Align.Right);
            const float ly = 37.5f;
            switch (lamp)
            {
                case Lamp.Ok: cv.Dot(wx - 20, ly, 7, Acc); break;
                case Lamp.Active: cv.Dot(wx - 24, ly, 6, Acc); cv.Ring(wx - 24, ly, 11.5f, Acc, 2); break;
                case Lamp.Problem: cv.Dot(wx - 20, ly, 7, Amber); break;
                case Lamp.Wait: cv.Ring(wx - 20, ly, 6, Tx3, 2); break;
            }
            if (m.Toast.Length > 0)  // a short confirmation, left of the state
                cv.Text(m.Toast, wx - 48, 47, 22, Tx2, Wt.Regular, Align.Right);
            cv.Line(32, 73, 992, 73, Rule, 2, round: false);
        }

        static readonly Bitmap AppIcon = Res.Image("thumb.png");

        // ------------------------------------------------------------ main screen

        static void MainScreen(Canvas cv, PanelModel m)
        {
            bool waiting = m.State == WorkerState.WaitingForSteamVR;
            Header(cv, m, waiting ? "Waiting for SteamVR" : m.StateText, waiting ? Lamp.Wait : Lamp.Ok);
            StatusColumn(cv, m);
            AutoStart(cv, m);
            MainFigure(cv, m, waiting ? Dots.Off : Dots.Live);
            PoseColumn(cv, m);
            Readouts(cv, m);
            BottomRow(cv, m, waiting ? Kind.Disabled : Kind.Secondary, waiting ? Kind.Disabled : Kind.Secondary, Kind.Secondary,
                      waiting ? "needs SteamVR" : null);
        }

        static void StatusColumn(Canvas cv, PanelModel m)
        {
            bool waiting = m.State == WorkerState.WaitingForSteamVR;
            var items = new List<(string label, string value, string extra, int kind)>  // kind 0 ok, 1 problem, 2 none
            {
                waiting ? ("SteamVR driver", "–", null, 2) : m.DriverLoaded ? ("SteamVR driver", "loaded", null, 0) : ("SteamVR driver", "NOT loaded", null, 1),
                waiting || !m.DriverLoaded || m.TrackersInProfile == 0 ? ("Trackers", "–", null, 2)
                    : ("Trackers", $"{m.TrackersFound} / {m.TrackersInProfile}", null, m.MissingTrackers.Length > 0 ? 1 : 0),
                m.IkDebugSeen ? ("VRChat log", "watching", null, 0) : ("VRChat log", "no IK debug log", null, 1),
            };
            int dot = m.LastCalibration.IndexOf(" · ", StringComparison.Ordinal);
            items.Add(dot > 0 ? ("Last calibration", m.LastCalibration.Substring(0, dot), "· " + m.LastCalibration.Substring(dot + 3), 0)
                              : ("Last calibration", "none yet", null, 2));
            float y = 118;
            foreach (var (label, value, extra, kind) in items)
            {
                cv.Text(label, L0, y, 21, Tx3);
                Color col = kind == 1 ? Amber : kind == 2 ? Tx2 : Tx;
                if (kind == 1) cv.Fill(L0 - 16, y - 18, 4, 56, Amber);
                float x = cv.Text(value, L0, y + 34, 28, col);
                if (extra != null) cv.Text(extra, x + 9, y + 34, 28, Tx2);
                y += 84;
            }
        }

        static void AutoStart(Canvas cv, PanelModel m) =>
            cv.Buttons.Add(new Btn(new RectangleF(L0, 452, L1 - L0, 64), PanelAction.ToggleAutoStart, "Start with SteamVR", null,
                                   m.AutoStart ? Kind.CheckOn : Kind.CheckOff));

        static void PoseColumn(Canvas cv, PanelModel m)
        {
            cv.Text("Pose VRChat will see", R0, 116, 22, Tx2);
            ChoicePair(cv, m, 134);
            if (m.PoseTitle.Length == 0) return;
            if (m.Source == PoseSource.SlimeVr)
            {
                cv.Text(m.PoseDetail.Split('\n')[0], R0, 240, 27, Tx);
                cv.Text("proportions · " + (m.PoseDetail.Split('\n').ElementAtOrDefault(1) ?? ""), R0, 272, 22, Tx2);
            }
            else
            {
                cv.Text(m.PoseTitle, R0, 240, 27, Tx);
                cv.Text(m.PoseDetail.Replace("\n", " "), R0, 272, 22, Tx2);
            }
        }

        static void ChoicePair(Canvas cv, PanelModel m, float y)
        {
            float bw = (R1 - R0 - 12) / 2;
            bool slime = m.Source == PoseSource.SlimeVr;
            cv.Buttons.Add(new Btn(new RectangleF(R0, y, bw, 64), PanelAction.SourceCapture, "Captured", null, slime ? Kind.Unselected : Kind.Selected, Center: true));
            cv.Buttons.Add(new Btn(new RectangleF(R0 + bw + 12, y, bw, 64), PanelAction.SourceSlimeVr, "SlimeVR", null, slime ? Kind.Selected : Kind.Unselected, Center: true));
        }

        static void Readouts(Canvas cv, PanelModel m)
        {
            var rows = new[]
            {
                ("Stance", double.IsNaN(m.StanceCm) ? "–" : $"{m.StanceCm:0}", "cm"),
                ("Toe-out", double.IsNaN(m.ToeOutDeg) ? "–" : $"{m.ToeOutDeg:0}", "°"),
                ("Feet forward", m.ForwardCm == 0 ? "0" : $"{m.ForwardCm:+0;-0}", "cm"),
            };
            float y = 352, nx = R1 - 38;
            foreach (var (label, num, unit) in rows)
            {
                cv.Text(label, R0, y, 22, Tx2);
                cv.Text(num, nx, y, 28, Tx, Wt.Regular, Align.Right);
                if (unit == "°") cv.Text("°", nx + 1, y, 28, Tx);
                else cv.Text(unit, nx + 7, y, 22, Tx2);
                y += 46;
            }
        }

        static string CaptureSub(PanelModel m) =>
            m.Source == PoseSource.Capture && m.PoseTitle.Length > 0 ? $"replaces the {m.PoseTitle.Split(',')[0]} pose" : $"starts in {VrWorker.CapturePanelDelayS} s";

        static void BottomRow(Canvas cv, PanelModel m, Kind capture, Kind test, Kind settings, string captureSub = null)
        {
            cv.Buttons.Add(new Btn(new RectangleF(L0, BottomY, L1 - L0, BottomH), PanelAction.Capture, "Capture standing pose", captureSub ?? CaptureSub(m), capture));
            cv.Buttons.Add(new Btn(new RectangleF(C0, BottomY, C1 - C0, BottomH), PanelAction.Test, "Test", "ideal pose for 20 s", test));
            cv.Buttons.Add(new Btn(new RectangleF(R0, BottomY, R1 - R0, BottomH), PanelAction.OpenSettings, "Pose settings", null, settings, Chev.Right));
        }

        // ------------------------------------------------------------ problems

        static void ProblemScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Not ready", Lamp.Problem);
            StatusColumn(cv, m);
            AutoStart(cv, m);
            var probs = ProblemsOf(m);
            float y = 98;
            foreach (var p in probs)
            {
                y = p switch
                {
                    Prob.Driver => Block(cv, C0, y, "SteamVR driver NOT loaded", (x, by) =>
                    {
                        float yy = cv.Para("Without it, fbtcal can’t move your trackers.", x, by, 25, 620, Tx);
                        return Steps(cv, x, yy + 52, new[] { "SteamVR Settings › Startup / Shutdown", "Manage Add-ons › fbtcal: On", "Restart SteamVR" }, 25, 620, 46) + 14;
                    }),
                    Prob.Trackers => TrackersBlock(cv, m, y),
                    Prob.IkLog => Block(cv, C0, y, "No IK debug log", (x, by) => IkBody(cv, x, by, lead: probs.Count == 1)),
                    Prob.NoPose => Block(cv, C0, y, "No standing pose yet", (x, by) =>
                    {
                        float yy = cv.Para("Stand up once, do a SlimeVR full reset, then press Capture standing pose.", x, by, 25, 620, Tx);
                        yy = cv.Para("Or let VRChat see a pose made from your SlimeVR proportions:", x, yy + 50, 25, 620, Tx2);
                        cv.Buttons.Add(new Btn(new RectangleF(x, yy + 24, 300, 64), PanelAction.SourceSlimeVr, "Use SlimeVR instead", null, Kind.Secondary));
                        return yy + 24 + 64;
                    }),
                    _ => Block(cv, C0, y, "No SlimeVR proportions", (x, by) =>
                    {
                        float yy = cv.Para(m.PoseProblem, x, by, 25, 620, Tx);
                        yy = cv.Para("Or stand up once and capture your own standing pose:", x, yy + 50, 25, 620, Tx2);
                        cv.Buttons.Add(new Btn(new RectangleF(x, yy + 24, 300, 64), PanelAction.SourceCapture, "Use a captured pose", null, Kind.Secondary));
                        return yy + 24 + 64;
                    }),
                } + 44;
            }
            if (probs[0] == Prob.Driver)
                BottomRow(cv, m, Kind.Disabled, Kind.Disabled, Kind.Secondary, "needs the driver");
            else if (probs[0] == Prob.NoPose || probs[0] == Prob.NoSlimeVr)
                BottomRow(cv, m, Kind.Primary, Kind.Disabled, Kind.Disabled, $"starts in {VrWorker.CapturePanelDelayS} s");
            else
                BottomRow(cv, m, Kind.Secondary, Kind.Secondary, Kind.Secondary);
        }

        // An amber rule on the left, the title, then what body draws; returns the bottom.
        static float Block(Canvas cv, float x, float y, string title, Func<float, float, float> body)
        {
            cv.Text(title, x + 24, y + 28, 30, Amber, Wt.Semibold);
            float bottom = body(x + 24, y + 68);
            cv.Fill(x, y, 4, bottom - y, Amber);
            return bottom;
        }

        // The fix as numbered steps; step 2 is the launch option in its own field, with Copy.
        static float IkBody(Canvas cv, float x, float y, bool lead)
        {
            if (lead) y = cv.Para("fbtcal can’t see when VRChat starts calibrating.", x, y, 25, 620, Tx) + 52;
            cv.Text("1", x, y, 25, Tx3);
            cv.Text("Steam › VRChat › Properties › Launch options", x + 36, y, 25, Tx);
            float top = y + 22;
            cv.Text("2", x, top + 32 + 0.355f * 25, 25, Tx3);
            const string code = "--enable-ik-debug-logging";
            float w = cv.Measure(code, 23, Wt.Mono) + 40;
            cv.Fill(x + 36, top, w, 64, Field, 6);
            cv.Text(code, x + 56, top + 32 + 0.32f * 23, 23, Tx, Wt.Mono);
            cv.Buttons.Add(new Btn(new RectangleF(x + 36 + w + 12, top, 120, 64), PanelAction.CopyIkFlag, "Copy", null, Kind.Secondary, Center: true));
            float y3 = top + 64 + 44;
            cv.Text("3", x, y3, 25, Tx3);
            cv.Text("Restart VRChat", x + 36, y3, 25, Tx);
            return y3 + 14;
        }

        static float TrackersBlock(Canvas cv, PanelModel m, float y)
        {
            var missing = m.MissingTrackers.Split(", ", StringSplitOptions.RemoveEmptyEntries);
            bool all = m.TrackersFound == 0;
            string title = $"Trackers {m.TrackersFound} / {m.TrackersInProfile}";
            float bottom = Block(cv, C0, y, title, (x, by) =>
            {
                string text = all ? "SteamVR shows none of your trackers. Is the SlimeVR server running?"
                    : missing.Length == 1 ? $"The {missing[0]} tracker isn’t in SteamVR. Turn it on and wait until SlimeVR shows it."
                    : $"These trackers aren’t in SteamVR: {string.Join(", ", missing)}. Turn them on and wait until SlimeVR shows them.";
                return cv.Para(text, x, by, 25, 440, Tx) + 14;
            });
            // the missing ones on a small front view
            const float fs = 0.9f, hy0 = 10;
            Figure(cv, m.Pose, fs, y + hy0, fx: 916, sx: null, Dots.Live, floor: null, k: 0.7f, captions: false,
                   missing: m.MissingTrackers.Length > 0 ? m.MissingTrackers : null);
            return Math.Max(bottom, y + hy0 + 157 * fs + 10);
        }

        static float Steps(Canvas cv, float x, float y, string[] items, float size, float width, float gap)
        {
            for (int i = 0; i < items.Length; i++)
            {
                cv.Text((i + 1).ToString(), x, y, size, Tx3);
                y = cv.Para(items[i], x + 36, y, size, width - 36, Tx) + gap;
            }
            return y - gap;
        }

        // ------------------------------------------------------------ settings

        static readonly (float top, string title, string hint, PanelAction minus, PanelAction plus, PanelAction reset)[] Rows =
        {
            (96, "Stance", "distance between the feet", PanelAction.StanceMinus, PanelAction.StancePlus, PanelAction.StanceReset),
            (240, "Toe-out", "out +, in −", PanelAction.ToeOutMinus, PanelAction.ToeOutPlus, PanelAction.ToeOutReset),
            (384, "Feet forward/back", "forward +, back −", PanelAction.ForwardMinus, PanelAction.ForwardPlus, PanelAction.ForwardReset),
        };

        static void SettingsScreen(Canvas cv, PanelModel m)
        {
            bool testing = m.TestLeftS >= 0;
            Header(cv, m, testing ? "Test" : m.StateText, testing ? Lamp.Active : Lamp.Ok, page: "Pose settings");
            const float s = 2.55f, hy = 122;
            float feetY = hy + 157 * s, capY = feetY + 34;
            Figure(cv, m.Pose, s, hy, fx: 104, sx: null, testing ? Dots.Held : Dots.Live, floor: (40, 168), captionY: capY, sideCaption: false);
            FromAbove(cv, 316, feetY - 46, 4.6f, (m.StanceCm, m.ToeOutDeg, m.ForwardCm), (m.BaseStanceCm, m.BaseToeOutDeg, 0), arrowTop: 128);
            cv.Text("Feet from above", 316, capY, 20, Tx3, Wt.Regular, Align.Center);
            // legend
            const float ly = 612;
            cv.GhostDot(48, ly - 7, 7);
            float lx = cv.Text(m.Source == PoseSource.SlimeVr ? "from SlimeVR" : "as captured", 64, ly, 20, Tx3);
            cv.Tracker(lx + 30, ly - 7, Dots.Live, 7);
            cv.Text("with your settings", lx + 46, ly, 20, Tx3);

            const float x0 = 464;
            bool[] custom = { m.StanceCustom, m.ToeOutCustom, m.ForwardCm != 0 };
            string[] nums =
            {
                double.IsNaN(m.StanceCm) ? "–" : $"{m.StanceCm:0}",
                double.IsNaN(m.ToeOutDeg) ? "–" : $"{m.ToeOutDeg:0}",
                m.ForwardCm == 0 ? "0" : $"{m.ForwardCm:+0;-0}",
            };
            string[] units = { "cm", "°", "cm" };
            string[] resets =
            {
                $"Reset to {m.BaseStanceCm:0} cm", $"Reset to {m.BaseToeOutDeg:0}°", "Reset to 0 cm",
            };
            string own = m.Source == PoseSource.SlimeVr ? "as in SlimeVR" : "as captured";
            for (int i = 0; i < Rows.Length; i++)
            {
                var r = Rows[i];
                float tx = cv.Text(r.title, x0, r.top + 26, 26, Tx);
                cv.Text(r.hint, tx + 14, r.top + 26, 20, Tx2);
                float cy = r.top + 44;
                cv.Buttons.Add(new Btn(new RectangleF(x0, cy, 64, 64), r.minus, "", null, Kind.Minus));
                cv.Buttons.Add(new Btn(new RectangleF(x0 + 228, cy, 64, 64), r.plus, "", null, Kind.Plus));
                float vc = x0 + 146;
                if (units[i] == "°")
                    cv.Text(nums[i] + "°", vc, cy + 48, 46, Tx, Wt.Light, Align.Center);
                else
                {
                    float vw = cv.Measure(nums[i], 46, Wt.Light) + 6 + cv.Measure(units[i], 22, Wt.Regular);
                    float nx = cv.Text(nums[i], vc - vw / 2, cy + 48, 46, Tx, Wt.Light);
                    cv.Text(units[i], nx + 6, cy + 48, 22, Tx2);
                }
                if (custom[i]) cv.Buttons.Add(new Btn(new RectangleF(780, cy, 212, 64), r.reset, resets[i], null, Kind.Secondary));
                else cv.Text(own, 780, cy + 32 + 0.355f * 22, 22, Tx3);
            }
            cv.Buttons.Add(testing
                ? new Btn(new RectangleF(x0, BottomY, 992 - x0, BottomH), PanelAction.Cancel, "Stop", $"{m.TestLeftS} s left – back to your real movement", Kind.Secondary)
                : new Btn(new RectangleF(x0, BottomY, 992 - x0, BottomH), PanelAction.Test, "Test", "ideal pose for 20 s – you can adjust meanwhile", Kind.Secondary));
        }

        // The feet seen from above, forward = up, oy = the floor point under the headset: what the
        // settings do, drawn from their values (and the pose's own ones as outlines).
        static void FromAbove(Canvas cv, float cx, float oy, float s, (double stance, double toe, double fwd) cur,
                              (double stance, double toe, double fwd) ghost, float arrowTop)
        {
            const float footW = 8;
            IEnumerable<(PointF heel, PointF toe, PointF ankle)> Feet((double stance, double toe, double fwd) p)
            {
                double f = (double.IsNaN(p.stance) ? 28 : p.stance) / 2, th = (double.IsNaN(p.toe) ? 0 : p.toe) * Math.PI / 180;
                double ay = -(1 + p.fwd);
                foreach (int side in new[] { -1, 1 })
                {
                    double ax = side * f, dx = side * Math.Sin(th), dy = -Math.Cos(th);
                    yield return (new PointF((float)(cx + (ax - 3 * dx) * s), (float)(oy + (ay - 3 * dy) * s)),
                                  new PointF((float)(cx + (ax + 15 * dx) * s), (float)(oy + (ay + 15 * dy) * s)),
                                  new PointF((float)(cx + ax * s), (float)(oy + ay * s)));
                }
            }
            cv.Line(cx, oy + 6, cx, arrowTop + 4, Plumb, 2, round: false, dash: true);
            cv.Poly(new[] { new PointF(cx - 9, arrowTop + 12), new PointF(cx, arrowTop + 2), new PointF(cx + 9, arrowTop + 12) }, Plumb, 2.5f);
            cv.Text("forward", cx + 18, arrowTop + 16, 20, Tx3);
            var g = Feet(ghost).ToList();
            var c = Feet(cur).ToList();
            foreach (var (heel, toe, _) in g)
            {
                cv.Line(heel.X, heel.Y, toe.X, toe.Y, GhostRing, footW * s);
                cv.Line(heel.X, heel.Y, toe.X, toe.Y, Bg, footW * s - 5);
            }
            foreach (var (heel, toe, _) in c) cv.Line(heel.X, heel.Y, toe.X, toe.Y, Bone, footW * s);
            for (int i = 0; i < c.Count; i++)
                if (Math.Abs(c[i].ankle.X - g[i].ankle.X) > 0.5 || Math.Abs(c[i].ankle.Y - g[i].ankle.Y) > 0.5)
                    cv.GhostDot(g[i].ankle.X, g[i].ankle.Y, 8);
            foreach (var (_, _, ankle) in c) cv.Tracker(ankle.X, ankle.Y, Dots.Live, 10);
        }

        // ------------------------------------------------------------ while the trackers are held

        static void ActiveRight(Canvas cv, PanelModel m)
        {
            cv.Text("Pose VRChat sees now", R0, 116, 22, Tx2);
            Figure(cv, m.Pose, 2.2f, 164, fx: 748, sx: 884, Dots.Held, floor: (R0, R1));
        }

        static void CalibratingScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Calibrating", Lamp.Active);
            cv.Text("VRChat is calibrating", 32, 116, 22, Tx2);
            cv.Text("Pull both", 28, 224, 92, Tx, Wt.Semibold);
            cv.Text("triggers", 28, 322, 92, Tx, Wt.Semibold);
            cv.Para("Your trackers hold the ideal pose until you pull the triggers, then follow you again.", 32, 398, 26, 560, Tx);
            ActiveRight(cv, m);
            cv.Buttons.Add(new Btn(new RectangleF(32, BottomY, 400, BottomH), PanelAction.Cancel, "Turn the ideal pose off", "trackers follow you again now", Kind.Secondary));
        }

        static void TestScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Test", Lamp.Active);
            cv.Text("Testing the ideal pose", 32, 116, 22, Tx2);
            float x = cv.Text(m.TestLeftS.ToString(), 24, 304, 196, Tx, Wt.Light);
            cv.Text("s left", x + 14, 304, 34, Tx2);
            cv.Para("Look down at your trackers: are they under your head, as if you stood?", 32, 384, 26, 560, Tx);
            ActiveRight(cv, m);
            cv.Buttons.Add(new Btn(new RectangleF(32, BottomY, 296, BottomH), PanelAction.Cancel, "Stop", "back to your real movement", Kind.Secondary));
            cv.Buttons.Add(new Btn(new RectangleF(344, BottomY, 296, BottomH), PanelAction.OpenSettings, "Pose settings", null, Kind.Secondary, Chev.Right));
        }

        static string KeepsSub(PanelModel m) =>
            m.Source == PoseSource.Capture && m.PoseTitle.Length > 0 ? $"keeps the {m.PoseTitle.Split(',')[0]} pose" : "nothing is saved";

        static void CountdownScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Capturing", Lamp.Active);
            cv.Text("Capture starts in", 32, 116, 22, Tx2);
            float x = cv.Text(m.CountdownS.ToString(), 20, 334, 236, Tx, Wt.Light);
            cv.Text("s", x + 10, 334, 40, Tx2);
            Steps(cv, 344, 190, new[] { "Do a SlimeVR full reset", "Stand up straight, legs straight", "Look ahead and hold still\nfor 3 s after the long beep" }, 28, 600, 60);
            cv.Buttons.Add(new Btn(new RectangleF(32, BottomY, 400, BottomH), PanelAction.Cancel, "Cancel", KeepsSub(m), Kind.Secondary));
        }

        static void HoldingScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Capturing", Lamp.Active);
            cv.Text("Capturing your standing pose", 32, 116, 22, Tx2);
            cv.Text("Hold still", 28, 224, 92, Tx, Wt.Semibold);
            cv.Para("Three seconds, until the double beep.", 32, 300, 26, 560, Tx);
        }

        static void PendingScreen(Canvas cv, PanelModel m)
        {
            Header(cv, m, "Not saved", Lamp.Problem);
            StatusColumn(cv, m);
            AutoStart(cv, m);
            Block(cv, C0, 98, "Problem with the capture", (x, y) =>
            {
                foreach (string line in m.PendingProblems.Split('\n'))
                    y = cv.Para(line, x, y, 25, 620, Tx) + 44;
                return y - 44 + 14;
            });
            cv.Buttons.Add(new Btn(new RectangleF(L0, BottomY, L1 - L0, BottomH), PanelAction.SaveAnyway, "Save anyway", "despite the warning", Kind.Secondary));
            cv.Buttons.Add(new Btn(new RectangleF(C0, BottomY, C1 - C0, BottomH), PanelAction.Discard, "Discard", "keeps the old pose", Kind.Primary));
        }

        // ------------------------------------------------------------ the figure

        enum Dots { Live, Held, Off }

        static void MainFigure(Canvas cv, PanelModel m, Dots dots) =>
            Figure(cv, m.Pose, 2.4f, 124, fx: 428, sx: 562, dots, floor: (C0 + 16, C1 - 16));

        // The pose as the trackers really are: front view (as in a mirror) and side view (forward =
        // right, a headset on the head, a dashed plumb line under it). s = px per cm, hy = the head.
        static void Figure(Canvas cv, PosePoint[] pose, float s, float hy, float? fx, float? sx, Dots dots,
                           (float a, float b)? floor, float k = 1, bool captions = true, float? captionY = null,
                           string missing = null, bool sideCaption = true)
        {
            if (pose == null || pose.Length == 0) return;
            var at = new Dictionary<string, PosePoint>();
            foreach (var p in pose) at[p.Part] = p;
            bool Has(string n) => at.ContainsKey(n);
            // a taller body is drawn smaller: the feet always stand where the layout puts them (157 cm down)
            float drop = pose.Max(p => -p.Y * 100);
            if (drop > 157) s *= 157 / drop;
            float hr = 15 * k, dr = 10 * k, bw = 5 * k;
            float feetY = hy + Math.Max(157, drop) * s;
            if (floor is (float a, float b)) cv.Line(a, feetY + 0.5f, b, feetY + 0.5f, Floor, 2, round: false);

            PointF F(string n) => new PointF(fx.Value + at[n].X * 100 * s, hy - at[n].Y * 100 * s);
            PointF Avg(string a, string b2)
            {
                var pts = new[] { a, b2 }.Where(Has).Select(n => at[n]).ToList();
                return new PointF(sx.Value - pts.Average(p => p.Z) * 100 * s, hy - pts.Average(p => p.Y) * 100 * s);
            }
            PointF S(string n) => new PointF(sx.Value - at[n].Z * 100 * s, hy - at[n].Y * 100 * s);

            if (fx != null)
            {
                foreach (var chain in new[] { new[] { "head", "CHEST", "WAIST" }, new[] { "WAIST", "LEFT_KNEE", "LEFT_FOOT" }, new[] { "WAIST", "RIGHT_KNEE", "RIGHT_FOOT" } })
                    cv.Poly(chain.Where(Has).Select(F).ToArray(), Bone, bw);
                cv.Dot(fx.Value, hy, hr, Head);
                foreach (string n in new[] { "CHEST", "WAIST", "LEFT_KNEE", "RIGHT_KNEE", "LEFT_FOOT", "RIGHT_FOOT" })
                {
                    if (!Has(n)) continue;
                    var p = F(n);
                    if (missing != null && missing.Contains(TrackerName(n)))
                    {
                        cv.Dot(p.X, p.Y, dr, Bg);
                        cv.Ring(p.X, p.Y, dr, Amber, 3);
                    }
                    else cv.Tracker(p.X, p.Y, dots, dr);
                }
            }
            if (sx != null)
            {
                cv.Line(sx.Value, hy + hr + 6, sx.Value, feetY - dr - 4, Plumb, 2, round: false, dash: true);
                var chain = new List<PointF> { new PointF(sx.Value, hy) };
                if (Has("CHEST")) chain.Add(S("CHEST"));
                if (Has("WAIST")) chain.Add(S("WAIST"));
                if (Has("LEFT_KNEE") || Has("RIGHT_KNEE")) chain.Add(Avg("LEFT_KNEE", "RIGHT_KNEE"));
                if (Has("LEFT_FOOT") || Has("RIGHT_FOOT")) chain.Add(Avg("LEFT_FOOT", "RIGHT_FOOT"));
                cv.Poly(chain.ToArray(), Bone, bw);
                // the head with a headset, its visor in front (forward = right)
                cv.Dot(sx.Value, hy, hr, Head);
                cv.Fill(sx.Value + 4 * k, hy - 9 * k, 29 * k, 18 * k, Head, 5 * k);
                cv.Line(sx.Value + 9 * k, hy - 9 * k, sx.Value + 9 * k, hy + 9 * k, Bg, 2.5f * k, round: false);
                foreach (var p in chain.Skip(1)) cv.Tracker(p.X, p.Y, dots, dr);
            }
            if (captions)
            {
                float cy = captionY ?? feetY + 34;
                if (fx != null) cv.Text("Front", fx.Value, cy, 20, Tx3, Wt.Regular, Align.Center);
                if (sx != null && sideCaption) cv.Text("Side", sx.Value, cy, 20, Tx3, Wt.Regular, Align.Center);
            }
        }

        // "LEFT_KNEE" -> "left knee": how the trackers are named on the panel (VrWorker uses the same).
        internal static string TrackerName(string part) => part.Replace("human://", "").Replace('_', ' ').ToLowerInvariant();

        // ------------------------------------------------------------ the drawing primitives

        enum Align { Left, Center, Right }

        sealed class Canvas
        {
            readonly Graphics _g;
            readonly bool _paint;
            public readonly List<Btn> Buttons = new List<Btn>();

            public Canvas(Graphics g, bool paint)
            {
                _g = g;
                _paint = paint;
            }

            public float Measure(string s, float size, Wt w) =>
                string.IsNullOrEmpty(s) ? 0 : _g.MeasureString(s, Font(size, w), PointF.Empty, Typo).Width;

            // Text by its baseline; returns where it ends on the right.
            public float Text(string s, float x, float baseline, float size, Color c, Wt w = Wt.Regular, Align a = Align.Left)
            {
                var f = Font(size, w);
                float width = Measure(s, size, w);
                float x0 = a == Align.Right ? x - width : a == Align.Center ? x - width / 2 : x;
                if (_paint && !string.IsNullOrEmpty(s))
                {
                    using var b = new SolidBrush(c);
                    _g.DrawString(s, f, b, x0, baseline - size * Ascent[w], Typo);
                }
                return x0 + width;
            }

            // Wrapped text (explicit \n first); returns the last baseline.
            public float Para(string s, float x, float baseline, float size, float width, Color c, Wt w = Wt.Regular)
            {
                float lead = (float)Math.Round(size * 1.3);
                var lines = new List<string>();
                foreach (string part in s.Split('\n'))
                {
                    string cur = "";
                    foreach (string word in part.Split(' '))
                    {
                        string t = (cur + " " + word).Trim();
                        if (cur.Length == 0 || Measure(t, size, w) <= width) cur = t;
                        else { lines.Add(cur); cur = word; }
                    }
                    if (cur.Length > 0) lines.Add(cur);
                }
                foreach (string ln in lines)
                {
                    Text(ln, x, baseline, size, c, w);
                    baseline += lead;
                }
                return baseline - lead;
            }

            public void Fill(float x, float y, float w, float h, Color c, float r = 0)
            {
                if (!_paint) return;
                using var b = new SolidBrush(c);
                if (r <= 0) _g.FillRectangle(b, x, y, w, h);
                else using (var p = Round(new RectangleF(x, y, w, h), r)) _g.FillPath(b, p);
            }

            // A stroke inside the box, like the mockups' CSS.
            public void Stroke(RectangleF r, float radius, Color c, float sw)
            {
                if (!_paint) return;
                var inner = RectangleF.Inflate(r, -sw / 2, -sw / 2);
                using var p = Round(inner, Math.Max(0, radius - sw / 2));
                using var pen = new Pen(c, sw);
                _g.DrawPath(pen, p);
            }

            public void Line(float x1, float y1, float x2, float y2, Color c, float sw, bool round = true, bool dash = false)
            {
                if (!_paint) return;
                using var pen = new Pen(c, sw);
                if (round) pen.StartCap = pen.EndCap = LineCap.Round;
                if (dash) pen.DashPattern = new[] { 6 / sw, 6 / sw };
                _g.DrawLine(pen, x1, y1, x2, y2);
            }

            public void Poly(PointF[] pts, Color c, float sw)
            {
                if (!_paint || pts.Length < 2) return;
                using var pen = new Pen(c, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                _g.DrawLines(pen, pts);
            }

            public void Dot(float cx, float cy, float r, Color c)
            {
                if (!_paint) return;
                using var b = new SolidBrush(c);
                _g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            }

            public void Ring(float cx, float cy, float r, Color c, float sw)
            {
                if (!_paint) return;
                using var pen = new Pen(c, sw);
                _g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            }

            // A tracker: a purple dot in a thin gap of background; held = with a ring.
            public void Tracker(float x, float y, Dots mode, float r)
            {
                Dot(x, y, r + 1.75f, Bg);
                Dot(x, y, r - 1.75f, mode == Dots.Off ? Off : Acc);
                if (mode == Dots.Held) Ring(x, y, r + 7, Acc, 2);
            }

            public void GhostDot(float x, float y, float r)
            {
                Dot(x, y, r, Bg);
                Ring(x, y, r, GhostRing, 2.5f);
            }

            public void Image(Bitmap bmp, float x, float y, float w, float h)
            {
                if (_paint) _g.DrawImage(bmp, x, y, w, h);
            }

            public void Button(Btn b, bool hover, bool pressed)
            {
                var r = b.Rect;
                Color title, sub;
                switch (b.Kind)
                {
                    case Kind.Primary:
                        Fill(r.X, r.Y, r.Width, r.Height, pressed ? C(0xC8C8CE) : Tx, 6);
                        if (hover) Stroke(r, 6, Acc, 2);
                        title = Bg; sub = PrimarySub;
                        break;
                    case Kind.Selected:
                        Fill(r.X, r.Y, r.Width, r.Height, SelBg, 6);
                        Stroke(r, 6, Acc, 2);
                        title = Color.White; sub = SelSub;
                        break;
                    case Kind.Disabled:
                        Stroke(r, 6, DisLine, 2);
                        title = DisTx; sub = DisTx;
                        break;
                    default:
                        Fill(r.X, r.Y, r.Width, r.Height, pressed ? C(0x24242A) : BtnBg, 6);
                        Stroke(r, 6, hover ? Acc : BtnLine, 2);
                        title = b.Kind == Kind.Unselected ? Tx2 : Tx; sub = Tx2;
                        break;
                }
                if (b.Kind == Kind.Minus || b.Kind == Kind.Plus)
                {
                    float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    Line(cx - 14, cy, cx + 14, cy, Tx, 3.2f);
                    if (b.Kind == Kind.Plus) Line(cx, cy - 14, cx, cy + 14, Tx, 3.2f);
                    return;
                }
                if (b.Kind == Kind.CheckOn || b.Kind == Kind.CheckOff)
                {
                    float bx = r.X + 20, by = r.Y + r.Height / 2 - 13;
                    if (b.Kind == Kind.CheckOn)
                    {
                        Fill(bx, by, 26, 26, Acc, 5);
                        Poly(new[] { new PointF(bx + 6, by + 13.5f), new PointF(bx + 11, by + 18.5f), new PointF(bx + 20, by + 8) }, Bg, 3.4f);
                    }
                    else Stroke(new RectangleF(bx, by, 26, 26), 5, Tx2, 2);
                    Text(b.Title, bx + 42, r.Y + r.Height / 2 + 0.355f * 24, 24, Tx);
                    return;
                }
                float pad = 22;
                if (b.Chev == Chev.Left)
                {
                    float cx = r.X + 26, cy = r.Y + r.Height / 2;
                    Poly(new[] { new PointF(cx + 7, cy - 9), new PointF(cx - 2, cy), new PointF(cx + 7, cy + 9) }, title, 3);
                    pad = 48;
                }
                bool two = !string.IsNullOrEmpty(b.Sub);
                float ty = two ? r.Y + r.Height / 2 - 4 : r.Y + r.Height / 2 + 0.355f * 24;
                if (b.Center) Text(b.Title, r.X + r.Width / 2, ty, 24, title, two ? Wt.Semibold : Wt.Regular, Align.Center);
                else Text(b.Title, r.X + pad, ty, 24, title, two ? Wt.Semibold : Wt.Regular);
                if (two) Text(b.Sub, r.X + pad, ty + 27, 20, sub);
                if (b.Chev == Chev.Right)
                {
                    float cx = r.X + r.Width - 28, cy = r.Y + r.Height / 2;
                    Poly(new[] { new PointF(cx - 4, cy - 9), new PointF(cx + 5, cy), new PointF(cx - 4, cy + 9) }, b.Kind == Kind.Disabled ? DisTx : Tx2, 3);
                }
            }

            static GraphicsPath Round(RectangleF r, float radius)
            {
                var path = new GraphicsPath();
                float d = radius * 2;
                if (d <= 0) { path.AddRectangle(r); return path; }
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
