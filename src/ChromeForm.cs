using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HdogExtractor
{
    /// <summary>
    /// Borderless dark window with a Windows 11 style title bar (white H logo, caption buttons),
    /// native drag / snap / resize and a DWM shadow. Content is placed below <see cref="ContentTop"/>.
    /// </summary>
    public class ChromeForm : Form
    {
        protected const int TitleH = 36;
        private const int ResizeBorder = 7;

        private readonly bool resizable;
        private readonly CaptionButton capMax;

        /// <summary>Y offset applied by <see cref="Place"/>; subclasses with a banner raise it.</summary>
        protected int ContentTop = TitleH;

        public ChromeForm(bool resizable)
        {
            this.resizable = resizable;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            DoubleBuffered = true;
            ResizeRedraw = true;
            try
            {
                using (Stream s = typeof(ChromeForm).Assembly.GetManifestResourceStream("app.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch (Exception) { }

            int n = resizable ? 3 : 1;
            CaptionButton[] b = new CaptionButton[n];
            if (resizable)
            {
                b[0] = new CaptionButton(CaptionKind.Minimize);
                b[1] = capMax = new CaptionButton(CaptionKind.Maximize);
            }
            b[n - 1] = new CaptionButton(CaptionKind.Close);
            for (int i = 0; i < n; i++)
            {
                b[i].Anchor = AnchorStyles.Top | AnchorStyles.Right;
                Controls.Add(b[i]);
            }
            Layout += delegate { LayoutCaptionButtons(b); };
            if (resizable)
            {
                b[0].Click += delegate { WindowState = FormWindowState.Minimized; };
                b[1].Click += delegate { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; };
            }
            b[n - 1].Click += delegate { Close(); };
        }

        private void LayoutCaptionButtons(CaptionButton[] b)
        {
            for (int i = 0; i < b.Length; i++)
                b[i].Location = new Point(ClientSize.Width - 46 * (b.Length - i), 0);
        }

        // ---------------------------------------------------------------- helpers

        protected void Place(Control c, int x, int y, int w, int h, AnchorStyles anchor)
        {
            c.Location = new Point(x, y + ContentTop);
            c.Size = new Size(w, h);
            c.Anchor = anchor;
            Controls.Add(c);
        }

        protected Label AddLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Muted;
            l.TextAlign = ContentAlignment.MiddleLeft;
            Place(l, x, y, w, 30, AnchorStyles.Top | AnchorStyles.Left);
            return l;
        }

        // ---------------------------------------------------------- window plumbing

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00040000 | 0x00020000;          // WS_THICKFRAME | WS_MINIMIZEBOX: shadow, snap, animations
                if (resizable) cp.Style |= 0x00010000;         // WS_MAXIMIZEBOX
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleWindow(this, Theme.Border);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (capMax != null) capMax.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024;
            const int WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086;
            switch (m.Msg)
            {
                case WM_NCPAINT:
                    m.Result = IntPtr.Zero;                       // never let Windows draw its own frame/caption
                    return;
                case WM_NCACTIVATE:
                    // Gaining/losing focus (e.g. when a dialog opens) would repaint the native title bar over ours.
                    // lParam = -1 keeps the activation state but suppresses the non-client repaint.
                    m.Result = DefWndProc2(ref m);
                    return;
                case WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }   // no non-client area
                    break;
                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    if ((int)m.Result == 1) m.Result = (IntPtr)HitTest(m.LParam);       // 1 = HTCLIENT
                    return;
                case WM_GETMINMAXINFO:
                    base.WndProc(ref m);
                    Screen s = Screen.FromHandle(Handle);
                    Native.MINMAXINFO mmi = (Native.MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                    Rectangle wa = s.WorkingArea, sb = s.Bounds;
                    mmi.ptMaxPosition = new Point(wa.X - sb.X, wa.Y - sb.Y);
                    mmi.ptMaxSize = new Point(wa.Width, wa.Height);
                    mmi.ptMinTrackSize = new Point(MinimumSize.Width, MinimumSize.Height);
                    Marshal.StructureToPtr(mmi, m.LParam, true);
                    return;
            }
            base.WndProc(ref m);
        }

        private IntPtr DefWndProc2(ref Message m)
        {
            Message copy = Message.Create(m.HWnd, m.Msg, m.WParam, (IntPtr)(-1));
            base.DefWndProc(ref copy);
            return (IntPtr)1;     // TRUE: allow the activation change
        }

        private int HitTest(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            Point p = PointToClient(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));
            if (resizable && WindowState == FormWindowState.Normal)
            {
                bool l = p.X < ResizeBorder, r = p.X >= Width - ResizeBorder, t = p.Y < ResizeBorder, b = p.Y >= Height - ResizeBorder;
                if (t && l) return 13; if (t && r) return 14; if (b && l) return 16; if (b && r) return 17;
                if (l) return 10; if (r) return 11; if (t) return 12; if (b) return 15;
            }
            return p.Y < TitleH ? 2 : 1;       // HTCAPTION over the title bar, else HTCLIENT
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Gfx.DrawLogoH(g, 14, (TitleH - 15) / 2, 15, Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            using (Font f = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, Text, f, new Rectangle(38, 0, 400, TitleH), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (WindowState == FormWindowState.Normal)
                using (Pen pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>Lets a child (e.g. a banner) start a window drag.</summary>
        protected void MakeDraggable(Control c)
        {
            c.MouseDown += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Native.DragWindow(this); };
        }
    }
}
