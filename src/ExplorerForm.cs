using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdogExtractor
{
    /// <summary>Browse the inside of a Unity bundle: scripts, objects by type, and each object's properties.</summary>
    internal sealed class ExplorerForm : ChromeForm
    {
        private readonly string title;
        private readonly byte[] bytes;
        private readonly ScriptResolver resolver;

        private readonly Field searchField = new Field();
        private readonly FlatButton btnExpand = new FlatButton(false);
        private readonly FlatButton btnCollapse = new FlatButton(false);
        private readonly Label lblCount = new Label();
        private readonly TreeList tree = new TreeList();
        private readonly TreeList props = new TreeList();
        private readonly Label lblInfo = new Label();
        private readonly Label lblSub = new Label();
        private readonly Label lblStatus = new Label();
        private readonly Panel splitter = new Panel();

        private BundleModel model;
        private Dictionary<SerializedFile, Dictionary<long, ExplorerItem>> byPath;
        private float split = 0.40f;
        private bool dragSplit;

        public ExplorerForm(string title, byte[] bytes, ScriptResolver resolver) : base(true)
        {
            this.title = title; this.bytes = bytes; this.resolver = resolver;
            Text = "Explorer - " + title;
            ClientSize = new Size(1120, 700 + TitleH);
            MinimumSize = new Size(820, 520 + TitleH);
            StartPosition = FormStartPosition.CenterParent;

            AddLabel("Search", 16, 10, 60);
            Place(searchField, 78, 10, 330, 30, AnchorStyles.Top | AnchorStyles.Left);
            Place(btnExpand, 420, 10, 100, 30, AnchorStyles.Top | AnchorStyles.Left); btnExpand.Text = "Expand all";
            Place(btnCollapse, 526, 10, 104, 30, AnchorStyles.Top | AnchorStyles.Left); btnCollapse.Text = "Collapse all";
            Place(lblCount, 700, 15, 404, 20, AnchorStyles.Top | AnchorStyles.Right);
            lblCount.TextAlign = ContentAlignment.MiddleRight; lblCount.ForeColor = Theme.Muted;

            Controls.Add(tree); Controls.Add(props); Controls.Add(lblInfo); Controls.Add(lblSub); Controls.Add(splitter);
            tree.EmptyText = "Reading bundle...";
            props.DetailColumn = true; props.DetailRatio = 0.42f;
            props.EmptyText = "Select an item on the left";
            lblInfo.ForeColor = Theme.Text; lblInfo.Font = new Font("Segoe UI Semibold", 10.5f); lblInfo.AutoEllipsis = true;
            lblSub.ForeColor = Theme.Muted; lblSub.AutoEllipsis = true;
            splitter.BackColor = Theme.Bg; splitter.Cursor = Cursors.VSplit;

            Place(lblStatus, 16, 662, 1088, 24, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            lblStatus.ForeColor = Theme.Muted; lblStatus.AutoEllipsis = true;

            btnExpand.Click += delegate { tree.ExpandAll(true); };
            btnCollapse.Click += delegate { tree.ExpandAll(false); };
            searchField.Box.TextChanged += delegate { if (model != null) BuildTree(); };
            tree.SelectedChanged += delegate { ShowProperties(); };
            splitter.MouseDown += delegate { dragSplit = true; splitter.Capture = true; };
            splitter.MouseUp += delegate { dragSplit = false; splitter.Capture = false; };
            splitter.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!dragSplit) return;
                int x = PointToClient(Cursor.Position).X - 16;
                split = Math.Max(0.2f, Math.Min(0.7f, x / (float)(ClientSize.Width - 32)));
                LayoutPanes();
            };
            SizeChanged += delegate { LayoutPanes(); };
            LayoutPanes();

            Shown += async delegate { await LoadAsync(); };
        }

        private void LayoutPanes()
        {
            int top = ContentTop + 52, bottom = ClientSize.Height - 40;
            int w = ClientSize.Width - 32;
            int leftW = (int)(w * split);
            tree.SetBounds(16, top, leftW, bottom - top);
            splitter.SetBounds(16 + leftW, top, 8, bottom - top);
            int rx = 16 + leftW + 8, rw = w - leftW - 8;
            lblInfo.SetBounds(rx + 6, top, rw - 6, 22);
            lblSub.SetBounds(rx + 6, top + 22, rw - 6, 20);
            props.SetBounds(rx, top + 48, rw, bottom - top - 48);
            lblStatus.SetBounds(16, ClientSize.Height - 32, ClientSize.Width - 32, 24);
        }

        // ---------------------------------------------------------------- loading

        private async Task LoadAsync()
        {
            try
            {
                lblStatus.Text = "Reading bundle and resolving scripts...";
                BundleModel m = await Task.Run(() => BundleModel.Load(title, bytes, resolver));
                model = m;
                byPath = new Dictionary<SerializedFile, Dictionary<long, ExplorerItem>>();
                foreach (ExplorerItem it in m.Items)
                {
                    Dictionary<long, ExplorerItem> d;
                    if (!byPath.TryGetValue(it.File, out d)) byPath[it.File] = d = new Dictionary<long, ExplorerItem>();
                    d[it.Info.PathId] = it;
                }
                int classes = m.Items.Where(i => i.Info.ClassId == 114 && i.Script != null).Select(i => i.Script.Full).Distinct().Count();
                string status = m.Items.Count + " objects in " + m.Files.Count + " file(s)  ·  " + classes + " script classes used";
                if (m.Partial > 0) status += "  ·  " + m.Partial + " partly decoded";
                if (m.Failed > 0) status += "  ·  " + m.Failed + " unreadable";
                lblStatus.Text = status;
                BuildTree();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not read bundle.";
                tree.EmptyText = ex.Message;
                MessageBox.Show(this, "Could not read this bundle:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -------------------------------------------------------------------- tree

        private static string Bytes(long b)
        {
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return b + " B";
        }

        private static string ItemDetail(ExplorerItem it)
        {
            if (it.Type == "MonoScript" && it.Script != null) return it.Script.Assembly ?? "";
            if (it.Type == "MonoBehaviour" && !string.IsNullOrEmpty(it.Name) && !string.IsNullOrEmpty(it.Owner)) return "on " + it.Owner;
            return Bytes(it.Info.Size);
        }

        private void BuildTree()
        {
            string q = searchField.Box.Text.Trim();
            bool filtering = q.Length > 0;
            Func<ExplorerItem, bool> match = it =>
                !filtering ||
                it.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                it.Type.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (it.Owner != null && it.Owner.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (it.Info.ClassId == 114 && it.ScriptName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            List<ExplorerItem> shown = model.Items.Where(match).ToList();
            List<TreeItem> roots = new List<TreeItem>();

            // scripts (MonoBehaviour components, grouped by script class)
            var scriptGroups = shown.Where(i => i.Info.ClassId == 114).GroupBy(i => i.ScriptName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (scriptGroups.Count > 0)
            {
                TreeItem root = new TreeItem("Scripts in use", scriptGroups.Count + " classes");
                foreach (var g in scriptGroups)
                {
                    TreeItem cls = new TreeItem(g.Key, g.Count().ToString());
                    foreach (ExplorerItem it in g.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase))
                        cls.Add(new TreeItem(it.Display, ItemDetail(it)) { Tag = it });
                    root.Add(cls);
                }
                root.Expanded = filtering || scriptGroups.Count <= 40;
                foreach (TreeItem c in root.Children) c.Expanded = filtering && c.Children.Count <= 30;
                roots.Add(root);
            }

            // everything else, by Unity type
            foreach (var g in shown.Where(i => i.Info.ClassId != 114).GroupBy(i => i.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                TreeItem t = new TreeItem(g.Key, g.Count().ToString());
                foreach (ExplorerItem it in g.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase))
                    t.Add(new TreeItem(it.Display, ItemDetail(it)) { Tag = it });
                t.Expanded = filtering && t.Children.Count <= 30;
                roots.Add(t);
            }

            // raw files (resource streams etc.)
            if (!filtering && model.Resources.Count > 0)
            {
                TreeItem r = new TreeItem("Other files", model.Resources.Count.ToString());
                foreach (BundleFile bf in model.Resources) r.Add(new TreeItem(bf.Name, Bytes(bf.Data.Length)));
                roots.Add(r);
            }

            tree.EmptyText = filtering ? "Nothing matches your search" : "This bundle is empty";
            tree.SetRoots(roots);
            lblCount.Text = shown.Count + " of " + model.Items.Count + " objects";
        }

        // ------------------------------------------------------------- properties

        private void ShowProperties()
        {
            TreeItem sel = tree.Selected;
            ExplorerItem it = sel != null ? sel.Tag as ExplorerItem : null;
            if (it == null)
            {
                lblInfo.Text = sel != null ? sel.Text : "";
                lblSub.Text = sel != null && sel.Children.Count > 0 ? sel.Children.Count + " item(s)" : "";
                props.EmptyText = "Select an item on the left";
                props.SetRoots(new TreeItem[0]);
                return;
            }

            lblInfo.Text = it.Display + "   ·   " + it.Type;
            string sub = (it.Info.ClassId == 114 ? "Script: " + it.ScriptName + "   ·   " : "")
                       + "Path ID " + it.Info.PathId + "   ·   " + Bytes(it.Info.Size) + "   ·   " + it.File.Name;
            if (it.Script != null && it.Script.Assembly != null && it.Info.ClassId == 114) sub = sub.Replace("Script: " + it.ScriptName, "Script: " + it.ScriptName + " (" + it.Script.Assembly + ")");
            lblSub.Text = sub;

            List<TreeItem> nodes = new List<TreeItem>();
            if (it.Error != null)
            {
                props.EmptyText = "";
                nodes.Add(new TreeItem("Could not decode this object", it.Error) { Bold = true });
            }
            else
            {
                try
                {
                    Value v = it.File.Read(it.Info);
                    if (it.Partial) nodes.Add(new TreeItem("Partly decoded", "this object uses [SerializeReference] data, which isn't fully supported") { Bold = true });
                    if (v.Items != null) foreach (Value c in v.Items) nodes.Add(ToNode(c, it.File));
                }
                catch (Exception ex) { nodes.Add(new TreeItem("Could not decode this object", ex.Message) { Bold = true }); }
            }
            props.SetRoots(nodes);
        }

        private static string Fmt(object p)
        {
            if (p is float) return ((float)p).ToString("0.######", CultureInfo.InvariantCulture);
            if (p is double) return ((double)p).ToString("0.##########", CultureInfo.InvariantCulture);
            if (p is bool) return (bool)p ? "true" : "false";
            if (p is string) { string s = (string)p; return "\"" + (s.Length > 300 ? s.Substring(0, 300) + "..." : s) + "\""; }
            return Convert.ToString(p, CultureInfo.InvariantCulture);
        }

        private string Describe(SerializedFile f, Value v)
        {
            if (v.PathId == 0) return "null";
            if (v.FileId == 0)
            {
                Dictionary<long, ExplorerItem> d; ExplorerItem t;
                if (byPath.TryGetValue(f, out d) && d.TryGetValue(v.PathId, out t))
                    return "→ " + t.Type + (t.Type == "MonoBehaviour" && t.Script != null ? " " + t.Script.Full : "") + " '" + t.Display + "'";
                return "→ local #" + v.PathId;
            }
            if (v.Type == "PPtr<MonoScript>" && resolver != null && v.FileId - 1 < f.Externals.Count)
            {
                ScriptInfo si = resolver.Resolve(f.Externals[v.FileId - 1], v.PathId);
                if (si != null) return "→ " + si.Full + (si.Assembly != null ? "  (" + si.Assembly + ")" : "");
            }
            string ext = v.FileId - 1 < f.Externals.Count ? ScriptResolver.CabOf(f.Externals[v.FileId - 1]) : "file " + v.FileId;
            return "→ external #" + v.PathId + " in " + ext;
        }

        private TreeItem ToNode(Value v, SerializedFile f)
        {
            TreeItem n = new TreeItem(v.Name, null);
            if (v.IsPPtr) n.Detail = Describe(f, v);
            else if (v.IsArray)
            {
                n.Detail = v.Summary ?? (v.Type == "vector" || v.Type == "Array" ? "" : v.Type) + "[" + v.ArrayCount + "]";
                if (v.Items != null)
                {
                    foreach (Value c in v.Items) n.Add(ToNode(c, f));
                    if (v.Summary == null && v.ArrayCount > v.Items.Count) n.Add(new TreeItem("… " + (v.ArrayCount - v.Items.Count) + " more", null));
                }
            }
            else if (v.Prim != null) n.Detail = Fmt(v.Prim);
            else if (v.Summary != null) n.Detail = v.Summary;
            else if (v.Items != null)
            {
                n.Detail = v.Type;
                // vectors, quaternions, colours...: one line with the numbers, no child rows
                if (v.Items.Count > 0 && v.Items.Count <= 4 && v.Items.All(c => c.Prim != null && !(c.Prim is string)))
                    n.Detail = "(" + string.Join(", ", v.Items.Select(c => Fmt(c.Prim)).ToArray()) + ")";
                else
                    foreach (Value c in v.Items) n.Add(ToNode(c, f));
            }
            return n;
        }
    }
}
