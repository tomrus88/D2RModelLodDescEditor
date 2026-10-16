using System.Buffers.Binary;
using System.Text;

namespace Granny.Format;

/// <summary>One resolved pointer relocation: a 4-byte slot plus its target.</summary>
public readonly record struct Relocation(uint FromOffset, ushort ToSection, uint ToOffset);

/// <summary>
/// A fully decompressed, relocated view of a grn file.
/// </summary>
/// <remarks>
/// <para>
/// Two facts about the pointer fixups drive this class. Both were established
/// empirically and then cross-checked against real assets.
/// </para>
/// <para>
/// <b>The slots are 4 bytes, not 8.</b> Every sampled fixup lands on a
/// 4-aligned offset and roughly half sit at <c>4 mod 8</c>, so an 8-byte slot
/// is impossible. Writing 8 bytes (as an in-memory tagged pointer would)
/// overruns the following field and corrupts bulk payloads that happen to sit
/// beside a pointer slot. A file reader writes exactly 4 bytes and keeps the
/// target's section index separately - see <see cref="Relocation"/>.
/// </para>
/// <para>
/// <b>A fixup's slot lives in the section that owns the fixup blob.</b>
/// <c>FromOffset</c> is an offset into the decompressed bytes of the section
/// whose header declares the blob, <em>not</em> into
/// <c>ToSection</c>. Every fixup decoded from section <c>i</c> therefore patches
/// <c>Sections[i]</c>. <c>ToSection</c> is retained purely as metadata: it says
/// where the slot ultimately dereferences and is exposed through
/// <see cref="TryResolve"/>, but it does not select the buffer being written.
/// (Applying each blob to the section named by <c>ToSection</c> instead
/// silently corrupts every cross-section pointer.)
/// </para>
/// <para>
/// Relocations patch <em>structure</em> only. Bulk payloads (index buffers,
/// vertex streams) are read from <see cref="Raw"/> so a pointer slot landing
/// inside them cannot corrupt the data.
/// </para>
/// </remarks>
public sealed class GrnImage
{
    private readonly GrnFile _file;
    private readonly List<Relocation>[] _bySection;

    /// <summary>Decompressed section bytes, before any relocation.</summary>
    public IReadOnlyList<byte[]> Raw { get; }

    /// <summary>Decompressed section bytes, after all relocations.</summary>
    public IReadOnlyList<byte[]> Sections { get; }

    public GrnFile File => _file;

    public GrnImage(string path) : this(new GrnFile(path)) { }

    public GrnImage(GrnFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));

        int n = file.Sections.Count;
        var raw = new byte[n][];
        var fixedUp = new byte[n][];
        _bySection = new List<Relocation>[n];

        for (int i = 0; i < n; i++)
        {
            Section s = file.Sections[i];
            byte[] d = s.IsBitKnit2
                ? new BitKnit2().Decompress(
                    file.Data.AsSpan((int)s.DataOffset, (int)s.DataSize), (int)s.ExpandedSize)
                : file.Data.AsSpan((int)s.DataOffset, (int)s.DataSize).ToArray();
            raw[i] = d;
            fixedUp[i] = (byte[])d.Clone();
            _bySection[i] = [];
        }

        // Pass 1: decode every blob and record where each relocation points.
        for (int i = 0; i < n; i++)
        {
            Section s = file.Sections[i];
            if (s.PointerFixupCount == 0 || s.PointerFixupOffset == 0)
            {
                continue;
            }

            int bytes = checked((int)s.PointerFixupCount * FixupBlob.PointerElementSize);
            byte[]? blob = FixupBlob.Read(file.Data, (int)s.PointerFixupOffset, bytes);
            if (blob is null)
            {
                continue;
            }

            for (uint k = 0; k < s.PointerFixupCount; k++)
            {
                int o = (int)k * FixupBlob.PointerElementSize;
                uint from = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o));
                uint toSec = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o + 4));
                uint toOff = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o + 8));

                if (toSec >= (uint)n)
                {
                    continue;
                }
                _bySection[i].Add(new Relocation(from, (ushort)toSec, toOff));
            }
        }

        // Pass 2: apply each slot to the section it actually targets.
        for (int i = 0; i < n; i++)
        {
            foreach (Relocation r in _bySection[i])
            {
                if (r.FromOffset + 4 > (uint)fixedUp[i].Length)
                {
                    continue;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(
                    fixedUp[i].AsSpan((int)r.FromOffset), r.ToOffset);
            }
        }

        Raw = raw;
        Sections = fixedUp;
    }

    /// <summary>All relocations emitted by <paramref name="section"/>.</summary>
    public IReadOnlyList<Relocation> Relocations(int section) =>
        (uint)section < (uint)_bySection.Length ? _bySection[section] : [];

    /// <summary>Where the pointer slot at <paramref name="fromOffset"/> points.</summary>
    public bool TryResolve(int section, uint fromOffset, out ushort toSection, out uint toOffset)
    {
        foreach (Relocation r in Relocations(section))
        {
            if (r.FromOffset == fromOffset)
            {
                toSection = r.ToSection;
                toOffset = r.ToOffset;
                return true;
            }
        }
        toSection = 0;
        toOffset = 0;
        return false;
    }

    public uint U32(int section, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Sections[section].AsSpan(offset, 4));

    public int I32(int section, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Sections[section].AsSpan(offset, 4));

    public float F32(int section, int offset) => BitConverter.Int32BitsToSingle(I32(section, offset));

    /// <summary>NUL-terminated Latin-1 string at <paramref name="offset"/>.</summary>
    public string Str(int section, int offset)
    {
        byte[] b = Sections[section];
        int end = Array.IndexOf(b, (byte)0, offset);
        if (end < 0)
        {
            end = b.Length;
        }
        return Encoding.Latin1.GetString(b, offset, end - offset);
    }

    /// <summary>Follows the pointer slot at <paramref name="offset"/>.</summary>
    public bool TryFollow(int section, uint offset, out ushort target, out uint targetOffset)
    {
        if (offset > int.MaxValue || offset + 4 > (uint)Sections[section].Length)
        {
            target = 0;
            targetOffset = 0;
            return false;
        }
        return TryResolve(section, offset, out target, out targetOffset);
    }
}

