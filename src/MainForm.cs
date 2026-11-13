using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdogExtractor
{
    public sealed class MainForm : ChromeForm
    {
        private const int BannerH = 70;                      // logo banner under the title bar

        private readonly Field fileField = new Field();
        private readonly Field outField = new Field();
        private readonly Field filterField = new Field();
        private readonly Label lblCount = new Label();
        private readonly Label lblStatus = new Label();
        private readonly EntryList list = new EntryList();
        private readonly FlatBar bar = new FlatBar();
        private readonly FlatButton btnBrowse = new FlatButton(false);
        private readonly FlatButton btnOut = new FlatButton(false);
        private readonly FlatButton btnAll = new FlatButton(false);
        private readonly FlatButton btnNone = new FlatButton(false);
        private readonly FlatButton btnPack = new FlatButton(false);
        private readonly FlatButton btnExplore = new FlatButton(false);
        private ScriptResolver resolver;
        private readonly FlatButton btnCancel = new FlatButton(false);
        private readonly FlatButton btnExtract = new FlatButton(true);

        private HdogArchive archive;
        private CancellationTokenSource cts;
        private bool busy;

        public MainForm(string initialFile) : base(true)
        {
            Text = "HdogExtractor";
            ContentTop = TitleH + BannerH;
            ClientSize = new Size(900, 600 + ContentTop);
            MinimumSize = new Size(760, 480 + ContentTop);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            AddBanner();

            AddLabel("HDOG file", 16, 14, 90);
            Place(fileField, 108, 14, 668, 30, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Place(btnBrowse, 788, 14, 96, 30, AnchorStyles.Top | AnchorStyles.Right); btnBrowse.Text = "Browse";

            AddLabel("Output folder", 16, 52, 90);
            Place(outField, 108, 52, 668, 30, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            outField.Box.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extracted");
            Place(btnOut, 788, 52, 96, 30, AnchorStyles.Top | AnchorStyles.Right); btnOut.Text = "Browse";

            AddLabel("Filter", 16, 90, 90);
            Place(filterField, 108, 90, 300, 30, AnchorStyles.Top | AnchorStyles.Left);
            Place(btnAll, 420, 90, 96, 30, AnchorStyles.Top | AnchorStyles.Left); btnAll.Text = "Select all";
            Place(btnNone, 522, 90, 104, 30, AnchorStyles.Top | AnchorStyles.Left); btnNone.Text = "Select none";
            Place(lblCount, 636, 96, 248, 20, AnchorStyles.Top | AnchorStyles.Right);
            lblCount.TextAlign = ContentAlignment.MiddleRight;
            lblCount.ForeColor = Theme.Muted;

            Place(list, 16, 132, 868, 366, AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);

            Place(bar, 16, 512, 868, 8, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            Place(lblStatus, 16, 530, 400, 36, AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.AutoEllipsis = true;
            lblStatus.Text = "Choose an .hdog file, or drag one onto this window.";
            Place(btnExplore, 428, 530, 110, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnExplore.Text = "Explore...";
            Place(btnPack, 548, 530, 110, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnPack.Text = "Pack...";
            Place(btnCancel, 668, 530, 100, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnCancel.Text = "Cancel"; btnCancel.Enabled = false;
            Place(btnExtract, 778, 530, 106, 34, AnchorStyles.Bottom | AnchorStyles.Right); btnExtract.Text = "Extract"; btnExtract.Enabled = false;

            btnBrowse.Click += OnBrowseFile;
            btnOut.Click += OnBrowseOut;
            btnAll.Click += delegate { list.SetAllChecked(true); };
            btnNone.Click += delegate { list.SetAllChecked(false); };
            btnPack.Click += OnPack;
            btnExplore.Click += OnExplore;
            list.OpenRequested += delegate (object s, EntryEventArgs e) { ExploreEntry(e.Entry); };
            list.FocusedChanged += delegate
            {
                if (list.FocusedEntry != null && !busy) lblStatus.Text = list.FocusedEntry.Name + "  ·  double-click or press Explore to look inside";
            };
            btnCancel.Click += delegate { if (cts != null) cts.Cancel(); btnCancel.Enabled = false; };
            btnExtract.Click += OnExtract;
            list.SelectionChanged += delegate { UpdateCount(); };
            filterField.Box.TextChanged += delegate { if (archive != null) ApplyFilter(); };
            fileField.Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && File.Exists(fileField.Box.Text)) { e.SuppressKeyPress = true; OpenArchive(fileField.Box.Text); }
            };
            DragEnter += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) OpenArchive(files[0]);
            };

            UpdateCount();
            if (!string.IsNullOrEmpty(initialFile) && File.Exists(initialFile))
                Shown += delegate { OpenArchive(Path.GetFullPath(initialFile)); };
        }

        private void AddBanner()
        {
            PictureBox pb = new PictureBox();
            pb.Location = new Point(8, TitleH);
            pb.Size = new Size(ClientSize.Width - 16, BannerH);
            pb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pb.BackColor = Theme.Bg;
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("splash.jpg"))
                using (Bitmap full = new Bitmap(s))
                    pb.Image = full.Clone(new Rectangle(70, 250, 674, 75), full.PixelFormat);
            }
            catch (Exception) { /* banner is cosmetic */ }
            MakeDraggable(pb);
            Controls.Add(pb);
        }

        // ---------------------------------------------------------------- behaviour

        private void OnBrowseFile(object sender, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "HDOG archives (*.hdog)|*.hdog|All files (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
            }
        }

        private void OnBrowseOut(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.SelectedPath = outField.Box.Text;
                if (d.ShowDialog(this) == DialogResult.OK) outField.Box.Text = d.SelectedPath;
            }
        }

        private ScriptResolver GetResolver()
        {
            if (archive == null) return null;
            if (resolver == null) resolver = new ScriptResolver(archive);
            return resolver;
        }

        private void ExploreEntry(HdogEntry entry)
        {
            if (archive == null || entry == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                byte[] head = archive.ReadEntryHead(entry, 8);
                if (head.Length < 8 || System.Text.Encoding.ASCII.GetString(head, 0, 7) != "UnityFS")
                {
                    MessageBox.Show(this, "\"" + entry.Name + "\" is not a Unity bundle, so there is nothing to explore.\n(Extract it instead.)", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                new ExplorerForm(entry.Name, archive.ReadEntry(entry), GetResolver()).Show(this);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open entry", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Cursor = Cursors.Default; }
        }

        private void OnExplore(object sender, EventArgs e)
        {
            if (archive != null && list.FocusedEntry != null) { ExploreEntry(list.FocusedEntry); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = archive == null ? "Choose a .bundle to explore" : "Choose a .bundle to explore (or click a row first to explore an entry of the open archive)";
                d.Filter = "Unity bundles (*.bundle)|*.bundle|All files (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    new ExplorerForm(Path.GetFileName(d.FileName), File.ReadAllBytes(d.FileName), GetResolver()).Show(this);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open bundle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        private void OnPack(object sender, EventArgs e)
        {
            using (PackForm pf = new PackForm(archive != null ? archive.Path : null))
                pf.ShowDialog(this);
        }

        private void OpenArchive(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                lblStatus.Text = "Reading index...";
                Refresh();
                archive = HdogArchive.Open(path);
                resolver = null;
                fileField.Box.Text = path;
                list.Filter = filterField.Box.Text;
                list.SetEntries(archive.Entries);
                ApplyFilter();
                long total = 0;
                foreach (HdogEntry en in archive.Entries) total += en.Size;
                lblStatus.Text = archive.Entries.Count + " entries, " + EntryList.FormatSize(total) + " total"
                    + (archive.UsedFooter ? "." : " (footer missing; index rebuilt by scanning).");
            }
            catch (Exception ex)
            {
                archive = null;
                list.SetEntries(new HdogEntry[0]);
                list.EmptyText = "Drop an .hdog file here";
                lblStatus.Text = "Failed to read file.";
                MessageBox.Show(this, "Could not read file:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ApplyFilter()
        {
            list.Filter = filterField.Box.Text;
            list.EmptyText = archive != null && archive.Entries.Count == 0 ? "This archive is empty" : "No entries match the filter";
        }

        private void UpdateCount()
        {
            lblCount.Text = list.CheckedCount + " of " + list.RowCount + " selected  ·  " + EntryList.FormatSize(list.CheckedBytes);
            btnExtract.Enabled = list.CheckedCount > 0 && !busy;
        }

        private async void OnExtract(object sender, EventArgs e)
        {
            if (archive == null) return;
            List<HdogEntry> selected = list.GetChecked();
            if (selected.Count == 0) return;

            HdogArchive arc = archive;
            string outRoot = outField.Box.Text.Trim();
            if (outRoot.Length == 0) { MessageBox.Show(this, "Choose an output folder.", Text); return; }

            SetBusy(true);
            bar.Value = 0;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            Progress<ExtractProgress> progress = new Progress<ExtractProgress>(delegate (ExtractProgress p)
            {
                bar.Value = p.BytesTotal > 0 ? (int)(100L * p.BytesDone / p.BytesTotal) : 100;
                lblStatus.Text = "[" + p.FilesDone + "/" + p.FilesTotal + "]  " + p.Current
                    + "   " + EntryList.FormatSize(p.BytesDone) + " / " + EntryList.FormatSize(p.BytesTotal);
            });

            try
            {
                string dest = await Task.Run(() => arc.Extract(selected, outRoot, progress, token));
                bar.Value = 100;
                lblStatus.Text = "Done. Extracted " + selected.Count + " files to " + dest;
                if (MessageBox.Show(this, "Extracted " + selected.Count + " files to:\n" + dest + "\n\nOpen the folder?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("explorer.exe", "\"" + dest + "\"");
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                cts.Dispose();
                cts = null;
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            btnBrowse.Enabled = btnOut.Enabled = btnAll.Enabled = btnNone.Enabled = btnPack.Enabled = btnExplore.Enabled = !value;
            fileField.Enabled = outField.Enabled = filterField.Enabled = list.Enabled = !value;
            btnCancel.Enabled = value;
            UpdateCount();
        }
    }
}
