using System.Numerics;

namespace Granny.Format;

/// <summary>One contiguous run of triangles sharing a material.</summary>
public readonly record struct MaterialGroup(int MaterialIndex, int FirstTriangle, int TriangleCount);

/// <summary>One bone's influence region plus its oriented bounding box.</summary>
public sealed record BoneBinding(
    string BoneName,
    Vector3 ObbMin,
    Vector3 ObbMax,
    int TriangleCount);

/// <summary>A decoded mesh: geometry, indices, and its bone bindings.</summary>
public sealed class MeshData
{
    public required string Name { get; init; }
    public required int VertexCount { get; init; }
    public required Vector3[] Positions { get; init; }
    /// <summary>
    /// Triangle list, three indices per triangle.
    /// </summary>
    /// <remarks>
    /// <see cref="uint"/> rather than <see cref="ushort"/>. The granny topology
    /// type has two buffers — <c>Indices</c> (32-bit) and <c>Indices16</c>
    /// (16-bit) — so an index can be either width on the wire. Widening the
    /// container accepts both without truncation; a <c>ushort</c> array could
    /// not represent the 32-bit case at all.
    /// </para>
    /// <para>
    /// Which one assets actually use was measured rather than assumed: a census
    /// of all 12,392 corpus meshes reports narrow16Only=12392, wide32Only=0.
    /// So <c>uint</c> is the safe common width, but the 32-bit buffer is
    /// exercised by zero real files here.
    /// </remarks>
    public required uint[] Indices { get; init; }
    public IReadOnlyList<MaterialGroup> Groups { get; init; } = [];
    public IReadOnlyList<BoneBinding> Bones { get; init; } = [];

    public int TriangleCount => Indices.Length / 3;

    /// <summary>Axis-aligned bounds of <see cref="Positions"/>.</summary>
    public (Vector3 Min, Vector3 Max) Bounds
    {
        get
        {
            if (Positions.Length == 0)
            {
                return (Vector3.Zero, Vector3.Zero);
            }
            var min = Positions[0];
            var max = Positions[0];
            foreach (Vector3 p in Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min, max);
        }
    }

    /// <summary>
    /// Fraction of triangles with (near) zero area. A correct decode yields a
    /// small value; a wrong stride or component order yields roughly one.
    /// </summary>
    public double DegenerateTriangleRatio()
    {
        int tris = TriangleCount;
        if (tris == 0)
        {
            return 0.0;
        }

        int bad = 0;
        for (int t = 0; t < tris; t++)
        {
            Vector3 p = Positions[Indices[t * 3]];
            Vector3 q = Positions[Indices[(t * 3) + 1]];
            Vector3 r = Positions[Indices[(t * 3) + 2]];
            if (Vector3.Cross(q - p, r - p).LengthSquared() <= 1e-18f)
            {
                bad++;
            }
        }
        return (double)bad / tris;
    }

    /// <summary>Bounding sphere of every mesh combined, as (centre, radius).</summary>
    /// <remarks>
    /// Shared by the CLI and the renderer so framing is computed from one
    /// definition. The radius is half the box diagonal, floored so an empty or
    /// degenerate scene still yields a usable camera distance.
    /// </remarks>
    public static (Vector3 Centre, float Radius) SceneBounds(IReadOnlyList<MeshData> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (MeshData m in meshes)
        {
            foreach (Vector3 p in m.Positions)
            {
                any = true;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        if (!any)
        {
            return (Vector3.Zero, 1f);
        }
        return ((min + max) * 0.5f, MathF.Max(0.5f, (max - min).Length() * 0.5f));
    }
}
