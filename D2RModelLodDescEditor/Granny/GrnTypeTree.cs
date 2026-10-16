namespace Granny.Format;

/// <summary>The granny member-kind tags, from <c>granny_member_type</c>.</summary>
/// <remarks>
/// Values recovered from the granny symbols in <c>bg3_Release.exe</c>
/// (<c>granny_member_type</c> ord. 955). <c>GrannyBool32Member</c> shares the
/// value 0x13 with <c>GrannyInt32Member</c> in that enum, so it is not
/// distinguishable here and is folded into <see cref="Int32"/>.
/// </remarks>
public enum GrnMemberType
{
    End = 0x00,
    Inline = 0x01,
    Reference = 0x02,
    ReferenceToArray = 0x03,
    ArrayOfReferences = 0x04,
    VariantReference = 0x05,
    UnsupportedRemove = 0x06,
    ReferenceToVariantArray = 0x07,
    String = 0x08,
    Transform = 0x09,
    Real32 = 0x0A,
    Int8 = 0x0B,
    UInt8 = 0x0C,
    BinormalInt8 = 0x0D,
    NormalUInt8 = 0x0E,
    Int16 = 0x0F,
    UInt16 = 0x10,
    BinormalInt16 = 0x11,
    NormalUInt16 = 0x12,
    Int32 = 0x13,
    UInt32 = 0x14,
    Real16 = 0x15,
    EmptyReference = 0x16,
    OnePastLast = 0x17,
}

/// <summary>
/// One node of a grn file's type tree.
/// </summary>
/// <remarks>
/// The root type section holds a tree per granny type. A node's
/// <c>ReferenceType</c> points at the root of a <em>different</em> tree in the
/// same section (typically earlier in the file), which is why references are
/// resolved by reading a nested tree rather than by indexing a flat list.
/// </remarks>
/// <param name="MemberType">The kind of member this node describes.</param>
/// <param name="Name">The member or type name, or empty when absent.</param>
/// <param name="ArrayWidth">Element count for array members, else 0.</param>
/// <param name="ReferenceType">
/// For members whose payload is described by another type, the member list of
/// that referenced type; empty otherwise. A type is a <em>list</em> of members,
/// not a single node, so this is a list even for scalar references.
/// </param>
public sealed record GrnTypeNode(
    GrnMemberType MemberType,
    string Name,
    int ArrayWidth,
    IReadOnlyList<GrnTypeNode> ReferenceType);

/// <summary>
/// Reads a grn file's type tree and walks arbitrary objects by reflection over
/// it.
/// </summary>
/// <remarks>
/// <para>
/// A grn file stores a type tree in its root type section and points the root
/// object at it. Walking the tree lets a caller read any structure granny
/// defined without hard-coding offsets, which is what
/// <see cref="MeshReader"/> otherwise has to do per struct.
/// </para>
/// <para>
/// Field offsets come from the granny symbols in <c>bg3_Release.exe</c>:
/// <c>granny_data_type_definition</c> (ord. 54278, size 44) is
/// <c>Type</c>, <c>Name</c>, <c>ReferenceType</c>, <c>ArrayWidth</c>,
/// <c>Extra[3]</c>, <c>Ignored</c>; <c>granny_member_type</c> ord. 955 gives the
/// member tags. The type tree is a flat sibling list terminated by
/// <see cref="GrnMemberType.End"/>, with <c>ReferenceType</c> linking to an
/// earlier or later node rather than nesting.
/// </para>
/// </remarks>
public static class GrnTypeTree
{
    /// Field offsets of <c>granny_data_type_definition</c> as stored in the
    /// file.
    /// <remarks>
    /// <para>
    /// The on-disk record is byte-for-byte the 64-bit in-memory struct that
    /// granny 2.11.8 (and bg3's 2.12) compile: members are laid out with
    /// 4-byte alignment (so a pointer field may start at <c>4 mod 8</c>) and
    /// pointers are 8 bytes wide, but a fixup only ever writes a 4-byte
    /// section-relative value into the low half of the slot; the high half
    /// stays zero on disk.
    /// </para>
    /// <code>
    /// +0   u32  member_type   granny_member_type, 0 = EndMember (terminator)
    /// +4   ptr  name          8-byte field, 4-byte fixed-up value
    /// +12  ptr  reference_type 8-byte field, 4-byte fixed-up value
    /// +20  i32  array_width   0 for scalars; element count for inline arrays
    /// +24  i32  extra[3]     reserved, zero in every record observed
    /// +36  u64  ignored       reserved, zero in every record observed
    /// 44 bytes total
    /// </code>
    /// Established three independent ways that agree exactly: the IDA local
    /// type <c>granny_data_type_definition</c> in granny2_x64 2.11.8 (Type@0,
    /// Name@4, ReferenceType@12, ArrayWidth@20, Extra@24, Ignored@36, size 44);
    /// a raw dump of <c>act2female.skeleton</c> section 6, where the four
    /// <c>art_tool_info</c> vector records (Origin/Right/Up/Back) carry the
    /// value 3 at +20 and zero at +16; and the SDK's own
    /// <c>GetTotalTypeSize</c>/<c>data_type_definition</c> layout in
    /// <c>granny_data_type_definition.cpp</c>.
    /// </remarks>
    public const int TypeType = 0;
    public const int TypeName = 4;
    public const int TypeReferenceType = 12;
    public const int TypeArrayWidth = 20;
    public const int TypeSize = 44;

