using System.Buffers.Binary;
using System.Numerics;
using System.Threading;

namespace Granny.Format;

/// <summary>
/// Walks a <see cref="GrnImage"/> object graph and extracts mesh geometry.
/// </summary>
/// <remarks>
/// Verified against <c>torso_lod0.model</c> (4282 vertices, 18594 indices, 6198
/// triangles, 13 bones, 0.06% degenerate) and
/// <c>special1_rol_lod0.model</c> (466 vertices, 2364 indices, 788 triangles,
/// one group whose TriCount matches Index16Count exactly).
/// </remarks>
public static class MeshReader
{
    /// <summary>Reads every mesh in the file's root <c>granny_file_info</c>.</summary>
    public static IReadOnlyList<MeshData> ReadAll(string path)
    {
        var image = new GrnImage(path);
        return ReadAll(image);
    }

    public static IReadOnlyList<MeshData> ReadAll(GrnImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int rootSec = (int)image.File.RootObjectSection;
        int root = (int)image.File.RootObjectOffset;

        int count = image.I32(rootSec, root + GrannyLayout.FileMeshCount);
        if (count <= 0 || !image.TryFollow(rootSec, (uint)(root + GrannyLayout.FileMeshes),
                                            out ushort arrSec, out uint arrOff))
        {
            return [];
        }

        // The Meshes array is an array of pointers to granny_mesh records.
        var meshes = new List<MeshData>(count);
        for (int i = 0; i < count; i++)
        {
            int slot = (int)arrOff + (i * 4);
            if (slot + 4 > image.Sections[arrSec].Length ||
                !image.TryFollow(arrSec, (uint)slot, out ushort meshSec, out uint meshOff))
            {
                continue;
            }
            if (TryReadMesh(image, meshSec, (int)meshOff, out MeshData? mesh) && mesh is not null)
            {
                meshes.Add(mesh);
            }
        }
        return meshes;
    }

    private static bool TryReadMesh(GrnImage image, int meshSec, int meshOff, out MeshData? mesh)
    {
        mesh = null;

        if (!image.TryFollow(meshSec, (uint)(meshOff + GrannyLayout.MeshName),
                             out ushort nameSec, out uint nameOff))
        {
            return false;
        }
        string name = image.Str(nameSec, (int)nameOff);

        if (!image.TryFollow(meshSec, (uint)(meshOff + GrannyLayout.MeshPrimaryVertexData),
                             out ushort vdSec, out uint vdOff) ||
            !image.TryFollow(meshSec, (uint)(meshOff + GrannyLayout.MeshPrimaryTopology),
                             out ushort topoSec, out uint topoOff))
        {
            return false;
        }

        int vertexCount = image.I32(vdSec, (int)vdOff + GrannyLayout.VertexDataVertexCount);
        if (vertexCount <= 0)
        {
            return false;
        }

        if (!image.TryResolve(vdSec,
                (uint)((int)vdOff + GrannyLayout.VertexDataVertices), out ushort vbSec, out uint vbOff))
        {
            return false;
        }

        if (!TryReadPositions(image, vbSec, (int)vbOff, vertexCount, out Vector3[]? positions))
        {
            return false;
        }

        if (!TryReadIndices(image, topoSec, (int)topoOff, vertexCount, out uint[]? indices))
        {
            return false;
        }

        mesh = new MeshData
        {
            Name = name,
            VertexCount = vertexCount,
            Positions = positions!,
            Indices = indices!,
            Groups = ReadGroups(image, topoSec, (int)topoOff),
            Bones = ReadBones(image, meshSec, meshOff),
        };
        return true;
    }


    private static IReadOnlyList<MaterialGroup> ReadGroups(GrnImage image, int topoSec, int topoOff)
    {
        var groups = new List<MaterialGroup>();
        if (!image.TryFollow(topoSec, (uint)(topoOff + GrannyLayout.TopologyGroups),
                             out ushort grpSec, out uint grpOff))
        {
            return groups;
        }

        int count = image.I32(topoSec, topoOff + GrannyLayout.TopologyGroupCount);
        for (int g = 0; g < count; g++)
        {
            int at = (int)grpOff + (g * GrannyLayout.TriMaterialGroupSize);
            groups.Add(new MaterialGroup(
                image.I32(grpSec, at + GrannyLayout.GroupMaterialIndex),
                image.I32(grpSec, at + GrannyLayout.GroupTriFirst),
                image.I32(grpSec, at + GrannyLayout.GroupTriCount)));
        }
        return groups;
    }

    private static IReadOnlyList<BoneBinding> ReadBones(GrnImage image, int meshSec, int meshOff)
    {
        var bones = new List<BoneBinding>();
        int count = image.I32(meshSec, meshOff + GrannyLayout.MeshBoneBindingCount);
        if (count <= 0 ||
            !image.TryFollow(meshSec, (uint)(meshOff + GrannyLayout.MeshBoneBindings),
                             out ushort boneSec, out uint boneOff))
        {
            return bones;
        }

        for (int b = 0; b < count; b++)
        {
            int at = (int)boneOff + (b * GrannyLayout.BoneBindingSize);
            string boneName = image.TryFollow(boneSec, (uint)at, out ushort ns, out uint no)
                ? image.Str(ns, (int)no)
                : string.Empty;
            bones.Add(new BoneBinding(
                boneName,
                ReadVec3(image, boneSec, at + GrannyLayout.BoneBindingObbMin),
                ReadVec3(image, boneSec, at + GrannyLayout.BoneBindingObbMax),
                image.I32(boneSec, at + GrannyLayout.BoneBindingTriangleCount)));
        }
        return bones;
    }

