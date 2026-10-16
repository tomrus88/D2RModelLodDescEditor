using System.Buffers.Binary;
using System.Text;

namespace Granny.Format;

/// <summary>
/// One member read out of an object by <see cref="GrnObjectWalker"/>.
/// </summary>
/// <param name="Node">The type-tree node describing this member.</param>
/// <param name="Section">Section the value was read from.</param>
/// <param name="Offset">Offset of the value within that section.</param>
/// <param name="Number">
/// Scalar value as a raw integer (a <c>Real32</c> member keeps its IEEE-754
/// bit pattern, so nothing is lost and no precision is implied), or the element
/// count for an inline array. Null for references.
/// </param>
/// <param name="Text">String value, or null when the member is not a string.</param>
/// <param name="Children">Nested members, or null for a scalar or a reference.</param>
public sealed record GrnMemberValue(
    GrnTypeNode Node,
    ushort Section,
    int Offset,
    long? Number,
    string? Text,
    IReadOnlyList<GrnMemberValue>? Children);

/// <summary>
/// Reads arbitrary granny objects by walking a type tree, so structures need
/// no hard-coded offsets.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MeshReader"/> hard-codes offsets for the handful of structs it
/// needs. This walks the same data through <see cref="GrnTypeTree"/>, which is
/// what the tree is stored for: given the member list of a type, each member's
/// width follows from its <see cref="GrnMemberType"/>, so any structure granny
/// defined can be read without a matching constant in <see cref="GrannyLayout"/>.
/// </para>
/// <para>
    /// <b>Array count prefix.</b> The type tree lists an array member but NOT
    /// the element count that precedes it in the struct. A granny struct stores
    /// a 32-bit count and then the pointer, so a tree-only walk drifts 4 bytes
    /// per array. Measured on <c>torso_lod0.model</c>, the root object's
    /// relocations are at 0, 16, 28, 40, 52, 76, 88 and 100; skipping the
    /// counts puts <c>Meshes</c> at 64 (unresolvable), while counting them puts
    /// it at 88, which resolves and matches
    /// <see cref="GrannyLayout.FileMeshes"/>.
    /// </para>
/// <para>
/// <b>Layout rule.</b> A granny struct lays its members out end to end. A
    /// reference member occupies <see cref="ReferenceFieldSize"/> bytes; the
    /// object it points at is stored <em>elsewhere</em> in the section, which is
    /// precisely why the file needs fixups at all. So a reference's
    /// <c>ReferenceType</c> member list is walked at the <em>target</em>, never
    /// inline. Every other kind is inline data and advances by its own width
    /// times <c>ArrayWidth</c>.
    /// </para>
/// </remarks>
public sealed class GrnObjectWalker
{
    /// <summary>Bytes a relocation WRITES at a pointer slot.</summary>
    /// <remarks>
    /// 4, not 8. Every sampled fixup lands on a 4-aligned offset and roughly
    /// half sit at <c>4 mod 8</c>, so an 8-byte write is impossible: it would
    /// overrun the following field.
    /// </remarks>
    public const int PointerSlotSize = 4;

    /// <summary>Bytes a reference member OCCUPIES in the struct layout.</summary>
    /// <remarks>
    /// <para>
    /// 8, even though <see cref="PointerSlotSize"/> is 4. granny keeps its
    /// 64-bit field layout and narrows only the relocated slot, so the field
    /// spans 8 bytes while the pointer inside it is 4.
    /// </para>
    /// <para>
    /// Measured on <c>torso_lod0.model</c>: the root object's relocations land
    /// at 0, 16, 28, 40, 52, 76, 88 and 100. Walking with a 4-byte reference
    /// stride puts <c>FromFileName</c> at 8 and <c>Textures</c> at 12, neither of
    /// which resolves; with an 8-byte stride they land on 16 and 28, which do.
    /// Those match the 64-bit <see cref="GrannyLayout"/> offsets exactly.
    /// </para>
    /// </remarks>
    public const int ReferenceFieldSize = 8;

    /// <summary>Guards against a type list that fails to self-terminate.</summary>
    private const int MaxMembers = 4096;

    /// <summary>
    /// Sanity bound for an array element count. Real counts are far below this
    /// (the largest corpus mesh has 60,807 indices), so exceeding it means the
    /// cursor has drifted onto data that is not a count.
    /// </summary>
    private const int MaxArrayElements = 1 << 24;

