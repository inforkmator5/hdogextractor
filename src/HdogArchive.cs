using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HdogExtractor
{
    /// <summary>A user-supplied .bundle to put into a packed archive.</summary>
    public sealed class PackItem
    {
        public string SourcePath;
        public string Name;     // entry name inside the archive; an existing name means "replace"
        public string Guids;    // optional asset GUIDs (32 hex chars) to map to this bundle in the manifest
    }

    public sealed class PackResult
    {
        public int Replaced;
        public int Added;
        public long OutputSize;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>One file stored inside an .hdog archive.</summary>
    public sealed class HdogEntry
    {
        public string Name;       // logical name from the footer, e.g. "footlong.base_Home"
        public string Tag;        // INFO = manifest, UABP/UABR = Unity bundle, FMBK = FMOD bank, ...
        public long Offset;
        public long Size;
        public string FileName;   // unique, filesystem-safe output name incl. extension

        public bool IsManifest { get { return Tag == "INFO"; } }

        public string TypeLabel
        {
            get { return Path.GetExtension(FileName).TrimStart('.'); }
        }
    }

    public sealed class ExtractProgress
    {
        public int FilesDone;
        public int FilesTotal;
        public long BytesDone;
        public long BytesTotal;
        public string Current;
    }

    /// <summary>
    /// Reader/extractor for the .hdog container.
    ///
    /// Layout (little-endian):
    ///   "HDOG\0"                                   5-byte magic
    ///   payloads                                   gzip'd JSON manifest, then raw Unity bundles / FMOD banks
    ///   footer entries:  u32 offset, u32 size, u8 nameLen, name[nameLen], char[4] tag
    ///   u32 entryCount, u32 footerOffset           last 8 bytes of the file
    ///
    /// If the footer is missing or inconsistent, the reader falls back to walking
    /// consecutive UnityFS bundles after the manifest.
    /// </summary>
    public sealed class HdogArchive
    {
        public string Path { get; private set; }
        public long FileLength { get; private set; }
        public bool UsedFooter { get; private set; }
        public HdogEntry Manifest { get; private set; }
        public List<HdogEntry> Entries { get; private set; }

        private HdogArchive(string path)
        {
            Path = path;
            Entries = new List<HdogEntry>();
        }

        public static HdogArchive Open(string path)
        {
            HdogArchive a = new HdogArchive(path);
            using (FileStream fs = OpenRead(path))
            {
                a.FileLength = fs.Length;
                byte[] magic = ReadAt(fs, 0, 5);
                if (magic[0] != 'H' || magic[1] != 'D' || magic[2] != 'O' || magic[3] != 'G' || magic[4] != 0)
                    throw new InvalidDataException("Not an HDOG file (bad magic).");

                List<HdogEntry> list = TryReadFooter(fs);
                a.UsedFooter = list != null;
                if (list == null) list = WalkBundles(fs);

                foreach (HdogEntry e in list)
                {
                    if (e.IsManifest && a.Manifest == null) a.Manifest = e;
                    else a.Entries.Add(e);
                }
                AssignFileNames(fs, a.Entries);
            }
            return a;
        }

        // ---------------------------------------------------------------- parsing

        private static List<HdogEntry> TryReadFooter(FileStream fs)
        {
            long len = fs.Length;
            if (len < 20) return null;
            byte[] tail = ReadAt(fs, len - 8, 8);
            uint count = BitConverter.ToUInt32(tail, 0);
            long footerOffset = BitConverter.ToUInt32(tail, 4);
            if (count < 1 || count > 1000000 || footerOffset >= len - 8) return null;

            byte[] blob = ReadAt(fs, footerOffset, (int)(len - 8 - footerOffset));
            List<HdogEntry> list = new List<HdogEntry>();
            int p = 0;
            for (uint i = 0; i < count; i++)
            {
                if (p + 9 > blob.Length) return null;
                long off = BitConverter.ToUInt32(blob, p);
                long size = BitConverter.ToUInt32(blob, p + 4);
                int nl = blob[p + 8];
                p += 9;
                if (p + nl + 4 > blob.Length) return null;
                string name = Encoding.UTF8.GetString(blob, p, nl); p += nl;
                string tag = Encoding.ASCII.GetString(blob, p, 4); p += 4;
                if (off + size > footerOffset) return null;
                list.Add(new HdogEntry { Name = name, Tag = tag, Offset = off, Size = size });
            }
            return p == blob.Length ? list : null;
        }

        /// <summary>Fallback: manifest gzip, then UnityFS bundles back-to-back (size = BE int64 in each header).</summary>
        private static List<HdogEntry> WalkBundles(FileStream fs)
        {
            int headLen = (int)Math.Min(1024 * 1024, fs.Length - 5);
            byte[] head = ReadAt(fs, 5, headLen);
            long first = -1;
            for (int i = 0; i < head.Length - 8; i++)
            {
                if (head[i] == 'U' && head[i + 1] == 'n' && UnityFsSize(fs, 5 + i) > 0) { first = 5 + i; break; }
            }
            if (first < 0) throw new InvalidDataException("No footer and no UnityFS bundle found.");

            List<HdogEntry> list = new List<HdogEntry>();
            list.Add(new HdogEntry { Name = "manifest", Tag = "INFO", Offset = 5, Size = first - 5 });
            long pos = first;
            int idx = 0;
            while (pos < fs.Length)
            {
                long size = UnityFsSize(fs, pos);
                if (size <= 0) break; // unknown trailing data
                list.Add(new HdogEntry { Name = "bundle_" + idx.ToString("D3"), Tag = "UABR", Offset = pos, Size = size });
                pos += size;
                idx++;
            }
            return list;
        }

        /// <summary>Total bundle size from a UnityFS header at pos, or -1 if there is no valid bundle.</summary>
        private static long UnityFsSize(FileStream fs, long pos)
        {
            if (pos + 40 > fs.Length) return -1;
            int n = (int)Math.Min(128, fs.Length - pos);
            byte[] b = ReadAt(fs, pos, n);
            if (Encoding.ASCII.GetString(b, 0, 7) != "UnityFS" || b[7] != 0) return -1;
            int p = 12;                                   // skip magic + format version
            while (p < n && b[p] != 0) p++; p++;          // "5.x.x"
            while (p < n && b[p] != 0) p++; p++;          // engine version
            if (p + 8 > n) return -1;
            long size = 0;
            for (int i = 0; i < 8; i++) size = (size << 8) | b[p + i];
            return (size > 0 && pos + size <= fs.Length) ? size : -1;
        }

        private static void AssignFileNames(FileStream fs, List<HdogEntry> entries)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (HdogEntry e in entries)
            {
                StringBuilder sb = new StringBuilder(e.Name);
                for (int i = 0; i < sb.Length; i++) if (Array.IndexOf(bad, sb[i]) >= 0) sb[i] = '_';
                string safe = sb.Length == 0 ? "unnamed" : sb.ToString();
                string ext = DetectExtension(fs, e);
                string fn = safe + ext;
                for (int n = 1; !used.Add(fn); n++) fn = safe + "_" + n + ext;
                e.FileName = fn;
            }
        }

        private static string DetectExtension(FileStream fs, HdogEntry e)
        {
            if (e.Size < 8) return ".bin";
            string m = Encoding.ASCII.GetString(ReadAt(fs, e.Offset, 8));
            if (m.StartsWith("UnityFS", StringComparison.Ordinal)) return ".bundle";
            if (m.StartsWith("RIFF", StringComparison.Ordinal)) return ".bank";
            return ".bin";
        }

        // --------------------------------------------------------------- manifest

        /// <summary>Decoded manifest JSON (asset GUID -> bundle map, dependencies), or null if absent.</summary>
        public string ReadManifestJson()
        {
            if (Manifest == null) return null;
            using (FileStream fs = OpenRead(Path))
            {
                byte[] raw = ReadAt(fs, Manifest.Offset, (int)Manifest.Size);
                using (MemoryStream ms = new MemoryStream(raw))
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    gz.CopyTo(o);
                    string s = Encoding.UTF8.GetString(o.ToArray());
                    return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
                }
            }
        }

        // ------------------------------------------------------------- reading entries

        /// <summary>The whole payload of an entry as bytes.</summary>
        public byte[] ReadEntry(HdogEntry e)
        {
            using (FileStream fs = OpenRead(Path)) return ReadAt(fs, e.Offset, checked((int)e.Size));
        }

        /// <summary>The first <paramref name="max"/> bytes (or fewer, for small entries) of an entry.</summary>
        public byte[] ReadEntryHead(HdogEntry e, int max)
        {
            using (FileStream fs = OpenRead(Path)) return ReadAt(fs, e.Offset, (int)Math.Min(max, e.Size));
        }

        // ------------------------------------------------------------- extraction

        /// <summary>
        /// Writes manifest.json, index.json and the selected entries to
        /// &lt;outRoot&gt;\&lt;archive name&gt;\. Returns that folder. Throws OperationCanceledException on cancel.
        /// </summary>
        public string Extract(IList<HdogEntry> selected, string outRoot, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            string dest = System.IO.Path.Combine(outRoot, System.IO.Path.GetFileNameWithoutExtension(Path));
            Directory.CreateDirectory(dest);

            string manifest = ReadManifestJson();
            if (manifest != null)
                File.WriteAllText(System.IO.Path.Combine(dest, "manifest.json"), manifest, new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(dest, "index.json"), BuildIndexJson(), new UTF8Encoding(false));

            ExtractProgress state = new ExtractProgress { FilesTotal = selected.Count };
            foreach (HdogEntry e in selected) state.BytesTotal += e.Size;
            if (progress != null) progress.Report(Snapshot(state));

            byte[] buf = new byte[1024 * 1024];
            using (FileStream src = OpenRead(Path))
            {
                foreach (HdogEntry e in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    state.Current = e.FileName;
                    string target = System.IO.Path.Combine(dest, e.FileName);
                    bool finished = false;
                    try
                    {
                        src.Position = e.Offset;
                        using (FileStream outFs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            long left = e.Size;
                            while (left > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                int r = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (r <= 0) throw new EndOfStreamException("Unexpected end of file while reading " + e.Name);
                                outFs.Write(buf, 0, r);
                                left -= r;
                                state.BytesDone += r;
                                if (progress != null) progress.Report(Snapshot(state));
                            }
                        }
                        finished = true;
                    }
                    finally
                    {
                        if (!finished) { try { File.Delete(target); } catch (IOException) { } }
                    }
                    state.FilesDone++;
                    if (progress != null) progress.Report(Snapshot(state));
                }
            }
            return dest;
        }

        // ---------------------------------------------------------------- packing

        /// <summary>
        /// Writes a new .hdog made of this archive with the given bundles replaced (same name) or appended (new name).
        /// The manifest is rewritten; GUIDs given for an item are mapped to its bundle in AssetToBundleMap.
        /// Everything else is copied byte-for-byte. The base archive is never modified.
        /// </summary>
        public PackResult Pack(IList<PackItem> items, string outPath, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            PackResult result = new PackResult();
            if (items == null || items.Count == 0) throw new ArgumentException("Add at least one bundle.");
            if (string.Equals(System.IO.Path.GetFullPath(outPath), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output file must be different from the base archive.");

            // ---- validate
            Dictionary<string, PackItem> byName = new Dictionary<string, PackItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HdogEntry> baseByName = new Dictionary<string, HdogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (HdogEntry e in Entries) baseByName[e.Name] = e;
            foreach (PackItem it in items)
            {
                string nm = (it.Name ?? "").Trim();
                it.Name = nm;
                if (nm.Length == 0) throw new InvalidDataException("A bundle has no name.");
                if (Encoding.UTF8.GetByteCount(nm) > 255) throw new InvalidDataException("Name too long (max 255 bytes): " + nm);
                if (byName.ContainsKey(nm)) throw new InvalidDataException("Two bundles use the same name: " + nm);
                if (!File.Exists(it.SourcePath)) throw new FileNotFoundException("File not found.", it.SourcePath);
                FileInfo fi = new FileInfo(it.SourcePath);
                if (fi.Length < 8) throw new InvalidDataException(fi.Name + " is not a Unity bundle.");
                using (FileStream t = OpenRead(it.SourcePath))
                    if (Encoding.ASCII.GetString(ReadAt(t, 0, 7)) != "UnityFS")
                        throw new InvalidDataException(fi.Name + " is not a Unity bundle (missing UnityFS header).");
                byName[nm] = it;
            }

            // ---- manifest
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 200;
            string json = ReadManifestJson();
            Dictionary<string, object> root = json != null ? ser.DeserializeObject(json) as Dictionary<string, object> : null;
            if (root == null)
            {
                root = new Dictionary<string, object>();
                root["ModuleNamespace"] = System.IO.Path.GetFileNameWithoutExtension(Path);
                root["FileIndex"] = 0;
            }
            if (!(root.ContainsKey("AssetToBundleMap") && root["AssetToBundleMap"] is Dictionary<string, object>))
                root["AssetToBundleMap"] = new Dictionary<string, object>();
            if (!root.ContainsKey("BundleDependencyMap")) root["BundleDependencyMap"] = new Dictionary<string, object>();
            Dictionary<string, object> map = (Dictionary<string, object>)root["AssetToBundleMap"];

            foreach (PackItem it in items)
            {
                bool replace = baseByName.ContainsKey(it.Name);
                if (replace) { result.Replaced++; it.Name = baseByName[it.Name].Name; } else result.Added++;
                int mapped = 0;
                foreach (string raw in Regex.Split(it.Guids ?? "", @"[\s,;]+"))
                {
                    if (raw.Length == 0) continue;
                    if (!Regex.IsMatch(raw, "^[0-9a-fA-F]{32}$")) throw new InvalidDataException("Not a valid asset GUID (need 32 hex characters): " + raw);
                    map[raw.ToLowerInvariant()] = it.Name;
                    mapped++;
                }
                if (!replace && mapped == 0)
                    result.Warnings.Add("\"" + it.Name + "\" is new and has no asset GUIDs, so nothing in the manifest points at it.");
            }
            byte[] manifestGz;
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    byte[] bom = { 0xEF, 0xBB, 0xBF };
                    gz.Write(bom, 0, 3);
                    byte[] body = Encoding.UTF8.GetBytes(ser.Serialize(root));
                    gz.Write(body, 0, body.Length);
                }
                manifestGz = ms.ToArray();
            }

            // ---- plan the output (order: manifest, base entries in order, then new bundles)
            List<HdogEntry> plan = new List<HdogEntry>();        // Offset/Size filled while writing
            List<string> sources = new List<string>();           // null = copy from base, else path of replacement file
            foreach (HdogEntry e in Entries)
            {
                PackItem rep;
                if (byName.TryGetValue(e.Name, out rep) && string.Equals(rep.Name, e.Name, StringComparison.Ordinal))
                { plan.Add(new HdogEntry { Name = e.Name, Tag = e.Tag }); sources.Add(rep.SourcePath); }
                else { plan.Add(new HdogEntry { Name = e.Name, Tag = e.Tag, Offset = e.Offset, Size = e.Size }); sources.Add(null); }
            }
            foreach (PackItem it in items)
                if (!baseByName.ContainsKey(it.Name)) { plan.Add(new HdogEntry { Name = it.Name, Tag = "UABR" }); sources.Add(it.SourcePath); }

            long total = manifestGz.Length;
            for (int i = 0; i < plan.Count; i++)
                total += sources[i] != null ? new FileInfo(sources[i]).Length : plan[i].Size;
            if (5 + total + 9L * plan.Count > uint.MaxValue)
                throw new InvalidDataException("The archive would be larger than 4 GB, which the .hdog format can't address.");

            // ---- write
            string temp = outPath + ".tmp";
            ExtractProgress state = new ExtractProgress { FilesTotal = plan.Count, BytesTotal = total };
            byte[] buf = new byte[1024 * 1024];
            try
            {
                using (FileStream src = OpenRead(Path))
                using (FileStream o = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    o.Write(new byte[] { (byte)'H', (byte)'D', (byte)'O', (byte)'G', 0 }, 0, 5);
                    HdogEntry man = new HdogEntry { Name = Manifest != null ? Manifest.Name : "manifest", Tag = "INFO", Offset = 5, Size = manifestGz.Length };
                    o.Write(manifestGz, 0, manifestGz.Length);
                    state.BytesDone += manifestGz.Length;

                    for (int i = 0; i < plan.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        HdogEntry pe = plan[i];
                        state.Current = pe.Name;
                        long start = o.Position;
                        if (sources[i] != null)
                        {
                            using (FileStream rf = OpenRead(sources[i]))
                                CopyStream(rf, o, rf.Length, buf, ct, state, progress);
                            pe.Size = o.Position - start;
                        }
                        else
                        {
                            src.Position = pe.Offset;
                            CopyStream(src, o, pe.Size, buf, ct, state, progress);
                        }
                        pe.Offset = start;
                        state.FilesDone++;
                        if (progress != null) progress.Report(Snapshot(state));
                    }

                    long footerOffset = o.Position;
                    using (BinaryWriter w = new BinaryWriter(o, Encoding.UTF8, true))
                    {
                        List<HdogEntry> all = new List<HdogEntry>();
                        all.Add(man); all.AddRange(plan);
                        foreach (HdogEntry e in all)
                        {
                            byte[] nb = Encoding.UTF8.GetBytes(e.Name);
                            w.Write((uint)e.Offset); w.Write((uint)e.Size); w.Write((byte)nb.Length); w.Write(nb);
                            w.Write(Encoding.ASCII.GetBytes(e.Tag.PadRight(4).Substring(0, 4)));
                        }
                        w.Write((uint)all.Count);
                        w.Write((uint)footerOffset);
                    }
                }

                // sanity check: the file we wrote must read back with the expected entries
                HdogArchive check = HdogArchive.Open(temp);
                if (!check.UsedFooter || check.Entries.Count != plan.Count)
                    throw new InvalidDataException("Verification of the written archive failed.");
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(temp, outPath);
            }
            finally
            {
                if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } }
            }
            result.OutputSize = new FileInfo(outPath).Length;
            return result;
        }

        private static void CopyStream(Stream from, Stream to, long count, byte[] buf, CancellationToken ct, ExtractProgress state, IProgress<ExtractProgress> progress)
        {
            long left = count;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int r = from.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (r <= 0) throw new EndOfStreamException("Unexpected end of file while copying.");
                to.Write(buf, 0, r);
                left -= r;
                state.BytesDone += r;
                if (progress != null) progress.Report(Snapshot(state));
            }
        }

        private static ExtractProgress Snapshot(ExtractProgress s)
        {
            return new ExtractProgress { FilesDone = s.FilesDone, FilesTotal = s.FilesTotal, BytesDone = s.BytesDone, BytesTotal = s.BytesTotal, Current = s.Current };
        }

        private string BuildIndexJson()
        {
            StringBuilder sb = new StringBuilder("[\n");
            for (int i = 0; i < Entries.Count; i++)
            {
                HdogEntry e = Entries[i];
                sb.Append("  {\"name\": ").Append(Quote(e.Name))
                  .Append(", \"tag\": ").Append(Quote(e.Tag))
                  .Append(", \"offset\": ").Append(e.Offset)
                  .Append(", \"size\": ").Append(e.Size)
                  .Append(", \"file\": ").Append(Quote(e.FileName)).Append('}')
                  .Append(i < Entries.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("]\n").ToString();
        }

        private static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ------------------------------------------------------------------ io

        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }

        private static byte[] ReadAt(FileStream fs, long offset, int count)
        {
            fs.Position = offset;
            byte[] b = new byte[count];
            int got = 0;
            while (got < count)
            {
                int r = fs.Read(b, got, count - got);
                if (r <= 0) throw new EndOfStreamException("Unexpected end of file at offset " + offset);
                got += r;
            }
            return b;
        }
    }
}