    private static Vector3 ReadVec3(GrnImage image, int sec, int at) =>
        new(image.F32(sec, at), image.F32(sec, at + 4), image.F32(sec, at + 8));

    /// <summary>
    /// Decodes the packed int16 position component of each vertex record.
    /// </summary>
    /// <remarks>
    /// Read from the unfixed-up section on purpose: a pointer slot that happens
    /// to sit inside the payload would otherwise overwrite vertex bytes.
    /// </remarks>
    private static bool TryReadPositions(GrnImage image, int sec, int offset,
                                         int count, out Vector3[]? positions)
    {
        positions = null;
        byte[] buf = image.Raw[sec];
        long span = (long)offset + ((long)GrannyLayout.VertexStride * count);
        if (offset < 0 || span > buf.Length)
        {
            return false;
        }

        int[] order = GrannyLayout.PositionComponentOrder;
        float scale = GrannyLayout.PositionScale;
        var result = new Vector3[count];
        Span<short> q = stackalloc short[4];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * GrannyLayout.VertexStride);
            for (int c = 0; c < 4; c++)
            {
                q[c] = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(at + (c * 2)));
            }
            result[i] = new Vector3(q[order[0]] * scale, q[order[1]] * scale, q[order[2]] * scale);
        }

        positions = result;
        return true;
    }

    /// <summary>
    /// Reads the index buffer, preferring 16-bit and falling back to 32-bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both layouts are read. The granny topology type carries two index buffers
    /// side by side: <c>IndexCount</c>/<c>Indices</c> at +12/+16 (32-bit) and
    /// <c>Index16Count</c>/<c>Indices16</c> at +24/+28 (<c>unsigned __int16
    /// *</c>, 16-bit). The field names are confirmed from granny's type-name
    /// table in d2r.exe, which contains the literal strings "Indices" and
    /// "Indices16" (and no "Indices32" — that name was an earlier mistake here).
    /// </para>
    /// <para>
    /// Which one real assets use was measured, not assumed: a census across all
    /// 12,392 meshes in the 9,916-file corpus reports narrow16Only=12392,
    /// wide32Only=0, both=0. Every mesh uses the 16-bit buffer, because the
    /// largest is 60,807 vertices (<c>burning_pit_rocksShape</c>), under the
    /// 65,536 ceiling. The 32-bit branch is therefore unexercised by real data
    /// and is retained only because the format permits it; the size at which
    /// granny would select it is not something this corpus can demonstrate.
    /// </para>
    /// </remarks>
    private static int NarrowOnlyTopology;
    private static int WideOnlyTopology;
    private static int BothLayoutsTopology;

    public static int TopologyNarrowOnlyCount => NarrowOnlyTopology;
    public static int TopologyWideOnlyCount => WideOnlyTopology;
    public static int TopologyBothLayoutsCount => BothLayoutsTopology;

    public static string TopologyLayoutCensus =>
        $"narrow16Only={NarrowOnlyTopology} wide32Only={WideOnlyTopology} both={BothLayoutsTopology}";

    private static bool TryReadIndices(GrnImage image, int topoSec, int topoOff,
                                       int vertexCount, out uint[]? indices)
    {
        indices = null;

        int c16 = image.I32(topoSec, topoOff + GrannyLayout.TopologyIndex16Count);
        int c32 = image.I32(topoSec, topoOff + GrannyLayout.TopologyIndexCount);

        // Census of which buffer real assets actually populate. Both layouts
        // exist in the format, but only one is ever used in practice, and that
        // is not something the struct layout alone can tell you.
        if (c16 > 0 && c32 > 0)
        {
            Interlocked.Increment(ref BothLayoutsTopology);
        }
        else if (c32 > 0)
        {
            Interlocked.Increment(ref WideOnlyTopology);
        }
        else if (c16 > 0)
        {
            Interlocked.Increment(ref NarrowOnlyTopology);
        }

        if (c16 > 0 &&
            image.TryFollow(topoSec, (uint)(topoOff + GrannyLayout.TopologyIndices16),
                            out ushort s16, out uint o16))
        {
            indices = ReadIndexPayload(image, s16, (int)o16, c16, 2, vertexCount);
            if (indices is not null)
            {
                return true;
            }
        }

        if (c32 > 0 &&
            image.TryFollow(topoSec, (uint)(topoOff + GrannyLayout.TopologyIndices),
                            out ushort s32, out uint o32))
        {
            indices = ReadIndexPayload(image, s32, (int)o32, c32, 4, vertexCount);
            if (indices is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static uint[]? ReadIndexPayload(GrnImage image, int sec, int offset,
                                              int count, int elementSize, int vertexCount)
    {
        byte[] buf = image.Raw[sec];
        long span = (long)offset + ((long)count * elementSize);
        if (offset < 0 || span > buf.Length || count > int.MaxValue / 2)
        {
            return null;
        }

        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * elementSize);
            uint v = elementSize == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(at))
                : BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(at));

            // Out-of-range indices are the cheapest reliable signal that the
            // walk went astray, so treat them as a decode failure. Checking the
            // full 32-bit value (never casting down first) is what lets a mesh
            // with more than 65,535 vertices survive the read at all.
            if (v >= (uint)vertexCount)
            {
                return null;
            }
            result[i] = v;
        }
        return result;
    }
}
