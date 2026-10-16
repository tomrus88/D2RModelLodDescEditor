using System.Buffers.Binary;
using System.Text;

namespace Granny.Format;

/// <summary>
/// A parsed grn_file_magic_value: magic, header, and the section array.
/// </summary>
/// <remarks>
/// This part of the format is fully understood and is validated against
/// every sampled asset (762/762 <c>.model</c> files parse cleanly).
/// </remarks>
public sealed class GrnFile
{
    public const int MagicSize = 32;
    public const int HeaderSize = 72;
    public const int SectionRecordSize = Section.Size;

    /// <summary>Win32/32-bit/little-endian platform magic seen in D2R files.</summary>
    public static ReadOnlySpan<uint> ExpectedMagic =>
        [0x5E499BE5u, 0x141F636Fu, 0xA9EB131Eu, 0xC4EDBE90u];

    public string Path { get; }
    public byte[] Data { get; }

    public uint Magic0 { get; }
    public uint Magic1 { get; }
    public uint Magic2 { get; }
    public uint Magic3 { get; }

    /// <summary>Magic + header + section array; the end of the fixed part.</summary>
    public uint MagicHeaderSize { get; }

    public uint HeaderFormat { get; }
    public uint Version { get; }
    public uint TotalSize { get; }
    public uint Crc { get; }
    public uint SectionArrayOffset { get; }
    public uint SectionArrayCount { get; }
    public uint RootTypeSection { get; }
    public uint RootTypeOffset { get; }
    public uint RootObjectSection { get; }
    public uint RootObjectOffset { get; }
    public uint TypeTag { get; }

    public IReadOnlyList<Section> Sections { get; }

    /// <summary>End of the last section payload, where the fixup arrays begin.</summary>
    public uint SectionDataEnd { get; }

    public GrnFile(string path)
    {
        Path = path;
        Data = File.ReadAllBytes(path);

        if (Data.Length < MagicSize + HeaderSize)
        {
            throw new InvalidDataException($"file too small ({Data.Length} bytes)");
        }

        Magic0 = ReadU32(0);
        Magic1 = ReadU32(4);
        Magic2 = ReadU32(8);
        Magic3 = ReadU32(12);
        MagicHeaderSize = ReadU32(16);
        HeaderFormat = ReadU32(20);
        ReadU32(24); // reserved r0
        ReadU32(28); // reserved r1

        Version = ReadU32(MagicSize + 0);
        TotalSize = ReadU32(MagicSize + 4);
        Crc = ReadU32(MagicSize + 8);
        SectionArrayOffset = ReadU32(MagicSize + 12);
        SectionArrayCount = ReadU32(MagicSize + 16);
        RootTypeSection = ReadU32(MagicSize + 20);
        RootTypeOffset = ReadU32(MagicSize + 24);
        RootObjectSection = ReadU32(MagicSize + 28);
        RootObjectOffset = ReadU32(MagicSize + 32);
        TypeTag = ReadU32(MagicSize + 36);

        uint arrayBase = MagicSize + SectionArrayOffset;
        var sections = new List<Section>((int)SectionArrayCount);
        for (int i = 0; i < SectionArrayCount; i++)
        {
            int o = (int)arrayBase + (i * SectionRecordSize);
            sections.Add(Section.Read(Data.AsSpan(o, SectionRecordSize)));
        }
        Sections = sections;

        uint cursor = MagicHeaderSize;
        foreach (var s in Sections)
        {
            cursor = s.DataOffset + s.DataSize;
        }
        SectionDataEnd = cursor;
    }