    /// <summary>Observed element stride for array descriptors, -1 when none fit.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _arrayStrides = new();

    public static IReadOnlyDictionary<int, long> ArrayStrides =>
        new Dictionary<int, long>(_arrayStrides);

    public static void ResetArrayStrides() => _arrayStrides.Clear();

    private static long _unknownMemberCount;
    private static long _suspiciousCountCount;

    /// <summary>Element cap for an inline array descent, as a display guard.</summary>
    private const int MaxInlineArrayElements = 1024;

    /// <summary>Depth cap for the object walk; serialized graphs are shallow.</summary>
    private const int MaxWalkDepth = 32;

    /// <summary>
    /// Member kinds encountered so far that <see cref="SizeOf"/> does not know.
    /// A non-zero value means some subtree was skipped, not that data is wrong.
    /// </summary>
    public static long UnknownMemberCount => Interlocked.Read(ref _unknownMemberCount);

    public static void ResetUnknownMemberCount() =>
    Interlocked.Exchange(ref _unknownMemberCount, 0);

    /// <summary>
    /// Array members whose presumed count prefix failed validation, meaning the
    /// cursor was not where the layout rule expected.
    /// </summary>
    public static long SuspiciousCountCount => Interlocked.Read(ref _suspiciousCountCount);

    public static void ResetSuspiciousCountCount() =>
        Interlocked.Exchange(ref _suspiciousCountCount, 0);

    /// <summary>
    /// Inline width of one member of the given kind, or -1 when unknown.
    /// </summary>
    /// <remarks>
    /// These are granny's own unit sizes: the Size64 column of
    /// <c>MemberTypeInfo[]</c> in the SDK's <c>granny_data_type_definition.cpp</c>,
    /// cross-checked against the size table the 2.11.8 DLL indexes in
    /// <c>GetMemberUnitSize</c> (every entry matches; pointers occupy 8 bytes
    /// in these 64-bit-layout files). A struct member (<c>Inline</c>) has no
    /// constant width: it is the total size of its <c>ReferenceType</c>, so
    /// <see cref="TotalSize"/> computes it instead, and returns -1 here.
    /// </remarks>
    public static int SizeOf(GrnMemberType t) => t switch
    {
        // A sentinel, not a field: granny emitted it in an older revision to mark
        // a deleted member. It occupies no bytes and must not be treated as a
        // real member. If one ever appears it will not advance the cursor.
        GrnMemberType.UnsupportedRemove => 0,

        GrnMemberType.End or GrnMemberType.OnePastLast => 0,
        GrnMemberType.Reference
            or GrnMemberType.ReferenceToArray
            or GrnMemberType.ArrayOfReferences
            or GrnMemberType.VariantReference
            or GrnMemberType.ReferenceToVariantArray
            or GrnMemberType.String
            or GrnMemberType.EmptyReference => PointerSlotSize,

        // Flags + 3 position + 4 orientation + 9 scale/shear floats = 68,
        // the size of granny_transform in both the SDK headers and the IDB.
        GrnMemberType.Transform => 68,

        GrnMemberType.Real32 or GrnMemberType.Int32 or GrnMemberType.UInt32 => 4,
        GrnMemberType.Int16 or GrnMemberType.UInt16 or GrnMemberType.Real16 => 2,
        GrnMemberType.Int8 or GrnMemberType.UInt8 => 1,
        GrnMemberType.NormalUInt8 or GrnMemberType.BinormalInt8 => 1,
        GrnMemberType.NormalUInt16 or GrnMemberType.BinormalInt16 => 2,

        _ => -1,
    };

    /// <summary>True for members that are a relocated pointer slot.</summary>
    public static bool IsReference(GrnMemberType t) => t is
        GrnMemberType.Reference or GrnMemberType.ReferenceToArray
        or GrnMemberType.ArrayOfReferences or GrnMemberType.VariantReference
        or GrnMemberType.ReferenceToVariantArray or GrnMemberType.String
        or GrnMemberType.EmptyReference;

    /// <summary>Bytes of the inline element-count prefix of an array member.</summary>
    public const int CountPrefixSize = 4;

