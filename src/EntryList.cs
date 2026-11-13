using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HdogExtractor
{
    internal sealed class EntryEventArgs : EventArgs
    {
        public readonly HdogEntry Entry;
        public EntryEventArgs(HdogEntry e) { Entry = e; }
    }

    /// <summary>
    /// Custom-drawn, checkable file list with sortable columns and a slim modern scrollbar.
    /// Clicking a row toggles it; clicking a header sorts.
    /// </summary>
    internal sealed class EntryList : Control
    {
        private const int HeaderH = 34;
        private const int RowH = 30;
        private const int ScrollW = 14;            // reserved on the right
        private const int TypeW = 80, SizeW = 110, OffsetW = 150;

        private readonly List<HdogEntry> all = new List<HdogEntry>();
        private readonly HashSet<HdogEntry> checkedSet = new HashSet<HdogEntry>();
        private List<HdogEntry> rows = new List<HdogEntry>();
        private string filter = "";
        private int sortCol = -1;
        private bool sortAsc = true;
        private int scroll;
        private int hoverRow = -1;
        private bool thumbHover, thumbDrag;
        private int dragGrabY;
        private string emptyText = "Drop an .hdog file here";

        public event EventHandler SelectionChanged;
        public event EventHandler FocusedChanged;
        public event EventHandler<EntryEventArgs> OpenRequested;     // double-click

        private HdogEntry focused;
        /// <summary>The row last clicked (shown with an accent bar); used by the Explore button.</summary>
        public HdogEntry FocusedEntry { get { return focused; } }

        public EntryList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
        }

        // ------------------------------------------------------------------ model

        public string EmptyText { get { return emptyText; } set { emptyText = value; Invalidate(); } }
        public int RowCount { get { return rows.Count; } }

        public int CheckedCount
        {
            get { int n = 0; foreach (HdogEntry e in rows) if (checkedSet.Contains(e)) n++; return n; }
        }

        public long CheckedBytes
        {
            get { long n = 0; foreach (HdogEntry e in rows) if (checkedSet.Contains(e)) n += e.Size; return n; }
        }

        public List<HdogEntry> GetChecked()
        {
            List<HdogEntry> r = new List<HdogEntry>();
            foreach (HdogEntry e in rows) if (checkedSet.Contains(e)) r.Add(e);
            return r;
        }

        public string Filter
        {
            get { return filter; }
            set { filter = (value ?? "").Trim(); Rebuild(); }
        }

        public void SetEntries(IEnumerable<HdogEntry> entries)
        {
            all.Clear(); checkedSet.Clear(); focused = null;
            foreach (HdogEntry e in entries) { all.Add(e); checkedSet.Add(e); }
            scroll = 0;
            Rebuild();
        }

        public void SetAllChecked(bool value)
        {
            foreach (HdogEntry e in rows) { if (value) checkedSet.Add(e); else checkedSet.Remove(e); }
            Invalidate();
            RaiseSelectionChanged();
        }

        private void Rebuild()
        {
            IEnumerable<HdogEntry> q = all;
            if (filter.Length > 0) q = q.Where(e => e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            if (sortCol >= 0)
            {
                switch (sortCol)
                {
                    case 0: q = sortAsc ? q.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase) : q.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase); break;
                    case 1: q = sortAsc ? q.OrderBy(e => e.TypeLabel, StringComparer.OrdinalIgnoreCase) : q.OrderByDescending(e => e.TypeLabel, StringComparer.OrdinalIgnoreCase); break;
                    case 2: q = sortAsc ? q.OrderBy(e => e.Size) : q.OrderByDescending(e => e.Size); break;
                    default: q = sortAsc ? q.OrderBy(e => e.Offset) : q.OrderByDescending(e => e.Offset); break;
                }
            }
            rows = q.ToList();
            ClampScroll();
            Invalidate();
            RaiseSelectionChanged();
        }

        private void RaiseSelectionChanged()
        {
            EventHandler h = SelectionChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ----------------------------------------------------------------- layout

        private int ViewH { get { return Math.Max(0, Height - HeaderH); } }
        private int MaxScroll { get { return Math.Max(0, rows.Count * RowH - ViewH); } }
        private void ClampScroll() { scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); }

        private int NameW { get { return Math.Max(120, Width - ScrollW - TypeW - SizeW - OffsetW); } }

        private Rectangle ColumnRect(int col)
        {
            int x = 0, w;
            switch (col)
            {
                case 0: w = NameW; break;
                case 1: x = NameW; w = TypeW; break;
                case 2: x = NameW + TypeW; w = SizeW; break;
                default: x = NameW + TypeW + SizeW; w = OffsetW; break;
            }
            return new Rectangle(x, 0, w, HeaderH);
        }

        private Rectangle TrackRect { get { return new Rectangle(Width - ScrollW, HeaderH + 2, ScrollW - 2, Math.Max(0, ViewH - 4)); } }

        private Rectangle ThumbRect
        {
            get
            {
                Rectangle t = TrackRect;
                int content = rows.Count * RowH;
                if (MaxScroll <= 0 || content <= 0) return Rectangle.Empty;
                int h = Math.Max(36, (int)((long)t.Height * ViewH / content));
                int y = t.Y + (int)((long)(t.Height - h) * scroll / MaxScroll);
                int w = (thumbHover || thumbDrag) ? 8 : 4;
                return new Rectangle(t.Right - w - 1, y, w, h);
            }
        }

        // --------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            // rows
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, emptyText, new Font("Segoe UI", 12f), new Rectangle(0, HeaderH, Width, ViewH), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            int first = Math.Max(0, scroll / RowH);
            int last = Math.Min(rows.Count - 1, (scroll + ViewH) / RowH);
            Region clip = new Region(new Rectangle(0, HeaderH, Width, ViewH));
            g.Clip = clip;
            for (int i = first; i <= last; i++)
            {
                HdogEntry en = rows[i];
                int y = HeaderH + i * RowH - scroll;
                Rectangle row = new Rectangle(0, y, Width - ScrollW, RowH);
                Color bg = i == hoverRow ? Theme.Hover : (i % 2 == 0 ? Theme.Bg : Theme.RowAlt);
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, row);

                if (en == focused) using (SolidBrush fb = new SolidBrush(Theme.Accent)) g.FillRectangle(fb, 0, y + 4, 3, RowH - 8);
                bool chk = checkedSet.Contains(en);
                Rectangle box = new Rectangle(14, y + (RowH - 16) / 2, 16, 16);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Gfx.RoundRect(box, 4))
                {
                    if (chk)
                    {
                        using (SolidBrush ab = new SolidBrush(Theme.Accent)) g.FillPath(ab, p);
                        using (Pen cp = new Pen(Theme.AccentText, 2f))
                        {
                            cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round; cp.LineJoin = LineJoin.Round;
                            g.DrawLines(cp, new PointF[] { new PointF(box.X + 4, box.Y + 8.5f), new PointF(box.X + 7, box.Y + 11.5f), new PointF(box.X + 12, box.Y + 5) });
                        }
                    }
                    else using (Pen bp = new Pen(i == hoverRow ? Theme.Muted : Theme.Border, 1.5f)) g.DrawPath(bp, p);
                }
                g.SmoothingMode = SmoothingMode.Default;

                TextRenderer.DrawText(g, en.Name, Font, new Rectangle(42, y, NameW - 50, RowH), Theme.Text, f | TextFormatFlags.Left);
                TextRenderer.DrawText(g, en.TypeLabel, Font, Rectangle.Inflate(Offset(ColumnRect(1), y), -8, 0), Theme.Muted, f | TextFormatFlags.Left);
                TextRenderer.DrawText(g, FormatSize(en.Size), Font, Rectangle.Inflate(Offset(ColumnRect(2), y), -8, 0), Theme.Muted, f | TextFormatFlags.Right);
                TextRenderer.DrawText(g, en.Offset.ToString("N0"), Font, Rectangle.Inflate(Offset(ColumnRect(3), y), -8, 0), Theme.Muted, f | TextFormatFlags.Right);
            }
            g.ResetClip();
            clip.Dispose();

            // scrollbar thumb
            Rectangle th = ThumbRect;
            if (!th.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Gfx.RoundRect(th, th.Width / 2))
                using (SolidBrush b = new SolidBrush((thumbHover || thumbDrag) ? Color.FromArgb(150, 148, 142) : Color.FromArgb(92, 90, 85)))
                    g.FillPath(b, p);
                g.SmoothingMode = SmoothingMode.Default;
            }

            // header (drawn last so scrolled rows slide beneath it)
            using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, 0, 0, Width, HeaderH);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, HeaderH - 1, Width, HeaderH - 1);
            string[] names = { "Name", "Type", "Size", "Offset" };
            using (Font hf = new Font("Segoe UI Semibold", 9f))
            {
                for (int c = 0; c < 4; c++)
                {
                    string t = names[c] + (c == sortCol ? (sortAsc ? "  ▲" : "  ▼") : "");
                    Rectangle r = ColumnRect(c);
                    TextFormatFlags hfl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                    if (c == 0) { r = new Rectangle(42, 0, r.Width - 42, HeaderH); hfl |= TextFormatFlags.Left; }
                    else { r = Rectangle.Inflate(r, -8, 0); hfl |= c == 1 ? TextFormatFlags.Left : TextFormatFlags.Right; }
                    TextRenderer.DrawText(g, t, hf, r, c == sortCol ? Theme.Text : Theme.Muted, hfl);
                }
            }
        }

        private static Rectangle Offset(Rectangle r, int y) { return new Rectangle(r.X, y, r.Width, RowH); }

        // ------------------------------------------------------------------ input

        private int RowAt(int y)
        {
            if (y < HeaderH || y >= Height) return -1;
            int i = (y - HeaderH + scroll) / RowH;
            return i >= 0 && i < rows.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag)
            {
                Rectangle t = TrackRect; Rectangle th = ThumbRect;
                int range = Math.Max(1, t.Height - th.Height);
                int y = e.Y - dragGrabY - t.Y;
                scroll = (int)((long)MaxScroll * Math.Max(0, Math.Min(range, y)) / range);
                Invalidate();
                return;
            }
            bool th2 = !ThumbRect.IsEmpty && e.X >= Width - ScrollW - 2 && e.Y >= HeaderH;
            int hr = th2 ? -1 : RowAt(e.Y);
            if (hr != hoverRow || th2 != thumbHover) { hoverRow = hr; thumbHover = th2; Invalidate(); }
            Cursor = (hr >= 0 || (e.Y < HeaderH && HeaderColumnAt(e.X) >= 0)) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverRow != -1 || thumbHover) { hoverRow = -1; thumbHover = false; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Form f = FindForm();
            if (f != null && !(f.ActiveControl is TextBox)) Focus();    // so the mouse wheel scrolls the list
        }

        private int HeaderColumnAt(int x)
        {
            for (int c = 0; c < 4; c++) if (ColumnRect(c).Contains(x, 5)) return c;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.Y < HeaderH)
            {
                int c = HeaderColumnAt(e.X);
                if (c >= 0)
                {
                    sortAsc = c == sortCol ? !sortAsc : true;
                    sortCol = c;
                    Rebuild();
                }
                return;
            }
            if (e.X >= Width - ScrollW - 2 && MaxScroll > 0)
            {
                Rectangle th = ThumbRect;
                if (e.Y >= th.Y && e.Y <= th.Bottom) { thumbDrag = true; dragGrabY = e.Y - th.Y; Capture = true; }
                else { scroll += e.Y < th.Y ? -ViewH : ViewH; ClampScroll(); }
                Invalidate();
                return;
            }
            int i = RowAt(e.Y);
            if (i >= 0)
            {
                HdogEntry en = rows[i];
                if (!checkedSet.Remove(en)) checkedSet.Add(en);
                if (focused != en)
                {
                    focused = en;
                    EventHandler fh = FocusedChanged;
                    if (fh != null) fh(this, EventArgs.Empty);
                }
                Invalidate();
                RaiseSelectionChanged();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (thumbDrag) { thumbDrag = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = RowAt(e.Y);
            if (i >= 0 && e.X < Width - ScrollW - 2)
            {
                EventHandler<EntryEventArgs> h = OpenRequested;
                if (h != null) h(this, new EntryEventArgs(rows[i]));
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= e.Delta / 120 * RowH * 3;
            ClampScroll();
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.PageUp || keyData == Keys.PageDown ||
                   keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up: scroll -= RowH; break;
                case Keys.Down: scroll += RowH; break;
                case Keys.PageUp: scroll -= ViewH; break;
                case Keys.PageDown: scroll += ViewH; break;
                case Keys.Home: scroll = 0; break;
                case Keys.End: scroll = MaxScroll; break;
                default: return;
            }
            ClampScroll();
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.00") + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0.0") + " KB";
            return bytes + " B";
        }
    }
}
