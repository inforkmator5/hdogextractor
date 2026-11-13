using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdogExtractor
{
    internal sealed class ScriptInfo
    {
        public string Namespace, ClassName, Assembly;
        public string Full { get { return string.IsNullOrEmpty(Namespace) ? ClassName : Namespace + "." + ClassName; } }
    }

    /// <summary>
    /// Finds MonoScript names for MonoBehaviours whose script lives in another bundle of the same archive.
    /// Bundles refer to each other by CAB name (e.g. archive:/CAB-xxxx/CAB-xxxx).
    /// </summary>
    internal sealed class ScriptResolver
    {
        private readonly HdogArchive archive;
        private readonly object gate = new object();
        private Dictionary<string, HdogEntry> cabIndex;
        private readonly Dictionary<string, Dictionary<long, ScriptInfo>> scriptsByCab = new Dictionary<string, Dictionary<long, ScriptInfo>>();

        public ScriptResolver(HdogArchive archive) { this.archive = archive; }

        public static string CabOf(string externalPath)
        {
            int i = externalPath.LastIndexOf('/');
            return i >= 0 ? externalPath.Substring(i + 1) : externalPath;
        }

        private void EnsureIndex()
        {
            if (cabIndex != null) return;
            Dictionary<string, HdogEntry> idx = new Dictionary<string, HdogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (HdogEntry e in archive.Entries)
            {
                if (e.Size < 64) continue;
                foreach (int max in new int[] { 16 * 1024, 256 * 1024 })
                {
                    byte[] head = archive.ReadEntryHead(e, max);
                    if (head.Length < 8 || head[0] != (byte)'U' || head[1] != (byte)'n') break;
                    List<string> names = UnityBundle.PeekNames(head);
                    if (names == null) { if (head.Length < max) break; continue; }
                    foreach (string n in names) if (!idx.ContainsKey(n)) idx[n] = e;
                    break;
                }
            }
            cabIndex = idx;
        }

        /// <summary>Resolves a script by the external file path and path id. Returns null if unknown.</summary>
        public ScriptInfo Resolve(string externalPath, long pathId)
        {
            lock (gate)
            {
                string cab = CabOf(externalPath);
                Dictionary<long, ScriptInfo> map;
                if (!scriptsByCab.TryGetValue(cab, out map))
                {
                    map = new Dictionary<long, ScriptInfo>();
                    scriptsByCab[cab] = map;
                    try
                    {
                        EnsureIndex();
                        HdogEntry e;
                        if (cabIndex.TryGetValue(cab, out e))
                        {
                            foreach (BundleFile bf in UnityBundle.Open(archive.ReadEntry(e)))
                            {
                                if (!string.Equals(bf.Name, cab, StringComparison.OrdinalIgnoreCase)) continue;
                                SerializedFile sf = SerializedFile.Parse(bf.Name, bf.Data);
                                foreach (ObjInfo o in sf.Objects)
                                    if (o.TypeName == "MonoScript") { try { map[o.PathId] = BundleModel.ToScript(sf.Read(o)); } catch (Exception) { } }
                            }
                        }
                    }
                    catch (Exception) { /* leave the map empty: scripts show as unresolved */ }
                }
                ScriptInfo si;
                return map.TryGetValue(pathId, out si) ? si : null;
            }
        }
    }

    internal sealed class ExplorerItem
    {
        public SerializedFile File;
        public ObjInfo Info;
        public string Type;            // Unity type name (GameObject, Mesh, MonoBehaviour, ...)
        public string Name;            // m_Name, if the object has one
        public string Owner;           // name of the GameObject this component sits on
        public ScriptInfo Script;      // MonoBehaviour only
        public string ScriptNote;      // why the script is unknown, if so
        public bool Partial;           // parsed, but not byte-exact (e.g. [SerializeReference] data)
        public string Error;           // could not be parsed at all

        public string Display
        {
            get
            {
                if (Type == "MonoScript" && Script != null) return Script.Full;
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (!string.IsNullOrEmpty(Owner)) return Type == "MonoBehaviour" ? "(on " + Owner + ")" : Type + " (on " + Owner + ")";
                return "#" + Info.PathId;
            }
        }
        public string ScriptName { get { return Script != null ? Script.Full : (ScriptNote ?? "unknown script"); } }
    }

    internal sealed class BundleModel
    {
        public string Title;
        public List<SerializedFile> Files = new List<SerializedFile>();
        public List<ExplorerItem> Items = new List<ExplorerItem>();
        public List<BundleFile> Resources = new List<BundleFile>();
        public int Partial, Failed;

        public static ScriptInfo ToScript(Value v)
        {
            ScriptInfo s = new ScriptInfo();
            s.Namespace = v.Str("m_Namespace"); s.ClassName = v.Str("m_ClassName"); s.Assembly = v.Str("m_AssemblyName");
            return s;
        }

        public static BundleModel Load(string title, byte[] bytes, ScriptResolver resolver)
        {
            BundleModel m = new BundleModel();
            m.Title = title;
            foreach (BundleFile bf in UnityBundle.Open(bytes))
            {
                SerializedFile sf = null;
                if (!bf.IsResource) { try { sf = SerializedFile.Parse(bf.Name, bf.Data); } catch (Exception) { sf = null; } }
                if (sf == null) { m.Resources.Add(bf); continue; }
                m.Files.Add(sf);
                m.LoadFile(sf, resolver);
            }
            return m;
        }

        private void LoadFile(SerializedFile sf, ScriptResolver resolver)
        {
            Dictionary<long, string> goNames = new Dictionary<long, string>();
            Dictionary<long, ScriptInfo> localScripts = new Dictionary<long, ScriptInfo>();
            Value[] values = new Value[sf.Objects.Count];
            bool[] exact = new bool[sf.Objects.Count];
            string[] errors = new string[sf.Objects.Count];

            for (int i = 0; i < sf.Objects.Count; i++)
            {
                ObjInfo o = sf.Objects[i];
                try
                {
                    values[i] = sf.Read(o, out exact[i]);
                    if (o.TypeName == "GameObject") goNames[o.PathId] = values[i].Str("m_Name") ?? "";
                    else if (o.TypeName == "MonoScript") localScripts[o.PathId] = ToScript(values[i]);
                }
                catch (Exception ex) { errors[i] = ex.Message; }
            }

            for (int i = 0; i < sf.Objects.Count; i++)
            {
                ObjInfo o = sf.Objects[i];
                ExplorerItem it = new ExplorerItem();
                it.File = sf; it.Info = o; it.Type = o.TypeName ?? ("Class " + o.ClassId);
                Value v = values[i];
                if (v == null) { it.Error = errors[i]; Failed++; }
                else
                {
                    if (!exact[i]) { it.Partial = true; Partial++; }
                    it.Name = v.Str("m_Name");
                    Value go = v.Child("m_GameObject");
                    string gn;
                    if (go != null && go.IsPPtr && go.FileId == 0 && go.PathId != 0 && goNames.TryGetValue(go.PathId, out gn)) it.Owner = gn;
                }
                if (o.ClassId == 114) ResolveScript(sf, o, it, localScripts, resolver);
                else if (o.TypeName == "MonoScript" && v != null) it.Script = ToScript(v);
                Items.Add(it);
            }
        }

        private static void ResolveScript(SerializedFile sf, ObjInfo o, ExplorerItem it, Dictionary<long, ScriptInfo> local, ScriptResolver resolver)
        {
            int si = sf.Types[o.TypeIndex].ScriptIndex;
            if (si < 0 || si >= sf.Scripts.Count) { it.ScriptNote = "no script reference"; return; }
            ScriptRef sr = sf.Scripts[si];
            if (sr.FileIndex == 0)
            {
                ScriptInfo s;
                if (local.TryGetValue(sr.PathId, out s)) it.Script = s; else it.ScriptNote = "script #" + sr.PathId + " (not found)";
                return;
            }
            if (sr.FileIndex - 1 >= sf.Externals.Count) { it.ScriptNote = "script in unknown file"; return; }
            string ext = sf.Externals[sr.FileIndex - 1];
            ScriptInfo r = resolver != null ? resolver.Resolve(ext, sr.PathId) : null;
            if (r != null) it.Script = r;
            else it.ScriptNote = "script #" + sr.PathId + " in " + ScriptResolver.CabOf(ext) + (resolver == null ? " (open the .hdog first to resolve)" : "");
        }
    }
}
