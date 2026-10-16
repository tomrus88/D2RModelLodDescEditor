using CommunityToolkit.Mvvm.ComponentModel;
using System.Buffers.Binary;
using System.Globalization;

namespace D2RModelLodDescEditor.Models;

/// <summary>
/// ModelCachePerMeshData — the game's per-mesh descriptor, 32 B each; the
/// elements of <see cref="ModelInfoRecord.Meshes"/>.
///
///   +0  f32[3] boundsMin
///   +12 f32[3] boundsMax
///   +24 u32 defaultRenderLayer   granny ExtendedData "meshRenderLayer"
///   +28 u16 boneBindingsCount    granny_mesh.BoneBindingCount
///   +30 u16 numBoneInfluences:4  BoneWeights ArrayWidth
///           hasVertexAlpha:1     vertexTotalSize == 24
///           isRigid:1           !hasBones
///
/// Records added by the editor ("Add mesh") have no buffer offset until the
/// next repack; they live in a shadow buffer and are materialized there.
/// </summary>
public sealed partial class MeshRecord : ObservableObject
{
    private readonly LodDescDocument _doc;
    private readonly byte[]? _shadow;   // non-null => pending, not yet in the buffer

    private MeshRecord(LodDescDocument doc, int offset, byte[]? shadow)
    {
        _doc = doc;
        Offset = offset;
        _shadow = shadow;
    }

    /// <summary>A record that lives inside the document buffer.</summary>
    internal MeshRecord(LodDescDocument doc, int offset) : this(doc, offset, null) { }

