using System.Buffers.Binary;

namespace Granny.Format;

/// <summary>
/// A compressed fixup blob: a 4-byte compressed length followed by that many
/// bytes of BitKnit2 stream.
/// </summary>
/// <remarks>
/// <para>
/// Layout confirmed two independent ways, which agreed exactly.
/// </para>
/// <para>
/// Measured on <c>special1_rol_lod0.model</c>: the blob at the pointer-fixup
/// offset does NOT begin with the BitKnit2 tag; a <c>u32</c> length sits at
/// +0 and the <c>0x75B1</c> tag at +4.  Decoding the declared span yields
/// 212 * 12 = 2544 bytes with 833 non-zero, and the mixed blob decodes to
/// 1 * 16 = 16 bytes with 7 non-zero - both fully populated, so this is the
/// right framing rather than a coincidence.
/// </para>
/// <para>
/// Then confirmed from the game's own reader,
/// <c>IncrementallyReadCompressedFixupBlobAt</c> (bg3 @ 0x148500AA0, which
/// has full symbols as granny 2.12.0.0):
/// <code>
/// if ( ReadAtMostCallback(Reader, Offset, 4, CompressedSize) == 4 ) {
///     if ( Reverse ) Reverse32(4u, CompressedSize);
///     CompressedDataAt = IncrementallyReadCompressedDataAt(
///                         Offset + 4, Reader, Reverse, Format,
///                         CompressedSize[0], ... );
/// }
/// </code>
/// So: read a length at <c>Offset</c>, decode the stream at <c>Offset + 4</c>
/// for exactly that many bytes.
/// </para>
/// <para>
/// Note that the section's <c>count</c> is a count of <em>decompressed</em>
/// elements, not a byte span: 12 bytes per pointer fixup and 16 per mixed
/// fixup.  That is why range-checking <c>count * 4</c> against the file
/// length is meaningless.
/// </para>
/// </remarks>
public static class FixupBlob
{
    /// <summary>Decompressed size of one pointer-fixup element.</summary>
    public const int PointerElementSize = 12;

    /// <summary>Decompressed size of one mixed-marshalling element.</summary>
    public const int MixedElementSize = 16;

    /// <summary>Size of the compressed-length prefix.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Reads a fixup blob at <paramref name="offset"/> and returns the
    /// decompressed <paramref name="decompressedSize"/> bytes.
    /// </summary>
    /// <param name="data">Whole file contents.</param>
    /// <param name="offset">Blob offset, i.e. the section's fixup offset.</param>
    /// <param name="decompressedSize">
    /// Element count times the element size (12 or 16), never a file span.
    /// </param>
    /// <returns>
    /// The decompressed elements, or <see langword="null"/> when the blob is
    /// absent, truncated, or the length prefix does not fit.
    /// </returns>
    public static byte[]? Read(ReadOnlySpan<byte> data, int offset,
                                int decompressedSize)
    {
        if (decompressedSize <= 0)
        {
            return null;
        }

        if (offset < 0 || offset + HeaderSize > data.Length)
        {
            return null;
        }

        int compressedSize = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        if (compressedSize <= 0 ||
            offset + HeaderSize + compressedSize > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> stream = data.Slice(offset + HeaderSize, compressedSize);
        return new BitKnit2().Decompress(stream, decompressedSize);
    }

    /// <summary>
    /// Reads a section's pointer-fixup blob. Returns an empty array when the
    /// section declares none.
    /// </summary>
    public static byte[] ReadPointers(ReadOnlySpan<byte> data, Section s) =>
        Read(data, checked((int)s.PointerFixupOffset),
             checked((int)s.PointerFixupCount * PointerElementSize))
        ?? [];

    /// <summary>
    /// Reads a section's mixed-marshalling-fixup blob.
    /// </summary>
    public static byte[] ReadMixed(ReadOnlySpan<byte> data, Section s) =>
        Read(data, checked((int)s.MixedFixupOffset),
             checked((int)s.MixedFixupCount * MixedElementSize))
        ?? [];
}

/// <summary>
/// One 12-byte pointer-fixup element:
/// <c>{ u32 FromOffset; u32 ToSectionIndex; u32 ToOffset; }</c>.
/// </summary>
/// <remarks>
/// Field roles are confirmed two independent ways.
/// <para>
/// First, from the game's own reader, <c>granny::GRNFixUp</c>
/// (bg3 @ 0x1484BE000, granny 2.12.0.0), whose entire body is:
/// <code>
/// for (each fixup) {
///     char* base = (char*)ToSections[v6-&gt;To.SectionIndex];
///     *(u64*)&amp;FromSectionInit[v6-&gt;FromOffset] = base;
///     if (base != nullptr)
///         *(u64*)&amp;FromSectionInit[v6-&gt;FromOffset] = &amp;base[v6-&gt;To.Offset];
/// }
/// </code>
/// </para>
/// <para>
/// Second, empirically on <c>special1_rol_lod0.model</c> (212 records): the
/// second field is always in <c>0..6</c> for an 8-section file, the third never
/// exceeds 13376 against a 28132-byte section, and the first never exceeds
/// 4448 — matching section index, target offset, and source offset
/// respectively.
/// </para>
/// <para>
/// Note this is an 8-byte write even on little-endian targets; the stored
/// value is a full pointer, not a 4-byte index.
/// </para>
/// </remarks>
public readonly record struct PointerFixup(uint FromOffset, uint ToSectionIndex, uint ToOffset)
{
    public const int Size = FixupBlob.PointerElementSize;

    public static PointerFixup Read(ReadOnlySpan<byte> raw) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(raw),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]));
}

