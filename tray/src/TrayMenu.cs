using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FbtCal
{
    // The tray menu in the tab's colours (PanelDraw.cs) instead of Windows' light grey one: near-black,
    // a grey hover, the purple only for a tick.
    sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Bg = Color.FromArgb(0x16, 0x16, 0x1A), Hover = Color.FromArgb(0x2A, 0x2A, 0x30),
                              Line = Color.FromArgb(0x2A, 0x2A, 0x30), Tx = Color.FromArgb(0xED, 0xED, 0xEF),
                              Tx2 = Color.FromArgb(0xA2, 0xA2, 0xA9), Tx3 = Color.FromArgb(0x8A, 0x8A, 0x92),
                              Acc = Color.FromArgb(0xA7, 0x43, 0xE8);

        public TrayMenuRenderer() : base(new Colors()) => RoundedEdges = false;

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Bg;
            public override Color CheckBackground => Bg;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }

        // Drawn here: the base class greys disabled text with the system colour, unreadable on black.
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.Item.Enabled ? Tx : Tx3, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Tx2;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            float s = r.Height;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Acc, s / 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(r.X + s * 0.2f, r.Y + s * 0.52f), new PointF(r.X + s * 0.42f, r.Y + s * 0.74f),
                                     new PointF(r.X + s * 0.82f, r.Y + s * 0.28f) });
        }
    }
}
