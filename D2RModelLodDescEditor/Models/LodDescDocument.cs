using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace D2RModelLodDescEditor.Models;

/// <summary>
/// The parsed binary desc cache (data/hd/model_lod_desc.bin). Layout:
///
///   header 24 B  3 x { u32 count; i32 rel }  — rel is anchored at the address
///                of the rel field itself (header+4 -> models, +12 -> ModelInfo,
///                +20 -> mesh data)
///   models[]              44 B each, ascending FNV-1a32(lowercased key)
///   ModelInfo             12 B each, per-asset runs in models[] order
///   ModelCachePerMeshData 32 B each, per-slot runs
///
/// Records are write-through wrappers over the live buffer; structural edits
/// (add/remove mesh, re-sort models) set <see cref="StructureDirty"/> and are
/// realized by <see cref="BuildBytes"/> on save.
/// </summary>
public sealed class LodDescDocument
{
    public const int HeaderSize = 24;
    public const int ModelAssetRecSize = 44;
    public const int ModelInfoRecSize = 12;
    public const int MeshRecSize = 32;

    private byte[] _buf = Array.Empty<byte>();

    public int ModelsOffset { get; private set; }
    public int ModelInfoOffset { get; private set; }
    public int MeshOffset { get; private set; }
    public int ModelCount { get; private set; }
    public int ModelInfoCount { get; private set; }
    public int MeshCount { get; private set; }

    public List<ModelAssetRecord> Models { get; } = new();

    /// <summary>
    /// Physical order of the ModelInfo/mesh runs in the file. The shipped
    /// offline writer emits them per manifest key, NOT in models[] (hash)
    /// order; the order is recovered from the rel targets and preserved by
    /// repack (the loader does not care about cross-asset order, but the
    /// round-trip target is the shipped layout).
    /// </summary>
    private readonly List<ModelAssetRecord> _emitOrder = new();

    public string? FilePath { get; private set; }
    public string? JsonPath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool StructureDirty { get; private set; }

    /// <summary>Raised after a repack re-created the record graph.</summary>
    public event Action? StructureChanged;

    public byte[] Buffer => _buf;

    // ------------------------------------------------------------- load/parse

    public static LodDescDocument Load(string path)
    {
        var doc = new LodDescDocument { _buf = File.ReadAllBytes(path), FilePath = path };
        doc.Parse();
        return doc;
    }

    /// <summary>Parses an in-memory image (used by the self-test).</summary>
    public static LodDescDocument LoadBytes(byte[] bytes)
    {
        var doc = new LodDescDocument { _buf = bytes };
        doc.Parse();
        return doc;
    }

