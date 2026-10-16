using System.Buffers.Binary;
using System.Text;

namespace Granny.Format;

/// <summary>
/// One 44-byte type record from the file's type section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Established from data, not from the textbook.</b> The conventional
/// granny <c>grn_type</c> is 80 bytes in 32-bit form and does NOT fit these
/// files. The 44-byte stride was pinned by section 6 own pointer fixups on
/// <c>special1_rol_lod0.model</c>: all 438 land on exactly two residues
/// modulo 44 (+4, 365 of them, and +12, 73 of them). Two residues across 438
/// samples fixes the stride. The section length 18172 is exactly 413x44, and
/// <c>root_type_off</c> 11440 is exactly 260x44.
/// </para>
/// <para>
/// Layout:
/// <code>
/// +0   u32  tag     discriminator, observed 0..21
/// +4   u64  name    pointer to a string in another section (365 records)
/// +12  u64  link    pointer to another type record, same section (73)
/// +20  u32  count   nonzero in only 42 records (values 2,3,4,16)
/// +24  .. +44       zero in every one of the 413 records observed
/// </code>
/// The <c>name</c> and <c>link</c> slots read zero in the file and are filled
/// by <see cref="FixupApplier"/>; that is why an early field profile showed
/// them as always zero.
/// </para>
/// <para>
/// <b>Resolved.</b> This record is the SDK's own
/// <c>granny_data_type_definition</c> (44 bytes in the 64-bit build), and a
/// struct's members are declared by a <em>sibling list of records</em>
/// terminated by an <c>EndMember</c> record (tag 0), not by
/// <see cref="Count"/>. <see cref="Count"/> is <c>ArrayWidth</c>: zero for
/// scalars and pointers, the element count for inline arrays (e.g. 3 for
/// art_tool_info's Origin/Right/Up/Back vectors, which is what pinned
/// <c>ArrayWidth</c> at +20 rather than +16). <see cref="Link"/> is
/// <c>ReferenceType</c>: a pointer to the first record of <em>another</em>
/// sibling list in the same section, giving the pointee's member list.
/// See <see cref="GrnTypeTree"/> and GRN_FORMAT.md, "Uncompressed section
/// payload format".
/// </para>
/// </remarks>
public readonly record struct GrnTypeRecord(uint Tag, ulong Name, ulong Link, uint Count)
{
    /// <summary>Size of one file-form type record.</summary>
    public const int Size = 44;

    /// <summary>Highest tag seen in sampled files; the enum is wider.</summary>
    public const uint MaxObservedTag = 21;

    /// <summary>Reads the raw 44 bytes. Pointers are still zero.</summary>
    public static GrnTypeRecord Read(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Size)
        {
            throw new ArgumentException(
                $"type record needs {Size} bytes, got {raw.Length}");
        }

        return new GrnTypeRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw),
            BinaryPrimitives.ReadUInt64LittleEndian(raw[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(raw[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]));
    }
}

/// <summary>
/// The type table of a single file: every type record in the section holding
/// the root type. Types are addressed by record index, which is simply
/// <c>offset / GrnTypeRecord.Size</c>.
/// </summary>
public sealed class TypeTable
{
    private GrnTypeRecord[] _records = [];
    private readonly byte[] _section;

    public TypeTable(byte[] section, int sectionIndex)
    {
        ArgumentNullException.ThrowIfNull(section);
        SectionIndex = sectionIndex;
        _section = section;
        Load();
    }

    /// <summary>Re-reads every record from the current bytes.</summary>
    private void Load()
    {
        int count = _section.Length / GrnTypeRecord.Size;
        _records = new GrnTypeRecord[count];
        for (int i = 0; i < count; i++)
        {
            _records[i] = GrnTypeRecord.Read(
                _section.AsSpan(i * GrnTypeRecord.Size, GrnTypeRecord.Size));
        }
    }

    public int SectionIndex { get; }

    /// <summary>Number of whole 44-byte records in the section.</summary>
    public int Count => _records.Length;

    /// <summary>Record index the file header names as the root type.</summary>
    public int RootIndex { get; init; }

    /// <summary>Trailing bytes that did not fill a whole record.</summary>
    public int TrailingBytes => _section.Length - (Count * GrnTypeRecord.Size);

    public GrnTypeRecord this[int index] =>
        (uint)index < (uint)_records.Length
            ? _records[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// Applies this section's pointer fixups in place, populating the
    /// <c>name</c> and <c>link</c> slots.
    /// </summary>
    /// <returns>How many pointers were written.</returns>
    public int ApplyFixups(IReadOnlyList<byte[]?> sections,
                           ReadOnlySpan<PointerFixup> fixups)
    {
        ArgumentNullException.ThrowIfNull(sections);
        int n = FixupApplier.Apply(sections, _section, fixups);
        Load();  // pointers were just written into the bytes
        return n;
    }

    /// <summary>Index of the type a record links to, if any.</summary>
    public bool TryGetLinkIndex(GrnTypeRecord record, out int index)
    {
        index = -1;
        if (record.Link == FixupApplier.NullPointer)
        {
            return false;
        }

        if (FixupApplier.SectionOf(record.Link) != (uint)SectionIndex)
        {
            return false;
        }

        uint offset = FixupApplier.OffsetOf(record.Link);
        if (offset % GrnTypeRecord.Size != 0)
        {
            return false;
        }

        index = (int)(offset / GrnTypeRecord.Size);
        return (uint)index < (uint)Count;
    }

    /// <summary>
    /// Resolves a record name pointer to text, or null when absent.
    /// </summary>
    public string? GetName(GrnTypeRecord record, IReadOnlyList<byte[]?> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (record.Name == FixupApplier.NullPointer)
        {
            return null;
        }

        uint section = FixupApplier.SectionOf(record.Name);
        uint offset = FixupApplier.OffsetOf(record.Name);
        if (section >= (uint)sections.Count)
        {
            return null;
        }

        byte[]? target = sections[(int)section];
        if (target is null || offset >= (uint)target.Length)
        {
            return null;
        }

        int start = (int)offset;
        int end = start;
        while (end < target.Length && target[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(target, start, end - start);
    }
}
