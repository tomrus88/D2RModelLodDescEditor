using System.Buffers.Binary;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace D2RModelLodDescEditor.Models;

/// <summary>
/// A models[i] element — the 44-byte per-asset descriptor of the binary desc
/// cache (hash, AABB, LOD set, rel -> the asset's ModelInfo run). Write-through
/// wrapper over the document buffer: every setter patches the bytes in place
/// and marks the document dirty.
///
///   +0  u32 hash          FNV-1a32 of the ASCII-lowercased manifest key
///   +4  f32[3] boundsMin  union of the asset's mesh boxes (FLT_MAX sentinel)
///   +16 f32[3] boundsMax  (-FLT_MAX sentinel)
///   +28 u8  lodMask       bit i+1  <=>  lod i
///   +29..31 pad           uninitialized offline-writer heap (preserved verbatim)
///   +32 u32 lodCount      popcount(lodMask)
///   +36 i32 rel           -> first ModelInfo record (dead value for zero-LOD assets)
///   +40 u32 typeMask      root-ExtendedData modelType
///
/// Records created by the editor ("Add model") have no buffer offset until the
/// next repack; they live in a shadow buffer and are materialized there.
/// </summary>
public sealed partial class ModelAssetRecord : ObservableObject
{
    private readonly LodDescDocument _doc;
    private readonly byte[]? _shadow;   // non-null => pending, not yet in the buffer

    private ModelAssetRecord(LodDescDocument doc, int offset, byte[]? shadow)
    {
        _doc = doc;
        Offset = offset;
        _shadow = shadow;
    }

    /// <summary>A record that lives inside the document buffer.</summary>
    internal ModelAssetRecord(LodDescDocument doc, int offset) : this(doc, offset, null)
    {
        for (int k = 0; k < 3; k++) Pad[k] = _doc.Buffer[offset + 29 + k];
    }