    private void Parse()
    {
        Models.Clear();
        ModelCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(0));
        ModelInfoCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(8));
        MeshCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(16));
        ModelsOffset = 4 + BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(4));
        ModelInfoOffset = 12 + BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(12));
        MeshOffset = 20 + BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(20));

        // model asset records, in physical (hash) order.
        for (int i = 0; i < ModelCount; i++)
        {
            int off = ModelsOffset + i * ModelAssetRecSize;
            var m = new ModelAssetRecord(this, off) { Index = i };
            m.OrigRel = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(off + 36));
            Models.Add(m);
        }

        // ModelInfo runs + mesh runs, linked through the rel fields. lodMask==0
        // assets carry a dead rel (the loader's lodMask walk never dereferences it).
        foreach (var m in Models)
        {
            int nSlots = (int)m.LodCount;
            if (nSlots == 0) continue;
            int slot0 = (m.RelTarget - ModelInfoOffset) / ModelInfoRecSize;
            for (int s = 0; s < nSlots; s++)
            {
                int off = ModelInfoOffset + (slot0 + s) * ModelInfoRecSize;
                var slot = new ModelInfoRecord(this, off) { Owner = m };
                m.Slots.Add(slot);
                int mesh0 = (slot.RelTarget - MeshOffset) / MeshRecSize;
                for (int k = 0; k < slot.MeshCount; k++)
                {
                    var mesh = new MeshRecord(this, MeshOffset + (mesh0 + k) * MeshRecSize) { Owner = slot };
                    slot.Meshes.Add(mesh);
                }
            }
        }

        // SlotLod = rank of the slot among the set bits of lodMask.
        foreach (var m in Models)
        {
            int rank = 0;
            for (int lod = 0; lod < 8; lod++)
            {
                if ((m.LodMask & (1 << (lod + 1))) == 0) continue;
                if (rank < m.Slots.Count) m.Slots[rank].SlotLod = lod;
                rank++;
            }
        }

        // Emission order: one contiguous ModelInfo run per asset in manifest
        // (key) order; the per-slot mesh runs follow the same walk.
        _emitOrder.Clear();
        _emitOrder.AddRange(Models
            .Where(m => m.Slots.Count > 0)
            .OrderBy(m => (m.RelTarget - ModelInfoOffset) / ModelInfoRecSize));
    }

    // ------------------------------------------------------------ path index

    /// <summary>FNV-1a32 — the exact hash the shipped models[] uses.</summary>
    public static uint FnvHash(string s)
    {
        uint h = 0x811C9DC5u;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x01000193u;
        }
        return h;
    }

    private readonly Dictionary<uint, string> _paths = new();
    private readonly Dictionary<uint, int> _jsonLods = new();
    private int _pathCollisions;

    /// <summary>
    /// The loaded manifest as an ordered key -> lods map (file order preserved
    /// for lossless re-serialization). <see cref="_paths"/>/<see cref="_jsonLods"/>
    /// are the hash-keyed lookups derived from it.
    /// </summary>
    private readonly Dictionary<string, int> _manifest = new();
    private readonly List<string> _manifestOrder = new();
    private readonly HashSet<string> _manifestKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _manifestDirty;

    /// <summary>
    /// Loads model_lod_desc.json (key = "data/hd/&lt;path&gt;.model", value
    /// {"lods": N}) to name the model hashes and cross-check the lod masks.
    /// </summary>
    public void LoadPaths(string jsonPath)
    {
        _paths.Clear();
        _jsonLods.Clear();
        _manifest.Clear();
        _manifestOrder.Clear();
        _manifestKeys.Clear();
        _manifestDirty = false;
        _pathCollisions = 0;
        using var jdoc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        foreach (var p in jdoc.RootElement.EnumerateObject())
        {
            string key = p.Name;
            int lods = p.Value.TryGetProperty("lods", out var l) ? l.GetInt32() : 0;
            if (!_manifestKeys.Add(key)) continue;   // duplicate key: first wins
            _manifest[key] = lods;
            _manifestOrder.Add(key);
            uint h = FnvHash(key.ToLowerInvariant());
            if (!_paths.TryAdd(h, key)) _pathCollisions++;
            if (!_jsonLods.ContainsKey(h)) _jsonLods[h] = lods;
        }
        JsonPath = jsonPath;
        foreach (var m in Models) m.RefreshDisplay();
        PathsChanged?.Invoke();
    }

    public string? PathFor(uint hash) => _paths.TryGetValue(hash, out var p) ? p : null;

    public int PathCount => _paths.Count;
    public int PathCollisions => _pathCollisions;
    public bool HasPaths => _paths.Count > 0;

    /// <summary>JSON lods bitmask for a hash, or null when unknown.</summary>
    public int? JsonLods(uint hash) => _jsonLods.TryGetValue(hash, out int v) ? v : null;

    /// <summary>True once a manifest was loaded (add/remove then mirror into it).</summary>
    public bool ManifestLoaded => JsonPath is not null;

    /// <summary>True after an add/remove/lodMask edit changed the in-memory
    /// manifest since the last load or <see cref="SaveManifest"/>.</summary>
    public bool IsManifestDirty => _manifestDirty;

    // ------------------------------------------------- manifest editing/saving

    /// <summary>
    /// Inserts (or refreshes) a manifest entry for <paramref name="hash"/>.
    /// No-op when no manifest is loaded. Callers use this from AddModel so the
    /// pair stays 1:1 without a separate "update json" step.
    /// </summary>
    public void AddManifestEntry(uint hash, string key, int lods)
    {
        if (!ManifestLoaded) return;
        if (string.IsNullOrWhiteSpace(key)) return;
        key = key.Trim();

        bool fresh = _manifestKeys.Add(key);
        if (fresh) _manifestOrder.Add(key);
        _manifest[key] = lods;
        _paths[hash] = key;
        _jsonLods[hash] = lods;
        _manifestDirty = true;
        PathsChanged?.Invoke();
    }

    /// <summary>Drops the manifest entry of a removed model (no-op when absent).</summary>
    public void RemoveManifestEntry(uint hash)
    {
        if (!ManifestLoaded) return;
        if (!_paths.Remove(hash, out string? key)) return;
        _jsonLods.Remove(hash);
        _manifest.Remove(key);
        _manifestKeys.Remove(key);
        _manifestOrder.Remove(key);
        _manifestDirty = true;
        PathsChanged?.Invoke();
    }

    /// <summary>
    /// Re-points an existing manifest entry at the model's current lodMask —
    /// called from the LodMask setter so LOD-checkbox edits keep the pair in
    /// sync. No-op for hashes the manifest does not name.
    /// </summary>
    public void SyncManifestLods(ModelAssetRecord m)
    {
        if (!ManifestLoaded) return;
        if (!_paths.TryGetValue(m.Hash, out string? key)) return;
        if (_manifest.TryGetValue(key, out int cur) && cur == m.LodMask) return;
        _manifest[key] = m.LodMask;
        _jsonLods[m.Hash] = m.LodMask;
        _manifestDirty = true;
    }

    /// <summary>
    /// Writes the in-memory manifest back out in the shipped format (compact
    /// single-line JSON, file order, "lods" only) — a re-serialization of an
    /// untouched pair is byte-identical to the shipped file.
    /// </summary>
    public void SaveManifest(string jsonPath)
    {
        if (!ManifestLoaded)
            throw new InvalidOperationException("no manifest loaded — nothing to persist");
        using var stream = File.Create(jsonPath);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        foreach (var key in _manifestOrder)
        {
            if (!_manifest.TryGetValue(key, out int lods)) continue;   // removed since load
            w.WriteStartObject(key);
            w.WriteNumber("lods", lods);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.Flush();
        JsonPath = jsonPath;
        _manifestDirty = false;
    }

    // ------------------------------------------------------------- bin/json pair

    /// <summary>The manifest path paired with a .bin (same stem, .json).</summary>
    public static string ManifestPathFor(string binPath) => Path.ChangeExtension(binPath, ".json");

    /// <summary>
    /// The .bin paired with a manifest: the same-stem file, else the folder's
    /// model_lod_desc.bin. Null when neither exists.
    /// </summary>
    public static string? FindPairedBin(string jsonPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".";
        string same = Path.ChangeExtension(jsonPath, ".bin");
        if (File.Exists(same)) return same;
        string fallback = Path.Combine(dir, "model_lod_desc.bin");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Manifest-first load: opens the .bin the json is paired with and indexes
    /// the manifest. Refuses (FileNotFoundException) when there is no paired
    /// bin — the editor needs both halves of the pair.
    /// </summary>
    public static LodDescDocument LoadPair(string jsonPath)
    {
        string? bin = FindPairedBin(jsonPath) ?? throw new FileNotFoundException(
            $"the manifest has no paired model_lod_desc.bin — expected " +
            $"{Path.ChangeExtension(jsonPath, ".bin")} or model_lod_desc.bin in the same folder");
        var doc = Load(bin);
        doc.LoadPaths(jsonPath);
        return doc;
    }

    public event Action? PathsChanged;

    // ----------------------------------------------------------------- edits

    public void Touch() => IsDirty = true;

    internal void MarkStructureDirty()
    {
        StructureDirty = true;
        IsDirty = true;
    }

    /// <summary>Re-orders models[] ascending by hash (the loader binary-searches).</summary>
    public void SortByHash()
    {
        var sorted = Models.OrderBy(m => m.Hash).ToList();
        Models.Clear();
        Models.AddRange(sorted);
        for (int i = 0; i < Models.Count; i++) Models[i].Index = i;
        MarkStructureDirty();
        StructureChanged?.Invoke();
    }

    /// <summary>Rebuilds the asset AABB as the union of its mesh boxes (FLT_MAX
    /// sentinels when the asset has no meshes) — the shipped writer's rule.</summary>
    public void RecomputeBounds(ModelAssetRecord m)
    {
        uint[] mn = { 0x7F7FFFFFu, 0x7F7FFFFFu, 0x7F7FFFFFu };
        uint[] mx = { 0xFF7FFFFFu, 0xFF7FFFFFu, 0xFF7FFFFFu };
        foreach (var slot in m.Slots)
            foreach (var mesh in slot.Meshes)
            {
                byte[] r = mesh.ToBytes();
                for (int c = 0; c < 3; c++)
                {
                    uint vMin = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4 * c));
                    uint vMax = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(12 + 4 * c));
                    if (FKey(vMin) < FKey(mn[c])) mn[c] = vMin;
                    if (FKey(vMax) > FKey(mx[c])) mx[c] = vMax;
                }
            }
        for (int c = 0; c < 3; c++)
        {
            m.WriteRawF(4 + 4 * c, mn[c]);
            m.WriteRawF(16 + 4 * c, mx[c]);
        }
        m.RefreshDisplay();
        Touch();
    }

    /// <summary>Monotonic ordering key for IEEE-754 bit patterns.</summary>
    private static uint FKey(uint bits) => bits < 0x80000000u ? bits | 0x80000000u : ~bits;

    // -------------------------------------------------------- structural ops

    /// <summary>Appends a blank mesh to a slot (materialized on next repack).</summary>
    public MeshRecord AddMesh(ModelInfoRecord slot)
    {
        var mesh = MeshRecord.CreatePending(this);
        mesh.Owner = slot;
        slot.Meshes.Add(mesh);
        slot.MeshCount = (uint)slot.Meshes.Count;
        MarkStructureDirty();
        return mesh;
    }

    public void RemoveMesh(ModelInfoRecord slot, MeshRecord mesh)
    {
        if (!slot.Meshes.Remove(mesh)) return;
        slot.MeshCount = (uint)slot.Meshes.Count;
        MarkStructureDirty();
    }

    // ---------------------------------------------------------- model-level ops

    /// <summary>
    /// Adds a new model asset. The record is pending until the next repack;
    /// models[] stays ascending by hash (the loader binary-searches), and the
    /// ModelInfo/mesh runs are appended in emission order (cross-asset order
    /// is loader-free, and appending keeps unmodified repacks byte-identical).
    /// When <paramref name="key"/> is given and a manifest is loaded, the
    /// manifest gains the same key -> lodMask entry, so the pair stays 1:1.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The hash already exists in models[].
    /// </exception>
    public ModelAssetRecord AddModel(uint hash, byte lodMask = 0, string? key = null)
    {
        if (Models.Any(m => m.Hash == hash))
            throw new InvalidOperationException($"models[] already contains hash 0x{hash:X8}");
        var m = ModelAssetRecord.CreatePending(this, hash);
        m.LodMask = lodMask;      // (manifest not yet aware of this hash — no sync yet)
        Models.Add(m);
        Reindex();
        _emitOrder.Add(m);
        if (key is not null)
        {
            AddManifestEntry(hash, key, m.LodMask);
            m.RefreshDisplay();
        }
        MarkStructureDirty();
        StructureChanged?.Invoke();
        return m;
    }

    /// <summary>Removes a model asset, its whole ModelInfo/mesh run, and — when
    /// a manifest is loaded — its manifest key.</summary>
    public void RemoveModel(ModelAssetRecord m)
    {
        if (!Models.Remove(m)) return;
        RemoveManifestEntry(m.Hash);
        _emitOrder.Remove(m);
        Reindex();
        MarkStructureDirty();
        StructureChanged?.Invoke();
    }

    private void Reindex()
    {
        var sorted = Models.OrderBy(m => m.Hash).ToList();
        Models.Clear();
        Models.AddRange(sorted);
        for (int i = 0; i < Models.Count; i++) Models[i].Index = i;
    }

    /// <summary>
    /// Re-sorts models[] after an in-place hash edit (the record editor binds
    /// HashHex two-way). The loader binary-searches models[], so a hash edit
    /// that breaks the ascending order must move the record; a no-order-change
    /// edit (and duplicate hashes, rejected in the setter) costs nothing.
    /// </summary>
    internal void ResortAfterHashChange(ModelAssetRecord changed)
    {
        bool sorted = true;
        for (int i = 1; i < Models.Count; i++)
            if (Models[i].Hash < Models[i - 1].Hash) { sorted = false; break; }
        if (sorted) return;
        Reindex();
        MarkStructureDirty();
        StructureChanged?.Invoke();
    }

    /// <summary>Adds one pending ModelInfo slot per set lodMask bit, each with
    /// <paramref name="meshesPerSlot"/> pending meshes (0 for an empty slot).</summary>
    public ModelInfoRecord AddSlot(ModelAssetRecord m, int lod, int meshesPerSlot = 0)
    {
        var slot = ModelInfoRecord.CreatePending(this);
        slot.Owner = m;
        slot.SlotLod = lod;
        for (int k = 0; k < meshesPerSlot; k++)
        {
            var mesh = MeshRecord.CreatePending(this);
            mesh.Owner = slot;
            slot.Meshes.Add(mesh);
        }
        m.Slots.Add(slot);
        MarkStructureDirty();
        return slot;
    }

    /// <summary>
    /// Adds a fully populated model parsed from .model file(s). Slots are
    /// created in LOD order, every mesh record is filled from its ExtendedData
    /// (the verified offline-writer recipe), and the asset AABB is recomputed
    /// as the union of the mesh boxes. The manifest gains the model's key ->
    /// lodMask entry (created by AddModel, refreshed by the final LodMask
    /// assignment through the record's sync hook).
    /// </summary>
    public ModelAssetRecord AddImportedModel(ImportedModel im)
    {
        var m = AddModel(im.Hash, 0, im.Key);   // duplicate-hash guard + manifest entry
        m.TypeMask = im.TypeMask;
        byte lodMask = 0;
        foreach (var slot in im.Slots.OrderBy(s => s.Lod))
        {
            lodMask |= (byte)(1 << (slot.Lod + 1));
            var info = AddSlot(m, slot.Lod, 0);
            info.IsFlipbook = im.IsFlipbook;
            foreach (var imesh in slot.Meshes)
            {
                var mesh = MeshRecord.CreatePending(this);
                mesh.ApplyImported(imesh);
                mesh.Owner = info;
                info.Meshes.Add(mesh);
            }
            info.MeshCount = (uint)info.Meshes.Count;
        }
        m.LodMask = lodMask;
        RecomputeBounds(m);
        m.RefreshDisplay();
        return m;
    }

    // ----------------------------------------------------------- save/repack

    /// <summary>
    /// Re-emits the whole file from the record graph: regions grow/shrink with
    /// the record counts, every rel is recomputed (anchored at its own field),
    /// pads ride along with their records, and zero-LOD rels are carried
    /// verbatim (they are dead in the loader). models[] keeps its order unless
    /// SortByHash() was used; the ModelInfo/mesh runs keep the shipped emission
    /// order (manifest order), which the shipped writer used.
    /// </summary>
    public byte[] BuildBytes()
    {
        long slotTotal = Models.Sum(m => (long)m.Slots.Count);
        long meshTotal = Models.Sum(m => m.Slots.Sum(s => (long)s.Meshes.Count));
        int modelsBase = HeaderSize;
        long infoBase = modelsBase + (long)Models.Count * ModelAssetRecSize;
        long meshBase = infoBase + slotTotal * ModelInfoRecSize;
        long total = meshBase + meshTotal * MeshRecSize;
        if (total > int.MaxValue) throw new InvalidOperationException("file too large");

        var o = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(0), (uint)Models.Count);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), modelsBase - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), (uint)slotTotal);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(12), (int)(infoBase - 12));
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16), (uint)meshTotal);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(20), (int)(meshBase - 20));

        // Pass 1 — physical position of every run, walked in the shipped
        // emission order, so the models[] rels can point at the right slots.
        long infoCursor = infoBase, meshCursor = meshBase;
        var firstSlotPos = new Dictionary<ModelAssetRecord, long>();
        foreach (var m in _emitOrder)
        {
            firstSlotPos[m] = infoCursor;
            infoCursor += (long)m.Slots.Count * ModelInfoRecSize;
            foreach (var slot in m.Slots)
                meshCursor += (long)slot.Meshes.Count * MeshRecSize;
        }

        // models[] records (hash order); rel -> first ModelInfo record.
        foreach (var m in Models)
        {
            int off = modelsBase + m.Index * ModelAssetRecSize;
            m.ToBytes(o.AsSpan(off, ModelAssetRecSize), off, firstSlotPos);
        }

        // ModelInfo + mesh records: emission order, slot order, mesh order.
        infoCursor = infoBase;
        meshCursor = meshBase;
        foreach (var m in _emitOrder)
        {
            foreach (var slot in m.Slots)
            {
                int rec = (int)infoCursor;
                slot.ToBytes(o.AsSpan(rec, ModelInfoRecSize), rec, meshCursor);
                infoCursor += ModelInfoRecSize;
                foreach (var mesh in slot.Meshes)
                {
                    mesh.ToBytes().CopyTo(o.AsSpan((int)meshCursor, MeshRecSize));
                    meshCursor += MeshRecSize;
                }
            }
        }
        return o;
    }

    /// <summary>
    /// Writes the document to <paramref name="path"/>, repacking first when
    /// structural edits happened. The record graph is rebuilt afterwards.
    /// </summary>
    public void Save(string path)
    {
        byte[] bytes = StructureDirty ? BuildBytes() : _buf;
        File.WriteAllBytes(path, bytes);
        _buf = bytes;
        FilePath = path;
        IsDirty = false;
        StructureDirty = false;
        Parse();
        StructureChanged?.Invoke();
    }

    // -------------------------------------------------------------- integrity

    /// <summary>Structural audit of the loaded/edited file.</summary>
    public List<string> Integrity()
    {
        var issues = new List<string>();
        void Err(string s) => issues.Add("ERROR: " + s);
        void Warn(string s) => issues.Add("WARN:  " + s);

        if (ModelsOffset != HeaderSize) Err($"models[] base {ModelsOffset} != {HeaderSize}");
        if (ModelInfoOffset - ModelsOffset != (long)ModelCount * ModelAssetRecSize)
            Err($"ModelInfo region size mismatch: {ModelInfoOffset - ModelsOffset} != {ModelCount}*44");
        if (MeshOffset - ModelInfoOffset != (long)ModelInfoCount * ModelInfoRecSize)
            Err($"mesh region size mismatch: {MeshOffset - ModelInfoOffset} != {ModelInfoCount}*12");
        if (_buf.Length - MeshOffset != (long)MeshCount * MeshRecSize)
            Err($"tail size mismatch: {_buf.Length - MeshOffset} != {MeshCount}*32");

        for (int i = 1; i < Models.Count; i++)
        {
            if (Models[i].Hash < Models[i - 1].Hash)
                Err($"models[] not ascending at rank {i} ({Models[i - 1].HashHex} > {Models[i].HashHex})");
            else if (Models[i].Hash == Models[i - 1].Hash && Models[i].Hash != 0)
                Warn($"duplicate hash at rank {i} ({Models[i].HashHex})");
        }

        foreach (var m in Models)
        {
            if (m.IsPending) continue;   // no buffer presence until the next repack
            int nSlots = m.Slots.Count;
            if (nSlots != m.LodCount)
                Err($"{m.Display}: {nSlots} ModelInfo records but lodCount={m.LodCount}");
            if (nSlots > 0)
            {
                int slot0 = (m.RelTarget - ModelInfoOffset) / ModelInfoRecSize;
                if (m.RelTarget < ModelInfoOffset || m.RelTarget > ModelInfoOffset + ModelInfoCount * ModelInfoRecSize - ModelInfoRecSize)
                    Err($"{m.Display}: rel->ModelInfo target {m.RelTarget} outside the ModelInfo region");
                else if (slot0 + nSlots > ModelInfoCount)
                    Err($"{m.Display}: {nSlots} slots at {slot0} overrun ModelInfo (n={ModelInfoCount})");
                foreach (var slot in m.Slots)
                {
                    int mesh0 = (slot.RelTarget - MeshOffset) / MeshRecSize;
                    if (slot.RelTarget < MeshOffset || slot.RelTarget > _buf.Length - MeshRecSize)
                        Err($"{m.Display} LOD {slot.SlotLod}: rel->mesh target outside the mesh region");
                    else if (mesh0 + slot.MeshCount > MeshCount)
                        Err($"{m.Display} LOD {slot.SlotLod}: {slot.MeshCount} meshes at {mesh0} overrun the mesh region (n={MeshCount})");
                }
            }
        }

        if (HasPaths)
        {
            int missing = 0;
            foreach (var m in Models)
                if (PathFor(m.Hash) is null) missing++;
            if (missing > 0) Warn($"{missing} hashes have no manifest path");
            int lodBad = 0;
            foreach (var m in Models)
                if (JsonLods(m.Hash) is int jl && (byte)jl != m.LodMask) lodBad++;
            if (lodBad > 0) Warn($"{lodBad} assets: bin lodMask != JSON lods");
        }
        return issues;
    }

    /// <summary>Header/region summary line.</summary>
    public string Summary =>
        $"models {ModelCount} @{ModelsOffset} | ModelInfo {ModelInfoCount} @{ModelInfoOffset} " +
        $"| meshes {MeshCount} @{MeshOffset} | {_buf.Length} B";
}
