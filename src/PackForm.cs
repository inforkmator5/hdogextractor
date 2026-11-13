using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdogExtractor
{
    /// <summary>Dialog that packs your own .bundle files into a new .hdog based on an existing one.</summary>
    internal sealed class PackForm : ChromeForm
    {
        private const int ColFile = 0, ColName = 1, ColAction = 2, ColGuids = 3;

        private readonly Field baseField = new Field();
        private readonly Field outField = new Field();
        private readonly DataGridView grid = new DataGridView();
        private readonly FlatButton btnBaseBrowse = new FlatButton(false);
        private readonly FlatButton btnOutBrowse = new FlatButton(false);
        private readonly FlatButton btnAdd = new FlatButton(false);
        private readonly FlatButton btnRemove = new FlatButton(false);
        private readonly FlatButton btnClose = new FlatButton(false);
        private readonly FlatButton btnPack = new FlatButton(true);
        private readonly FlatBar bar = new FlatBar();
        private readonly Label lblStatus = new Label();

        private HdogArchive baseArc;
        private HashSet<string> baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string autoOut = "";
        private CancellationTokenSource cts;
        private bool busy;

        public PackForm(string basePath) : base(false)
        {
            Text = "Pack bundles into .hdog";
            ClientSize = new Size(860, 560 + TitleH);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AllowDrop = true;

            AddLabel("Base archive", 16, 14, 100);
            Place(baseField, 120, 14, 624, 30, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Place(btnBaseBrowse, 756, 14, 88, 30, AnchorStyles.Top | AnchorStyles.Right); btnBaseBrowse.Text = "Browse";

            AddLabel("Output file", 16, 52, 100);
            Place(outField, 120, 52, 624, 30, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Place(btnOutBrowse, 756, 52, 88, 30, AnchorStyles.Top | AnchorStyles.Right); btnOutBrowse.Text = "Browse";

            AddLabel("Bundles to add or replace", 16, 96, 300);
            Place(btnAdd, 640, 96, 124, 30, AnchorStyles.Top | AnchorStyles.Right); btnAdd.Text = "Add bundles...";
            Place(btnRemove, 772, 96, 72, 30, AnchorStyles.Top | AnchorStyles.Right); btnRemove.Text = "Remove";

            Place(grid, 16, 134, 828, 252, AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            SetupGrid();

            Label note = new Label();
            note.ForeColor = Theme.Muted;
            note.Text = "A name that matches an entry in the base archive replaces it. A new name is appended. "
                      + "To make new assets reachable, list their asset GUIDs (32 hex characters, separated by spaces or commas); "
                      + "they are mapped to the bundle in the manifest. The base archive is never modified.";
            Place(note, 16, 394, 828, 54, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);

            Place(bar, 16, 462, 828, 8, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            Place(lblStatus, 16, 480, 540, 36, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            lblStatus.ForeColor = Theme.Muted; lblStatus.AutoEllipsis = true;
            lblStatus.Text = "Add one or more .bundle files.";
            Place(btnClose, 624, 480, 100, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnClose.Text = "Close";
            Place(btnPack, 734, 480, 110, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnPack.Text = "Pack";

            btnBaseBrowse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "HDOG archives (*.hdog)|*.hdog|All files (*.*)|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) LoadBase(d.FileName);
                }
            };
            btnOutBrowse.Click += delegate
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Filter = "HDOG archives (*.hdog)|*.hdog";
                    d.FileName = outField.Box.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) outField.Box.Text = d.FileName;
                }
            };
            btnAdd.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Unity bundles (*.bundle)|*.bundle|All files (*.*)|*.*";
                    d.Multiselect = true;
                    if (d.ShowDialog(this) == DialogResult.OK) foreach (string f in d.FileNames) AddRow(f);
                }
            };
            btnRemove.Click += delegate
            {
                for (int i = grid.SelectedRows.Count - 1; i >= 0; i--) grid.Rows.Remove(grid.SelectedRows[i]);
            };
            btnClose.Click += delegate { Close(); };
            btnPack.Click += OnPack;
            baseField.Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && File.Exists(baseField.Box.Text)) { e.SuppressKeyPress = true; LoadBase(baseField.Box.Text); }
            };
            DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                foreach (string f in files)
                {
                    if (f.EndsWith(".hdog", StringComparison.OrdinalIgnoreCase)) LoadBase(f);
                    else AddRow(f);
                }
            };

            if (!string.IsNullOrEmpty(basePath)) Shown += delegate { LoadBase(basePath); };
        }

        // --------------------------------------------------------------------- grid

        private void SetupGrid()
        {
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 34;
            grid.RowTemplate.Height = 32;
            grid.BackgroundColor = Theme.Bg;
            grid.GridColor = Theme.Border;
            grid.DefaultCellStyle.BackColor = Theme.Bg;
            grid.DefaultCellStyle.ForeColor = Theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = Theme.AccentDim;
            grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.RowAlt;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            AddColumn("Bundle file", 30, true);
            AddColumn("Name in archive", 30, false);
            AddColumn("Action", 12, true);
            AddColumn("Asset GUIDs (optional)", 28, false);

            grid.CellValueChanged += delegate (object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == ColName) RefreshAction(grid.Rows[e.RowIndex]); };
            grid.EditingControlShowing += delegate (object s, DataGridViewEditingControlShowingEventArgs e)
            {
                TextBox tb = e.Control as TextBox;
                if (tb != null) { tb.BackColor = Theme.Field; tb.ForeColor = Theme.Text; tb.BorderStyle = BorderStyle.None; }
            };
        }

        private void AddColumn(string header, float weight, bool readOnly)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.ReadOnly = readOnly;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(c);
        }

        private void AddRow(string path)
        {
            foreach (DataGridViewRow r in grid.Rows)
                if (string.Equals((string)r.Tag, path, StringComparison.OrdinalIgnoreCase)) return;
            int i = grid.Rows.Add(Path.GetFileName(path), Path.GetFileNameWithoutExtension(path), "", "");
            grid.Rows[i].Tag = path;
            RefreshAction(grid.Rows[i]);
            lblStatus.Text = grid.Rows.Count + " bundle(s) queued.";
        }

        private void RefreshAction(DataGridViewRow row)
        {
            string name = Convert.ToString(row.Cells[ColName].Value ?? "").Trim();
            string action = name.Length == 0 ? "-" : (baseNames.Contains(name) ? "Replace" : "Add new");
            row.Cells[ColAction].Value = action;
            row.Cells[ColAction].Style.ForeColor = action == "Replace" ? Theme.Accent : Theme.Muted;
        }

        // ---------------------------------------------------------------- base archive

        private void LoadBase(string path)
        {
            try
            {
                baseArc = HdogArchive.Open(path);
                baseField.Box.Text = path;
                baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (HdogEntry e in baseArc.Entries) baseNames.Add(e.Name);
                foreach (DataGridViewRow r in grid.Rows) RefreshAction(r);

                string suggested = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_modded.hdog");
                if (outField.Box.Text.Length == 0 || outField.Box.Text == autoOut) outField.Box.Text = suggested;
                autoOut = suggested;
                lblStatus.Text = "Base: " + baseArc.Entries.Count + " entries.";
            }
            catch (Exception ex)
            {
                baseArc = null;
                MessageBox.Show(this, "Could not read the base archive:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -------------------------------------------------------------------- packing

        private async void OnPack(object sender, EventArgs e)
        {
            grid.EndEdit();
            if (!string.IsNullOrEmpty(baseField.Box.Text) && (baseArc == null || baseArc.Path != baseField.Box.Text) && File.Exists(baseField.Box.Text))
                LoadBase(baseField.Box.Text);
            if (baseArc == null) { MessageBox.Show(this, "Choose a base .hdog first.", Text); return; }
            if (grid.Rows.Count == 0) { MessageBox.Show(this, "Add at least one .bundle.", Text); return; }
            string outPath = outField.Box.Text.Trim();
            if (outPath.Length == 0) { MessageBox.Show(this, "Choose an output file.", Text); return; }
            if (File.Exists(outPath) &&
                MessageBox.Show(this, outPath + "\n\nalready exists. Overwrite it?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            List<PackItem> items = new List<PackItem>();
            foreach (DataGridViewRow r in grid.Rows)
                items.Add(new PackItem
                {
                    SourcePath = (string)r.Tag,
                    Name = Convert.ToString(r.Cells[ColName].Value ?? ""),
                    Guids = Convert.ToString(r.Cells[ColGuids].Value ?? "")
                });

            HdogArchive arc = baseArc;
            SetBusy(true);
            bar.Value = 0;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            Progress<ExtractProgress> progress = new Progress<ExtractProgress>(delegate (ExtractProgress p)
            {
                bar.Value = p.BytesTotal > 0 ? (int)(100L * p.BytesDone / p.BytesTotal) : 100;
                lblStatus.Text = "[" + p.FilesDone + "/" + p.FilesTotal + "]  " + p.Current + "   "
                    + EntryList.FormatSize(p.BytesDone) + " / " + EntryList.FormatSize(p.BytesTotal);
            });
            try
            {
                PackResult res = await Task.Run(() => arc.Pack(items, outPath, progress, token));
                bar.Value = 100;
                lblStatus.Text = "Done: " + outPath;
                string msg = "Packed " + (res.Replaced + res.Added) + " bundle(s): " + res.Replaced + " replaced, " + res.Added + " added.\n"
                           + "Output: " + outPath + " (" + EntryList.FormatSize(res.OutputSize) + ")";
                if (res.Warnings.Count > 0) msg += "\n\nNote:\n- " + string.Join("\n- ", res.Warnings.ToArray());
                MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, res.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { lblStatus.Text = "Cancelled."; bar.Value = 0; }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Packing failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { cts.Dispose(); cts = null; SetBusy(false); }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            baseField.Enabled = outField.Enabled = grid.Enabled = !value;
            btnBaseBrowse.Enabled = btnOutBrowse.Enabled = btnAdd.Enabled = btnRemove.Enabled = btnPack.Enabled = !value;
            btnClose.Text = value ? "Cancel" : "Close";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && cts != null) { cts.Cancel(); e.Cancel = true; return; }   // Cancel first; close once it finishes
            base.OnFormClosing(e);
        }
    }
}
