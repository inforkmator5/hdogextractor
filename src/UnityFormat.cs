using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HdogExtractor
{
    // =====================================================================================
    //  Minimal read-only parser for Unity asset bundles (UnityFS, LZ4/uncompressed) and the
    //  serialized files inside them (format versions 17-22+, with type trees).
    // =====================================================================================

    /// <summary>Little/big endian cursor over a byte array.</summary>
    internal sealed class Rd
    {
        public byte[] B;
        public int Pos;
        public bool Big;

        public Rd(byte[] b, int pos, bool big) { B = b; Pos = pos; Big = big; }

        public byte U8() { return B[Pos++]; }
        public void Skip(int n) { if (n < 0 || Pos + n > B.Length) throw new InvalidDataException("Skip out of range"); Pos += n; }
        public void Align(int a) { Pos = (Pos + a - 1) / a * a; }

        public ushort U16()
        {
            ushort v = Big ? (ushort)((B[Pos] << 8) | B[Pos + 1]) : (ushort)(B[Pos] | (B[Pos + 1] << 8));
            Pos += 2; return v;
        }
        public uint U32()
        {
            uint v = Big ? (uint)((B[Pos] << 24) | (B[Pos + 1] << 16) | (B[Pos + 2] << 8) | B[Pos + 3])
                         : (uint)(B[Pos] | (B[Pos + 1] << 8) | (B[Pos + 2] << 16) | (B[Pos + 3] << 24));
            Pos += 4; return v;
        }
        public int I32() { return (int)U32(); }
        public ulong U64()
        {
            ulong a = U32(), b = U32();
            return Big ? (a << 32) | b : (b << 32) | a;
        }
        public long I64() { return (long)U64(); }
        public float F32() { return BitConverter.ToSingle(BitConverter.GetBytes(U32()), 0); }
        public double F64() { return BitConverter.Int64BitsToDouble(I64()); }

        public string CStr()
        {
            int s = Pos;
            while (B[Pos] != 0) Pos++;
            string r = Encoding.UTF8.GetString(B, s, Pos - s);
            Pos++;
            return r;
        }
        public byte[] Bytes(int n)
        {
            if (n < 0 || Pos + n > B.Length) throw new InvalidDataException("Read out of range");
            byte[] r = new byte[n]; Buffer.BlockCopy(B, Pos, r, 0, n); Pos += n; return r;
        }
    }

    internal static class Lz4
    {
        public static byte[] Decode(byte[] src, int so, int sl, int outLen)
        {
            byte[] dst = new byte[outLen];
            int sp = so, se = so + sl, dp = 0;
            while (sp < se)
            {
                int tok = src[sp++];
                int lit = tok >> 4;
                if (lit == 15) { int x; do { x = src[sp++]; lit += x; } while (x == 255); }
                if (dp + lit > outLen || sp + lit > se) throw new InvalidDataException("Corrupt LZ4 block");
                Buffer.BlockCopy(src, sp, dst, dp, lit); sp += lit; dp += lit;
                if (sp >= se) break;
                int off = src[sp] | (src[sp + 1] << 8); sp += 2;
                int ml = tok & 15;
                if (ml == 15) { int x; do { x = src[sp++]; ml += x; } while (x == 255); }
                ml += 4;
                int cp = dp - off;
                if (off == 0 || cp < 0 || dp + ml > outLen) throw new InvalidDataException("Corrupt LZ4 block");
                for (int i = 0; i < ml; i++) dst[dp++] = dst[cp++];
            }
            return dst;
        }
    }

    internal sealed class BundleFile
    {
        public string Name;
        public byte[] Data;
        public bool IsResource { get { return Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase); } }
    }

    internal static class UnityBundle
    {
        private struct Header { public uint Format; public int Csz, Usz; public uint Flags; public int BlocksPos; }

        private static uint Be32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }

        private static Header ReadHeader(byte[] f)
        {
            if (f.Length < 40 || Encoding.ASCII.GetString(f, 0, 7) != "UnityFS" || f[7] != 0)
                throw new InvalidDataException("Not a UnityFS bundle.");
            Header h = new Header();
            h.Format = Be32(f, 8);
            int p = 12;
            while (f[p] != 0) p++; p++;
            while (f[p] != 0) p++; p++;
            p += 8;                                    // total size
            h.Csz = (int)Be32(f, p); h.Usz = (int)Be32(f, p + 4); h.Flags = Be32(f, p + 8);
            p += 12;
            if (h.Format >= 7) p = (p + 15) & ~15;
            h.BlocksPos = (h.Flags & 0x80) != 0 ? f.Length - h.Csz : p;
            return h;
        }

        private static byte[] BlocksInfo(byte[] f, Header h)
        {
            int ct = (int)(h.Flags & 0x3F);
            if (ct == 0) { byte[] r = new byte[h.Csz]; Buffer.BlockCopy(f, h.BlocksPos, r, 0, h.Csz); return r; }
            if (ct == 2 || ct == 3) return Lz4.Decode(f, h.BlocksPos, h.Csz, h.Usz);
            throw new NotSupportedException("This bundle uses LZMA compression, which the explorer doesn't support.");
        }

        private struct Node { public long Off, Size; public string Name; }

        private static List<Node> ReadNodes(byte[] bi, out List<int[]> blocks)
        {
            Rd r = new Rd(bi, 16, true);
            int bc = r.I32();
            blocks = new List<int[]>();
            for (int i = 0; i < bc; i++) { int u = r.I32(), c = r.I32(); int fl = r.U16(); blocks.Add(new int[] { u, c, fl }); }
            int nc = r.I32();
            List<Node> nodes = new List<Node>();
            for (int i = 0; i < nc; i++)
            {
                Node n = new Node(); n.Off = r.I64(); n.Size = r.I64(); r.U32(); n.Name = r.CStr(); nodes.Add(n);
            }
            return nodes;
        }

        /// <summary>Names of the files inside a bundle, from just its first bytes (header + block info). Null if more bytes are needed.</summary>
        public static List<string> PeekNames(byte[] head)
        {
            try
            {
                Header h = ReadHeader(head);
                if ((h.Flags & 0x80) != 0 || h.BlocksPos + h.Csz > head.Length) return null;
                List<int[]> blocks;
                List<Node> nodes = ReadNodes(BlocksInfo(head, h), out blocks);
                List<string> names = new List<string>();
                foreach (Node n in nodes) names.Add(n.Name);
                return names;
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        public static List<BundleFile> Open(byte[] f)
        {
            Header h = ReadHeader(f);
            List<int[]> blocks;
            List<Node> nodes = ReadNodes(BlocksInfo(f, h), out blocks);
            int p;                                       // where the compressed data blocks start
            if ((h.Flags & 0x80) != 0)
            {
                // block info is stored at the end: data follows the header directly
                p = 12; while (f[p] != 0) p++; p++; while (f[p] != 0) p++; p++; p += 8 + 12;
                if (h.Format >= 7) p = (p + 15) & ~15;
            }
            else
            {
                p = h.BlocksPos + h.Csz;                 // data follows the block info...
                if ((h.Flags & 0x200) != 0) p = (p + 15) & ~15;   // ...padded to 16 bytes
            }

            long total = 0; foreach (int[] b in blocks) total += b[0];
            if (total > int.MaxValue) throw new NotSupportedException("Bundle is too large to explore.");
            byte[] data = new byte[total]; int dp = 0;
            foreach (int[] b in blocks)
            {
                int bt = b[2] & 0x3F;
                if (bt == 0) Buffer.BlockCopy(f, p, data, dp, b[0]);
                else if (bt == 2 || bt == 3) Buffer.BlockCopy(Lz4.Decode(f, p, b[1], b[0]), 0, data, dp, b[0]);
                else throw new NotSupportedException("This bundle uses LZMA compression, which the explorer doesn't support.");
                p += b[1]; dp += b[0];
            }
            List<BundleFile> files = new List<BundleFile>();
            foreach (Node n in nodes)
            {
                BundleFile bf = new BundleFile(); bf.Name = n.Name;
                bf.Data = new byte[n.Size]; Buffer.BlockCopy(data, (int)n.Off, bf.Data, 0, (int)n.Size);
                files.Add(bf);
            }
            return files;
        }
    }

    // --------------------------------------------------------------------------------- type trees

    internal sealed class TypeNode
    {
        public string Type, Name;
        public int ByteSize, Index, Meta, Level;
        public List<TypeNode> Children = new List<TypeNode>();
        public bool Align { get { return (Meta & 0x4000) != 0; } }
    }

    internal sealed class SType
    {
        public int ClassId;
        public int ScriptIndex = -1;
        public TypeNode Root;     // null when the file has no type tree
        public string TypeName { get { return Root != null ? Root.Type : null; } }
    }

    internal sealed class ObjInfo
    {
        public long PathId;
        public int TypeIndex;
        public int Offset;        // into SerializedFile.Data
        public int Size;
        public int ClassId;
        public string TypeName;
    }

    internal struct ScriptRef { public int FileIndex; public long PathId; }

    /// <summary>A parsed value from an object's type tree.</summary>
    internal sealed class Value
    {
        public string Name, Type;
        public object Prim;                 // number / bool / string
        public string Summary;              // for skipped blobs
        public List<Value> Items;           // struct fields, or array elements
        public bool IsArray;
        public int ArrayCount;              // real element count for arrays
        public bool IsPPtr;
        public int FileId;
        public long PathId;

        public Value Child(string name)
        {
            if (Items == null) return null;
            foreach (Value v in Items) if (v.Name == name) return v;
            return null;
        }
        public string Str(string name)
        {
            Value v = Child(name);
            return v != null ? v.Prim as string : null;
        }
    }

    internal sealed class SerializedFile
    {
        public string Name;
        public uint Version;
        public string UnityVersion;
        public bool HasTypeTree;
        public List<SType> Types = new List<SType>();
        public List<ObjInfo> Objects = new List<ObjInfo>();
        public List<ScriptRef> Scripts = new List<ScriptRef>();
        public List<string> Externals = new List<string>();
        public byte[] Data;
        private bool big;

        private const int MaxItems = 600;     // array elements kept for display

        // Unity's built-in string table, referenced by offset from type-tree string buffers.
        private static readonly Dictionary<int, string> Common = BuildCommon();
        private static Dictionary<int, string> BuildCommon()
        {
            string[] list = { "AABB", "AnimationClip", "AnimationCurve", "AnimationState", "Array", "Base", "BitField", "bitset", "bool", "char",
                "ColorRGBA", "Component", "data", "deque", "double", "dynamic_array", "FastPropertyName", "first", "float", "Font", "GameObject",
                "Generic Mono", "GradientNEW", "GUID", "GUIStyle", "int", "list", "long long", "map", "Matrix4x4f", "MdFour", "MonoBehaviour",
                "MonoScript", "m_ByteSize", "m_Curve", "m_EditorClassIdentifier", "m_EditorHideFlags", "m_Enabled", "m_ExtensionPtr",
                "m_GameObject", "m_Index", "m_IsArray", "m_IsStatic", "m_MetaFlag", "m_Name", "m_ObjectHideFlags", "m_PrefabInternal",
                "m_PrefabParentObject", "m_Script", "m_StaticEditorFlags", "m_Type", "m_Version", "Object", "pair", "PPtr<Component>",
                "PPtr<GameObject>", "PPtr<Material>", "PPtr<MonoBehaviour>", "PPtr<MonoScript>", "PPtr<Object>", "PPtr<Prefab>",
                "PPtr<Sprite>", "PPtr<TextAsset>", "PPtr<Texture>", "PPtr<Texture2D>", "PPtr<Transform>", "Prefab", "Quaternionf", "Rectf",
                "RectInt", "RectOffset", "second", "set", "short", "size", "SInt16", "SInt32", "SInt64", "SInt8", "staticvector", "string",
                "TextAsset", "TextMesh", "Texture", "Texture2D", "Transform", "TypelessData", "UInt16", "UInt32", "UInt64", "UInt8",
                "unsigned int", "unsigned long long", "unsigned short", "vector", "Vector2f", "Vector3f", "Vector4f",
                "m_ScriptingClassIdentifier", "Gradient", "Type*", "int2_storage", "int3_storage", "BoundsInt",
                "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "FileSize", "Hash128" };
            Dictionary<int, string> d = new Dictionary<int, string>();
            int off = 0;
            foreach (string s in list) { d[off] = s; off += s.Length + 1; }
            return d;
        }

        // ------------------------------------------------------------------------------ parsing

        public static SerializedFile Parse(string name, byte[] data)
        {
            SerializedFile f = new SerializedFile();
            f.Name = name; f.Data = data;
            Rd r = new Rd(data, 0, true);
            r.U32(); r.U32();
            f.Version = r.U32();
            long dataOff = r.U32();
            if (f.Version < 17) throw new NotSupportedException("Serialized file version " + f.Version + " is too old for the explorer.");
            byte endian = r.U8(); r.Skip(3);
            if (f.Version >= 22) { r.U32(); r.I64(); dataOff = r.I64(); r.I64(); }
            f.big = endian != 0;
            r.Big = f.big;

            f.UnityVersion = r.CStr();
            r.I32();                                            // target platform
            f.HasTypeTree = r.U8() != 0;
            int typeCount = r.I32();
            for (int i = 0; i < typeCount; i++) f.Types.Add(f.ReadType(r, false));

            int objCount = r.I32();
            for (int i = 0; i < objCount; i++)
            {
                r.Align(4);
                ObjInfo o = new ObjInfo();
                o.PathId = r.I64();
                long start = f.Version >= 22 ? r.I64() : r.U32();
                o.Size = (int)r.U32();
                o.TypeIndex = r.I32();
                o.Offset = (int)(start + dataOff);
                if (o.TypeIndex < 0 || o.TypeIndex >= f.Types.Count) throw new InvalidDataException("Bad type index");
                o.ClassId = f.Types[o.TypeIndex].ClassId;
                o.TypeName = f.Types[o.TypeIndex].TypeName;
                f.Objects.Add(o);
            }
            int scriptCount = r.I32();
            for (int i = 0; i < scriptCount; i++)
            {
                ScriptRef s = new ScriptRef(); s.FileIndex = r.I32(); r.Align(4); s.PathId = r.I64(); f.Scripts.Add(s);
            }
            int extCount = r.I32();
            for (int i = 0; i < extCount; i++)
            {
                r.CStr(); r.Skip(16); r.I32(); f.Externals.Add(r.CStr());
            }
            return f;
        }

        private SType ReadType(Rd r, bool isRef)
        {
            SType t = new SType();
            t.ClassId = r.I32();
            r.U8();                                             // is stripped
            t.ScriptIndex = (short)r.U16();
            if (t.ClassId == 114) r.Skip(16);                   // script hash
            r.Skip(16);                                         // type hash
            if (HasTypeTree)
            {
                t.Root = ReadTree(r);
                if (Version >= 21)
                {
                    if (isRef) { r.CStr(); r.CStr(); r.CStr(); }
                    else { int n = r.I32(); r.Skip(4 * n); }
                }
            }
            return t;
        }

        private TypeNode ReadTree(Rd r)
        {
            int count = r.I32(), strSize = r.I32();
            int nodeSize = Version >= 19 ? 32 : 24;
            int nodesPos = r.Pos;
            int strPos = nodesPos + count * nodeSize;
            List<TypeNode> flat = new List<TypeNode>();
            for (int i = 0; i < count; i++)
            {
                Rd n = new Rd(r.B, nodesPos + i * nodeSize, r.Big);
                n.U16();
                TypeNode tn = new TypeNode();
                tn.Level = n.U8(); n.U8();
                uint typeOff = n.U32(), nameOff = n.U32();
                tn.ByteSize = n.I32(); tn.Index = n.I32(); tn.Meta = n.I32();
                tn.Type = Str(r.B, strPos, strSize, typeOff);
                tn.Name = Str(r.B, strPos, strSize, nameOff);
                flat.Add(tn);
            }
            r.Pos = strPos + strSize;

            TypeNode root = flat[0];
            List<TypeNode> stack = new List<TypeNode>();
            stack.Add(root);
            for (int i = 1; i < flat.Count; i++)
            {
                TypeNode n = flat[i];
                while (stack.Count > n.Level) stack.RemoveAt(stack.Count - 1);
                stack[stack.Count - 1].Children.Add(n);
                stack.Add(n);
            }
            return root;
        }

        private static string Str(byte[] b, int bufPos, int bufSize, uint off)
        {
            if ((off & 0x80000000u) != 0)
            {
                string s;
                return Common.TryGetValue((int)(off & 0x7FFFFFFF), out s) ? s : "?" + (off & 0x7FFFFFFF);
            }
            int p = bufPos + (int)off, e = p;
            while (e < bufPos + bufSize && b[e] != 0) e++;
            return Encoding.UTF8.GetString(b, p, e - p);
        }

        // ------------------------------------------------------------------------- object values

        /// <summary>Reads an object through its type tree. Throws if the type has no tree.</summary>
        public Value Read(ObjInfo o, out bool exact)
        {
            SType t = Types[o.TypeIndex];
            if (t.Root == null) throw new NotSupportedException("This object type has no type tree (stripped).");
            Rd r = new Rd(Data, o.Offset, big);
            Value v = ReadValue(t.Root, r);
            int end = o.Offset + o.Size;
            exact = r.Pos <= end && end - r.Pos < 4;
            return v;
        }

        public Value Read(ObjInfo o) { bool e; return Read(o, out e); }

        private static Value ReadValue(TypeNode n, Rd r)
        {
            Value v = new Value(); v.Name = n.Name; v.Type = n.Type;
            bool align = n.Align;
            switch (n.Type)
            {
                case "SInt8": v.Prim = (sbyte)r.U8(); break;
                case "UInt8": case "char": v.Prim = r.U8(); break;
                case "bool": v.Prim = r.U8() != 0; break;
                case "short": case "SInt16": v.Prim = (short)r.U16(); break;
                case "UInt16": case "unsigned short": v.Prim = r.U16(); break;
                case "int": case "SInt32": v.Prim = r.I32(); break;
                case "UInt32": case "unsigned int": case "Type*": v.Prim = r.U32(); break;
                case "long long": case "SInt64": v.Prim = r.I64(); break;
                case "UInt64": case "unsigned long long": case "FileSize": v.Prim = r.U64(); break;
                case "float": v.Prim = r.F32(); break;
                case "double": v.Prim = r.F64(); break;
                case "string":
                    {
                        int len = r.I32();
                        v.Prim = Encoding.UTF8.GetString(r.Bytes(len));
                        align = true;
                        break;
                    }
                case "TypelessData":
                    {
                        int len = r.I32(); r.Skip(len);
                        v.Summary = "<" + len + " bytes>";
                        break;
                    }
                case "Array":
                    ReadArray(n, r, v);
                    break;
                default:
                    if (n.Children.Count == 1 && n.Children[0].Type == "Array")
                    {
                        // vector / list / set: show the array directly
                        Value a = ReadValue(n.Children[0], r);
                        a.Name = n.Name; a.Type = n.Type;
                        if (align) r.Align(4);
                        return a;
                    }
                    v.Items = new List<Value>();
                    foreach (TypeNode c in n.Children) v.Items.Add(ReadValue(c, r));
                    if (n.Type.StartsWith("PPtr<", StringComparison.Ordinal))
                    {
                        Value f = v.Child("m_FileID"), p = v.Child("m_PathID");
                        v.IsPPtr = true;
                        v.FileId = f != null && f.Prim is int ? (int)f.Prim : 0;
                        v.PathId = p != null && p.Prim is long ? (long)p.Prim : 0;
                    }
                    break;
            }
            if (align) r.Align(4);
            return v;
        }

        private static void ReadArray(TypeNode n, Rd r, Value v)
        {
            int size = r.I32();
            if (size < 0 || size > 50000000) throw new InvalidDataException("Implausible array size");
            TypeNode elem = n.Children[1];
            v.IsArray = true; v.ArrayCount = size; v.Items = new List<Value>();
            bool plainFixed = elem.Children.Count == 0 && elem.ByteSize > 0 && !elem.Align && elem.Type != "string";
            if (plainFixed && (long)size * elem.ByteSize > 256)
            {
                // large primitive array: keep a few leading elements, skip the rest
                int keep = Math.Min(size, 8);
                for (int i = 0; i < keep; i++) v.Items.Add(ReadValue(elem, r));
                r.Skip((int)((long)(size - keep) * elem.ByteSize));
                v.Summary = elem.Type + "[" + size + "]";
            }
            else
            {
                for (int i = 0; i < size; i++)
                {
                    Value e = ReadValue(elem, r);
                    if (i < MaxItems) { e.Name = "[" + i + "]"; v.Items.Add(e); }
                }
            }
            if (n.Align) r.Align(4);
        }
    }
}
