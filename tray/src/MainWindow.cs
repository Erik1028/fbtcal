using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FbtCal
{
    // The SteamVR tab on the desktop: the same picture (PanelDraw.cs), scaled to the window and
    // clicked with the mouse; the clicks go to the worker like the laser's. Opened from the desktop
    // shortcut or the tray icon. Closing it only hides it: FBTcal keeps running in the tray.
    sealed class MainWindow : Form
    {
        readonly VrWorker _worker;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 100 };  // the worker's picture changes up to ~10 times a second
        PanelModel _shown;
        PanelAction _hover, _pressed;
        float _scale = 1;
        public bool Quitting;  // set by the tray app: then closing really closes

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DwmwaUseImmersiveDarkMode = 20, DwmwaCaptionColor = 35;

        public MainWindow(VrWorker worker)
        {
            _worker = worker;
            Text = "FBTcal";
            Icon = Res.WindowIcon("fbtcal.ico");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;  // the picture scales itself (Fit)
            BackColor = Color.FromArgb(0x0E, 0x0E, 0x10);
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Fit(DeviceDpi);
            _timer.Tick += (s, e) => TakeModel(force: false);
        }

        // The tab's 1024 x 640 at the screen's scale, smaller if the screen can't hold it.
        void Fit(int dpi)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            _scale = Math.Min(dpi / 96f, Math.Min(area.Width * 0.9f / DashboardPanel.W, area.Height * 0.85f / DashboardPanel.H));
            ClientSize = new Size((int)Math.Round(DashboardPanel.W * _scale), (int)Math.Round(DashboardPanel.H * _scale));
        }

        public void ShowFront()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        PanelModel Current() => _worker.LatestModel is PanelModel m ? m with { OnDesktop = true } : null;

        // New picture from the worker: the button under the mouse may have changed too.
        void TakeModel(bool force)
        {
            var m = Current();
            if (!force && Equals(m, _shown)) return;
            _shown = m;
            var p = PointToClient(MousePosition);
            _hover = ClientRectangle.Contains(p) ? At(p) : PanelAction.None;
            Invalidate();
        }

        PanelAction At(Point p) => _shown == null ? PanelAction.None : DashboardPanel.HitAt(_shown, p.X / _scale, p.Y / _scale);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_shown == null)
            {
                e.Graphics.Clear(BackColor);
                return;
            }
            e.Graphics.ScaleTransform(_scale, _scale);
            DashboardPanel.Paint(e.Graphics, _shown, _hover, _pressed);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = At(e.Location);
            Cursor = h != PanelAction.None ? Cursors.Hand : Cursors.Default;
            if (h == _hover) return;
            _hover = h;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover == PanelAction.None && _pressed == PanelAction.None) return;
            _hover = _pressed = PanelAction.None;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _pressed = At(e.Location);
            Invalidate();
        }

        // a press and release on the same button, like the laser
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var up = At(e.Location);
            if (up != PanelAction.None && up == _pressed) _worker.PostAction(up);
            _pressed = PanelAction.None;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            _timer.Enabled = Visible;
            if (Visible) TakeModel(force: true);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Fit(e.DeviceDpiNew);
            Invalidate();
        }

        // A dark title bar in the picture's colour (Windows 11; Windows 10 takes only the dark mode).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, caption = 0x100E0E;  // COLORREF 0x00BBGGRR of #0E0E10
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref caption, sizeof(int));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