    /// <summary>
    /// A pending record: FLT_MAX sentinel box, render layer 1, no bones — the
    /// same defaults the offline writer uses for an empty mesh.
    /// </summary>
    public static MeshRecord CreatePending(LodDescDocument doc)
    {
        var shadow = new byte[32];
        for (int c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(4 * c), 0x7F7FFFFFu);
            BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(12 + 4 * c), 0xFF7FFFFFu);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(shadow.AsSpan(24), 1);
        return new MeshRecord(doc, -1, shadow);
    }

    public int Offset { get; }

    /// <summary>The slot this mesh belongs to.</summary>
    public ModelInfoRecord? Owner { get; internal set; }

    /// <summary>True until the next repack gives this record a buffer offset.</summary>
    public bool IsPending => _shadow is not null;

    private Span<byte> Rec => _shadow is not null ? _shadow.AsSpan() : _doc.Buffer.AsSpan(Offset, 32);

    // +0/+12 ------------------------------------------------------------ bounds
    public float MinX { get => ReadF(0); set => WriteF(0, value, nameof(MinX)); }
    public float MinY { get => ReadF(4); set => WriteF(4, value, nameof(MinY)); }
    public float MinZ { get => ReadF(8); set => WriteF(8, value, nameof(MinZ)); }
    public float MaxX { get => ReadF(12); set => WriteF(12, value, nameof(MaxX)); }
    public float MaxY { get => ReadF(16); set => WriteF(16, value, nameof(MaxY)); }
    public float MaxZ { get => ReadF(20); set => WriteF(20, value, nameof(MaxZ)); }

    // TextBox-edited view of the bounds (round-trips the raw float bits).
    public string MinXText { get => RawF(0); set => SetF(0, value, nameof(MinX)); }
    public string MinYText { get => RawF(4); set => SetF(4, value, nameof(MinY)); }
    public string MinZText { get => RawF(8); set => SetF(8, value, nameof(MinZ)); }
    public string MaxXText { get => RawF(12); set => SetF(12, value, nameof(MaxX)); }
    public string MaxYText { get => RawF(16); set => SetF(16, value, nameof(MaxY)); }
    public string MaxZText { get => RawF(20); set => SetF(20, value, nameof(MaxZ)); }

    // +24 ------------------------------------------------------- render layer
    public uint RenderLayer
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(24, 4));
        set
        {
            if (RenderLayer == value) return;
            BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(24, 4), value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    /// <summary>TextBox-edited render layer (decimal, or 0x-prefixed hex).</summary>
    public string RenderLayerText
    {
        get => RenderLayer.ToString(CultureInfo.InvariantCulture);
        set
        {
            string t = value.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h))
                    RenderLayer = h;
            }
            else if (uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d))
            {
                RenderLayer = d;
            }
            OnPropertyChanged(nameof(RenderLayerText));
        }
    }

    // +28 ------------------------------------------------------- bone bindings
    public ushort BoneBindingsCount
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(28, 2));
        set
        {
            if (BoneBindingsCount == value) return;
            BinaryPrimitives.WriteUInt16LittleEndian(Rec.Slice(28, 2), value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    /// <summary>TextBox-edited bone binding count (decimal, or 0x-prefixed hex).</summary>
    public string BoneBindingsText
    {
        get => BoneBindingsCount.ToString(CultureInfo.InvariantCulture);
        set
        {
            string t = value.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ushort.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort h))
                    BoneBindingsCount = h;
            }
            else if (ushort.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort d))
            {
                BoneBindingsCount = d;
            }
            OnPropertyChanged(nameof(BoneBindingsText));
        }
    }

    // +30 ------------------------------------------------------- format bits
    private ushort FmtBits
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(30, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Rec.Slice(30, 2), value);
    }

    /// <summary>Bone weights per vertex (granny BoneWeights ArrayWidth), 0..15.</summary>
    public int NumBoneInfluences
    {
        get => FmtBits & 0xF;
        set
        {
            int v = value & 0xF;
            if (NumBoneInfluences == v) return;
            FmtBits = (ushort)((FmtBits & ~0xF) | v);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    /// <summary>TextBox-edited influence count (masked to 4 bits).</summary>
    public string NumBoneInfluencesText
    {
        get => NumBoneInfluences.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                NumBoneInfluences = v;
            OnPropertyChanged(nameof(NumBoneInfluencesText));
        }
    }

    /// <summary>bit4 — 24-byte vertex (extra alpha stream).</summary>
    public bool HasVertexAlpha
    {
        get => (FmtBits & 0x10) != 0;
        set
        {
            if (HasVertexAlpha == value) return;
            FmtBits = (ushort)((FmtBits & ~0x10) | (value ? 0x10 : 0));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    /// <summary>bit5 — no bones (rigid mesh).</summary>
    public bool IsRigid
    {
        get => (FmtBits & 0x20) != 0;
        set
        {
            if (IsRigid == value) return;
            FmtBits = (ushort)((FmtBits & ~0x20) | (value ? 0x20 : 0));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            _doc.Touch();
        }
    }

    /// <summary>List-box label.</summary>
    public string Display =>
        $"layer {RenderLayer}, bones {BoneBindingsCount}, " +
        $"infl {NumBoneInfluences}{(HasVertexAlpha ? "+a" : "")}" +
        (IsRigid ? ", rigid" : "");

    /// <summary>The raw 32 bytes this record would emit (repack input).</summary>
    internal byte[] ToBytes()
    {
        var b = new byte[32];
        Rec.CopyTo(b);
        return b;
    }

    /// <summary>Fills a pending mesh from a parsed .model file (bitwise
    /// bounds, render layer, bone count, format word).</summary>
    internal void ApplyImported(ImportedMesh src)
    {
        for (int c = 0; c < 3; c++)
        {
            WriteRawF(4 * c, src.Min[c]);
            WriteRawF(12 + 4 * c, src.Max[c]);
        }
        RenderLayer = (uint)src.RenderLayer;
        BoneBindingsCount = (ushort)src.BoneBindingCount;
        NumBoneInfluences = src.FmtCode & 0xF;
        HasVertexAlpha = src.VertexSize == 24;
        IsRigid = !src.HasBones;
        OnPropertyChanged(nameof(Display));
    }

    /// <summary>Writes a float bit-pattern verbatim (bounds sentinel handling).</summary>
    internal void WriteRawF(int off, uint bits)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Rec.Slice(off, 4), bits);
        for (int k = 0; k < 3; k++)
        {
            if (off == 4 * k) OnPropertyChanged($"Min{(char)('X' + k)}");
            if (off == 12 + 4 * k) OnPropertyChanged($"Max{(char)('X' + k)}");
        }
        _doc.Touch();
    }

    private float ReadF(int off) => BinaryPrimitives.ReadSingleLittleEndian(Rec.Slice(off, 4));

    private string RawF(int off) => ReadF(off).ToString("R", CultureInfo.InvariantCulture);

    private void SetF(int off, string text, string prop)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            WriteF(off, v, prop);
        OnPropertyChanged(prop + "Text");
    }

    private void WriteF(int off, float value, string prop)
    {
        if (ReadF(off) == value) return;
        BinaryPrimitives.WriteSingleLittleEndian(Rec.Slice(off, 4), value);
        OnPropertyChanged(prop);
        _doc.Touch();
    }
}
