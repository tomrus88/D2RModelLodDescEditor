using System.Buffers.Binary;

namespace Granny.Format;

/// <summary>
/// One 44-byte grn_section_magic_value record.
/// </summary>
/// <remarks>
/// Layout is fixed by the container spec and verified against every
/// sampled D2R asset: <c>fmt</c> is 4 for every populated section in
/// these files, which is BitKnit2Compression. Granny 2.7 cannot decode it.
/// </remarks>
public sealed class Section
{
    public const int Size = 44;

    public uint Format { get; init; }
    public uint DataOffset { get; init; }
    public uint DataSize { get; init; }
    public uint ExpandedSize { get; init; }
    public uint Alignment { get; init; }
    public uint First16 { get; init; }
    public uint First8 { get; init; }

    /// <summary>Offset of the pointer-fixup array (post-section data).</summary>
    public uint PointerFixupOffset { get; init; }
    public uint PointerFixupCount { get; init; }

    /// <summary>Offset of the mixed-marshalling-fixup array.</summary>
    public uint MixedFixupOffset { get; init; }
    public uint MixedFixupCount { get; init; }

    /// <summary>True when the payload is BitKnit2 compressed.</summary>
    public bool IsBitKnit2 => Format == BitKnit2.FormatCode;

    public static Section Read(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Size)
        {
            throw new ArgumentException($"section record needs {Size} bytes, got {raw.Length}");
        }

        Span<uint> f = stackalloc uint[11];
        for (int i = 0; i < 11; i++)
        {
            f[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]);
        }

        return new Section
        {
            Format = f[0],
            DataOffset = f[1],
            DataSize = f[2],
            ExpandedSize = f[3],
            Alignment = f[4],
            First16 = f[5],
            First8 = f[6],
            PointerFixupOffset = f[7],
            PointerFixupCount = f[8],
            MixedFixupOffset = f[9],
            MixedFixupCount = f[10],
        };
    }
}