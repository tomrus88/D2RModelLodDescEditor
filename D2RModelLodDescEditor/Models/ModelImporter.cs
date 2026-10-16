using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Granny.Format;

namespace D2RModelLodDescEditor.Models;

/// <summary>
/// One mesh as the offline writer recorded it in the ModelCachePerMeshData
/// array. Every field comes from the .model file's granny ExtendedData —
/// this is the verified recipe from the probe's RegenEngine (byte-exact
/// against the shipped build-93847 model_lod_desc.bin):
///
///   +0/+12 f32[3]      ExtendedData bbMin / bbMax (bitwise)
///   +24    u32         ExtendedData meshRenderLayer (default 1)
///   +28    u16         granny_mesh.BoneBindingCount
///   +30    u16         BoneWeights ArrayWidth | (vertexSize==24)<<4 | !hasBones<<5
/// </summary>
public sealed class ImportedMesh
{
    public string Name { get; set; } = "?";
    public uint[] Min { get; } = new uint[3];
    public uint[] Max { get; } = new uint[3];
    public bool HasBox { get; set; }
    public long RenderLayer { get; set; } = 1;
    public int BoneBindingCount { get; set; }
    public int FmtCode { get; set; }
    public bool HasBones { get; set; }
    public long VertexSize { get; set; }
}

/// <summary>One LOD slot of the imported asset (its ModelInfo record).</summary>
public sealed class ImportedSlot
{
    public int Lod { get; init; }
    public List<ImportedMesh> Meshes { get; } = new();
}

/// <summary>A whole parsed asset, ready to be added to the document.</summary>
public sealed class ImportedModel
{
    /// <summary>Manifest key, e.g. "data/hd/character/enemy/act2hire/basebody.model".</summary>
    public required string Key { get; init; }
    public uint Hash { get; init; }
    /// <summary>Root ExtendedData modelType -> ModelAssetRecord +40.</summary>
    public uint TypeMask { get; set; }
    /// <summary>Root ExtendedData isMeshFlipbook -> ModelInfo byte0.</summary>
    public bool IsFlipbook { get; set; }
    public List<ImportedSlot> Slots { get; } = new();
    public List<string> SourceFiles { get; } = new();

    public int MeshCount => Slots.Sum(s => s.Meshes.Count);
}

