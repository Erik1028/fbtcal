using System;
using System.Drawing;
using System.Windows.Forms;

namespace FbtCal
{
    // The installer's one window: what it will do, Install / Cancel, then what happens next.
    sealed class SetupWindow : Form
    {
        // the dashboard tab's palette (PanelDraw.cs): near-black, light primary button, amber = a problem
        static readonly Color Bg = Color.FromArgb(0x0E, 0x0E, 0x10), TextC = Color.FromArgb(0xED, 0xED, 0xEF),
                              MutedC = Color.FromArgb(0xA2, 0xA2, 0xA9), Line = Color.FromArgb(0x5A, 0x5A, 0x63),
                              ButtonBg = Color.FromArgb(0x16, 0x16, 0x1A), Warn = Color.FromArgb(0xE8, 0xA3, 0x3A);

        readonly Installer.Options _o;
        readonly Label _body, _note;
        readonly CheckBox _desktop;
        readonly Button _primary, _secondary;
        bool _done;

        public SetupWindow(Installer.Options o)
        {
            _o = o;
            SuspendLayout();  // laid out at 96 dpi, scaled to the screen (the designer's order)
            AutoScaleDimensions = new SizeF(96F, 96F);
            Text = "fbtcal setup";
            Icon = Res.TrayIcon("fbtcal.ico");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextC;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont(10.5f);
            ClientSize = new Size(560, 344);

            var pic = new PictureBox { Image = Res.Image("thumb.png"), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(24, 28, 96, 96) };
            var title = new Label { Text = "fbtcal " + Installer.Version, Font = UiFont(19f, semibold: true), AutoSize = true, Location = new Point(136, 30) };
            var tagline = new Label
            {
                Text = "VRChat full-body calibration without standing up. Made for SlimeVR.",
                ForeColor = MutedC, AutoSize = false, Bounds = new Rectangle(138, 72, 400, 44),
            };
            _body = new Label { AutoSize = false, Bounds = new Rectangle(24, 132, 512, 66) };
            _desktop = new FlatCheck
            {
                Text = "Desktop shortcut", Checked = o.DesktopShortcut, Visible = o.Shortcuts,
                Cursor = Cursors.Hand, Bounds = new Rectangle(22, 202, 260, 28),
            };
            _desktop.CheckedChanged += (s, e) => _o.DesktopShortcut = _desktop.Checked;
            _note = new Label { AutoSize = false, Bounds = new Rectangle(24, 234, 512, 44), ForeColor = Warn };
            _primary = MakeButton("Install", true, new Rectangle(296, 286, 240, 40));
            _secondary = MakeButton("Cancel", false, new Rectangle(176, 286, 108, 40));
            _primary.Click += (s, e) => Primary();
            _secondary.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { pic, title, tagline, _body, _desktop, _note, _primary, _secondary });
            AcceptButton = _primary;
            CancelButton = _secondary;
            ShowPlan();
            ResumeLayout(false);
            PerformLayout();
        }

        static Button MakeButton(string text, bool primary, Rectangle r)
        {
            var b = new Button
            {
                Text = text, Bounds = r, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? TextC : ButtonBg, ForeColor = primary ? Bg : TextC,
                Font = UiFont(11f, semibold: primary),
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 2;
            b.FlatAppearance.BorderColor = Line;
            return b;
        }

        // A checkbox drawn like the buttons: WinForms' flat one paints a pale box (from the dark
        // background) with a white tick on it. Ticked = light box with a dark tick, like Install.
        sealed class FlatCheck : CheckBox
        {
            public FlatCheck() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent?.BackColor ?? Bg);
                int s = (int)Math.Round(Font.Height * 0.95);
                var box = new Rectangle(2, (Height - s) / 2, s, s);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (Checked)
                {
                    using (var fill = new SolidBrush(Enabled ? TextC : MutedC)) g.FillRectangle(fill, box);
                    using var tick = new Pen(Bg, Math.Max(2f, s / 8f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round,
                                                                         LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                    g.DrawLines(tick, new[] { new PointF(box.X + s * 0.24f, box.Y + s * 0.52f), new PointF(box.X + s * 0.43f, box.Y + s * 0.71f),
                                              new PointF(box.X + s * 0.77f, box.Y + s * 0.31f) });
                }
                else
                {
                    using (var fill = new SolidBrush(ButtonBg)) g.FillRectangle(fill, box);
                    using var border = new Pen(Line, 2f) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                    g.DrawRectangle(border, box);
                }
                var text = new Rectangle(box.Right + s / 2, 0, Width - box.Right - s / 2, Height);
                TextRenderer.DrawText(g, Text, Font, text, Enabled ? TextC : MutedC, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(g, new Rectangle(text.X - 2, 2, TextRenderer.MeasureText(Text, Font).Width + 2, Height - 4), TextC, Bg);
            }
        }

        // Bahnschrift like the dashboard tab; Segoe UI where it is missing
        static Font UiFont(float pt, bool semibold = false)
        {
            var f = new Font(semibold ? "Bahnschrift SemiBold" : "Bahnschrift", pt);
            return f.Name.StartsWith("Bahnschrift") ? f : new Font("Segoe UI", pt, semibold ? FontStyle.Bold : FontStyle.Regular);
        }

        void ShowPlan()
        {
            string installed = Installer.InstalledVersion();
            _body.Text = (installed != null ? $"Updates fbtcal {installed} to {Installer.Version} in\n" : "Installs into\n") + _o.Dir +
                         "\nYour pose and settings (%APPDATA%\\fbtcal) are kept.";
            _note.Text = Installer.DriverInUse(_o.Dir) ? "SteamVR is running with fbtcal's driver loaded: close SteamVR first." : "";
            _primary.Text = installed != null ? "Update" : "Install";
        }

        void Primary()
        {
            if (_done) { Close(); return; }
            if (Installer.DriverInUse(_o.Dir)) { ShowPlan(); return; }
            _primary.Enabled = _secondary.Enabled = _desktop.Enabled = false;
            UseWaitCursor = true;
            try
            {
                Installer.Install(_o);
                _done = true;
                _body.Text = "fbtcal is installed and starts together with SteamVR from now on.\n" +
                             "In VR: open the SteamVR dashboard and look for the fbtcal tab.";
                _note.Text = Installer.SteamVRRunning() ? "SteamVR is running: restart it once to load the driver." : "";
                _primary.Text = "Done";
                _secondary.Visible = _desktop.Visible = false;
            }
            catch (Exception ex)
            {
                Log.Warn("setup failed: " + ex);
                string why = ex.Message.Length > 150 ? ex.Message.Substring(0, 150) + "…" : ex.Message;
                _note.Text = "Could not install: " + why + " (details in %APPDATA%\\fbtcal\\fbtcal.log)";
            }
            finally
            {
                UseWaitCursor = false;
                _primary.Enabled = _secondary.Enabled = _desktop.Enabled = true;
            }
        }
    }
}
