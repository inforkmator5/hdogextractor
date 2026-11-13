using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HdogExtractor
{
    internal sealed class TreeItem
    {
        public string Text;
        public string Detail;
        public object Tag;
        public bool Expanded;
        public bool Bold;
        public TreeItem Parent;
        public int Depth;
        public List<TreeItem> Children = new List<TreeItem>();

        public TreeItem(string text, string detail) { Text = text; Detail = detail; }
        public TreeItem Add(TreeItem c) { Children.Add(c); return c; }
    }

    /// <summary>
    /// Custom-drawn tree with an optional value column and a slim scrollbar.
    /// Click selects, the arrow / double-click / Enter expands, arrow keys navigate.
    /// </summary>
    internal sealed class TreeList : Control
    {
        private const int RowH = 26;
        private const int ScrollW = 14;
        private const int Indent = 18;

        private readonly List<TreeItem> roots = new List<TreeItem>();
        private readonly List<TreeItem> vis = new List<TreeItem>();
        private TreeItem selected;
        private int scroll, hover = -1, dragGrab;
        private bool thumbHover, thumbDrag;
        private string emptyText = "";

        /// <summary>When true, Detail is drawn in its own left-aligned column; otherwise right-aligned.</summary>
        public bool DetailColumn;
        public float DetailRatio = 0.45f;

        public event EventHandler SelectedChanged;

        public TreeList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
        }

        public string EmptyText { get { return emptyText; } set { emptyText = value; Invalidate(); } }
        public TreeItem Selected { get { return selected; } }
        public int VisibleCount { get { return vis.Count; } }

        public void SetRoots(IEnumerable<TreeItem> items)
        {
            roots.Clear();
            foreach (TreeItem t in items) { roots.Add(t); Prepare(t, null, 0); }
            selected = null;
            scroll = 0;
            Rebuild();
            RaiseSelected();
        }

        private static void Prepare(TreeItem t, TreeItem parent, int depth)
        {
            t.Parent = parent; t.Depth = depth;
            foreach (TreeItem c in t.Children) Prepare(c, t, depth + 1);
        }

        public void ExpandAll(bool expand)
        {
            foreach (TreeItem t in roots) SetExpanded(t, expand);
            Rebuild();
        }

        private static void SetExpanded(TreeItem t, bool e)
        {
            if (t.Children.Count > 0) t.Expanded = e;
            foreach (TreeItem c in t.Children) SetExpanded(c, e);
        }

        private void Rebuild()
        {
            vis.Clear();
            foreach (TreeItem t in roots) AddVisible(t);
            ClampScroll();
            Invalidate();
        }

        private void AddVisible(TreeItem t)
        {
            vis.Add(t);
            if (t.Expanded) foreach (TreeItem c in t.Children) AddVisible(c);
        }

        private void RaiseSelected()
        {
            EventHandler h = SelectedChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ----------------------------------------------------------------------- layout

        private int ViewH { get { return Height; } }
        private int MaxScroll { get { return Math.Max(0, vis.Count * RowH - ViewH); } }
        private void ClampScroll() { scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); }
        private int DetailX { get { return (int)((Width - ScrollW) * DetailRatio); } }

        private Rectangle TrackRect { get { return new Rectangle(Width - ScrollW, 2, ScrollW - 2, Math.Max(0, Height - 4)); } }

        private Rectangle ThumbRect
        {
            get
            {
                Rectangle t = TrackRect;
                int content = vis.Count * RowH;
                if (MaxScroll <= 0 || content <= 0) return Rectangle.Empty;
                int h = Math.Max(36, (int)((long)t.Height * ViewH / content));
                int y = t.Y + (int)((long)(t.Height - h) * scroll / MaxScroll);
                int w = (thumbHover || thumbDrag) ? 8 : 4;
                return new Rectangle(t.Right - w - 1, y, w, h);
            }
        }

        private static Rectangle ArrowRect(int depth, int rowY) { return new Rectangle(6 + depth * Indent, rowY + 6, 14, 14); }

        // ---------------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            if (vis.Count == 0 && emptyText.Length > 0)
            {
                using (Font f = new Font("Segoe UI", 11f))
                    TextRenderer.DrawText(g, emptyText, f, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int first = Math.Max(0, scroll / RowH);
            int last = Math.Min(vis.Count - 1, (scroll + ViewH) / RowH);
            int contentW = Width - ScrollW;
            Font bold = new Font(Font, FontStyle.Bold);
            for (int i = first; i <= last; i++)
            {
                TreeItem t = vis[i];
                int y = i * RowH - scroll;
                Color bg = t == selected ? Theme.AccentDim : (i == hover ? Theme.Hover : Theme.Bg);
                if (bg != Theme.Bg) using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, 0, y, contentW, RowH);
                if (t == selected) using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, y + 3, 3, RowH - 6);

                if (t.Children.Count > 0)
                {
                    Rectangle ar = ArrowRect(t.Depth, y);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    PointF c = new PointF(ar.X + 7, ar.Y + 7);
                    PointF[] tri = t.Expanded
                        ? new PointF[] { new PointF(c.X - 4, c.Y - 2), new PointF(c.X + 4, c.Y - 2), new PointF(c.X, c.Y + 3) }
                        : new PointF[] { new PointF(c.X - 2, c.Y - 4), new PointF(c.X - 2, c.Y + 4), new PointF(c.X + 3, c.Y) };
                    using (SolidBrush b = new SolidBrush(Theme.Muted)) g.FillPolygon(b, tri);
                    g.SmoothingMode = SmoothingMode.Default;
                }
                int tx = 24 + t.Depth * Indent;
                int textRight = DetailColumn ? DetailX - 8 : contentW - 8;
                Font fnt = (t.Bold || (t.Depth == 0 && t.Children.Count > 0)) ? bold : Font;
                if (!DetailColumn && !string.IsNullOrEmpty(t.Detail))
                {
                    Size ds = TextRenderer.MeasureText(t.Detail, Font);
                    textRight = Math.Max(tx + 40, contentW - 12 - ds.Width - 8);
                    TextRenderer.DrawText(g, t.Detail, Font, new Rectangle(contentW - 12 - ds.Width, y, ds.Width + 4, RowH), Theme.Muted, tf | TextFormatFlags.Right);
                }
                TextRenderer.DrawText(g, t.Text ?? "", fnt, new Rectangle(tx, y, Math.Max(10, textRight - tx), RowH), Theme.Text, tf | TextFormatFlags.Left);
                if (DetailColumn && !string.IsNullOrEmpty(t.Detail))
                    TextRenderer.DrawText(g, t.Detail, Font, new Rectangle(DetailX, y, contentW - DetailX - 8, RowH), Theme.Muted, tf | TextFormatFlags.Left);
            }
            bold.Dispose();

            Rectangle th = ThumbRect;
            if (!th.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Gfx.RoundRect(th, th.Width / 2))
                using (SolidBrush b = new SolidBrush((thumbHover || thumbDrag) ? Color.FromArgb(150, 148, 142) : Color.FromArgb(92, 90, 85)))
                    g.FillPath(b, p);
            }
        }

        // ------------------------------------------------------------------------- input

        private int RowAt(int y)
        {
            int i = (y + scroll) / RowH;
            return y >= 0 && i >= 0 && i < vis.Count ? i : -1;
        }

        private void Toggle(TreeItem t)
        {
            if (t.Children.Count == 0) return;
            t.Expanded = !t.Expanded;
            Rebuild();
        }

        private void Select(TreeItem t)
        {
            if (t == selected) return;
            selected = t;
            EnsureVisible(vis.IndexOf(t));
            Invalidate();
            RaiseSelected();
        }

        private void EnsureVisible(int i)
        {
            if (i < 0) return;
            if (i * RowH < scroll) scroll = i * RowH;
            else if ((i + 1) * RowH > scroll + ViewH) scroll = (i + 1) * RowH - ViewH;
            ClampScroll();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag)
            {
                Rectangle t = TrackRect, th = ThumbRect;
                int range = Math.Max(1, t.Height - th.Height);
                int y = e.Y - dragGrab - t.Y;
                scroll = (int)((long)MaxScroll * Math.Max(0, Math.Min(range, y)) / range);
                Invalidate();
                return;
            }
            bool onBar = !ThumbRect.IsEmpty && e.X >= Width - ScrollW - 2;
            int hr = onBar ? -1 : RowAt(e.Y);
            if (hr != hover || onBar != thumbHover) { hover = hr; thumbHover = onBar; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1 || thumbHover) { hover = -1; thumbHover = false; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Form f = FindForm();
            if (f != null && !(f.ActiveControl is TextBox)) Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (e.X >= Width - ScrollW - 2 && MaxScroll > 0)
            {
                Rectangle th = ThumbRect;
                if (e.Y >= th.Y && e.Y <= th.Bottom) { thumbDrag = true; dragGrab = e.Y - th.Y; Capture = true; }
                else { scroll += e.Y < th.Y ? -ViewH : ViewH; ClampScroll(); }
                Invalidate();
                return;
            }
            int i = RowAt(e.Y);
            if (i < 0) return;
            TreeItem t = vis[i];
            if (t.Children.Count > 0 && ArrowRect(t.Depth, i * RowH - scroll).Contains(e.Location)) { Toggle(t); return; }
            Select(t);
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
            if (i >= 0) Toggle(vis[i]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= e.Delta / 120 * RowH * 3;
            ClampScroll();
            Invalidate();
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.PageUp ||
                   k == Keys.PageDown || k == Keys.Home || k == Keys.End || k == Keys.Enter || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (vis.Count == 0) return;
            int cur = selected == null ? -1 : vis.IndexOf(selected);
            int page = Math.Max(1, ViewH / RowH - 1);
            switch (e.KeyCode)
            {
                case Keys.Up: Select(vis[Math.Max(0, cur - 1)]); break;
                case Keys.Down: Select(vis[Math.Min(vis.Count - 1, cur + 1)]); break;
                case Keys.PageUp: Select(vis[Math.Max(0, cur - page)]); break;
                case Keys.PageDown: Select(vis[Math.Min(vis.Count - 1, cur + page)]); break;
                case Keys.Home: Select(vis[0]); break;
                case Keys.End: Select(vis[vis.Count - 1]); break;
                case Keys.Enter: if (selected != null) Toggle(selected); break;
                case Keys.Right:
                    if (selected != null && selected.Children.Count > 0 && !selected.Expanded) Toggle(selected);
                    else if (selected != null && selected.Expanded && selected.Children.Count > 0) Select(selected.Children[0]);
                    break;
                case Keys.Left:
                    if (selected != null && selected.Expanded) Toggle(selected);
                    else if (selected != null && selected.Parent != null) Select(selected.Parent);
                    break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
        }
    }
}