/// <summary>
/// Applies pointer fixups to decompressed section data.
/// </summary>
/// <remarks>
/// <para>
/// The game applies fixups in two places, and it matters which one you mirror.
/// <c>granny::FixupFileSectionPhase1</c> (bg3 @ 0x1485000C0) runs
/// <c>GRNFixUp</c> for a section <b>unconditionally</b> — no byte-order gate.
/// That is the path native little-endian D2R files take.
/// </para>
/// <para>
/// <c>FixupFileSectionPhase2</c> (@ 0x1485001D0) is the marshalling path: its
/// whole body sits inside <c>if (File-&gt;IsByteReversed != 0)</c>, and it runs
/// <c>GRNMarshall</c> then <c>GRNFixUp</c> again. It is only for byte-swapped
/// files and must NOT be used as evidence that fixups are skippable.
/// </para>
/// <para>
/// On disk every affected pointer slot reads zero before fixups run — verified
/// on all 212 records of <c>special1_rol_lod0.model</c> — so this step is
/// mandatory, not an optimisation.
/// </para>
/// <para>
/// <para>
/// Because .NET cannot hand out stable interior pointers into managed arrays,
/// a resolved pointer is stored as a tagged virtual address: the section index
/// in the high 32 bits and the byte offset in the low 32. Use
/// <see cref="SectionOf"/> and <see cref="OffsetOf"/> to take it apart.
/// </para>
/// <para>
/// <b>Scope: this is the type-tree path only.</b> It deliberately differs from
/// <see cref="GrnImage"/>, which is the path the mesh and OBJ readers use.
/// The two write different widths on purpose:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="GrnImage"/> writes <b>4 bytes</b> (the bare offset) and keeps the
/// target section index beside it in <see cref="GrnImage.Relocations"/>. Slots
/// sit at <c>4 mod 8</c> about half the time, so an 8-byte write would overrun
/// the next field.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="FixupApplier"/> writes <b>8 bytes</b> holding the tagged address,
/// because <see cref="TypeTree"/> has to walk arbitrary linked structures and
/// needs to recover both halves in one load.
/// </description>
/// </item>
/// </list>
/// <para>
/// Do not use this class to read data-section pointers: pick
/// <see cref="GrnImage"/> for that. It is kept for type-tree link resolution,
/// which it supports and which is covered by tests.
/// </para>
/// </remarks>
public static class FixupApplier
{
    /// <summary>The value the game writes when the target section is null.</summary>
    public const ulong NullPointer = 0;

    /// <summary>Builds the 8-byte value stored in a fixed-up pointer slot.</summary>
    public static ulong MakePointer(uint sectionIndex, uint offset) =>
        ((ulong)sectionIndex << 32) | offset;

    /// <summary>Section index of a pointer produced by <see cref="MakePointer"/>.</summary>
    public static uint SectionOf(ulong pointer) => (uint)(pointer >> 32);

    /// <summary>Byte offset within the section of a fixed-up pointer.</summary>
    public static uint OffsetOf(ulong pointer) => (uint)pointer;

    /// <summary>
    /// Applies <paramref name="fixups"/> to <paramref name="section"/> in place.
    /// </summary>
    /// <param name="sections">
    /// All decompressed sections; an entry may be <see langword="null"/> to
    /// model an unloaded section, in which case targets resolve to
    /// <see cref="NullPointer"/> exactly as the game does.
    /// </param>
    /// <param name="section">Decompressed bytes of the section being patched.</param>
    /// <param name="fixups">Fixup records belonging to that section.</param>
    /// <returns>How many pointers were written.</returns>
    public static int Apply(IReadOnlyList<byte[]?> sections, byte[] section,
                            ReadOnlySpan<PointerFixup> fixups)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(section);

        if (fixups.IsEmpty)
        {
            return 0;
        }

        int written = 0;
        foreach (PointerFixup fixup in fixups)
        {
            long slot = fixup.FromOffset;
            if (slot < 0 || slot + 8 > section.Length)
            {
                // The game would scribble past the end here; refuse instead.
                continue;
            }

            ulong value = NullPointer;
            if ((uint)fixup.ToSectionIndex < (uint)sections.Count)
            {
                byte[]? target = sections[(int)fixup.ToSectionIndex];
                if (target is not null)
                {
                    value = MakePointer(fixup.ToSectionIndex, fixup.ToOffset);
                }
            }

            BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan((int)slot), value);
            written++;
        }

        return written;
    }

    /// <summary>Reads a fixed-up pointer back out of section data.</summary>
    public static ulong ReadPointer(ReadOnlySpan<byte> section, uint fromOffset)
    {
        int at = (int)fromOffset;
        if (at < 0 || at + 8 > section.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromOffset), fromOffset,
                "Pointer slot lies outside the section.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(section[at..]);
    }
}

/// <summary>
/// One 16-byte mixed-marshalling fixup element: four raw 32-bit fields.
/// </summary>
/// <remarks>
/// Only the size and count semantics are established. The fields are exposed
/// raw because, unlike pointer fixups, these are only consumed by
/// <c>GRNMarshall</c> on the byte-reversed path, which D2R never takes — so
/// there is nothing in D2R data to validate a field layout against.
/// </remarks>
public readonly record struct MixedFixup(uint A, uint B, uint C, uint D)
{
    public const int Size = FixupBlob.MixedElementSize;

    public static MixedFixup Read(ReadOnlySpan<byte> raw) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(raw),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]));
}