    /// <summary>
    /// Total layout size of a member list: the sum of each member's unit size
    /// times its <c>ArrayWidth</c> (SDK <c>GetTotalObjectSize</c>).
    /// </summary>
    /// <remarks>
    /// Used for the two places where a member's width is not a constant: the
    /// total size of an <c>Inline</c> struct, and the element stride of a
    /// <c>ReferenceToArray</c> (whose elements are stored inline at the target
    /// rather than behind an array of pointer slots). Count-prefixed array
    /// members count their prefix here too, so this reproduces the file's
    /// struct offsets: bone = 8 + 4 + 68 + 64 + 4 + 16 = 164.
    /// </remarks>
    public static long TotalSize(IReadOnlyList<GrnTypeNode> members, int depth = 0)
    {
        long total = 0;
        foreach (GrnTypeNode m in members)
        {
            if (m.MemberType is GrnMemberType.End or GrnMemberType.OnePastLast)
            {
                break;
            }

            long unit;                switch (m.MemberType)
                {
                    case GrnMemberType.Inline:
                        unit = depth >= MaxWalkDepth ? 0 : TotalSize(m.ReferenceType, depth + 1);
                        break;
                    case GrnMemberType.Reference:
                    case GrnMemberType.String:
                    case GrnMemberType.EmptyReference:
                        // A plain reference field spans ReferenceFieldSize in
                        // the layout even though only PointerSlotSize of it is
                        // ever written by a fixup.
                        unit = ReferenceFieldSize;
                        break;
                    case GrnMemberType.ReferenceToArray:
                    case GrnMemberType.ArrayOfReferences:
                        unit = CountPrefixSize + ReferenceFieldSize;
                        break;
                case GrnMemberType.ReferenceToVariantArray:
                    unit = ReferenceFieldSize + CountPrefixSize + ReferenceFieldSize;
                    break;
                case GrnMemberType.VariantReference:
                    unit = ReferenceFieldSize * 2;
                    break;
                case GrnMemberType.UnsupportedRemove:
                    unit = 0;
                    break;
                default:
                    int s = SizeOf(m.MemberType);
                    unit = s < 0 ? 0 : s;
                    break;
            }

            total += unit * (m.ArrayWidth > 0 ? m.ArrayWidth : 1);
        }

        return total;
    }
    /// <summary>
    /// Walks one object of <paramref name="members"/> stored at
    /// (<paramref name="section"/>, <paramref name="offset"/>).
    /// </summary>
    public static IReadOnlyList<GrnMemberValue> Walk(
        GrnImage image,
        IReadOnlyList<GrnTypeNode> members,
        ushort section,
        int offset,
        int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(members);

        var outp = new List<GrnMemberValue>();
        if (depth > MaxWalkDepth)
        {
            return outp;
        }
        int cursor = offset;
        byte[] buf = image.Sections[section];

        foreach (GrnTypeNode node in members)
        {
            if (outp.Count >= MaxMembers)
            {
                break;
            }
            if (node.MemberType is GrnMemberType.End or GrnMemberType.OnePastLast)
            {
                break;
            }

            int width = node.ArrayWidth > 0 ? node.ArrayWidth : 1;
            int count = 0;

            // An array member is stored as a 32-bit element count followed by
            // the pointer. The tree names the array but not the count, so
            // consume it here or every later member shifts down by 4 bytes.
            if (node.MemberType is GrnMemberType.ArrayOfReferences
                                 or GrnMemberType.ReferenceToArray)
            {
                if (cursor + 4 > buf.Length)
                {
                    break;
                }

                count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(cursor, 4));

                // Validate the prefix instead of trusting it. An array member is
                // "count, then pointer", so when the count is non-zero the very
                // next slot must be a live relocation. Checking that turns the
                // assumption into something the data can confirm or refute: a
                // mis-drifted cursor reads a large number here and fails this
                // test, instead of printing a fabricated element count.
                bool follows = image.TryResolve(section, (uint)(cursor + 4),
                                                 out ushort _, out uint _);
                bool looksLikeCount = count >= 0 && count <= MaxArrayElements
                                      && (count == 0 || follows);

                if (!looksLikeCount)
                {
                    Interlocked.Increment(ref _suspiciousCountCount);
                    break;
                }

                outp.Add(new GrnMemberValue(
                    node with { Name = node.Name + "Count" }, section, cursor, count, null, null));
                cursor += 4;
            }

            if (IsReference(node.MemberType))
            {
                if (cursor + PointerSlotSize > buf.Length)
                {
                    break;
                }

                // The referenced member list describes the pointee, which lives
                // in whichever section the slot resolves to. Without a target
                // there is nothing to descend into, so record the slot itself
                // rather than inventing an empty object.
                IReadOnlyList<GrnMemberValue>? kids = null;
                if (node.MemberType == GrnMemberType.String)
                {
                    if (image.TryFollow(section, (uint)cursor, out ushort tsec, out uint toff) &&
                        tsec < image.Sections.Count && toff < (uint)image.Sections[tsec].Length)
                    {
                        string s = image.Str(tsec, (int)toff);
                        outp.Add(new GrnMemberValue(node, section, cursor, null, s, null));
                        cursor += ReferenceFieldSize;
                        continue;
                    }
                }
                else if (node.MemberType == GrnMemberType.VariantReference)
                {
                    // A variant is two pointer fields: Type at the member start
                    // and Object eight bytes in. The member list describes the
                    // payload, so descend through Object, never through Type
                    // (Type points at a type record in the type section, not at
                    // an object). Total span 16.
                    IReadOnlyList<GrnTypeNode>? vmembers =
                        VariantMembers(image, node, section, cursor);
                    if (vmembers is not null &&
                        image.TryFollow(section, (uint)(cursor + ReferenceFieldSize),
                                         out ushort vsec, out uint voff) &&
                        vsec < image.Sections.Count)
                    {
                        kids = Walk(image, vmembers, vsec, (int)voff, depth + 1);
                    }

                    outp.Add(new GrnMemberValue(node, section, cursor, null, null, kids));
                    cursor += ReferenceFieldSize * 2;
                    continue;
                }
                else if (node.MemberType == GrnMemberType.ReferenceToVariantArray)
                {
                    // { Type* @cursor, count @cursor+8, Object* @cursor+12 } =
                    // 20 bytes: vertex_data.Vertices is the archetype, where the
                    // count is VertexCount and Object is the vertex blob. The
                    // type-tree member list describes one element (one vertex),
                    // so walk it at Object. Field offsets are 8 apart: each
                    // pointer field spans ReferenceFieldSize even though only
                    // its low 4 bytes carry the fixed-up value.
                    if (cursor + ReferenceFieldSize + CountPrefixSize + ReferenceFieldSize > buf.Length)
                    {
                        break;
                    }

                    int vcount = BinaryPrimitives.ReadInt32LittleEndian(
                        buf.AsSpan(cursor + ReferenceFieldSize, CountPrefixSize));
                    bool vfollows = image.TryResolve(
                        section, (uint)(cursor + ReferenceFieldSize + CountPrefixSize),
                        out ushort _, out uint _);
                    bool looksLikeCount = vcount >= 0 && vcount <= MaxArrayElements
                                          && (vcount == 0 || vfollows);
                    if (!looksLikeCount)
                    {
                        Interlocked.Increment(ref _suspiciousCountCount);
                        break;
                    }

                    outp.Add(new GrnMemberValue(
                        node with { Name = node.Name + "Count" }, section,
                        cursor + ReferenceFieldSize, vcount, null, null));

                    IReadOnlyList<GrnTypeNode>? vmembers =
                        VariantMembers(image, node, section, cursor);
                    if (vcount > 0 && vmembers is not null &&
                        image.TryFollow(section,
                            (uint)(cursor + ReferenceFieldSize + CountPrefixSize),
                            out ushort psec, out uint poff) &&
                        psec < image.Sections.Count)
                    {
                        kids = Walk(image, vmembers, psec, (int)poff, depth + 1);
                    }

                    outp.Add(new GrnMemberValue(node, section, cursor, vcount, null, kids));
                    cursor += ReferenceFieldSize + CountPrefixSize + ReferenceFieldSize;
                    continue;
                }
                else if (node.MemberType == GrnMemberType.ReferenceToArray)
                {
                    // The count prefix was already consumed above; the slot at
                    // cursor points at INLINE elements laid end to end (skeleton
                    // bones, transform tracks, topology indices), NOT at an
                    // array of pointer slots. The element stride is the total
                    // size of the element type: 164 for bone, 60 for
                    // transform_track, 4 for an int32 index.
                    long stride = TotalSize(node.ReferenceType);
                    var elems = new List<GrnMemberValue>();
                    bool descended = false;

                    // Scalar element arrays carry nothing a walk could add that
                    // the count does not already say, and their counts run to
                    // tens of thousands, so descend only struct-sized elements.
                    if (count > 0 && stride > CountPrefixSize &&
                        node.ReferenceType.Count > 0 &&
                        image.TryFollow(section, (uint)cursor, out ushort isec, out uint ioff) &&
                        isec < image.Sections.Count)
                    {
                        int n = Math.Min(count, MaxInlineArrayElements);
                        for (int i = 0; i < n; i++)
                        {
                            long at = (long)ioff + (long)i * stride;
                            if (at < 0 || at + stride > image.Sections[isec].Length)
                            {
                                break;
                            }

                            var elem = new GrnMemberValue(node, isec, (int)at, i,
                                                          null, null);
                            elems.Add(elem with
                            {
                                Children = Walk(image, node.ReferenceType, isec,
                                                (int)at, depth + 1),
                            });
                            descended = true;
                        }
                    }

                    if (count > 0)
                    {
                        _arrayStrides[descended ? (int)stride : -1] =
                            _arrayStrides.GetValueOrDefault(descended ? (int)stride : -1) + 1;
                    }

                    outp.Add(new GrnMemberValue(node, section, cursor, count, null,
                                                elems.Count > 0 ? elems : null));
                    cursor += ReferenceFieldSize;
                    continue;
                }
                else if (node.MemberType == GrnMemberType.ArrayOfReferences)
                {
                    // An array-of-references pointer resolves to an array OF
                    // SLOTS, not to an element. Each slot is itself a relocated
                    // pointer to one element object, so descend per element.
                    // Using the element stride that makes every slot resolve
                    // keeps this measured rather than assumed.
                    int stride = 0;
                    var elems = new List<GrnMemberValue>();
                    if (count > 0 &&
                        image.TryFollow(section, (uint)cursor, out ushort asec, out uint aoff) &&
                        asec < image.Sections.Count)
                    {
                        for (int cand = PointerSlotSize; cand <= ReferenceFieldSize; cand++)
                        {
                            var probe = new List<GrnMemberValue>();
                            bool all = true;
                            for (int i = 0; i < count; i++)
                            {
                                long slot = (long)aoff + (long)i * cand;
                                if (slot + PointerSlotSize > image.Sections[asec].Length ||
                                    !image.TryFollow(asec, (uint)slot, out ushort es, out uint eo))
                                {
                                    all = false;
                                    break;
                                }

                                probe.Add(new GrnMemberValue(node, asec, (int)slot, i, null, null));
                                if (node.ReferenceType.Count > 0 && es < image.Sections.Count)
                                {
                                    probe[^1] = probe[^1] with
                                    {
                                        Children = Walk(image, node.ReferenceType, es, (int)eo,
                                                        depth + 1),
                                    };
                                }
                            }

                            if (all)
                            {
                                stride = cand;
                                elems = probe;
                                break;
                            }
                        }
                    }

                    _arrayStrides[stride == 0 ? -1 : stride] =
                        _arrayStrides.GetValueOrDefault(stride == 0 ? -1 : stride) + 1;
                    outp.Add(new GrnMemberValue(node, section, cursor, count, null, elems.Count > 0 ? elems : null));
                    cursor += ReferenceFieldSize;
                    continue;
                }
                else if (node.ReferenceType.Count > 0 &&
                         image.TryFollow(section, (uint)cursor, out ushort rsec, out uint roff) &&
                         rsec < image.Sections.Count)
                {
                    kids = Walk(image, node.ReferenceType, rsec, (int)roff, depth + 1);
                }

                outp.Add(new GrnMemberValue(node, section, cursor, null, null, kids));
                // A reference field spans ReferenceFieldSize bytes in the layout, but only the
                // first PointerSlotSize of those bytes are a relocated pointer.
                // Advancing by PointerSlotSize desynchronises the walk: on
                // torso_lod0.model it puts FromFileName at 8 instead of 16.
                cursor += ReferenceFieldSize;
                continue;
            }

            long span;
            if (node.MemberType == GrnMemberType.Inline)
            {
                // A struct member has no constant width: it is whatever its
                // referenced member list totals (pixel_layout = 36,
                // curve2 = 16). This is GetMemberUnitSize's Inline case.
                span = TotalSize(node.ReferenceType) * width;
            }
            else
            {
                int size = SizeOf(node.MemberType);
                if (size < 0)
                {
                    // Unknown width: the stride of everything after this member is
                    // unknowable, so stop instead of desynchronising.
                    Interlocked.Increment(ref _unknownMemberCount);
                    break;
                }

                span = (long)size * width;
            }

            if (cursor < 0 || cursor + span > buf.Length)
            {
                break;
            }

            if (node.MemberType == GrnMemberType.Inline)
            {
                // Inline members live AT the cursor, so the referenced member
                // list is walked in place (pixel_layout inside texture,
                // curve2 inside transform_track, ...). No scalar value exists.
                IReadOnlyList<GrnMemberValue>? inlineKids =
                    node.ReferenceType.Count > 0
                        ? Walk(image, node.ReferenceType, section, cursor, depth + 1)
                        : null;
                outp.Add(new GrnMemberValue(node, section, cursor,
                                            width == 1 ? null : width,
                                            null, inlineKids));
            }
            else if (width == 1)
            {
                long scalar = ReadScalar(image, node.MemberType, section, cursor);
                outp.Add(new GrnMemberValue(node, section, cursor, scalar, null, null));
            }
            else
            {
                // Array of inline values: record the count and advance. The
                // elements are not printed, since a 60k-entry vertex buffer is
                // not useful to read.
                outp.Add(new GrnMemberValue(node, section, cursor, width, null, null));
            }

            cursor += (int)span;
        }

