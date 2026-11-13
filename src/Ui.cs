using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HdogExtractor
{
    /// <summary>Colour palette (matches the logo's near-black background).</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(25, 24, 22);
        public static readonly Color Surface = Color.FromArgb(34, 33, 31);
        public static readonly Color RowAlt = Color.FromArgb(30, 29, 27);
        public static readonly Color Field = Color.FromArgb(42, 41, 38);
        public static readonly Color Border = Color.FromArgb(62, 60, 56);
        public static readonly Color Hover = Color.FromArgb(58, 56, 52);
        public static readonly Color Text = Color.FromArgb(238, 236, 232);
        public static readonly Color Muted = Color.FromArgb(152, 150, 144);
        public static readonly Color Accent = Color.FromArgb(255, 184, 48);
        public static readonly Color AccentHover = Color.FromArgb(255, 201, 98);
        public static readonly Color AccentDim = Color.FromArgb(72, 60, 28);
        public static readonly Color AccentText = Color.FromArgb(25, 24, 22);
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Gives the borderless window a shadow, dark frame and (on Windows 11) rounded corners.</summary>
        public static void StyleWindow(Form f, Color border)
        {
            try
            {
                MARGINS m = new MARGINS(); m.Left = m.Right = m.Top = m.Bottom = 1;
                DwmExtendFrameIntoClientArea(f.Handle, ref m);
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);                 // dark mode
                int round = 2;
                DwmSetWindowAttribute(f.Handle, 33, ref round, 4);              // rounded corners (Win11)
                int colour = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(f.Handle, 34, ref colour, 4);             // border colour (Win11)
            }
            catch (Exception) { /* older Windows: plain borderless window */ }
        }

        /// <summary>Starts a native window drag (keeps Aero Snap working).</summary>
        public static void DragWindow(Form f)
        {
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }
    }

    internal static class Gfx
    {
        /// <summary>The logo's "H" (43:46 proportions, 14-unit stems, 12-unit bar) as crisp vector shapes.</summary>
        public static void DrawLogoH(Graphics g, int x, int y, int height, Color colour)
        {
            float u = height / 46f;
            using (SolidBrush b = new SolidBrush(colour))
            {
                g.FillRectangle(b, x, y, 14 * u, height);
                g.FillRectangle(b, x + 29 * u, y, 14 * u, height);
                g.FillRectangle(b, x + 13 * u, y + 17 * u, 17 * u, 12 * u);
            }
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Slim rounded progress bar.</summary>
    internal sealed class FlatBar : Control
    {
        private int value;

        public FlatBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public int Value
        {
            get { return value; }
            set { int v = Math.Max(0, Math.Min(100, value)); if (v != this.value) { this.value = v; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Gfx.RoundRect(track, Height / 2))
            using (SolidBrush b = new SolidBrush(Theme.Field))
                e.Graphics.FillPath(b, p);
            int w = (int)((Width - 1) * (long)value / 100);
            if (w > 0)
            {
                Rectangle fill = new Rectangle(0, 0, Math.Max(w, Height - 1), Height - 1);
                using (GraphicsPath p = Gfx.RoundRect(fill, Height / 2))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    e.Graphics.FillPath(b, p);
            }
        }
    }

    /// <summary>A text box on a flat, bordered panel that highlights when focused.</summary>
    internal sealed class Field : Panel
    {
        public readonly TextBox Box = new TextBox();
        private bool focused;

        public Field()
        {
            BackColor = Theme.Field;
            Padding = new Padding(9, 6, 9, 0);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Box.Dock = DockStyle.Fill;
            Box.Enter += delegate { focused = true; Invalidate(); };
            Box.Leave += delegate { focused = false; Invalidate(); };
            Controls.Add(Box);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(focused ? Theme.Accent : Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Flat button with hover/pressed states; primary = accent coloured.</summary>
    internal sealed class FlatButton : Button
    {
        private readonly bool primary;
        private bool hover, down;

        public FlatButton(bool primary)
        {
            this.primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, fore;
            if (!Enabled) { fill = Theme.Surface; fore = Color.FromArgb(100, 98, 94); }
            else if (primary) { fill = down ? Theme.Accent : (hover ? Theme.AccentHover : Theme.Accent); fore = Theme.AccentText; }
            else { fill = down ? Theme.Border : (hover ? Theme.Hover : Theme.Field); fore = Theme.Text; }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Gfx.RoundRect(r, 6))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    internal enum CaptionKind { Minimize, Maximize, Close }

    /// <summary>Windows 11 style caption button (46x36) drawn with thin line glyphs.</summary>
    internal sealed class CaptionButton : Control
    {
        private readonly CaptionKind kind;
        private bool hover, down;

        public CaptionButton(CaptionKind kind)
        {
            this.kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(46, 36);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Theme.Bg, fg = Theme.Text;
            if (kind == CaptionKind.Close)
            {
                if (down) { bg = Color.FromArgb(199, 54, 62); fg = Color.White; }
                else if (hover) { bg = Color.FromArgb(232, 17, 35); fg = Color.White; }
            }
            else if (down) bg = Theme.Border;
            else if (hover) bg = Theme.Hover;
            g.Clear(bg);

            int cx = Width / 2, cy = Height / 2;
            using (Pen p = new Pen(fg, 1f))
            {
                g.SmoothingMode = SmoothingMode.None;
                switch (kind)
                {
                    case CaptionKind.Minimize:
                        g.DrawLine(p, cx - 5, cy, cx + 5, cy);
                        break;
                    case CaptionKind.Maximize:
                        Form f = FindForm();
                        if (f != null && f.WindowState == FormWindowState.Maximized)
                        {
                            g.DrawRectangle(p, cx - 5, cy - 3, 8, 8);
                            g.DrawLines(p, new Point[] { new Point(cx - 3, cy - 3), new Point(cx - 3, cy - 5), new Point(cx + 5, cy - 5), new Point(cx + 5, cy + 3), new Point(cx + 3, cy + 3) });
                        }
                        else g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
                        break;
                    default:
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(p, cx - 5, cy + 5, cx + 5, cy - 5);
                        break;
                }
            }
        }
    }
}