    private uint ReadU32(int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset, 4));

    /// <summary>The compressed payload of one section.</summary>
    public ReadOnlySpan<byte> SectionData(int index)
    {
        var s = Sections[index];
        return Data.AsSpan((int)s.DataOffset, (int)s.DataSize);
    }

    /// <summary>
    /// Returns a list of structural problems; empty means fully consistent.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errs = new List<string>();

        uint[] magic = [Magic0, Magic1, Magic2, Magic3];
        if (!magic.AsSpan().SequenceEqual(ExpectedMagic))
        {
            errs.Add($"magic {string.Join(' ', magic.Select(m => m.ToString("x8")))} != expected");
        }

        if (HeaderFormat != 0)
        {
            errs.Add($"HeaderFormat={HeaderFormat} (must be 0)");
        }

        if (TotalSize != (uint)Data.Length)
        {
            errs.Add($"TotalSize={TotalSize} but file is {Data.Length} bytes");
        }

        uint want = MagicSize + SectionArrayOffset + (SectionArrayCount * SectionRecordSize);
        if (want != MagicHeaderSize)
        {
            errs.Add($"magic.HeaderSize={MagicHeaderSize} but magic+header+array={want}");
        }

        // Sections are laid out in order and must end exactly where the
        // post-section fixup arrays (which live outside section payloads)
        // begin.
        uint cursor = MagicHeaderSize;
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (s.DataOffset != cursor)
            {
                errs.Add($"section {i} DataOffset={s.DataOffset}, expected {cursor}");
            }
            if (s.DataOffset + s.DataSize > (uint)Data.Length)
            {
                errs.Add($"section {i} runs past EOF");
            }
            cursor = s.DataOffset + s.DataSize;
        }

        // Fixup blobs live after every section payload, so their OFFSETS must
        // not fall inside section data.
        //
        // Their COUNTS must NOT be range-checked. Verified against d2r.exe's
        // reader (granny::PlatformConvertReaderToWriterAlignedAndChangeFormat,
        // 0x144248850, granny_file.cpp), which hands the counts to an
        // incremental *compressed* blob reader:
        //     IncrementallyReadCompressedFixupBlobAt(a2[7], ..., 12 * a2[8], ...)   // ptr
        //     IncrementallyReadCompressedFixupBlobAt(a2[9], ..., 16 * a2[10], ...)  // mix
        // The multiplier is the DECOMPRESSED element size (12 and 16 bytes), and
        // the blob itself is compressed, so `offset + count * stride` has no
        // relationship to the file length. Checking it that way flagged ten
        // perfectly valid assets - the files were never malformed, the check
        // was wrong.
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            foreach (var (tag, off, count) in new[]
                     {
                         ("ptrFixup", s.PointerFixupOffset, s.PointerFixupCount),
                         ("mixFixup", s.MixedFixupOffset, s.MixedFixupCount),
                     })
            {
                if (count == 0)
                {
                    continue;
                }
                if (off < cursor)
                {
                    errs.Add($"section {i} {tag} starts inside section data ({off} < {cursor})");
                }
                if (off > (uint)Data.Length)
                {
                    // The offset itself must be inside the file; the count
                    // is a decompressed element total and cannot be checked.
                    errs.Add($"section {i} {tag} offset {off} is past EOF ({Data.Length})");
                }
            }
        }

        return errs;
    }

    /// <summary>Short human-readable summary.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Path);
        sb.AppendLine($"  magic    : {Magic0:x8} {Magic1:x8} {Magic2:x8} {Magic3:x8}");
        sb.AppendLine($"  version  : {Version}  crc={Crc:x8}");
        sb.AppendLine($"  size     : total={TotalSize} hdr={MagicHeaderSize} sections={SectionArrayCount}");
        sb.AppendLine($"  root     : type={RootTypeSection}/{RootTypeOffset} object={RootObjectSection}/{RootObjectOffset}");
        sb.AppendLine("  sections :");
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            sb.AppendLine($"    {i}: fmt={s.Format} data={s.DataOffset}+{s.DataSize} " +
                          $"expanded={s.ExpandedSize} align={s.Alignment} " +
                          $"pf={s.PointerFixupOffset}+{s.PointerFixupCount} " +
                          $"mf={s.MixedFixupOffset}+{s.MixedFixupCount}");
        }
        return sb.ToString();
    }
}