    private const int MaxNodes = 1 << 16;

    private const int MaxDepth = 24;

    /// <summary>
    /// Reads the root type tree of the file: one node per member of the root
    /// object type, up to the first <see cref="GrnMemberType.End"/>.
    /// </summary>
    /// <remarks>
    /// Each node's <see cref="GrnTypeNode.ReferenceType"/> is resolved to the
    /// nested tree it points at, so the result describes the whole object
    /// graph rather than just the top level. References are followed
    /// iteratively (see <see cref="ReadTree"/>) because the trees are
    /// self-referential: <c>granny_file_info</c> reaches
    /// <c>granny_tri_topology</c>, which points back into type definitions
    /// that reference the same nodes again.
    /// </remarks>
    public static IReadOnlyList<GrnTypeNode> Read(GrnImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int sec = (int)image.File.RootTypeSection;
        int off = (int)image.File.RootTypeOffset;
        return ReadAt(image, sec, off);
    }

    /// <summary>
    /// Reads and fully links the type tree rooted at an arbitrary record.
    /// </summary>
    /// <remarks>
    /// Besides the root, this is how a variant payload type is recovered: a
    /// <c>VariantReference</c> member's own <c>ReferenceType</c> link is often
    /// null on disk (curve2.CurveData is), while the variant *instance*
    /// carries a live pointer to its type record. Follow that pointer here.
    /// </remarks>
    public static IReadOnlyList<GrnTypeNode> ReadAt(GrnImage image, int sec, int off)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Seed the visited set with the root so a type that references itself
        // stops instead of recursing.
        var visiting = new HashSet<int> { off };
        return Link(ReadTree(image, sec, off), image, sec, off, visiting, 0);
    }

    /// <summary>
    /// Reads one type tree starting at (<paramref name="sec"/>,
    /// <paramref name="off"/>), terminating at the first
    /// <see cref="GrnMemberType.End"/>.
    /// </summary>
    public static IReadOnlyList<GrnTypeNode> ReadTree(GrnImage image, int sec, int off)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] raw = image.Raw[sec];

        var nodes = new List<GrnTypeNode>();
        for (int i = 0; i < MaxNodes; i++)
        {
            int at = off + (i * TypeSize);
            if (at < 0 || at + TypeSize > raw.Length)
            {
                break;
            }

            var mt = (GrnMemberType)image.I32(sec, at + TypeType);
            if (mt is GrnMemberType.End or GrnMemberType.OnePastLast)
            {
                break;
            }

            string name = image.TryFollow(sec, (uint)(at + TypeName),
                                          out ushort nameSec, out uint nameOff)
                ? image.Str(nameSec, (int)nameOff)
                : string.Empty;

            int width = image.I32(sec, at + TypeArrayWidth);

            // ReferenceType stays empty here; Link fills it in.
            nodes.Add(new GrnTypeNode(mt, name, width, []));
        }

        return nodes;
    }

    /// <summary>
    /// Resolves each node's <code>ReferenceType</code> into the nested tree
    /// it points at, recursively, bounded by <see cref="MaxDepth"/> and by
    /// the set of already-visited tree offsets so a self-referential type
    /// terminates instead of recursing forever.
    /// </summary>
    /// <remarks>
    /// The recursion matters: a type graph is more than two levels deep
    /// (<c>file_info</c> to <c>mesh</c> to <c>vertex_data</c> to the vertex
    /// layout, or <c>file_info</c> to <c>model</c> to <c>skeleton</c> to
    /// <c>bone</c>), and resolving only the root level leaves every deeper
    /// node with an empty <c>ReferenceType</c>, which silently stops any
    /// object walk before it reaches bones, textures, or animation tracks.
    /// </remarks>
    private static IReadOnlyList<GrnTypeNode> Link(
        IReadOnlyList<GrnTypeNode> nodes, GrnImage image, int sec, int off,
        HashSet<int> visiting, int depth)
    {
        if (depth > MaxDepth)
        {
            return nodes;
        }

        var linked = new List<GrnTypeNode>(nodes.Count);

        for (int i = 0; i < nodes.Count; i++)
        {
            GrnTypeNode n = nodes[i];
            IReadOnlyList<GrnTypeNode> resolved = [];

            int at = off + (i * TypeSize);
            if (image.TryFollow(sec, (uint)(at + TypeReferenceType),
                                out ushort refSec, out uint refOff) &&
                refSec == sec)
            {
                int target = (int)refOff;
                if (!visiting.Contains(target))
                {
                    visiting.Add(target);
                    resolved = Link(ReadTree(image, refSec, target), image,
                                    sec, target, visiting, depth + 1);
                    visiting.Remove(target);
                }
            }

            linked.Add(n with { ReferenceType = resolved });
        }

        return linked;
    }
}