        return outp;
    }

    /// <summary>
    /// Member list that describes a variant's payload.
    /// </summary>
    /// <remarks>
    /// Prefer the node's own <c>ReferenceType</c>; when the file leaves that
    /// link null (curve2.CurveData does, and D2R vertex layouts may), fall
    /// back to the type record the variant instance's Type slot points at.
    /// That in-instance pointer is the authoritative source: granny resolves
    /// curve formats by comparing <c>CurveData.Type</c> against its format
    /// table on every load.
    /// </remarks>
    private static IReadOnlyList<GrnTypeNode>? VariantMembers(
        GrnImage image, GrnTypeNode node, ushort section, int typeSlot)
    {
        if (node.ReferenceType.Count > 0)
        {
            return node.ReferenceType;
        }

        if (image.TryFollow(section, (uint)typeSlot, out ushort tsec, out uint toff) &&
            tsec < image.Sections.Count)
        {
            IReadOnlyList<GrnTypeNode> t = GrnTypeTree.ReadAt(image, tsec, (int)toff);
            return t.Count > 0 ? t : null;
        }

        return null;
    }

    private static long ReadScalar(GrnImage image, GrnMemberType t, int sec, int at)
    {
        byte[] b = image.Sections[sec];
        return t switch
        {
            GrnMemberType.Int8 => b[at],
            GrnMemberType.UInt8 => b[at],
            GrnMemberType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(at)),
            GrnMemberType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)),
            GrnMemberType.Real16 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)),
            GrnMemberType.Int32 => image.I32(sec, at),
            GrnMemberType.UInt32 => image.U32(sec, at),
            // Real32 is recorded as its IEEE-754 bit pattern, not its decimal
            // value: that keeps the read lossless and avoids implying a
            // precision the raw bytes never had.
            GrnMemberType.Real32 => BitConverter.SingleToInt32Bits(image.F32(sec, at)),
            _ => 0,
        };
    }

    /// <summary>
    /// Renders a walk as indented text, so any structure in any file can be
    /// inspected without writing a reader for it first.
    /// </summary>
    public static string Describe(IReadOnlyList<GrnMemberValue> members, int indent = 0)
    {
        var sb = new StringBuilder();
        foreach (GrnMemberValue m in members)
        {
            sb.Append(' ', indent * 2);
            sb.Append(m.Node.Name.Length == 0 ? "(unnamed)" : m.Node.Name);
            sb.Append(" : ").Append(m.Node.MemberType);

            if (m.Text is not null)
            {
                sb.Append(" = \"").Append(m.Text).Append('"');
            }
            else if (m.Number is { } n)
            {
                sb.Append(" = ").Append(n);
            }

            if (m.Node.ArrayWidth > 0)
            {
                sb.Append(" [").Append(m.Node.ArrayWidth).Append(']');
            }

            sb.AppendLine();

            if (m.Children is { Count: > 0 })
            {
                sb.Append(Describe(m.Children, indent + 1));
            }
        }
        return sb.ToString();
    }
}