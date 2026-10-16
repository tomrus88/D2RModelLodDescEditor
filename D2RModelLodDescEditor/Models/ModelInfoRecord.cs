using System.Buffers.Binary;
using CommunityToolkit.Mvvm.ComponentModel;

namespace D2RModelLodDescEditor.Models;

/// <summary>
/// ModelInfo — the game's own name (ModelAsset::GetModelInfo). 12 B per LOD
/// slot; the per-asset run is indexed by slot ordinal = rank of the requested
/// lod among the set bits of the asset's lodMask.
///
///   +0  u8  isMeshFlipbook  file-level granny ExtendedData flag
///   +1..3 pad               uninitialized offline-writer heap (preserved verbatim)
///   +4  u32 meshes.size     mesh count
///   +8  i32 meshes.offset   rel -> the slot's first ModelCachePerMeshData record
///
/// Slots created by the editor ("Add model") have no buffer offset until the
/// next repack; they live in a shadow buffer and are materialized there.
/// </summary>
public sealed partial class ModelInfoRecord : ObservableObject
{
    private readonly LodDescDocument _doc;
    private readonly byte[]? _shadow;   // non-null => pending, not yet in the buffer

    private ModelInfoRecord(LodDescDocument doc, int offset, byte[]? shadow)
    {
        _doc = doc;
        Offset = offset;
        _shadow = shadow;
    }

    /// <summary>A record that lives inside the document buffer.</summary>
    internal ModelInfoRecord(LodDescDocument doc, int offset) : this(doc, offset, null)
    {
        for (int k = 0; k < 3; k++) Pad[k] = _doc.Buffer[offset + 1 + k];
    }

    /// <summary>
    /// A pending record: no flipbook, no meshes, dead rel — materialized by
    /// the next repack.
    /// </summary>
    internal static ModelInfoRecord CreatePending(LodDescDocument doc) =>
        new(doc, -1, new byte[LodDescDocument.ModelInfoRecSize]);

    /// <summary>Uninitialized heap bytes +1..3; carried verbatim through repack.</summary>
    internal byte[] Pad = new byte[3];

    public int Offset { get; }

    /// <summary>True until the next repack gives this record a buffer offset.</summary>
    public bool IsPending => _shadow is not null;

    private Span<byte> Rec => _shadow is not null
        ? _shadow.AsSpan()
        : _doc.Buffer.AsSpan(Offset, LodDescDocument.ModelInfoRecSize);

    /// <summary>The model asset this slot belongs to.</summary>
    public ModelAssetRecord? Owner { get; internal set; }

    /// <summary>LOD level this slot serves (rank among the owner's lodMask bits).</summary>
    public int SlotLod { get; internal set; }

    /// <summary>The meshes of this slot, in file order.</summary>
    public List<MeshRecord> Meshes { get; } = new();

    // +0 ------------------------------------------------------- isMeshFlipbook
    public bool IsFlipbook
    {
        get => Rec[0] != 0;
        set
        {
            byte b = value ? (byte)1 : (byte)0;
            if (Rec[0] == b) return;
            Rec[0] = b;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    // +4 ------------------------------------------------------------ meshCount
    public uint MeshCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(4, 4));
        internal set
        {
            if (MeshCount == value) return;
            BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(4, 4), value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    // +8 ------------------------------------------------- rel -> first mesh
    /// <summary>Absolute offset of this slot's first mesh record.</summary>
    public int RelTarget => Offset + 8 + BinaryPrimitives.ReadInt32LittleEndian(Rec.Slice(8, 4));

    /// <summary>Display-only target address of the rel.</summary>
    public string RelTargetHex => IsPending ? "(pending)" : "0x" + RelTarget.ToString("X");

    /// <summary>List-box label.</summary>
    public string Display =>
        $"LOD {SlotLod} — {MeshCount} mesh{(MeshCount == 1 ? "" : "es")}" +
        (IsFlipbook ? " (flipbook)" : "") +
        (IsPending ? " (pending)" : "");

    /// <summary>Emits this record into a 12-byte repack slot.</summary>
    internal void ToBytes(Span<byte> dst, int newOffset, long meshTarget)
    {
        dst[0] = IsFlipbook ? (byte)1 : (byte)0;
        Pad.CopyTo(dst.Slice(1, 3));
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), (uint)Meshes.Count);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(8, 4),
            (int)(meshTarget - (newOffset + 8)));
    }
}