/// <summary>
/// Reads a .model (granny) file into <see cref="ImportedModel"/> records.
/// Direct port of the probe's verified RegenEngine decode path
/// (F:\d2r_steam\91923\binaries\releaseassert\x86_64\Granny.Sharp\probe\RegenEngine.cs).
/// </summary>
public static class ModelImporter
{
    private static readonly Regex LodSuffix =
        new(@"^(?<stem>.+)_lod(?<lod>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses one or more .model files as a single asset. The LOD each file
    /// contributes comes from its <c>_lodN</c> filename suffix (0..7); within
    /// a file only the models whose ExtendedData lodLevel matches that LOD
    /// are used — the exact rule the offline writer used.
    /// </summary>
    public static ImportedModel ParseFiles(IReadOnlyList<string> files, string key)
    {
        if (files.Count == 0) throw new ArgumentException("no files", nameof(files));
        var im = new ImportedModel { Key = key, Hash = LodDescDocument.FnvHash(key.ToLowerInvariant()) };

        uint typeMask = 0;
        bool haveType = false, isFlip = false, haveFlip = false;
        var slots = new SortedDictionary<int, ImportedSlot>();

        foreach (var file in files)
        {
            int lod = LodOf(file);
            var img = new GrnImage(file);
            var meshes = DecodeSlot(img, lod, file, ref typeMask, ref haveType, ref isFlip, ref haveFlip);
            if (!slots.TryGetValue(lod, out var slot))
            {
                slot = new ImportedSlot { Lod = lod };
                slots.Add(lod, slot);
            }
            slot.Meshes.AddRange(meshes);
            im.SourceFiles.Add(file);
        }

        foreach (var s in slots.Values) im.Slots.Add(s);
        im.TypeMask = typeMask;
        im.IsFlipbook = isFlip;
        return im;
    }

    /// <summary>LOD encoded in the file name (0 for a bare stem.model).</summary>
    public static int LodOf(string filePath)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filePath);
        var m = LodSuffix.Match(name);
        if (m.Success && int.TryParse(m.Groups["lod"].Value, out int lod) && lod is >= 0 and <= 7)
            return lod;
        return 0;
    }

    /// <summary>File name without the _lodN suffix ("basebody_lod3" -> "basebody").</summary>
    public static string StemOf(string filePath)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filePath);
        var m = LodSuffix.Match(name);
        return m.Success ? m.Groups["stem"].Value : name;
    }

    /// <summary>
    /// Best guess at the manifest key. Manifest keys are always
    /// "data/hd/&lt;path&gt;.model" relative to the CASC hd root, so we anchor on
    /// the LAST "/hd/" and take everything after it (stripping the _lodN
    /// suffix). Anchoring on the last "/hd/" (not on "data/hd/") matters when
    /// the file lives under an extra "data/" layer before the real hd root
    /// (e.g. .../data_93847/data/data/hd/...), which would otherwise drop the
    /// "data/hd/" prefix entirely.
    /// </summary>
    public static string GuessKey(string filePath)
    {
        string full = filePath.Replace('\\', '/');
        int i = full.LastIndexOf("/hd/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            string rest = full[(i + "/hd/".Length)..];
            rest = Regex.Replace(rest, "_lod\\d+\\.model$", ".model", RegexOptions.IgnoreCase);
            return "data/hd/" + rest;
        }
        return "data/hd/" + StemOf(filePath) + ".model";
    }

    // ------------------------------------------------------------- decoding
    // Ported one-for-one from RegenEngine.ComputeSlot + MakeA2Rec; every rule
    // there is verified against the shipped bin.

    private static List<ImportedMesh> DecodeSlot(GrnImage img, int lod, string file,
        ref uint typeMask, ref bool haveType, ref bool isFlip, ref bool haveFlip)
    {
        var outp = new List<ImportedMesh>();
        int rsec = (int)img.File.RootObjectSection;
        int roff = (int)img.File.RootObjectOffset;
        var tree = GrnTypeTree.Read(img);
        var rv = GrnObjectWalker.Walk(img, tree, (ushort)rsec, roff);

        var fileEd = Find(rv, "ExtendedData");
        if (!haveType && Find(fileEd?.Children, "modelType")?.Number is { } mt)
        {
            typeMask = (uint)mt;
            haveType = true;
        }
        if (!haveFlip && Find(fileEd?.Children, "isMeshFlipbook")?.Number is { } fb)
        {
            isFlip = fb != 0;
            haveFlip = true;
        }

        var meshCache = new Dictionary<(int sec, int off), ImportedMesh>();
        IReadOnlyList<GrnTypeNode>? meshType =
            Find(rv, "Meshes")?.Children is { Count: > 0 } mk ? mk[0].Node.ReferenceType : null;

        ImportedMesh GetMesh(int sec, int off)
        {
            if (meshCache.TryGetValue((sec, off), out var hit)) return hit;
            var m = new ImportedMesh { Name = "?" };
            if (meshType is { Count: > 0 })
            {
                var kids = GrnObjectWalker.Walk(img, meshType, (ushort)sec, off);
                m.Name = Find(kids, "Name")?.Text ?? "?";
                m.BoneBindingCount = img.I32(sec, off + 48);   // granny_mesh.BoneBindingCount
                if (img.TryFollow((ushort)sec, (uint)(off + 8), out ushort vds, out uint vdo) &&
                    img.TryFollow(vds, (uint)((int)vdo + 0), out ushort vts, out uint vto))
                {
                    var members = GrnTypeTree.ReadAt(img, vts, (int)vto);
                    int boneW = -1;
                    bool hasIdx = false;
                    foreach (var mm in members)
                    {
                        if (mm.MemberType is GrnMemberType.End or GrnMemberType.OnePastLast) break;
                        if (mm.Name == "BoneWeights") boneW = mm.ArrayWidth;
                        else if (mm.Name == "BoneIndices") hasIdx = true;
                    }
                    m.VertexSize = GrnObjectWalker.TotalSize(members);
                    m.HasBones = boneW >= 0 && hasIdx;
                    m.FmtCode = boneW >= 0
                        ? (boneW & 0xF)
                        : (m.BoneBindingCount == 1 &&
                           Find(Find(kids, "ExtendedData")?.Children, "needsSingleJointSkinning") is not null
                               ? 1 : 0);
                }
                var ed = Find(kids, "ExtendedData");
                var mn = Find(ed?.Children, "bbMin");
                var mx = Find(ed?.Children, "bbMax");
                if (mn is not null && mx is not null)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        m.Min[k] = img.U32(mn.Section, mn.Offset + 4 * k);
                        m.Max[k] = img.U32(mx.Section, mx.Offset + 4 * k);
                    }
                    m.HasBox = true;
                }
                if (Find(ed?.Children, "meshRenderLayer")?.Number is { } rln && rln != 0)
                    m.RenderLayer = rln;
            }
            meshCache[(sec, off)] = m;
            return m;
        }

        var models = new List<(long lod, List<(int sec, int off)> refs)>();
        foreach (var m in Elements(Find(rv, "Models")))
        {
            long lodLvl = Find(Find(m.Children, "ExtendedData")?.Children, "lodLevel")?.Number ?? -1;
            var refs = new List<(int, int)>();
            foreach (var b in Elements(Find(m.Children, "MeshBindings")))
            {
                var meshNode = Find(b.Children, "Mesh");
                if (meshNode is not null &&
                    img.TryFollow(meshNode.Section, (uint)meshNode.Offset, out ushort bs, out uint bo))
                    refs.Add((bs, (int)bo));
            }
            models.Add((lodLvl, refs));
        }

        bool any = false;
        foreach (var (lodLvl, refs) in models)
        {
            if (lodLvl != lod) continue;
            any = true;
            foreach (var r in refs)
                outp.Add(GetMesh(r.sec, r.off));
        }
        if (!any && models.Count > 0)
        {
            // no model carries this lodLevel: take every binding (writer fallback)
            foreach (var (_, refs) in models)
                foreach (var r in refs)
                    outp.Add(GetMesh(r.sec, r.off));
        }
        return outp;
    }

    private static GrnMemberValue? Find(IReadOnlyList<GrnMemberValue>? nodes, string name)
    {
        if (nodes is null) return null;
        foreach (var n in nodes) if (n.Node.Name == name) return n;
        return null;
    }

    private static IEnumerable<GrnMemberValue> Elements(GrnMemberValue? a)
    {
        if (a?.Children is null) yield break;
        foreach (var e in a.Children) yield return e;
    }
}