    /// <summary>
    /// A pending record: FLT_MAX sentinel box, no LODs, empty rel — the same
    /// defaults the offline writer uses for an asset without meshes.
    /// </summary>
    public static ModelAssetRecord CreatePending(LodDescDocument doc, uint hash)
    {
        var shadow = new byte[LodDescDocument.ModelAssetRecSize];
        BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(0, 4), hash);
        for (int c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(4 + 4 * c), 0x7F7FFFFFu);
            BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(16 + 4 * c), 0xFF7FFFFFu);
        }
        return new ModelAssetRecord(doc, -1, shadow);
    }

    /// <summary>Uninitialized heap bytes +29..31; carried verbatim through repack.</summary>
    internal byte[] Pad = new byte[3];

    public int Offset { get; }

    /// <summary>True until the next repack gives this record a buffer offset.</summary>
    public bool IsPending => _shadow is not null;

    private Span<byte> Rec => _shadow is not null
        ? _shadow.AsSpan()
        : _doc.Buffer.AsSpan(Offset, LodDescDocument.ModelAssetRecSize);

    /// <summary>Physical rank in the (hash-sorted) models array.</summary>
    public int Index { get; internal set; }

    /// <summary>The per-LOD ModelInfo records of this asset, in slot order.</summary>
    public List<ModelInfoRecord> Slots { get; } = new();

    /// <summary>
    /// The rel value exactly as loaded. For zero-LOD assets the loader never
    /// dereferences it (the lodMask walk finds no bit), and the shipped value
    /// follows the offline tool's 1500-asset batching rule, which is not
    /// recoverable from the file alone — so repack carries it verbatim.
    /// </summary>
    internal int OrigRel;

    // +0 ---------------------------------------------------------------- hash
    public uint Hash
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(0, 4));
        set
        {
            if (Hash == value) return;
            // duplicate hashes break the loader's binary search — keep the old
            // value and push it back into the (two-way bound) text box
            foreach (var m in _doc.Models)
                if (!ReferenceEquals(m, this) && m.Hash == value)
                {
                    OnPropertyChanged(nameof(HashHex));
                    return;
                }
            BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(0, 4), value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HashHex));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(KeyDisplay));
            _doc.Touch();
            // models[] must stay ascending — re-sort when the edit moved this
            // record out of order (no-op, and no event churn, otherwise)
            _doc.ResortAfterHashChange(this);
        }
    }

    public string HashHex
    {
        get => Hash.ToString("X8");
        set
        {
            string t = value.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint h))
                Hash = h;
        }
    }

    // +4/+16 ---------------------------------------------------------- bounds
    public float MinX { get => ReadF(4); set => WriteF(4, value); }
    public float MinY { get => ReadF(8); set => WriteF(8, value); }
    public float MinZ { get => ReadF(12); set => WriteF(12, value); }
    public float MaxX { get => ReadF(16); set => WriteF(16, value); }
    public float MaxY { get => ReadF(20); set => WriteF(20, value); }
    public float MaxZ { get => ReadF(24); set => WriteF(24, value); }

    // TextBox-edited view of the bounds (round-trips the raw float bits).
    public string MinXText { get => RawF(4); set => SetF(4, value, nameof(MinX)); }
    public string MinYText { get => RawF(8); set => SetF(8, value, nameof(MinY)); }
    public string MinZText { get => RawF(12); set => SetF(12, value, nameof(MinZ)); }
    public string MaxXText { get => RawF(16); set => SetF(16, value, nameof(MaxX)); }
    public string MaxYText { get => RawF(20); set => SetF(20, value, nameof(MaxY)); }
    public string MaxZText { get => RawF(24); set => SetF(24, value, nameof(MaxZ)); }

    // +28 ------------------------------------------------------------ lodMask
    public byte LodMask
    {
        get => Rec[28];
        set
        {
            if (LodMask == value) return;
            Rec[28] = value;
            // +32 must stay == popcount(+28): the loader asserts slot ordinal < nLod.
            LodCount = (uint)System.Numerics.BitOperations.PopCount(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
            // keep the loaded manifest's "lods" in sync (no-op without one)
            _doc.SyncManifestLods(this);
        }
    }

    /// <summary>lod bit i+1 (lod i) as a bindable checkbox.</summary>
    public bool GetLodBit(int lod) => (LodMask & (1 << (lod + 1))) != 0;
    public void SetLodBit(int lod, bool on)
    {
        byte m = (byte)(1 << (lod + 1));
        LodMask = on ? (byte)(LodMask | m) : (byte)(LodMask & ~m);
    }

    /// <summary>Per-LOD checkbox bindings: lod i lives in lodMask bit i+1.</summary>
    public bool LodBit0 { get => GetLodBit(0); set => SetLodBit(0, value); }
    public bool LodBit1 { get => GetLodBit(1); set => SetLodBit(1, value); }
    public bool LodBit2 { get => GetLodBit(2); set => SetLodBit(2, value); }
    public bool LodBit3 { get => GetLodBit(3); set => SetLodBit(3, value); }
    public bool LodBit4 { get => GetLodBit(4); set => SetLodBit(4, value); }
    public bool LodBit5 { get => GetLodBit(5); set => SetLodBit(5, value); }
    public bool LodBit6 { get => GetLodBit(6); set => SetLodBit(6, value); }
    public bool LodBit7 { get => GetLodBit(7); set => SetLodBit(7, value); }

    public uint LodCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(32, 4));
        private set
        {
            if (LodCount == value) return;
            BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(32, 4), value);
            OnPropertyChanged();
        }
    }

    // +36 -------------------------------------------------- rel -> first slot
    /// <summary>Absolute offset of this asset's first ModelInfo record
    /// (-1 while pending).</summary>
    public int RelTarget => IsPending ? -1
        : Offset + 36 + BinaryPrimitives.ReadInt32LittleEndian(Rec.Slice(36, 4));

    /// <summary>Display-only target address of the rel (dead weight when zero-LOD).</summary>
    public string RelTargetHex => IsPending ? "(pending)" : "0x" + RelTarget.ToString("X");

    // +40 ---------------------------------------------------------- typeMask
    public uint TypeMask
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(40, 4));
        set
        {
            if (TypeMask == value) return;
            BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(40, 4), value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TypeMaskHex));
            _doc.Touch();
        }
    }

    public string TypeMaskHex
    {
        get => TypeMask.ToString("X8");
        set
        {
            string t = value.Trim();
            if (t.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint h))
                TypeMask = h;
        }
    }

    /// <summary>List-box label: manifest path when the JSON index is loaded.</summary>
    public string Display => _doc.PathFor(Hash) ?? HashHex;

    /// <summary>
    /// Record-editor "path (key)" column: the manifest key when the loaded
    /// manifest names this hash, else an explicit note. The hash itself is
    /// FNV-1a32 of the lowercased key — derived, never edited directly.
    /// </summary>
    public string KeyDisplay => _doc.PathFor(Hash) ?? "(not in the loaded manifest)";

    /// <summary>Re-evaluates <see cref="Display"/> after the path index changes.</summary>
    internal void RefreshDisplay()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(KeyDisplay));
    }

    private float ReadF(int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(Rec.Slice(off, 4));

    private string RawF(int off) => ReadF(off).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Parses TextBox text into a bounds field; keeps the old value on
    /// garbage input and re-formats the box.</summary>
    private void SetF(int off, string text, string prop)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            WriteF(off, v);
        OnPropertyChanged(prop + "Text");
    }

    /// <summary>Emits this record into a 44-byte repack slot.</summary>
    internal void ToBytes(Span<byte> dst, int newOffset, Dictionary<ModelAssetRecord, long> firstSlotPos)
    {
        Rec.Slice(0, 36).CopyTo(dst);                 // hash, bounds, lodMask, pad, lodCount
        Rec.Slice(40, 4).CopyTo(dst.Slice(40, 4));    // typeMask
        long slotTarget = 0;
        bool hasSlots = Slots.Count > 0 && firstSlotPos.TryGetValue(this, out slotTarget);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(36, 4),
            hasSlots ? (int)(slotTarget - (newOffset + 36)) : OrigRel);
    }

    /// <summary>Writes a float bit-pattern verbatim (bounds sentinel handling).</summary>
    internal void WriteRawF(int off, uint bits)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(off, 4), bits);
        for (int k = 0; k < 3; k++)
        {
            if (off == 4 + 4 * k) OnPropertyChanged($"Min{(char)('X' + k)}");
            if (off == 16 + 4 * k) OnPropertyChanged($"Max{(char)('X' + k)}");
        }
        _doc.Touch();
    }

    private void WriteF(int off, float value)
    {
        if (ReadF(off) == value) return;
        BinaryPrimitives.WriteSingleLittleEndian(Rec.Slice(off, 4), value);
        OnPropertyChanged(off switch
        {
            4 => nameof(MinX), 8 => nameof(MinY), 12 => nameof(MinZ),
            16 => nameof(MaxX), 20 => nameof(MaxY), _ => nameof(MaxZ),
        });
        _doc.Touch();
    }
}
